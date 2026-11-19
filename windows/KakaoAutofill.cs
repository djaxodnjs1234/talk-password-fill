using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace TalkPasswordFill;

internal sealed class KakaoAutofill(PasswordVault vault)
{
    private readonly Dictionary<(int Id, DateTime Started), bool> signatureResults = new();
    private Candidate? filled;
    private int lastRetry;

    internal string Scan(int retry, CancellationToken cancellation)
    {
        if (lastRetry != retry)
        {
            lastRetry = retry;
            filled = null;
        }

        var found = new List<Candidate>();
        var running = new HashSet<(int Id, DateTime Started)>();
        bool sawUnverifiedApp = false;
        bool sawVerifiedApp = false;
        foreach (Process process in Process.GetProcessesByName("KakaoTalk"))
        {
            using (process)
            {
                try
                {
                    var processKey = (process.Id, process.StartTime);
                    running.Add(processKey);
                    if (!signatureResults.TryGetValue(processKey, out bool trusted))
                    {
                        string? executable = process.MainModule?.FileName;
                        trusted = executable != null &&
                            string.Equals(Path.GetFileName(executable), "KakaoTalk.exe", StringComparison.OrdinalIgnoreCase) &&
                            Publisher.IsKakao(executable);
                        signatureResults[processKey] = trusted;
                    }
                    if (!trusted)
                    {
                        sawUnverifiedApp = true;
                        continue;
                    }
                    sawVerifiedApp = true;
                    found.AddRange(FindLoginFields(processKey.Id, processKey.StartTime));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    // The target may exit between enumeration and inspection.
                }
            }
        }

        foreach (var key in signatureResults.Keys.Where(key => !running.Contains(key)).ToArray())
        {
            signatureResults.Remove(key);
        }
        if (found.Count == 0)
        {
            filled = null;
            if (sawVerifiedApp) return "Waiting for a supported KakaoTalk login window";
            return sawUnverifiedApp ? "Could not verify KakaoTalk’s publisher" : "Waiting for KakaoTalk";
        }
        if (found.Count != 1) return "More than one KakaoTalk login field found";

        Candidate candidate = found[0];
        if (filled == candidate) return "Already filled this login window";
        int? length = Native.TextLength(candidate.Field);
        if (length is null) return "KakaoTalk password field is unavailable";
        if (length != 0) return "The password field already contains text";
        if (cancellation.IsCancellationRequested) return "Autofill stopped";

        char[] password = vault.Read();
        try
        {
            if (!candidate.IsCurrent() || Native.TextLength(candidate.Field) != 0 || cancellation.IsCancellationRequested)
            {
                return "Waiting for an empty KakaoTalk password field";
            }
            if (!Native.SetText(candidate.Field, password) || Native.TextLength(candidate.Field) != password.Length - 1)
            {
                return "KakaoTalk did not accept autofill";
            }
            filled = candidate;
            return "Password filled. Click Log In in KakaoTalk.";
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private static IEnumerable<Candidate> FindLoginFields(int pid, DateTime started)
    {
        foreach (nint window in Native.VisibleWindows(pid))
        {
            string title = Native.WindowTitle(window).Trim();
            bool namedLogin = IsLoginLabel(title) || title.Equals("KakaoTalk Login", StringComparison.OrdinalIgnoreCase) || title == "카카오톡 로그인";
            bool namedApp = title.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase) || title == "카카오톡";
            if (!namedLogin && !namedApp) continue;

            var fields = Native.ChildWindows(window).Where(field => Native.IsPasswordEdit(field, window, pid)).Take(2).ToArray();
            if (fields.Length == 1 && (namedLogin || HasLoginButton(window, pid)))
            {
                yield return new Candidate(window, fields[0], pid, started);
            }
        }
    }

    private static bool HasLoginButton(nint window, int pid)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var conditions = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            return root.FindAll(TreeScope.Descendants, conditions).Cast<AutomationElement>()
                .Any(button => !button.Current.IsOffscreen && IsLoginLabel(button.Current.Name));
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsLoginLabel(string? title) => title?.Trim().ToLowerInvariant() is "로그인" or "login" or "log in" or "sign in";

    private readonly record struct Candidate(nint Window, nint Field, int Pid, DateTime ProcessStart)
    {
        internal bool IsCurrent() => Native.IsPasswordEdit(Field, Window, Pid);
    }
}

internal static class Native
{
    private const int StyleIndex = -16;
    private const long PasswordStyle = 0x20;
    private const long ReadOnlyStyle = 0x800;
    private const uint GetTextLength = 0x000E;
    private const uint SetTextMessage = 0x000C;
    private const uint AbortIfHung = 0x0002;
    private const uint ErrorOnExit = 0x0020;
    private const uint RootAncestor = 2;

    internal static List<nint> VisibleWindows(int pid)
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (OwnsWindow(window, pid) && IsWindowVisible(window)) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    internal static List<nint> ChildWindows(nint parent)
    {
        var windows = new List<nint>();
        EnumChildWindows(parent, (window, _) => { windows.Add(window); return true; }, 0);
        return windows;
    }

    internal static string WindowTitle(nint window)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static bool IsPasswordEdit(nint field, nint parent, int pid)
    {
        if (!OwnsWindow(field, pid) || GetAncestor(field, RootAncestor) != parent ||
            !IsWindowVisible(field) || !IsWindowEnabled(field)) return false;
        var buffer = new StringBuilder(64);
        if (GetClassName(field, buffer, buffer.Capacity) == 0) return false;
        string fieldClass = buffer.ToString();
        if (!fieldClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
            !fieldClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;
        long style = GetWindowLongPtr(field, StyleIndex);
        return (style & (PasswordStyle | ReadOnlyStyle)) == PasswordStyle;
    }

    internal static int? TextLength(nint field)
    {
        if (SendMessageTimeout(field, GetTextLength, 0, 0, AbortIfHung | ErrorOnExit, 250, out nint length) == 0) return null;
        return length >= 0 && length <= int.MaxValue ? (int)length : null;
    }

    internal static unsafe bool SetText(nint field, char[] characters)
    {
        fixed (char* password = characters)
        {
            return SendMessageTimeout(field, SetTextMessage, 0, (nint)password, AbortIfHung | ErrorOnExit, 250, out nint result) != 0 && result != 0;
        }
    }

    private static bool OwnsWindow(nint window, int pid) => GetWindowThreadProcessId(window, out int owner) != 0 && owner == pid;

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}

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
    private FieldIdentity? filled;
    private int lastRetry;

    internal string Scan(int retry, bool manual, CancellationToken cancellation)
    {
        if (lastRetry != retry)
        {
            lastRetry = retry;
            filled = null;
        }

        var found = new List<LoginField>();
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
                    foreach (nint window in Native.VisibleWindows(process.Id))
                    {
                        LoginField? login = FindLoginField(window, process.Id, process.StartTime);
                        if (login is not null) found.Add(login);
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    // The target may exit between process enumeration and window inspection.
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
            if (sawVerifiedApp) return "KakaoTalk is open. Waiting for a supported login window.";
            return sawUnverifiedApp ? "Windows could not verify KakaoTalk’s Kakao Corp. signature." : "Start KakaoTalk to check autofill.";
        }
        if (found.Count != 1) return "More than one KakaoTalk login field is open. Close extra login windows.";

        LoginField candidate = found[0];
        if (filled == candidate.Identity) return "Password filled for this login window. Click Log In in KakaoTalk.";
        if (CheckReadiness(candidate, manual, cancellation) is string unavailable) return unavailable;

        char[] password = vault.Read();
        try
        {
            if (CheckReadiness(candidate, manual, cancellation) is string changed) return changed;
            if (!candidate.Set(password)) return "KakaoTalk did not accept autofill on this login field.";

            filled = candidate.Identity;
            return "Password filled. Click Log In in KakaoTalk.";
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private static string? CheckReadiness(LoginField candidate, bool manual, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return "Autofill stopped.";
        if (!candidate.IsCurrent()) return "KakaoTalk’s password field changed.";
        int? length = candidate.NativeHandle == 0 ? null : Native.TextLength(candidate.NativeHandle);
        if (length > 0) return "The KakaoTalk password field already contains text.";
        if (length is null && (!manual || candidate.Automation is null))
        {
            return candidate.Automation is null
                ? "Windows could not verify that the KakaoTalk password field is empty."
                : "Windows hides the field length. Clear the KakaoTalk field and choose Fill now.";
        }
        return null;
    }

    internal static LoginField? FindLoginField(nint window, int pid, DateTime started)
    {
        string title = Native.WindowTitle(window).Trim();
        bool namedLogin = IsLoginLabel(title) ||
            title.Equals("KakaoTalk Login", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("KakaoTalk - Login", StringComparison.OrdinalIgnoreCase) ||
            title is "카카오톡 로그인" or "카카오톡 - 로그인";
        bool namedApp = title.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase) || title == "카카오톡";
        if (!namedLogin && !namedApp) return null;

        nint[] edits = Native.ChildWindows(window).Where(field => Native.IsEditable(field, window, pid)).ToArray();
        nint[] passwordEdits = edits.Where(Native.IsMasked).Take(2).ToArray();
        if (passwordEdits.Length > 1) return null;

        // The normal login view has one account field and one masked password field.
        bool hasAccountAndPassword = edits.Length == 2 && passwordEdits.Length == 1;
        if (passwordEdits.Length == 1 && (namedLogin || hasAccountAndPassword))
        {
            nint field = passwordEdits[0];
            return new LoginField(new(pid, started, window, field.ToString()), field, null);
        }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            bool hasLoginButton = namedLogin || HasLoginButton(root, pid);
            if (!hasLoginButton) return null;
            if (passwordEdits.Length == 1)
            {
                nint field = passwordEdits[0];
                return new LoginField(new(pid, started, window, field.ToString()), field, null);
            }

            var conditions = new AndCondition(
                new PropertyCondition(AutomationElement.IsPasswordProperty, true),
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            var protectedFields = root.FindAll(TreeScope.Descendants, conditions).Cast<AutomationElement>()
                .Where(element => element.Current.IsEnabled && !element.Current.IsOffscreen)
                .Take(2).ToArray();
            if (protectedFields.Length != 1) return null;
            AutomationElement target = protectedFields[0];
            if (!CanWrite(target, pid)) return null;

            nint nativeHandle = (nint)(uint)target.Current.NativeWindowHandle;
            if (!Native.IsEditable(nativeHandle, window, pid)) nativeHandle = 0;
            return new LoginField(new(pid, started, window, string.Join(",", target.GetRuntimeId())), nativeHandle, target);
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasLoginButton(AutomationElement root, int pid)
    {
        var conditions = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
        return root.FindAll(TreeScope.Descendants, conditions).Cast<AutomationElement>()
            .Any(button => !button.Current.IsOffscreen && IsLoginLabel(button.Current.Name));
    }

    private static bool CanWrite(AutomationElement target, int pid) =>
        target.Current.ProcessId == pid && target.Current.IsPassword &&
        target.Current.IsEnabled && !target.Current.IsOffscreen &&
        target.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) &&
        pattern is ValuePattern value && !value.Current.IsReadOnly;

    internal static bool IsLoginLabel(string? title) => title?.Trim().ToLowerInvariant() is "로그인" or "login" or "log in" or "sign in";

    internal readonly record struct FieldIdentity(int Pid, DateTime ProcessStart, nint Window, string Control);

    internal sealed record LoginField(FieldIdentity Identity, nint NativeHandle, AutomationElement? Automation)
    {
        internal bool IsCurrent()
        {
            if (!Native.IsVisibleWindow(Identity.Window, Identity.Pid)) return false;
            if (Automation is null)
            {
                return Native.IsEditable(NativeHandle, Identity.Window, Identity.Pid) && Native.IsMasked(NativeHandle);
            }

            try
            {
                return CanWrite(Automation, Identity.Pid) &&
                    (NativeHandle == 0 || Native.IsEditable(NativeHandle, Identity.Window, Identity.Pid));
            }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                return false;
            }
        }

        internal bool Set(char[] password)
        {
            int expectedLength = password.Length - 1;
            if (Automation is null)
            {
                return Native.SetText(NativeHandle, password) && Native.TextLength(NativeHandle) == expectedLength;
            }

            try
            {
                if (!Automation.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) ||
                    pattern is not ValuePattern value || value.Current.IsReadOnly) return false;
                value.SetValue(new string(password, 0, expectedLength));
                int? actualLength = NativeHandle == 0 ? null : Native.TextLength(NativeHandle);
                return actualLength is null || actualLength == expectedLength;
            }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }
    }
}

internal static class Native
{
    private const int StyleIndex = -16;
    private const long PasswordStyle = 0x20;
    private const long ReadOnlyStyle = 0x800;
    private const uint GetPasswordChar = 0x00D2;
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
            if (IsVisibleWindow(window, pid)) windows.Add(window);
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

    internal static bool IsVisibleWindow(nint window, int pid) =>
        OwnsWindow(window, pid) && IsWindowVisible(window) && !IsIconic(window);

    internal static bool IsEditable(nint field, nint parent, int pid)
    {
        if (!OwnsWindow(field, pid) || GetAncestor(field, RootAncestor) != parent ||
            !IsWindowVisible(field) || !IsWindowEnabled(field)) return false;
        var buffer = new StringBuilder(64);
        if (GetClassName(field, buffer, buffer.Capacity) == 0) return false;
        string fieldClass = buffer.ToString();
        if (!fieldClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
            !fieldClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;
        return (GetWindowLongPtr(field, StyleIndex) & ReadOnlyStyle) == 0;
    }

    internal static bool IsMasked(nint field) => (GetWindowLongPtr(field, StyleIndex) & PasswordStyle) != 0 ||
        (SendMessageTimeout(field, GetPasswordChar, 0, 0, AbortIfHung | ErrorOnExit, 250, out nint mask) != 0 && mask != 0);

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
    private static extern bool IsIconic(nint window);

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

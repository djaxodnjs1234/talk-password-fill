using Microsoft.Win32;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Forms;
using Application = System.Windows.Forms.Application;

namespace TalkPasswordFill;

internal static class Program
{
    private const string InstanceName = @"Local\TalkPasswordFillPreview";
    private const string RequestName = @"Local\TalkPasswordFill.ShowSettings";

    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.Ordinal);
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

            using var running = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                if (!background && !RequestSettings())
                {
                    MessageBox.Show("An older Talk Password Fill is running. Quit it from the notification area or Task Manager, then open this version again.",
                        "Talk Password Fill", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, RequestName);
            using var tray = new Tray(showRequest);
            if (!background) tray.OpenSettings();
            Application.Run(tray);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Talk Password Fill could not start or stopped unexpectedly ({error.GetType().Name}). Extract the entire Windows ZIP to a folder before opening the app.",
                "Talk Password Fill", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool RequestSettings()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(RequestName, out EventWaitHandle? request))
            {
                using (request) return request.Set();
            }
            Thread.Sleep(80);
        }
        return false;
    }
}

internal sealed class Tray : ApplicationContext
{
    private readonly PasswordVault vault = new();
    private readonly KakaoAutofill autofill;
    private readonly EventWaitHandle showRequest;
    private readonly ToolStripMenuItem status = new("Starting") { Enabled = false };
    private readonly ToolStripMenuItem pause = new("Pause autofill");
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private SettingsForm? settings;
    private CancellationTokenSource? scan;
    private bool paused;
    private bool fillRequested;
    private bool closing;
    private int retry;

    internal Tray(EventWaitHandle showRequest)
    {
        this.showRequest = showRequest;
        autofill = new KakaoAutofill(vault);
        icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "Talk Password Fill", Visible = true, ContextMenuStrip = menu };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Fill now", null, (_, _) => RequestFill());
        menu.Items.Add(pause);
        menu.Items.Add("Delete saved password…", null, (_, _) => DeletePassword());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        pause.Click += (_, _) =>
        {
            paused = !paused;
            pause.Text = paused ? "Resume autofill" : "Pause autofill";
            scan?.Cancel();
            ShowStatus(paused ? "Autofill is paused." : "Autofill resumed.");
        };
        icon.DoubleClick += (_, _) => OpenSettings();
        timer.Tick += Poll;
        timer.Start();
        ShowStatus(vault.Exists ? "Waiting for KakaoTalk…" : "Save a password to enable autofill.");
    }

    internal void OpenSettings()
    {
        if (settings is null)
        {
            settings = new SettingsForm();
            settings.Save.Click += (_, _) => SaveSettings();
            settings.Fill.Click += (_, _) => RequestFill();
            settings.FormClosing += (_, args) =>
            {
                if (args.CloseReason != CloseReason.UserClosing) return;
                args.Cancel = true;
                settings.Password.Clear();
                settings.Hide();
            };
        }

        if (!settings.Visible)
        {
            settings.Password.PlaceholderText = vault.Exists ? "Leave blank to keep the saved password" : "Enter your KakaoTalk password";
            settings.Status.Text = status.Text;
            settings.StartAtSignIn.Checked = StartupRegistration.HasEntry || !vault.Exists;
            settings.Show();
        }
        settings.Activate();
    }

    private async void Poll(object? sender, EventArgs args)
    {
        if (showRequest.WaitOne(0)) OpenSettings();
        if (scan != null || (paused && !fillRequested)) return;
        if (!vault.Exists)
        {
            fillRequested = false;
            ShowStatus("Save a password to enable autofill.");
            return;
        }

        using var pending = new CancellationTokenSource();
        scan = pending;
        bool manual = fillRequested;
        fillRequested = false;
        int requestedRetry = retry;
        try
        {
            string result = await Task.Run(() => autofill.Scan(requestedRetry, manual, pending.Token));
            if (!closing && !pending.IsCancellationRequested) ShowStatus(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or SecurityException)
        {
            if (!closing && !pending.IsCancellationRequested) ShowStatus("Could not access the saved password. Save it again in this app.");
        }
        catch (Exception error) when (error is Win32Exception or COMException or InvalidOperationException)
        {
            if (!closing && !pending.IsCancellationRequested) ShowStatus($"Windows could not inspect the KakaoTalk window ({error.GetType().Name}).");
        }
        finally
        {
            scan = null;
        }
    }

    private void ShowStatus(string message)
    {
        status.Text = message;
        if (settings is not null) settings.Status.Text = message;
    }

    private void RequestFill()
    {
        if (!vault.Exists)
        {
            OpenSettings();
            ShowStatus("Save a password before using Fill now.");
            return;
        }
        retry++;
        fillRequested = true;
        scan?.Cancel();
        ShowStatus("Checking the KakaoTalk login window…");
    }

    private void SaveSettings()
    {
        if (settings is null) return;
        string password = settings.Password.Text;
        if ((password.Length == 0 && !vault.Exists) || password.Contains('\0'))
        {
            ShowStatus("Enter your KakaoTalk password first.");
            settings.Password.Focus();
            return;
        }

        scan?.Cancel();
        try
        {
            if (password.Length > 0)
            {
                vault.Save(password);
                settings.Password.Clear();
                settings.Password.PlaceholderText = "Leave blank to keep the saved password";
            }
            retry++;
            try
            {
                StartupRegistration.SetEnabled(settings.StartAtSignIn.Checked);
                ShowStatus("Settings saved. Open the KakaoTalk login window to check autofill.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or SecurityException)
            {
                ShowStatus("Password saved. The Windows sign-in setting could not be changed.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or SecurityException)
        {
            ShowStatus("Could not save your password on this PC.");
        }
    }

    private void DeletePassword()
    {
        if (MessageBox.Show("Delete the password saved on this PC?", "Talk Password Fill",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        scan?.Cancel();
        fillRequested = false;
        try
        {
            vault.Delete();
            StartupRegistration.SetEnabled(false);
            if (settings is not null)
            {
                settings.Password.Clear();
                settings.Password.PlaceholderText = "Enter your KakaoTalk password";
                settings.StartAtSignIn.Checked = false;
            }
            ShowStatus("Saved password deleted.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowStatus("Could not complete password removal or update Windows startup.");
        }
    }

    protected override void ExitThreadCore()
    {
        closing = true;
        timer.Stop();
        scan?.Cancel();
        settings?.Dispose();
        icon.Visible = false;
        icon.Dispose();
        timer.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class SettingsForm : Form
{
    internal TextBox Password { get; } = new() { UseSystemPasswordChar = true, MaxLength = 512, Dock = DockStyle.Top, AccessibleName = "KakaoTalk password" };
    internal CheckBox StartAtSignIn { get; } = new() { Text = "Launch at Windows sign-in", AutoSize = true };
    internal Button Save { get; } = new() { Text = "Save settings", AutoSize = true };
    internal Button Fill { get; } = new() { Text = "Fill now", AutoSize = true };
    internal Label Status { get; } = new() { Dock = DockStyle.Fill, AccessibleName = "Autofill status" };

    internal SettingsForm()
    {
        Text = "Talk Password Fill";
        Icon = SystemIcons.Shield;
        Font = SystemFonts.MessageBoxFont!;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(450, 248);
        MinimumSize = new Size(420, 280);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AcceptButton = Save;

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        actions.Controls.AddRange([Save, Fill]);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        for (int row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "Save your KakaoTalk password in this window.", AutoSize = true });
        grid.Controls.Add(Password);
        grid.Controls.Add(StartAtSignIn);
        grid.Controls.Add(actions);
        grid.Controls.Add(new Label { Text = "Use Fill now only with an empty KakaoTalk password field.", AutoSize = true });
        Status.Padding = new Padding(3, 12, 3, 0);
        grid.Controls.Add(Status);
        Controls.Add(grid);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Password.IsDisposed) Password.Clear();
        base.Dispose(disposing);
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "TalkPasswordFillPreview";

    internal static bool HasEntry
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(Entry) is string { Length: > 0 };
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
            string command = $"\"{path}\" --background";
            if (command.Length > 260) throw new InvalidOperationException("Use a shorter install path.");
            run.SetValue(Entry, command);
        }
        else run.DeleteValue(Entry, throwOnMissingValue: false);
    }
}

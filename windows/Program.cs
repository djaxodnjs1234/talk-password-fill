using Microsoft.Win32;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using Application = System.Windows.Forms.Application;

namespace TalkPasswordFill;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var running = new Mutex(true, @"Local\TalkPasswordFillPreview", out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var tray = new Tray();
        Application.Run(tray);
    }
}

internal sealed class Tray : ApplicationContext
{
    private readonly PasswordVault vault = new();
    private readonly KakaoAutofill autofill;
    private readonly ToolStripMenuItem status = new("Starting") { Enabled = false };
    private readonly ToolStripMenuItem startup = new("Launch at Windows sign-in");
    private readonly ToolStripMenuItem pause = new("Pause autofill");
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private CancellationTokenSource? scan;
    private bool isPaused;
    private bool hasDialog;
    private int retry;

    internal Tray()
    {
        autofill = new KakaoAutofill(vault);
        icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "Talk Password Fill", Visible = true, ContextMenuStrip = menu };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set or change password…", null, (_, _) => EditPassword());
        menu.Items.Add("Fill again", null, (_, _) => { retry++; scan?.Cancel(); });
        menu.Items.Add(pause);
        menu.Items.Add(startup);
        menu.Items.Add("Delete saved password…", null, (_, _) => DeletePassword());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => startup.Checked = StartupRegistration.IsEnabled();
        startup.Click += (_, _) => UpdateStartup(!StartupRegistration.IsEnabled());
        pause.Click += (_, _) =>
        {
            isPaused = !isPaused;
            pause.Text = isPaused ? "Resume autofill" : "Pause autofill";
            scan?.Cancel();
            if (isPaused) ShowStatus("Autofill is paused");
        };
        icon.DoubleClick += (_, _) => EditPassword();
        timer.Tick += Poll;
        timer.Start();
        if (!vault.Exists)
        {
            Application.Idle += OnFirstRun;
        }
    }

    private void OnFirstRun(object? sender, EventArgs args)
    {
        Application.Idle -= OnFirstRun;
        EditPassword();
    }

    private async void Poll(object? sender, EventArgs args)
    {
        if (scan != null || isPaused || hasDialog) return;
        if (!vault.Exists)
        {
            ShowStatus("Set your password to start");
            return;
        }

        using var pending = new CancellationTokenSource();
        scan = pending;
        int requestedRetry = retry;
        try
        {
            string result = await Task.Run(() => autofill.Scan(requestedRetry, pending.Token));
            if (!pending.IsCancellationRequested) ShowStatus(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (!pending.IsCancellationRequested) ShowStatus("Could not access the saved password");
        }
        finally
        {
            scan = null;
        }
    }

    private void ShowStatus(string message)
    {
        status.Text = message;
        icon.Text = message;
    }

    private void EditPassword()
    {
        if (hasDialog) return;
        hasDialog = true;
        scan?.Cancel();
        try
        {
            using var dialog = new PasswordDialog(!vault.Exists || StartupRegistration.IsEnabled());
            if (dialog.ShowDialog() != DialogResult.OK) return;
            vault.Save(dialog.Password.Text);
            UpdateStartup(dialog.StartAtSignIn.Checked);
            retry++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            MessageBox.Show("Could not save your password on this PC.", "Talk Password Fill");
        }
        finally
        {
            hasDialog = false;
        }
    }

    private void DeletePassword()
    {
        if (hasDialog) return;
        hasDialog = true;
        scan?.Cancel();
        try
        {
            if (MessageBox.Show("Delete the password saved on this PC?", "Talk Password Fill",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            vault.Delete();
            UpdateStartup(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not delete your saved password.", "Talk Password Fill");
        }
        finally
        {
            hasDialog = false;
        }
    }

    private void UpdateStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            startup.Checked = enabled;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("Could not update Windows startup. Keep the app running to enable autofill.", "Talk Password Fill");
        }
    }

    protected override void ExitThreadCore()
    {
        Application.Idle -= OnFirstRun;
        timer.Stop();
        scan?.Cancel();
        icon.Visible = false;
        icon.Dispose();
        timer.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class PasswordDialog : Form
{
    internal TextBox Password { get; } = new() { UseSystemPasswordChar = true, MaxLength = 512, Dock = DockStyle.Fill, AccessibleName = "KakaoTalk password" };
    internal CheckBox StartAtSignIn { get; } = new() { Text = "Launch at Windows sign-in", AutoSize = true };

    internal PasswordDialog(bool launchAtSignIn)
    {
        Text = "Set KakaoTalk password";
        Font = SystemFonts.MessageBoxFont!;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(392, 156);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        StartAtSignIn.Checked = launchAtSignIn;

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            if (Password.TextLength == 0 || Password.Text.Contains('\0'))
            {
                Password.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        grid.Controls.Add(new Label { Text = "KakaoTalk password", AutoSize = true });
        grid.Controls.Add(Password);
        grid.Controls.Add(StartAtSignIn);
        grid.Controls.Add(buttons);
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

    internal static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(run?.GetValue(Entry) as string, Command(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string command = Command();
            if (command.Length > 260) throw new InvalidOperationException("Use a shorter install path.");
            run.SetValue(Entry, command);
        }
        else run.DeleteValue(Entry, throwOnMissingValue: false);
    }

    private static string Command() => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException()}\"";
}

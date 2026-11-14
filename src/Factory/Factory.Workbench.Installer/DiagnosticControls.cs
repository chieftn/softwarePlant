using System.Diagnostics;
using Microsoft.Win32;

namespace Factory.Workbench.Installer;

internal static class DiagnosticControls
{
    public static string? InstallerValue(string name) =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Factory\WorkbenchInstaller", name, null) as string;

    public static FlowLayoutPanel Create(Form owner, SetupDiagnostics diagnostics, string? currentMsiLog = null)
    {
        var panel = new FlowLayoutPanel { AutoSize = true };
        Add("Open setup logs", () => Open(diagnostics.DirectoryPath));
        Add("Open MSI log", () =>
        {
            var log = currentMsiLog ?? InstallerValue("LastMsiLogFile");
            if (log is null || !File.Exists(log))
                throw new IOException("No Windows Installer log is available for this operation.");
            Open(log);
        });
        Add("Save diagnostics...", () =>
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Diagnostic archive (*.zip)|*.zip", FileName = $"Factory-Workbench-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            diagnostics.Export(dialog.FileName, ConfigurationStore.DefaultRoot, currentMsiLog ?? InstallerValue("LastMsiLogFile"));
            MessageBox.Show(owner, "Diagnostics saved. The export is user-owned and will not be deleted by uninstall.",
                "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        return panel;

        void Add(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                    System.Text.Json.JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(owner, "Diagnostics action failed: " + error.Message,
                        "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            panel.Controls.Add(button);
        }
    }

    private static void Open(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })
            ?? throw new IOException("Windows could not open the diagnostic location.");
    }
}

internal sealed class RemovalDiagnosticsForm : Form
{
    public RemovalDiagnosticsForm(SetupDiagnostics diagnostics, string msiLog)
    {
        Text = "Factory Workbench - before removal";
        ClientSize = new Size(740, 235);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), AutoScroll = true
        };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(690, 0),
            Text = "Uninstall removes owned local software, configuration, certificate/private key and diagnostic journals/logs.\n\n" +
                "Save diagnostics now if you need them. Remote retirement is best effort; its outcome will be reported separately. " +
                "An exported archive remains in the location you choose."
        });
        layout.Controls.Add(DiagnosticControls.Create(this, diagnostics, msiLog));
        var proceed = new Button { Text = "Continue uninstall", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(proceed);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        var confirmed = false;
        proceed.Click += (_, _) => { confirmed = true; Close(); };
        cancel.Click += (_, _) => Close();
        FormClosed += (_, _) => Environment.ExitCode = confirmed ? 0 : 1602;
    }
}

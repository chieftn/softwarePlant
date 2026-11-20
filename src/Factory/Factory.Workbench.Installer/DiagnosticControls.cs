using System.Diagnostics;
using Microsoft.Win32;

namespace Factory.Workbench.Installer;

internal static class DiagnosticControls
{
    public static string? InstallerValue(string name) =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Factory\WorkbenchInstaller", name, null) as string;

    public static FlowLayoutPanel Create(Form owner, SetupDiagnostics diagnostics, string? currentMsiLog = null,
        Func<Task<MissionDiagnosticStatus?>>? collectMissionDiagnostics = null)
    {
        var panel = new FlowLayoutPanel { AutoSize = true };
        Add("Open setup logs", () => Open(diagnostics.DirectoryPath));
        Add("View mission telemetry...", () => ViewMissionTelemetry(owner));
        Add("Open mission logs", () =>
        {
            var directory = MissionTelemetry.DirectoryPath(ConfigurationStore.DefaultRoot);
            if (!Directory.Exists(directory))
                throw new IOException("No mission telemetry has been recorded by this installation yet.");
            Open(directory);
        });
        Add("Open MSI log", () =>
        {
            var log = currentMsiLog ?? InstallerValue("LastMsiLogFile");
            if (log is null || !File.Exists(log))
                throw new IOException("No Windows Installer log is available for this operation.");
            Open(log);
        });
        AddAsync("Save diagnostics...", async () =>
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Diagnostic archive (*.zip)|*.zip",
                FileName = $"Factory-Workbench-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            var mission = collectMissionDiagnostics is null ? null : await collectMissionDiagnostics();
            diagnostics.Export(dialog.FileName, ConfigurationStore.DefaultRoot, currentMsiLog ?? InstallerValue("LastMsiLogFile"), mission);
            MessageBox.Show(owner, mission?.State == "failed"
                    ? $"Diagnostics saved, but retained mission collection failed: {mission.Detail}"
                    : "Diagnostics saved. The export is user-owned and will not be deleted by uninstall.",
                "Factory Workbench", MessageBoxButtons.OK,
                mission?.State == "failed" ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        });
        return panel;

        void Add(string text, Action action)
            => AddAsync(text, () => { action(); return Task.CompletedTask; });

        void AddAsync(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await action(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                    System.Text.Json.JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
                    OperationCanceledException)
                {
                    MessageBox.Show(owner, "Diagnostics action failed: " + error.Message,
                        "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    if (!button.IsDisposed)
                        button.Enabled = true;
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

    private static void ViewMissionTelemetry(Form owner)
    {
        var window = new Form { Text = "Factory Workbench - mission telemetry", Width = 1100, Height = 650 };
        var trace = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 10)
        };
        window.Controls.Add(trace);
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => Refresh();
        window.FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
        Refresh();
        timer.Start();
        window.Show(owner);

        void Refresh()
        {
            try
            {
                var root = ConfigurationStore.DefaultRoot;
                if (!File.Exists(WorkbenchMissionDiagnostics.SnapshotPath(root)))
                {
                    trace.Text = "No retained mission exists.";
                    return;
                }
                var mission = WorkbenchMissionDiagnostics.ReadSnapshot(root).Mission;
                var entries = MissionTelemetry.ReadLocal(root, mission.Id, mission.Digest, live: true);
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"Mission {mission.Id}; model {mission.Model}. Refreshes every two seconds.");
                if (entries.Length == 0)
                    lines.AppendLine("No telemetry recorded yet. Existing discarded errors cannot be recovered; the updated worker records the next explicit retry.");
                foreach (var entry in entries.TakeLast(200))
                {
                    lines.AppendLine($"{entry.At:O} {entry.Source} {entry.Stage} {entry.Outcome} iteration={entry.Iteration} durationMs={entry.DurationMilliseconds}");
                    foreach (var error in entry.Errors)
                        lines.AppendLine($"  {error.Type} code={error.Code} http={error.HttpStatus} rpc={error.RpcCode} exit={error.ExitCode} correlation={error.CorrelationId}: {MissionTelemetry.Safe(error.Message)}");
                }
                var text = lines.ToString();
                if (trace.Text != text)
                {
                    trace.Text = text;
                    trace.SelectionStart = trace.TextLength;
                    trace.ScrollToCaret();
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                trace.Text = "Mission telemetry read failed: " + MissionTelemetry.Safe(error.Message);
            }
        }
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
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(20),
            AutoScroll = true
        };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(690, 0),
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

using System.Runtime.InteropServices;
using System.Security.Principal;
using Factory.Workbench.Setup;

namespace Factory.Workbench.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            MessageBox.Show("Factory workbench requires Windows x64.", "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Run Factory Workbench Setup as administrator.", "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        try
        {
            if (args.Length != 0 && args is not ["--removal-diagnostics", "--msi-log", _])
                throw new ArgumentException("Unsupported setup arguments.");
            var removal = args is ["--removal-diagnostics", "--msi-log", _];
            MachineCertificates.ProtectStateDirectory(ConfigurationStore.DefaultRoot, removal);
            var diagnostics = new SetupDiagnostics(ConfigurationStore.DefaultRoot);
            diagnostics.Record("interface", "started");
            Application.Run(removal ? new RemovalDiagnosticsForm(diagnostics, args[2]) : new SetupForm(diagnostics));
            return Environment.ExitCode;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Setup could not start: " + error.Message +
                "\nWindows Installer logs remain separate from workbench state.", "Factory Workbench",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

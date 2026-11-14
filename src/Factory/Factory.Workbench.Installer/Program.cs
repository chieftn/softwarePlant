using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Factory.Workbench.Installer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            MessageBox.Show("Factory workbench requires Windows x64.", "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Run Factory Workbench Setup as administrator.", "Factory Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new SetupForm());
    }
}

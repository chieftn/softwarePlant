using System.Runtime.InteropServices;
using System.Text.Json;

namespace Factory.Workbench.Setup;

public static class WorkbenchInspection
{
    public static Task<int> RunAsync(string root, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteLine("Inspection is read-only. WSL is not invoked.");
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            output.WriteLine("FAILED: Windows x64 is required.");
            return Task.FromResult(1);
        }
        if (!File.Exists(Path.Combine(root, "config.json")))
        {
            output.WriteLine("FAILED: Configuration missing. Run configure with this state root.");
            return Task.FromResult(1);
        }
        var configuration = ConfigurationStore.Read(root);
        output.WriteLine($"PASSED: Configuration valid; installation {configuration.InstallationId}.");
        if (File.Exists(Path.Combine(root, "installation.json")))
        {
            var installation = LocalState.ReadInstallation(root);
            if (configuration.InstallationId != installation.InstallationId)
                throw new InvalidDataException("Configuration and installed machine identities do not match.");
            output.WriteLine($"Installation: {installation.State}; selected release {installation.ActiveRelease}; service {installation.ServiceName}.");
            if (installation.State != "installed")
            {
                output.WriteLine("FAILED: Installation is not active. Run recover for interrupted maintenance.");
                return Task.FromResult(1);
            }
            PackageInstaller.VerifyInstalled(Path.GetDirectoryName(installation.Executable())!, installation.ActiveRelease);
            output.WriteLine("PASSED: Selected release file digests.");
        }
        else
        {
            output.WriteLine("FAILED: Runtime is not installed. Run install.");
            return Task.FromResult(1);
        }
        if (File.Exists(LocalState.RuntimeStatusPath(root)))
            output.WriteLine("Last recorded runtime observation (not proof of a live process): " +
                JsonSerializer.Serialize(LocalState.Read<WorkbenchRuntimeStatus>(LocalState.RuntimeStatusPath(root)), ConfigurationStore.Json));
        output.WriteLine("UNVERIFIED: Service-owned WSL, unattended credentials, and cold-boot readiness.");
        if (File.Exists(Path.Combine(root, "binding.json")))
        {
            var binding = BindingStore.Read(root);
            output.WriteLine($"PASSED: Retained enrollment binding for plant {binding.PlantId}, workbench {binding.WorkbenchId}.");
        }
        else
            output.WriteLine("INCOMPLETE: Machine enrollment. Use Factory Workbench Setup to sign in and enroll.");
        output.WriteLine("UNAVAILABLE: Automatic certificate rotation and worker execution.");
        return Task.FromResult(2);
    }
}

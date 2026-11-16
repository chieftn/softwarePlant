using System.Security.Cryptography;
using Factory.Contracts;
using Microsoft.Identity.Client;

namespace Factory.Workbench.Setup;

public sealed class WorkbenchFullRemoval(string root, IWorkbenchService service, Action<string> removeCertificates,
    SetupDiagnostics diagnostics)
{
    private static readonly HashSet<string> StateFiles = new(StringComparer.Ordinal)
    {
        "config.json", "installation.json", "enrollment.json", "binding.json", "unenrollment.json",
        "installer-transaction.json", "maintenance.lock", "removal-intent.json", "worker-setup-request.json"
    };
    private static readonly HashSet<string> RuntimeFiles = new(StringComparer.Ordinal)
    {
        "runtime.json", "runtime-failure.log", "runtime.lock", "plant-reports.lock",
        "pending-registration.json", "pending-presence.json", "pending-factory-presence.json",
        "line-configuration.json", "pending-configuration-observation.json", "worker-setup.json"
    };
    private string IntentPath => Path.Combine(root, "removal-intent.json");

    public async Task<RemovalIntent> PrepareAsync(string transaction,
        Func<CancellationToken, Task<EnrollmentView>>? retire, CancellationToken cancellationToken,
        bool requestRetirement = true)
    {
        RemovalIntent intent;
        using (LocalState.Lock(root, "maintenance.lock"))
        {
            intent = File.Exists(IntentPath) ? ReadIntent() : Begin();
            LocalState.Write(IntentPath, intent);
        }
        if (File.Exists(Path.Combine(WslWorkerEnvironment.EnvironmentDirectory(root), "environment.json")))
            await new WorkbenchWorkerSetup(root, service).RemoveAsync(cancellationToken);
        await new WorkbenchLifecycle(root, service).PrepareInstallerUninstallAsync(transaction, cancellationToken);
        using (LocalState.Lock(root, "maintenance.lock"))
        using (LocalState.LockRuntime(root))
            ValidateOwnedState();
        return requestRetirement ? await RequestRetirementAsync(retire, cancellationToken) : intent;
    }

    public async Task<RemovalIntent> RequestRetirementAsync(
        Func<CancellationToken, Task<EnrollmentView>>? retire, CancellationToken cancellationToken)
    {
        var intent = ReadIntent();
        if (intent.RemoteOutcome is "confirmed" or "pending" or "attention" or "not-enrolled")
            return intent;
        diagnostics.Record("remote-retirement", "started");
        try
        {
            if (retire is null)
                throw new InvalidDataException("No usable device binding is available for self-retirement.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var view = await retire(deadline.Token);
            if (view.SchemaVersion != "1" || view.Id != intent.EnrollmentId ||
                view.WorkbenchId != $"wb-{Guid.Parse(intent.InstallationId):N}" ||
                view.State is not ("revoking" or "retired" or "retirementNeedsAttention"))
                throw new InvalidDataException("Factory did not confirm durable retirement acceptance for this device.");
            intent = intent with
            {
                RemoteOutcome = view.State switch
                {
                    "retired" => "confirmed",
                    "revoking" => "pending",
                    _ => "attention"
                }
            };
            diagnostics.Record("remote-retirement", intent.RemoteOutcome);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or MsalException or
            CryptographicException or InvalidOperationException or OperationCanceledException)
        {
            intent = intent with { RemoteOutcome = "unconfirmed" };
            diagnostics.Record("remote-retirement", "unconfirmed", error);
        }
        using (LocalState.Lock(root, "maintenance.lock"))
            LocalState.Write(IntentPath, intent);
        return intent;
    }

    public void Purge()
    {
        if (!File.Exists(IntentPath))
            throw new InvalidDataException("Full local removal requires its durable intent.");
        using (LocalState.Lock(root, "maintenance.lock"))
        using (LocalState.LockRuntime(root))
        {
            var intent = ReadIntent();
            ValidateOwnedState();
            WorkbenchInstallation? installation = null;
            if (File.Exists(Path.Combine(root, "installation.json")))
            {
                installation = LocalState.ReadInstallation(root);
                if (installation.State != "uninstalled" || service.IsRunning(installation))
                    throw new InvalidDataException("The owned service/releases must be removed before purging credentials and state.");
                var marker = Path.Combine(installation.InstallDirectory, "installation.id");
                if (File.Exists(marker) && File.ReadAllText(marker) != intent.InstallationId)
                    throw new InvalidDataException("Binary ownership changed during removal.");
            }
            if (!intent.CertificatesRemoved)
            {
                removeCertificates(root);
                intent = intent with { CertificatesRemoved = true };
                LocalState.Write(IntentPath, intent);
            }
            if (installation is not null)
            {
                File.Delete(Path.Combine(installation.InstallDirectory, "installation.id"));
                DeleteIfEmpty(Path.Combine(installation.InstallDirectory, "releases"));
                DeleteIfEmpty(installation.InstallDirectory);
            }
            if (Directory.Exists(WslWorkerEnvironment.EnvironmentDirectory(root)))
                Directory.Delete(WslWorkerEnvironment.EnvironmentDirectory(root));
            var runtime = LocalState.RuntimeDirectory(root);
            foreach (var path in Directory.EnumerateFiles(runtime).Where(path => Path.GetFileName(path) != "runtime.lock"))
                File.Delete(path);
            foreach (var path in Directory.EnumerateFiles(root).Where(path =>
                Path.GetFileName(path) is not ("config.json" or "maintenance.lock" or "removal-intent.json")))
                File.Delete(path);
            if (Directory.Exists(diagnostics.DirectoryPath))
            {
                foreach (var path in Directory.EnumerateFiles(diagnostics.DirectoryPath))
                    File.Delete(path);
                Directory.Delete(diagnostics.DirectoryPath);
            }
            File.Delete(Path.Combine(root, "config.json"));
        }
        File.Delete(Path.Combine(LocalState.RuntimeDirectory(root), "runtime.lock"));
        Directory.Delete(LocalState.RuntimeDirectory(root));
        File.Delete(Path.Combine(root, "maintenance.lock"));
        File.Delete(IntentPath);
        Directory.Delete(root);
        diagnostics.Suspend();
    }

    public static async Task<EnrollmentView> RetireMachineAsync(string root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Device credentials require Windows.");
        var binding = BindingStore.Read(root);
        var configuration = ConfigurationStore.Read(root);
        using var certificate = MachineCertificates.Read(binding.CertificateThumbprint);
        using var http = PlantHttp.Create(configuration.PlantServiceUrl);
        var tokens = new CertificateTokenSource(configuration, binding, certificate);
        return await new PlantHttp(http, tokens.TokenAsync).SendAsync<EnrollmentView>(HttpMethod.Post,
            $"v1/enrollments/{binding.EnrollmentId}/retire-self", null, binding.EnrollmentId, cancellationToken);
    }

    private RemovalIntent Begin()
    {
        if (!File.Exists(Path.Combine(root, "config.json")))
            return new("1", Guid.NewGuid().ToString("D"), null, "not-enrolled", true);
        var configuration = ConfigurationStore.Read(root);
        var binding = File.Exists(Path.Combine(root, "binding.json")) ? BindingStore.Read(root) : null;
        var journal = File.Exists(Path.Combine(root, "enrollment.json")) ? BindingStore.ReadJournal(root) : null;
        if (binding is not null && journal is not null && binding.EnrollmentId != journal.OperationId)
            throw new InvalidDataException("Binding and enrollment operation disagree.");
        var legacy = BindingStore.Removal(root);
        return new("1", configuration.InstallationId, binding?.EnrollmentId ?? journal?.OperationId ?? legacy?.Enrollment.OperationId,
            legacy?.State is "retired" or "completed" ? "confirmed" :
            binding is null && journal is null && legacy is null ? "not-enrolled" : "unconfirmed");
    }

    private RemovalIntent ReadIntent()
    {
        var intent = LocalState.Read<RemovalIntent>(IntentPath);
        if (intent.SchemaVersion != "1" || !Guid.TryParseExact(intent.InstallationId, "D", out var id) || id == Guid.Empty ||
            intent.RemoteOutcome is not ("confirmed" or "pending" or "attention" or "not-enrolled" or "unconfirmed") ||
            File.Exists(Path.Combine(root, "config.json")) && ConfigurationStore.Read(root).InstallationId != intent.InstallationId)
            throw new InvalidDataException("Removal intent does not match this installation.");
        return intent;
    }

    private void ValidateOwnedState()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Full removal will not traverse linked state.");
            var name = Path.GetFileName(path);
            if (name == "logs" && Directory.Exists(path))
                SetupDiagnostics.ValidateDirectory(path);
            else if (name == "linux" && Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    throw new InvalidDataException("Remove the owned WSL environment through the service before purging its state.");
            }
            else if (name == "runtime" && Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any(file =>
                    (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !RuntimeFiles.Contains(Path.GetFileName(file))))
                    throw new InvalidDataException("Unrecognized runtime artifacts require ownership reconciliation.");
            }
            else if (Directory.Exists(path) || !StateFiles.Contains(name))
                throw new InvalidDataException("Unrecognized state artifacts require ownership reconciliation before removal.");
        }
        if (!File.Exists(Path.Combine(root, "installation.json")))
            return;
        var installation = LocalState.ReadInstallation(root);
        if (installation.InstallationId != ReadIntent().InstallationId)
            throw new InvalidDataException("Runtime and removal identities disagree.");
        foreach (var release in installation.OwnedReleases)
        {
            var directory = Path.Combine(installation.InstallDirectory, "releases", release);
            if (!Directory.Exists(directory))
                continue;
            var manifest = PackageInstaller.ReadManifest(directory);
            if (manifest.ReleaseId != release)
                throw new InvalidDataException("Release ownership metadata disagrees.");
            var expected = manifest.Files.Keys.Append("workbench-package.json")
                .Select(file => Path.GetFullPath(Path.Combine(directory, file))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ValidateRelease(directory, expected);
        }
    }

    private static void ValidateRelease(string directory, HashSet<string> expected)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Full removal will not traverse a linked release.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Full removal will not traverse release links.");
            if (Directory.Exists(path))
                ValidateRelease(path, expected);
            else if (!expected.Contains(Path.GetFullPath(path)))
                throw new InvalidDataException("A release contains files outside its owned manifest.");
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}

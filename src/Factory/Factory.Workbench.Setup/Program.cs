using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Factory.Contracts;
using Factory.Workbench;
using Factory.Workbench.Setup;

if (args.Length == 0 || args is ["--help"] or ["help"])
{
    Console.WriteLine("""
        Factory workbench setup and maintenance (Windows x64)
          configure | inspect | status [--root <state-directory>]
          install --package <directory> --allow-unsigned-package [--install-directory <directory>]
          run | stop | recover | rollback | uninstall
          update --package <directory> --allow-unsigned-package

        Every command accepts --root <state-directory>.
        Default state: %ProgramData%\Factory\Workbench
        Default binaries: %ProgramFiles%\Factory\Workbench
        Configure before installing. Service maintenance requires an elevated terminal.
        run starts the service; the installed runtime's run command is diagnostic only.
        uninstall attempts device self-retirement and removes all owned local artifacts.
        Export diagnostics in graphical setup before uninstall; use MSI removal for MSI-owned software.
        Unsigned packages remain local-development only. No WSL or worker execution.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
SetupDiagnostics? diagnostics = null;
var failureStage = "startup";
var helperValidation = args[0] == "msi-validate-helper";
try
{
    Console.CancelKeyPress += (_, signal) =>
    {
        if (args[0] == "configure")
            return;
        signal.Cancel = true;
        cancellation.Cancel();
    };
    if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        throw new PlatformNotSupportedException("Factory workbench setup requires Windows x64.");
    var command = args[0];
    if (command is not ("configure" or "inspect" or "status" or "install" or "run" or "stop" or
        "recover" or "update" or "rollback" or "uninstall" or "msi-apply" or "msi-prepare-uninstall" or "msi-rollback" or "msi-commit" or
        "msi-offer-diagnostics" or "msi-removal-summary" or "msi-validate-helper" or "msi-retire"))
        throw new InvalidDataException($"Unknown command: {command}. Use --help.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Length; index++)
    {
        var name = args[index];
        var allowed = name == "--root" ||
            command == "install" && name == "--install-directory" ||
            command is "install" or "update" && name is "--package" or "--allow-unsigned-package" ||
            command == "msi-apply" && name == "--package" ||
            command == "msi-apply" && name == "--ui-level" ||
            command is "msi-offer-diagnostics" or "msi-validate-helper" && name == "--ui" ||
            command is "msi-removal-summary" or "msi-offer-diagnostics" && name == "--log" ||
            command.StartsWith("msi-", StringComparison.Ordinal) && name == "--transaction";
        if (!allowed || options.ContainsKey(name))
            throw new InvalidDataException($"Unknown or repeated option: {name}.");
        if (name == "--allow-unsigned-package")
            options.Add(name, "true");
        else
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidDataException($"Missing value for {name}.");
            options.Add(name, args[index]);
        }
    }
    var root = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(options.GetValueOrDefault("--root", ConfigurationStore.DefaultRoot)));
    if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("A filesystem root cannot be used as the workbench state directory.");
    if (command == "msi-offer-diagnostics")
    {
        await InstallerInteraction.OfferDiagnosticsAsync(options["--ui"], options["--log"], cancellation.Token);
        return 0;
    }
    if (command == "msi-removal-summary")
    {
        InstallerInteraction.ShowRemovalOutcome(options["--transaction"], options["--log"]);
        return 0;
    }
    if (command == "configure")
    {
        WorkbenchConfigurator.Configure(root, Console.In, Console.Out);
        return 0;
    }
    if (command == "inspect")
        return await WorkbenchInspection.RunAsync(root, Console.Out, cancellation.Token);
    var controller = new WindowsWorkbenchService();
    if (command == "status")
    {
        var installation = LocalState.ReadInstallation(root);
        Console.WriteLine(JsonSerializer.Serialize(installation, ConfigurationStore.Json));
        Console.WriteLine($"Windows service running: {controller.IsRunning(installation)}");
        Console.WriteLine("Execution readiness: UNAVAILABLE. Enrolled machines connect using their certificate; WSL/worker execution remain disabled.");
        return 0;
    }
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("Service lifecycle changes require an elevated terminal.");
    if (command == "uninstall" && root.Equals(ConfigurationStore.DefaultRoot, StringComparison.OrdinalIgnoreCase) &&
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Factory\WorkbenchInstaller", "InstallFolder", null) is not null)
        throw new InvalidDataException("This workbench is MSI-managed. Use Installed Apps or msiexec /x so MSI-owned software is removed too.");
    failureStage = "state-protection";
    MachineCertificates.ProtectStateDirectory(root, command is "uninstall" or "msi-prepare-uninstall" or "msi-commit" or
        "msi-retire" or "msi-validate-helper");
    failureStage = "diagnostics-initialization";
    diagnostics = new SetupDiagnostics(root);
    diagnostics.Record(command, "started");
    if (command == "msi-validate-helper")
    {
        failureStage = "helper-validation";
        MachineCertificates.ValidateInstallerExecutable(options["--ui"], diagnostics);
        diagnostics.Record(command, "completed");
        return 0;
    }
    var lifecycle = new WorkbenchLifecycle(root, controller);
    var removal = new WorkbenchFullRemoval(root, controller, MachineCertificates.RemoveOwned, diagnostics);
    if (command.StartsWith("msi-", StringComparison.Ordinal))
    {
        if (!options.TryGetValue("--transaction", out var transaction))
            throw new InvalidDataException("MSI maintenance requires its transaction ID.");
        switch (command)
        {
            case "msi-apply":
                if (!options.TryGetValue("--package", out var payload))
                    throw new InvalidDataException("MSI update requires its installed payload directory.");
                WorkbenchProfile.Configure(root, LocalState.Read<WorkbenchInstallationProfile>(
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(payload))!, "installation-profile.json")));
                if (!File.Exists(Path.Combine(root, "binding.json")) &&
                    (!options.TryGetValue("--ui-level", out var uiLevel) || uiLevel != "5"))
                    throw new InvalidDataException("Initial enrollment requires the full interactive MSI; software-only silent installation is not supported.");
                await lifecycle.ApplyInstallerUpdateAsync(transaction, payload, cancellation.Token);
                break;
            case "msi-prepare-uninstall":
                await removal.PrepareAsync(transaction, null, cancellation.Token, requestRetirement: false);
                break;
            case "msi-retire":
                var outcome = await removal.RequestRetirementAsync(
                    token => WorkbenchFullRemoval.RetireMachineAsync(root, token), cancellation.Token);
                Console.WriteLine($"FACTORY_RETIREMENT {Guid.Parse(transaction):D} {outcome.RemoteOutcome}");
                break;
            case "msi-rollback":
                await lifecycle.RollbackInstallerAsync(transaction, cancellation.Token);
                break;
            case "msi-commit":
                await lifecycle.CommitInstallerAsync(transaction, cancellation.Token);
                if (File.Exists(Path.Combine(root, "removal-intent.json")))
                    removal.Purge();
                break;
        }
        diagnostics.Record(command, "completed");
        return 0;
    }
    switch (command)
    {
        case "install":
        case "update":
            if (!options.TryGetValue("--package", out var package))
                throw new InvalidDataException($"{command} requires --package <directory>.");
            var consent = options.ContainsKey("--allow-unsigned-package");
            if (!consent)
                throw new InvalidDataException("Publisher verification is not implemented. Trusted development packages require --allow-unsigned-package.");
            Console.WriteLine("WARNING: Unsigned development package; publisher authenticity is not verified.");
            if (command == "install")
                await lifecycle.InstallAsync(package, options.GetValueOrDefault("--install-directory",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Factory", "Workbench")),
                    consent, cancellation.Token);
            else
                await lifecycle.UpdateAsync(package, consent, cancellation.Token);
            break;
        case "run":
            await lifecycle.StartAsync(cancellation.Token);
            break;
        case "stop":
            await lifecycle.StopAsync(cancellation.Token);
            break;
        case "recover":
            await lifecycle.RecoverAsync(cancellation.Token);
            break;
        case "rollback":
            await lifecycle.RollbackAsync(cancellation.Token);
            break;
        case "uninstall":
            var transaction = Guid.NewGuid().ToString("D");
            var outcome = await removal.PrepareAsync(transaction,
                token => WorkbenchFullRemoval.RetireMachineAsync(root, token), cancellation.Token);
            Console.WriteLine($"FACTORY_RETIREMENT {transaction} {outcome.RemoteOutcome}");
            await lifecycle.CommitInstallerAsync(transaction, cancellation.Token);
            removal.Purge();
            break;
    }
    diagnostics.Record(command, "completed");
    Console.WriteLine(command == "uninstall" ? "Owned local artifacts removed. Remote retirement outcome is reported separately."
        : $"Completed {command}. Configuration, identity, and journals are retained.");
    Console.WriteLine("Use Factory.Workbench.Installer.exe for sign-in and enrollment. Execution remains disabled; WSL was not invoked.");
    return 0;
}
catch (OperationCanceledException)
{
    diagnostics?.TryRecordFailure("setup", "cancelled");
    Console.Error.WriteLine("Operation cancelled. Inspect status and use recover if maintenance was interrupted.");
    return 130;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
    JsonException or ContractValidationException or ArgumentException or InvalidOperationException or
    PlatformNotSupportedException or Win32Exception or AggregateException or System.Security.Cryptography.CryptographicException or
    System.Security.SecurityException)
{
    diagnostics?.TryRecordFailure("setup", "failed", error);
    Console.Error.WriteLine($"Workbench setup failed at {failureStage} ({error.GetType().Name}, {error.HResult}).");
    return helperValidation ? failureStage switch
    {
        "state-protection" => 41,
        "diagnostics-initialization" => 42,
        "helper-validation" => 43,
        _ => 40
    } : 1;
}

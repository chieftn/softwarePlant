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
        uninstall preserves configuration, installation identity, and journals.
        Unsigned packages remain local-development only. No WSL or worker execution.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) =>
{
    if (args[0] == "configure")
        return;
    signal.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        throw new PlatformNotSupportedException("Factory workbench setup requires Windows x64.");
    var command = args[0];
    if (command is not ("configure" or "inspect" or "status" or "install" or "run" or "stop" or
        "recover" or "update" or "rollback" or "uninstall" or "msi-apply" or "msi-prepare-uninstall" or "msi-rollback" or "msi-commit"))
        throw new InvalidDataException($"Unknown command: {command}. Use --help.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Length; index++)
    {
        var name = args[index];
        var allowed = name == "--root" ||
            command == "install" && name == "--install-directory" ||
            command is "install" or "update" && name is "--package" or "--allow-unsigned-package" ||
            command == "msi-apply" && name == "--package" ||
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
    MachineCertificates.ProtectStateDirectory(root);
    var lifecycle = new WorkbenchLifecycle(root, controller);
    if (command.StartsWith("msi-", StringComparison.Ordinal))
    {
        if (!options.TryGetValue("--transaction", out var transaction))
            throw new InvalidDataException("MSI maintenance requires its transaction ID.");
        switch (command)
        {
            case "msi-apply":
                if (!options.TryGetValue("--package", out var payload))
                    throw new InvalidDataException("MSI update requires its installed payload directory.");
                await lifecycle.ApplyInstallerUpdateAsync(transaction, payload, cancellation.Token);
                break;
            case "msi-prepare-uninstall":
                await lifecycle.PrepareInstallerUninstallAsync(transaction, cancellation.Token);
                break;
            case "msi-rollback":
                await lifecycle.RollbackInstallerAsync(transaction, cancellation.Token);
                break;
            case "msi-commit":
                await lifecycle.CommitInstallerAsync(transaction, cancellation.Token);
                break;
        }
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
            await lifecycle.UninstallAsync(cancellation.Token);
            break;
    }
    Console.WriteLine($"Completed {command}. Configuration, identity, and journals are retained.");
    Console.WriteLine("Use Factory.Workbench.Installer.exe for sign-in and enrollment. Execution remains disabled; WSL was not invoked.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled. Inspect status and use recover if maintenance was interrupted.");
    return 130;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
    JsonException or ContractValidationException or ArgumentException or InvalidOperationException or
    PlatformNotSupportedException or Win32Exception or AggregateException)
{
    Console.Error.WriteLine($"Workbench setup failed: {error.Message}");
    return 1;
}

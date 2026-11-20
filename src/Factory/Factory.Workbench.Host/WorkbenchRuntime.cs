using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;

namespace Factory.Workbench.Host;

public static class WorkbenchRuntime
{
    public static async Task RunAsync(string root, string release, bool diagnostic, CancellationToken cancellationToken, ILogger? logger = null)
    {
        using var ownership = LocalState.LockRuntime(root);
        var installation = LocalState.ReadInstallation(root);
        if (installation.State != "installed" || installation.ActiveRelease != release)
            throw new InvalidDataException("Release activation is incomplete or this is not the selected release. Run setup recover.");
        var configuration = ConfigurationStore.Read(root);
        if (configuration.InstallationId != installation.InstallationId)
            throw new InvalidDataException("Configuration and installed machine identities do not match.");
        var environment = new WslWorkerEnvironment(root, installation, new WslCommands());
        string? processedDiagnosticRequest = null;
        if (await ProcessMissionDiagnostics())
            return;
        var requestPath = WslWorkerEnvironment.RequestPath(root);
        string? processedWorkerRequest = null;
        if (File.Exists(requestPath))
        {
            var request = LocalState.Read<WorkerSetupRequest>(requestPath);
            if (request.ReleaseId == release)
            {
                var previous = File.Exists(WslWorkerEnvironment.StatusPath(root))
                    ? LocalState.Read<WorkerSetupStatus>(WslWorkerEnvironment.StatusPath(root)) : null;
                if (previous?.RequestId != request.Id || previous.State == "working")
                    await ProcessWorkerSetup(request);
                else if (previous.State == "ready")
                    await ProcessWorkerSetup(request with { Operation = "check" });
            }
            processedWorkerRequest = request.Id;
            if (request.Operation == "remove")
                return;
        }
        if (!diagnostic && !installation.RunRequested)
            return;
        BindingStore.RequireEnrollmentAllowed(root);
        var status = new WorkbenchRuntimeStatus("1", installation.InstallationId, release, Environment.ProcessId,
            DateTimeOffset.UtcNow, "notReady", "Machine enrollment is incomplete. WSL is not started.");
        X509Certificate2? certificate = null;
        HttpClient? http = null;
        WorkbenchPlantConnection? connection = null;
        WorkbenchDevelopmentExecution? development = null;
        Exception? shutdownCause = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindingStore.RequireEnrollmentAllowed(root);
                if (await ProcessMissionDiagnostics())
                    return;
                if (File.Exists(requestPath))
                {
                    var request = LocalState.Read<WorkerSetupRequest>(requestPath);
                    if (request.Id != processedWorkerRequest)
                    {
                        if (diagnostic)
                            throw new InvalidOperationException("Worker preparation requires the Windows service, not a diagnostic host.");
                        if (configuration.Development?.Enabled == true || configuration.ExecutionConsent is not null)
                            throw new InvalidOperationException("Disable mission execution before preparing the Linux environment.");
                        await ProcessWorkerSetup(request);
                        processedWorkerRequest = request.Id;
                    }
                }
                if (File.Exists(Path.Combine(root, "binding.json")))
                {
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException("Machine certificate authentication requires Windows.");
                    try
                    {
                        if (connection is null)
                        {
                            var binding = BindingStore.Read(root);
                            certificate?.Dispose();
                            certificate = MachineCertificates.Read(binding.CertificateThumbprint);
                            var tokens = new CertificateTokenSource(configuration, binding, certificate);
                            http ??= PlantHttp.Create(configuration.PlantServiceUrl);
                            var plant = new PlantHttp(http, tokens.TokenAsync, tokens.RequestFreshToken);
                            development = new(plant, binding, configuration, root);
                            await development.RecoverAsync(cancellationToken);
                            connection = new(plant, binding, release, root, development.ObserveAsync,
                                () => (development.State, development.Reason));
                        }
                        await connection.PublishAsync(cancellationToken);
                        await development!.TickAsync(cancellationToken);
                        status = status with
                        {
                            PlantConnected = true,
                            ConfigurationObservation = connection.ConfigurationObservation,
                            State = development.State,
                            Reason = development.Reason
                        };
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or MsalException or CryptographicException ||
                        error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        if (development is not null)
                            await development.StopAsync(CancellationToken.None, error);
                        status = status with
                        {
                            PlantConnected = false,
                            State = "notReady",
                            ConfigurationObservation = connection?.ConfigurationObservation,
                            Reason = $"Plant connection unavailable: {error.GetType().Name}. Owned execution stopped; unresolved journals are retained."
                        };
                        if (logger is not null)
                            logger.LogWarning(error, "Plant connection unavailable. Reports are retained; owned execution is stopped.");
                        else
                            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {status.Reason} {error.Message}");
                    }
                }
                LocalState.Write(LocalState.RuntimeStatusPath(root), status with { ObservedAt = DateTimeOffset.UtcNow });
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (development is not null)
                await development.StopAsync(CancellationToken.None);
            LocalState.Write(LocalState.RuntimeStatusPath(root), status with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                State = "stopped",
                PlantConnected = false,
                Reason = "Host stopped; any owned Linux execution was terminated and journals retained."
            });
        }
        catch (Exception error)
        {
            shutdownCause = error;
            throw;
        }
        finally
        {
            try
            {
                if (development is not null)
                    await development.StopAsync(CancellationToken.None, shutdownCause);
            }
            finally
            {
                certificate?.Dispose();
                http?.Dispose();
            }

        }

        async Task ProcessWorkerSetup(WorkerSetupRequest request)
        {
            RequireServiceIdentity("Worker setup");
            try { await environment.ProcessAsync(request, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning("Worker setup cancelled during service shutdown; its failed-stage record is retained.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException or AggregateException)
            {
                if (logger is not null)
                    logger.LogError(error, "Worker setup failed; see worker-setup.json for the failed stage.");
                else
                    Console.Error.WriteLine($"Worker setup failed: {error.Message}. See worker-setup.json.");
            }
        }

        async Task<bool> ProcessMissionDiagnostics()
        {
            var path = WorkbenchMissionDiagnostics.RequestPath(root);
            if (!File.Exists(path))
                return false;
            var request = LocalState.Read<MissionDiagnosticRequest>(path);
            if (request.Id == processedDiagnosticRequest)
                return false;
            var previous = File.Exists(WorkbenchMissionDiagnostics.StatusPath(root))
                ? LocalState.Read<MissionDiagnosticStatus>(WorkbenchMissionDiagnostics.StatusPath(root)) : null;
            processedDiagnosticRequest = request.Id;
            if (previous?.RequestId == request.Id && previous.State is "collected" or "failed")
                return false;
            RequireServiceIdentity("Retained-mission diagnostics");
            try
            {
                await new WorkbenchMissionDiagnostics(root, installation,
                    new WslCommands(MissionTelemetry.MaximumBytes)).ProcessAsync(request, cancellationToken);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
                System.Text.Json.JsonException or Factory.Contracts.ContractValidationException or ArgumentException or OperationCanceledException)
            {
                logger?.LogWarning("Retained-mission diagnostics failed ({ErrorType}); the diagnostic result is retained. No mission was replayed.",
                    error.GetType().Name);
            }
            return request.DiagnosticOnly;
        }

        void RequireServiceIdentity(string operation)
        {
            if (!OperatingSystem.IsWindows() || diagnostic)
                throw new InvalidOperationException($"{operation} requires the dedicated Windows service, not a diagnostic host.");
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var expectedIdentity = new System.Security.Principal.NTAccount(installation.ServiceAccount)
                .Translate(typeof(System.Security.Principal.SecurityIdentifier));
            if (identity.User is null || !identity.User.Equals(expectedIdentity))
                throw new InvalidOperationException($"{operation} must run under the dedicated workbench service identity.");
        }
    }
}

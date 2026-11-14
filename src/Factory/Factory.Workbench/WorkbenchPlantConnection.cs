using System.Security.Cryptography.X509Certificates;
using Factory.Contracts;
using Microsoft.Identity.Client;

namespace Factory.Workbench;

public sealed class CertificateTokenSource
{
    private readonly IConfidentialClientApplication application;
    private readonly string[] scopes;
    private bool forceRefresh;
    public CertificateTokenSource(WorkbenchConfiguration configuration, WorkbenchBinding binding, X509Certificate2 certificate)
    {
        BindingStore.Validate(configuration, binding);
        if (binding.CertificateThumbprint != certificate.Thumbprint)
            throw new InvalidDataException("Machine certificate does not match the admitted binding.");
        scopes = [$"api://{configuration.ApiClientId}/.default"];
        application = ConfidentialClientApplicationBuilder.Create(binding.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, configuration.TenantId)
            .WithCertificate(certificate).Build();
    }
    public void RequestFreshToken() => forceRefresh = true;
    public async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        var token = await application.AcquireTokenForClient(scopes).WithForceRefresh(forceRefresh).ExecuteAsync(cancellationToken);
        forceRefresh = false;
        return token.AccessToken;
    }
}

public sealed record PendingWorkbenchReport<T>(string SchemaVersion, string InstallationId, string? PlantId,
    string WorkbenchId, string OperationId, T Body, string? EnrollmentId = null, long AssignmentVersion = 0);

public sealed class WorkbenchPlantConnection(PlantHttp plant, WorkbenchBinding binding, string release, string root)
{
    private bool activated;
    private long? registeredVersion;
    public string? AssignedPlantId { get; private set; }
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var runtime = LocalState.RuntimeDirectory(root);
        using var reportLock = LocalState.Lock(runtime, "plant-reports.lock");
        var enrollmentPath = $"v1/enrollments/{binding.EnrollmentId}";
        if (!activated)
        {
            await plant.WriteAsync(HttpMethod.Post, enrollmentPath + "/activate",
                null, binding.EnrollmentId, cancellationToken);
            activated = true;
        }
        await Report("factory-presence", enrollmentPath + "/presence", new WorkbenchPresence
        {
            SchemaVersion = "1", Status = "notReady", Reason = "Enrolled in Factory; execution is unavailable."
        }, null, 0);
        var assignment = await plant.ReadAsync<WorkbenchAssignment>(enrollmentPath + "/connection", cancellationToken);
        if (assignment.SchemaVersion != "1" || assignment.EnrollmentId != binding.EnrollmentId ||
            assignment.WorkbenchId != binding.WorkbenchId || assignment.Version < 0 ||
            assignment.PlantId is { } assigned && (!Guid.TryParseExact(assigned, "D", out var plantId) ||
                plantId == Guid.Empty || assignment.Version == 0))
            throw new InvalidDataException("Factory returned an invalid assignment.");
        AssignedPlantId = assignment.PlantId;
        if (AssignedPlantId is null)
        {
            registeredVersion = null;
            return;
        }
        var basePath = $"v1/plants/{AssignedPlantId}/workbenches/{binding.WorkbenchId}";
        if (registeredVersion != assignment.Version)
        {
            await Report("registration", basePath + "/registration", new WorkbenchRegistration
            {
                SchemaVersion = "1", MachineName = Environment.MachineName, HostVersion = release,
                WorkerVersion = "unavailable", ProtocolVersion = "1", AssignmentSchemaVersions = ["1"],
                Capabilities = ["plant-connection"], AssignmentVersion = assignment.Version
            }, AssignedPlantId, assignment.Version);
            registeredVersion = assignment.Version;
        }
        await Report("presence", basePath + "/presence", new WorkbenchPresence
        {
            SchemaVersion = "1", Status = "notReady", AssignmentVersion = assignment.Version,
            Reason = "Plant connected. Worker execution and service-owned WSL are not installed."
        }, AssignedPlantId, assignment.Version);

        async Task Report<T>(string type, string endpoint, T body, string? currentPlant, long version)
        {
            var path = Path.Combine(runtime, $"pending-{type}.json");
            if (File.Exists(path))
            {
                var previous = LocalState.Read<PendingWorkbenchReport<T>>(path);
                if (previous.SchemaVersion != "1" || previous.InstallationId != binding.InstallationId ||
                    previous.WorkbenchId != binding.WorkbenchId || previous.EnrollmentId != binding.EnrollmentId ||
                    !Guid.TryParseExact(previous.OperationId, "D", out var id) || id == Guid.Empty)
                    throw new InvalidDataException("A pending plant report does not match this installation.");
                if (previous.AssignmentVersion > version || previous.AssignmentVersion == version && previous.PlantId != currentPlant)
                    throw new InvalidDataException("A pending report conflicts with the authoritative assignment version.");
                if (previous.AssignmentVersion == version)
                    await plant.WriteAsync(HttpMethod.Put, endpoint, previous.Body, previous.OperationId, cancellationToken);
                else
                    Console.Error.WriteLine($"Discarding obsolete {type} report {previous.OperationId} after an assignment change.");
                File.Delete(path);
            }
            var current = new PendingWorkbenchReport<T>("1", binding.InstallationId, currentPlant,
                binding.WorkbenchId, Guid.NewGuid().ToString("D"), body, binding.EnrollmentId, version);
            LocalState.Write(path, current);
            await plant.WriteAsync(HttpMethod.Put, endpoint, current.Body, current.OperationId, cancellationToken);
            File.Delete(path);
        }
    }
}

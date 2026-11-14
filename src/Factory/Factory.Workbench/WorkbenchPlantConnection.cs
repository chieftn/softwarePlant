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

public sealed record PendingWorkbenchReport<T>(string SchemaVersion, string InstallationId, string PlantId,
    string WorkbenchId, string OperationId, T Body);

public sealed class WorkbenchPlantConnection(PlantHttp plant, WorkbenchBinding binding, string release, string root)
{
    private bool registered;
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var runtime = LocalState.RuntimeDirectory(root);
        using var reportLock = LocalState.Lock(runtime, "plant-reports.lock");
        var basePath = $"v1/plants/{binding.PlantId}/workbenches/{binding.WorkbenchId}";
        if (!registered)
        {
            await plant.WriteAsync(HttpMethod.Post,
                $"v1/plants/{binding.PlantId}/enrollments/{binding.EnrollmentId}/activate",
                null, binding.EnrollmentId, cancellationToken);
            await Report("registration", new WorkbenchRegistration
            {
                SchemaVersion = "1", MachineName = Environment.MachineName, HostVersion = release,
                WorkerVersion = "unavailable", ProtocolVersion = "1", AssignmentSchemaVersions = ["1"],
                Capabilities = ["plant-connection"]
            });
            registered = true;
        }
        await Report("presence", new WorkbenchPresence
        {
            SchemaVersion = "1", Status = "notReady",
            Reason = "Plant connected. Worker execution and service-owned WSL are not installed."
        });

        async Task Report<T>(string type, T body)
        {
            var path = Path.Combine(runtime, $"pending-{type}.json");
            if (File.Exists(path))
            {
                var previous = LocalState.Read<PendingWorkbenchReport<T>>(path);
                if (previous.SchemaVersion != "1" || previous.InstallationId != binding.InstallationId ||
                    previous.PlantId != binding.PlantId || previous.WorkbenchId != binding.WorkbenchId ||
                    !Guid.TryParseExact(previous.OperationId, "D", out var id) || id == Guid.Empty)
                    throw new InvalidDataException("A pending plant report does not match this installation.");
                await plant.WriteAsync(HttpMethod.Put, $"{basePath}/{type}", previous.Body, previous.OperationId, cancellationToken);
                File.Delete(path);
            }
            var current = new PendingWorkbenchReport<T>("1", binding.InstallationId, binding.PlantId,
                binding.WorkbenchId, Guid.NewGuid().ToString("D"), body);
            LocalState.Write(path, current);
            await plant.WriteAsync(HttpMethod.Put, $"{basePath}/{type}", current.Body, current.OperationId, cancellationToken);
            File.Delete(path);
        }
    }
}

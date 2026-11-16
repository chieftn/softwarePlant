using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Factory.Contracts;

namespace Factory.Workbench;

public sealed record EnrollmentJournal(string SchemaVersion, string InstallationId, string? PlantId,
    string OperationId, string CertificateThumbprint, string MachineName, string? PublicCertificate = null);
public sealed record UnenrollmentJournal(string SchemaVersion, EnrollmentJournal Enrollment, string State);
public sealed record RemovalIntent(string SchemaVersion, string InstallationId, string? EnrollmentId,
    string RemoteOutcome, bool CertificatesRemoved = false);

public sealed class PlantConnectionException(HttpStatusCode status, string message, string? correlationId = null,
    string? code = null) : IOException(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? CorrelationId { get; } = correlationId;
    public string? Code { get; } = code;
}

public static class BindingStore
{
    public static EnrollmentJournal ReadJournal(string root)
    {
        var journal = LocalState.Read<EnrollmentJournal>(Path.Combine(root, "enrollment.json"));
        ValidateJournal(root, journal);
        return journal;
    }

    public static void ValidateJournal(string root, EnrollmentJournal journal)
    {
        var configuration = ConfigurationStore.Read(root);
        if (journal.SchemaVersion != "1" || journal.InstallationId != configuration.InstallationId ||
            journal.PlantId is not null ||
            !Guid.TryParseExact(journal.OperationId, "D", out var operation) || operation == Guid.Empty ||
            journal.CertificateThumbprint.Length != 40 || !journal.CertificateThumbprint.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Enrollment journal does not match this installation.");
    }

    public static UnenrollmentJournal? Removal(string root)
    {
        var path = Path.Combine(root, "unenrollment.json");
        if (!File.Exists(path))
            return null;
        var journal = LocalState.Read<UnenrollmentJournal>(path);
        ValidateJournal(root, journal.Enrollment);
        if (journal.SchemaVersion != "1" || journal.State is not ("requested" or "retired" or "completed"))
            throw new InvalidDataException("Invalid unenrollment journal. Reconcile it before continuing.");
        return journal;
    }

    public static void RequireEnrollmentAllowed(string root)
    {
        if (File.Exists(Path.Combine(root, "removal-intent.json")))
            throw new InvalidDataException("Full uninstall is incomplete. Resume MSI uninstall; identity recovery or upgrade cannot cancel removal.");
        if (Removal(root) is { State: not "completed" })
            throw new InvalidDataException("Unenrollment is incomplete. Resume Unenroll before starting or re-enrolling this workbench.");
    }

    public static WorkbenchBinding Read(string root)
    {
        var binding = LocalState.Read<WorkbenchBinding>(Path.Combine(root, "binding.json"));
        Validate(ConfigurationStore.Read(root), binding);
        return binding;
    }

    public static void Validate(WorkbenchConfiguration configuration, WorkbenchBinding binding)
    {
        if (binding.SchemaVersion != "1" || binding.InstallationId != configuration.InstallationId ||
            !binding.TenantId.Equals(configuration.TenantId, StringComparison.OrdinalIgnoreCase) ||
            binding.PlantId is not null ||
            new[] { binding.ClientId, binding.ServicePrincipalId, binding.EnrollmentId }
                .Any(value => !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) ||
            binding.WorkbenchId != $"wb-{Guid.Parse(configuration.InstallationId):N}" ||
            binding.CertificateThumbprint.Length != 40 || !binding.CertificateThumbprint.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The machine binding does not match this installation.");
    }

    public static EnrollmentJournal Journal(string root, X509Certificate2 certificate)
    {
        RequireEnrollmentAllowed(root);
        var configuration = ConfigurationStore.Read(root);
        if (!certificate.HasPrivateKey)
            throw new InvalidDataException("Enrollment requires a machine private key.");
        var path = Path.Combine(root, "enrollment.json");
        var journal = File.Exists(path) ? LocalState.Read<EnrollmentJournal>(path) :
            new("1", configuration.InstallationId, null, Guid.NewGuid().ToString("D"),
                certificate.Thumbprint, Environment.MachineName, Convert.ToBase64String(certificate.RawData));
        if (journal.SchemaVersion != "1" || journal.InstallationId != configuration.InstallationId ||
            journal.PlantId is not null || journal.CertificateThumbprint != certificate.Thumbprint ||
            !Guid.TryParseExact(journal.OperationId, "D", out var operation) || operation == Guid.Empty)
            throw new InvalidDataException("An enrollment journal already exists. Resume its original key; do not create another identity.");
        LocalState.Write(path, journal);
        return journal;
    }
}

public sealed class PlantHttp(HttpClient http, Func<CancellationToken, Task<string>> token, Action? authenticationRejected = null)
{
    public async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken) =>
        await SendAsync<T>(HttpMethod.Get, path, null, null, cancellationToken);

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? operation,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, operation, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            throw new InvalidDataException("Plant returned no content for an operation requiring a response.");
        return await response.Content.ReadFromJsonAsync<T>(ConfigurationStore.Json, cancellationToken)
            ?? throw new InvalidDataException("Plant returned an empty response.");
    }

    public async Task WriteAsync(HttpMethod method, string path, object? body, string operation, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, operation, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidDataException("Plant returned an unexpected mutation response.");
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, string? operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken));
        if (operation is not null)
            request.Headers.Add("Idempotency-Key", operation);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: ConfigurationStore.Json);
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    authenticationRejected?.Invoke();
                var correlation = "";
                string? correlationId = null;
                string? code = null;
                var detail = "";
                if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
                {
                    var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (problem.TryGetProperty("code", out var problemCode) && problemCode.ValueKind == JsonValueKind.String)
                        code = problemCode.GetString();
                    if (problem.TryGetProperty("correlationId", out var value))
                    {
                        correlationId = value.GetString();
                        correlation = $" Correlation: {correlationId}.";
                    }
                    if (problem.TryGetProperty("detail", out var explanation) && explanation.ValueKind == JsonValueKind.String)
                    {
                        var text = explanation.GetString()!;
                        detail = " " + text[..Math.Min(text.Length, 1024)];
                    }
                }
                throw new PlantConnectionException(response.StatusCode,
                    $"Plant request failed (HTTP {(int)response.StatusCode}).{detail}{correlation} Check plant authority, enrollment configuration, and service logs.",
                    correlationId, code);
            }
        }
        return response;
    }

    public static HttpClient Create(string origin) => new(new HttpClientHandler { AllowAutoRedirect = false })
    { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 128 * 1024 };
}

public sealed class EnrollmentClient(PlantHttp plant)
{
    public Task<Page<EnrollmentView>> InventoryAsync(CancellationToken cancellationToken) =>
        plant.ReadAsync<Page<EnrollmentView>>("v1/enrollments?pageSize=1", cancellationToken);

    public async Task<IReadOnlyList<PlantView>> PlantsAsync(CancellationToken cancellationToken)
    {
        var plants = new List<PlantView>();
        string? continuation = null;
        var pages = 0;
        do
        {
            if (++pages > 1000 || plants.Count >= 10000)
                throw new InvalidDataException("Plant inventory is too large for this installer. Narrow the account's plant access.");
            var page = await plant.ReadAsync<Page<PlantView>>("v1/plants?pageSize=100" +
                (continuation is null ? "" : $"&continuationToken={Uri.EscapeDataString(continuation)}"), cancellationToken);
            plants.AddRange(page.Items.Where(item => item.State == "active"));
            continuation = page.ContinuationToken;
        } while (continuation is not null);
        return plants;
    }

    public async Task<EnrollmentView> SubmitAsync(EnrollmentJournal journal, X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        if (journal.CertificateThumbprint != certificate.Thumbprint)
            throw new InvalidDataException("Enrollment key does not match its journal.");
        var draft = new EnrollmentDraft
        {
            SchemaVersion = "1",
            OperationId = journal.OperationId,
            InstallationId = journal.InstallationId,
            MachineName = journal.MachineName,
            PublicCertificate = Convert.ToBase64String(certificate.RawData)
        };
        draft.Validate();
        var challenge = await plant.SendAsync<EnrollmentChallenge>(HttpMethod.Post,
            "v1/enrollments/challenge", new EnrollmentChallengeRequest(draft), null, cancellationToken);
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new InvalidDataException("Machine signing key is not available.");
        var proof = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge.Challenge),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return await plant.SendAsync<EnrollmentView>(HttpMethod.Post, "v1/enrollments",
            new EnrollmentSubmission(draft, challenge.Challenge, proof), journal.OperationId, cancellationToken);
    }

    public Task<EnrollmentView> StatusAsync(EnrollmentJournal journal, CancellationToken cancellationToken) =>
        plant.ReadAsync<EnrollmentView>($"v1/enrollments/{journal.OperationId}", cancellationToken);

    public Task<EnrollmentView> RevokeAsync(EnrollmentJournal journal, CancellationToken cancellationToken) =>
        plant.SendAsync<EnrollmentView>(HttpMethod.Post,
            $"v1/enrollments/{journal.OperationId}/revoke",
            journal.PublicCertificate is null ? null : new EnrollmentDraft
            {
                SchemaVersion = "1",
                OperationId = journal.OperationId,
                InstallationId = journal.InstallationId,
                MachineName = journal.MachineName,
                PublicCertificate = journal.PublicCertificate
            }, journal.OperationId, cancellationToken);

    public static WorkbenchBinding SaveBinding(string root, EnrollmentJournal journal, EnrollmentView view)
    {
        var configuration = ConfigurationStore.Read(root);
        if (view.SchemaVersion != "1" || view.Id != journal.OperationId ||
            view.State is not ("ready" or "active") || view.ClientId is null || view.ServicePrincipalId is null)
            throw new InvalidDataException("Enrollment is not ready or returned a different operation.");
        var binding = new WorkbenchBinding("1", journal.InstallationId, null, view.WorkbenchId,
            configuration.TenantId, view.ClientId, view.ServicePrincipalId, journal.CertificateThumbprint, journal.OperationId);
        BindingStore.Validate(configuration, binding);
        var path = Path.Combine(root, "binding.json");
        if (File.Exists(path) && BindingStore.Read(root) != binding)
            throw new InvalidDataException("Enrollment cannot replace an existing machine binding.");
        LocalState.Write(path, binding);
        return binding;
    }
}

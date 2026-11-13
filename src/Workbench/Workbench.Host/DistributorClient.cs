using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using Workbench.Contracts;

namespace Workbench.Host;

public sealed class DistributorClient(HttpClient http, Uri? invocationEndpoint = null, TokenCredential? credential = null) : IDisposable
{
    public static DistributorClient Create(string endpoint, string token)
    {
        var uri = ValidateEndpoint(endpoint);
        if (!uri.IsLoopback || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Local-token mode requires a loopback URL without query parameters. Use --foundry remotely.");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A local credential is required.");
        var client = Http();
        client.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(client);
    }

    public static DistributorClient CreateFoundry(string endpoint, string session, TokenCredential? credential = null)
    {
        var uri = ValidateEndpoint(endpoint);
        if (uri.Scheme != "https" || !uri.AbsolutePath.EndsWith("/endpoint/protocols/invocations", StringComparison.Ordinal))
            throw new ArgumentException("Use the deployed Foundry Invocations endpoint, not a project or agent-version URL.");
        if (string.IsNullOrWhiteSpace(session) || session.Length > 128 || session.Any(char.IsControl))
            throw new ArgumentException("Supply a stable Foundry session ID with --foundry-session.");
        if (!string.IsNullOrEmpty(uri.Query) && uri.Query != "?api-version=v1")
            throw new ArgumentException("Only api-version=v1 may be present in the Foundry endpoint URL.");
        var target = new UriBuilder(uri) { Query = "api-version=v1&agent_session_id=" + Uri.EscapeDataString(session) };
        return new(Http(), target.Uri, credential ?? new DefaultAzureCredential());
    }

    public async Task<AssignmentState> SubmitAsync(DispatchRequest dispatch, CancellationToken cancellationToken)
    {
        dispatch.Validate();
        return (await InvokeAsync(new() { Operation = "submit", Dispatch = dispatch }, cancellationToken)).Assignment
            ?? throw new InvalidDataException("Coordinator returned an empty submission receipt.");
    }

    public async Task<WorkbenchState> RegisterAsync(string id, WorkbenchRegistration registration, CancellationToken cancellationToken) =>
        (await InvokeAsync(new() { Operation = "register", WorkbenchId = id, Registration = registration }, cancellationToken)).Workbench
            ?? throw new InvalidDataException("Coordinator returned an empty registration.");

    public async Task<WorkClaim?> ClaimAsync(string id, string claimId, CancellationToken cancellationToken) =>
        (await InvokeAsync(new() { Operation = "claim", WorkbenchId = id, ClaimId = claimId }, cancellationToken)).Claim;

    public async Task<AssignmentState> GetAsync(string id, CancellationToken cancellationToken) =>
        (await InvokeAsync(new() { Operation = "get", AssignmentId = id }, cancellationToken)).Assignment
            ?? throw new InvalidDataException("Coordinator returned an empty assignment state.");

    public async Task HeartbeatAsync(WorkClaim claim, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(ForClaim(claim, "heartbeat"), cancellationToken);

    public async Task CompleteAsync(WorkClaim claim, WorkerResult result, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(ForClaim(claim, "result") with { Result = result }, cancellationToken);

    public async Task<CoordinatorReply> InvokeAsync(CoordinatorInvocation invocation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            invocationEndpoint ?? new Uri(http.BaseAddress ?? throw new InvalidOperationException("Missing coordinator URL."), "invocations"))
        {
            Content = JsonContent.Create(invocation, options: Wire.Options)
        };
        if (credential is not null)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var response = await http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"Coordinator unavailable: HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"Coordinator rejected the request: HTTP {(int)response.StatusCode}. Inspect identity, registration and assignment state.");
        return await response.Content.ReadFromJsonAsync<CoordinatorReply>(Wire.Options, cancellationToken)
            ?? throw new InvalidDataException("Coordinator returned an empty invocation reply.");
    }

    private static CoordinatorInvocation ForClaim(WorkClaim claim, string operation) => new()
    {
        Operation = operation,
        WorkbenchId = claim.Dispatch.WorkbenchId,
        AssignmentId = claim.Dispatch.Request.Assignment.Id,
        ClaimId = claim.ClaimId
    };

    private static Uri ValidateEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Use HTTPS; HTTP is allowed only on loopback.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Coordinator URLs cannot contain credentials or fragments.");
        return uri;
    }

    private static HttpClient Http() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public void Dispose() => http.Dispose();
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Workbench.Contracts;

namespace Workbench.Host;

public sealed class DistributorClient(HttpClient http) : IDisposable
{
    public async Task<AssignmentState> SubmitAsync(DispatchRequest dispatch, CancellationToken cancellationToken)
    {
        dispatch.Validate();
        using var response = await http.PostAsJsonAsync("assignments", dispatch, Wire.Options, cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<AssignmentState>(Wire.Options, cancellationToken)
            ?? throw new InvalidDataException("Distributor returned an empty submission receipt.");
    }

    public static DistributorClient Create(string endpoint, string token)
    {
        var uri = new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Use HTTPS for the distributor; HTTP is allowed only on loopback.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Distributor URLs cannot contain credentials, query strings or fragments.");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A workbench token is required.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(client);
    }

    public async Task<WorkClaim?> ClaimAsync(string id, string claimId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"workbenches/{id}/claim",
            new ClaimRequest(claimId), Wire.Options, cancellationToken);
        EnsureSuccess(response);
        return response.StatusCode == HttpStatusCode.NoContent ? null
            : await response.Content.ReadFromJsonAsync<WorkClaim>(Wire.Options, cancellationToken)
                ?? throw new InvalidDataException("Distributor returned an empty claim.");
    }

    public async Task<AssignmentState> GetAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"assignments/{id}", cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<AssignmentState>(Wire.Options, cancellationToken)
            ?? throw new InvalidDataException("Distributor returned an empty assignment state.");
    }

    public async Task HeartbeatAsync(WorkClaim claim, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(Path(claim, "heartbeat"), null, cancellationToken);
        EnsureSuccess(response);
    }

    public async Task CompleteAsync(WorkClaim claim, WorkerResult result, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(Path(claim, "result"), result, Wire.Options, cancellationToken);
        EnsureSuccess(response);
    }

    private static string Path(WorkClaim claim, string action) =>
        $"workbenches/{claim.Dispatch.WorkbenchId}/assignments/{claim.Dispatch.Request.Assignment.Id}/{claim.ClaimId}/{action}";

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"Distributor unavailable: HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"Distributor rejected the request: HTTP {(int)response.StatusCode}. Inspect authentication and assignment state.");
    }

    public void Dispose() => http.Dispose();
}

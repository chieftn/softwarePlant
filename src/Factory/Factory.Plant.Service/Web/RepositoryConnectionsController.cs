using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.UserPolicy)]
public sealed class RepositoryConnectionsController(ManagedGitHubConnections connections, ApiSettings settings) : Controller
{
    private ApiCaller Caller() => WebsiteAuthentication.Caller(User, settings);

    [HttpGet("/repository-connections")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await connections.ListAsync(Caller(), cancellationToken));

    [HttpPost("/repository-connections")]
    public async Task<IActionResult> Begin([FromForm] string operationKey, [FromForm] string name,
        [FromForm] string? organization, [FromForm] bool allowPublication, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            throw ApiProblems.Invalid("Supply a connection name and valid setup form.");
        return View("Manifest", await connections.BeginAsync(Caller(), operationKey, name, organization, allowPublication, cancellationToken));
    }

    [HttpGet("/repository-connections/manifest")]
    public async Task<IActionResult> Manifest([FromQuery] string state, [FromQuery] string code, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrEmpty(state) || state.Length > 4096 ||
            string.IsNullOrEmpty(code) || code.Length > 256)
            throw ApiProblems.Invalid("GitHub callback is incomplete.");
        var reference = await connections.ManifestAsync(Caller(), state, code, cancellationToken);
        return Redirect("/repository-connections/" + reference);
    }

    [HttpGet("/repository-connections/{reference}")]
    public async Task<IActionResult> Detail(string reference, CancellationToken cancellationToken) =>
        View(await connections.GetAsync(reference, Caller(), cancellationToken));

    [HttpPost("/repository-connections/{reference}/verify")]
    public async Task<IActionResult> Verify(string reference, CancellationToken cancellationToken)
    {
        await connections.VerifyAsync(reference, Caller(), cancellationToken);
        return Redirect("/repository-connections/" + reference);
    }

    [HttpPost("/repository-connections/{reference}/recover")]
    public async Task<IActionResult> Recover(string reference, CancellationToken cancellationToken)
    {
        await connections.RecoverAsync(reference, Caller(), cancellationToken);
        return Redirect("/repository-connections/" + reference);
    }

    [HttpPost("/repository-connections/{reference}/disable")]
    public async Task<IActionResult> Disable(string reference, CancellationToken cancellationToken)
    {
        await connections.DisableAsync(reference, Caller(), cancellationToken);
        return Redirect("/repository-connections/" + reference);
    }
}

using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.UserPolicy)]
public sealed class CopilotAccessController(ManagedCopilotAccess access, ApiSettings settings) : Controller
{
    [HttpGet("/copilot-access")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await access.GetAsync(Caller().Principal, cancellationToken));

    [HttpPost("/copilot-access/setup")]
    public async Task<IActionResult> Setup([FromForm] string operationKey, [FromForm] bool restart, CancellationToken cancellationToken) =>
        View("Manifest", await access.BeginSetupAsync(Caller(), operationKey, restart, cancellationToken));

    [HttpGet("/copilot-access/manifest")]
    public async Task<IActionResult> Manifest(string state, string code, CancellationToken cancellationToken)
    {
        await access.CompleteSetupAsync(Caller(), state, code, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/copilot-access/authorize")]
    public async Task<IActionResult> AuthorizeSubscription(CancellationToken cancellationToken) =>
        Redirect(await access.BeginAuthorizationAsync(Caller(), cancellationToken));

    [HttpGet("/copilot-access/callback")]
    public async Task<IActionResult> Callback(string state, string? code, string? error, CancellationToken cancellationToken)
    {
        if (error is not null || string.IsNullOrEmpty(code))
            throw ApiProblems.Invalid("GitHub authorization was not completed. Return to Model access to try again.");
        await access.CompleteAuthorizationAsync(Caller(), state, code, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/copilot-access/recover")]
    public async Task<IActionResult> Recover(CancellationToken cancellationToken)
    {
        await access.RecoverAsync(Caller(), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/copilot-access/disable")]
    public async Task<IActionResult> Disable(CancellationToken cancellationToken)
    {
        await access.DisableAsync(Caller(), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    private ApiCaller Caller() => WebsiteAuthentication.Caller(User, settings);
}

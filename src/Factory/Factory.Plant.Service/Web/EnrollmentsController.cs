using Factory.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.EnrollmentAdministratorPolicy)]
public sealed class EnrollmentsController(EnrollmentOperations operations, PlantOperations plants, ApiSettings settings) : Controller
{
    [HttpGet("/enrollments")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var caller = WebsiteAuthentication.Caller(User, settings);
        var (size, token) = PlantEndpoints.PageQuery(HttpContext);
        var choices = new List<PlantView>();
        string? after = null;
        do
        {
            if (choices.Count >= 10000)
                throw ApiProblems.Invalid("Plant inventory is too large for assignment selection.");
            var page = await plants.ListPlantsAsync(caller, 100, after, cancellationToken);
            choices.AddRange(page.Items);
            after = page.ContinuationToken;
        } while (after is not null);
        return View(new EnrollmentsPage(await operations.ListAsync(caller, size, token, cancellationToken), choices));
    }

    [HttpPost("/enrollments/{operationId}/assignment")]
    public async Task<IActionResult> Assign(string operationId, [FromForm] string? plantId,
        [FromForm] long expectedVersion, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            throw ApiProblems.Invalid("Assignment version is invalid. Refresh before assigning.");
        await operations.AssignAsync(PlantEndpoints.PlantId(operationId), WebsiteAuthentication.Caller(User, settings),
            new("1", expectedVersion, string.IsNullOrEmpty(plantId) ? null : PlantEndpoints.PlantId(plantId)), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/enrollments/{operationId}/revoke")]
    public async Task<IActionResult> Revoke(string operationId, [FromForm] bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm)
            throw ApiProblems.Invalid("Confirm retirement before revoking this enrollment.");
        await operations.RevokeAsync(PlantEndpoints.PlantId(operationId), WebsiteAuthentication.Caller(User, settings), cancellationToken);
        return RedirectToAction(nameof(Index));
    }
}

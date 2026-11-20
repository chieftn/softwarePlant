using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.UserPolicy)]
public sealed class WorkbenchesController(PlantOperations operations, ApiSettings settings) : Controller
{
    [HttpGet("/plants/{plantId}/workbenches")]
    public async Task<IActionResult> Index(string plantId, CancellationToken cancellationToken)
    {
        var id = PlantEndpoints.PlantId(plantId);
        var (size, token) = PlantEndpoints.PageQuery(HttpContext);
        return View(new WorkbenchesPage(id, await operations.ListWorkbenchesAsync(id,
            WebsiteAuthentication.Caller(User, settings), size, token, cancellationToken)));
    }

    [HttpGet("/plants/{plantId}/workbenches/{workbenchId}")]
    public async Task<IActionResult> Detail(string plantId, string workbenchId, CancellationToken cancellationToken)
    {
        var plant = PlantEndpoints.PlantId(plantId);
        var workbench = PlantEndpoints.WorkbenchId(workbenchId);
        var caller = WebsiteAuthentication.Caller(User, settings);
        return View(new WorkbenchDetailsPage(
            await operations.GetWorkbenchAsync(plant, workbench, caller, cancellationToken),
            await operations.GetWorkbenchConfigurationAsync(plant, workbench, caller, cancellationToken)));
    }
}

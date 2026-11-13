using Azure.Identity;
using Factory.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.UserPolicy)]
public sealed class PlantsController(PlantOperations operations, ApiSettings settings, ILogger<PlantsController> logger) : Controller
{
    [HttpGet("/plants")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var (size, token) = PlantEndpoints.PageQuery(HttpContext);
        var page = await operations.ListPlantsAsync(WebsiteAuthentication.Caller(User, settings), size, token, cancellationToken);
        return View(new PlantsPage(page));
    }

    [Authorize(Policy = WebsiteAuthentication.CreatorPolicy)]
    [HttpGet("/plants/create")]
    public IActionResult Create() => View(new CreatePlantForm { OperationKey = Guid.NewGuid().ToString("D") });

    [Authorize(Policy = WebsiteAuthentication.CreatorPolicy)]
    [HttpPost("/plants")]
    public async Task<IActionResult> CreatePlant([FromForm] CreatePlantForm form, CancellationToken cancellationToken)
    {
        try
        {
            if (!Guid.TryParseExact(form.OperationKey, "D", out var operationKey))
                throw ApiProblems.Invalid("The creation operation key is invalid. Reload the creation page.");
            var result = await operations.CreatePlantAsync(WebsiteAuthentication.Caller(User, settings),
                operationKey.ToString("D"), new PlantWriteRequest { SchemaVersion = "1", Name = form.Name ?? "" }, cancellationToken);
            return RedirectToAction(nameof(Detail), new { plantId = result.Plant.Id });
        }
        catch (Exception exception) when (exception is ApiException or ContractValidationException or CosmosException or
            AuthenticationFailedException or HttpRequestException or TimeoutException)
        {
            var problem = ApiProblems.FromException(exception);
            if (problem.Status >= 500)
                logger.LogError(exception, "Plant form failed with {Code}; correlation {CorrelationId}", problem.Code, HttpContext.TraceIdentifier);
            else
                logger.LogInformation("Plant form rejected with {Code}; correlation {CorrelationId}", problem.Code, HttpContext.TraceIdentifier);
            ModelState.AddModelError("", problem.Message);
            Response.StatusCode = problem.Status;
            if (problem.RetryAfter is not null)
                Response.Headers.RetryAfter = problem.RetryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return View("Create", form);
        }
    }

    [HttpGet("/plants/{plantId}")]
    public async Task<IActionResult> Detail(string plantId, CancellationToken cancellationToken) =>
        View((await operations.GetPlantAsync(PlantEndpoints.PlantId(plantId),
            WebsiteAuthentication.Caller(User, settings), cancellationToken)).Plant);
}

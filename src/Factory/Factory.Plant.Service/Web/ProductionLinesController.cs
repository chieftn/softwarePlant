using Azure.Identity;
using Factory.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace Factory.Plant.Service.Web;

[Authorize(Policy = WebsiteAuthentication.UserPolicy)]
public sealed class ProductionLinesController(PlantOperations operations, ApiSettings settings,
    ILogger<ProductionLinesController> logger) : Controller
{
    [HttpGet("/production-lines")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var (size, token) = PlantEndpoints.PageQuery(HttpContext);
        return View(new ProductionLinesPage(await operations.ListProductionLinesAsync(Caller(), size, token, cancellationToken)));
    }

    [HttpGet("/production-lines/create")]
    public IActionResult Create() => View("Edit", new ProductionLineForm { OperationKey = Guid.NewGuid().ToString("D") });

    [HttpGet("/production-lines/{lineId}")]
    public async Task<IActionResult> Detail(string lineId, CancellationToken cancellationToken) =>
        View("Edit", ProductionLineForm.From(await operations.GetProductionLineAsync(PlantEndpoints.PlantId(lineId),
            Caller(), cancellationToken)));

    [HttpPost("/production-lines")]
    public Task<IActionResult> CreateLine([FromForm] ProductionLineForm form, CancellationToken cancellationToken) =>
        SaveAsync(null, form, cancellationToken);

    [HttpPost("/production-lines/{lineId}")]
    public Task<IActionResult> UpdateLine(string lineId, [FromForm] ProductionLineForm form, CancellationToken cancellationToken) =>
        SaveAsync(PlantEndpoints.PlantId(lineId), form, cancellationToken);

    private async Task<IActionResult> SaveAsync(string? id, ProductionLineForm form, CancellationToken cancellationToken)
    {
        form.LineId = id;
        try
        {
            if (!ModelState.IsValid || !Guid.TryParseExact(form.OperationKey, "D", out var operation))
                throw ApiProblems.Invalid("The form or operation key is invalid. Reload before submitting.");
            var line = id is null
                ? await operations.CreateProductionLineAsync(Caller(), operation.ToString("D"), form.Definition(), cancellationToken)
                : await operations.UpdateProductionLineAsync(id, Caller(), operation.ToString("D"),
                    form.Revision ?? throw ApiProblems.Invalid("A revision is required when updating a line. Reload before submitting."),
                    form.Definition(), cancellationToken);
            return RedirectToAction(nameof(Detail), new { lineId = line.Id });
        }
        catch (Exception exception) when (FormFailure(exception))
        {
            Report(exception);
            return View("Edit", form);
        }
    }

    [HttpGet("/plants/{plantId}/production-line")]
    public async Task<IActionResult> Assignment(string plantId, CancellationToken cancellationToken)
    {
        var plant = PlantEndpoints.PlantId(plantId);
        var binding = await operations.GetProductionLineBindingAsync(plant, Caller(), cancellationToken);
        return View(new ProductionLineAssignmentForm
        {
            PlantId = plant,
            ExpectedVersion = binding.Version,
            ProductionLineId = binding.ProductionLineId
        });
    }

    [HttpPost("/plants/{plantId}/production-line")]
    public async Task<IActionResult> Assign(string plantId,
        [FromForm] ProductionLineAssignmentForm form, CancellationToken cancellationToken)
    {
        form.PlantId = PlantEndpoints.PlantId(plantId);
        try
        {
            if (!ModelState.IsValid)
                throw ApiProblems.Invalid("The assignment form is invalid.");
            await operations.AssignProductionLineAsync(form.PlantId, Caller(),
                new("1", form.ExpectedVersion, string.IsNullOrWhiteSpace(form.ProductionLineId) ? null : form.ProductionLineId.Trim()),
                cancellationToken);
            return RedirectToAction(nameof(Assignment), new { plantId = form.PlantId });
        }
        catch (Exception exception) when (FormFailure(exception))
        {
            Report(exception);
            return View("Assignment", form);
        }
    }

    private ApiCaller Caller() => WebsiteAuthentication.Caller(User, settings);
    private static bool FormFailure(Exception exception) => exception is ApiException or ContractValidationException or
        CosmosException or AuthenticationFailedException or HttpRequestException or TimeoutException;

    private void Report(Exception exception)
    {
        var problem = ApiProblems.FromException(exception);
        if (problem.Status >= 500)
            logger.LogError(exception, "Production-line form failed with {Code}; correlation {CorrelationId}",
                problem.Code, HttpContext.TraceIdentifier);
        else
            logger.LogInformation("Production-line form rejected with {Code}; correlation {CorrelationId}",
                problem.Code, HttpContext.TraceIdentifier);
        ModelState.AddModelError("", problem.Message);
        Response.StatusCode = problem.Status;
        if (problem.RetryAfter is { } retry)
            Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

using System.Net;
using System.Text.Json;
using Azure.Identity;
using Factory.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Azure.Cosmos;

namespace Factory.Plant.Service;

public sealed class ApiException(int status, string code, string detail, int? retryAfter = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfter { get; } = retryAfter;
}

public static class ApiProblems
{
    public static ApiException Invalid(string detail) => new(400, "invalid_request", detail);
    public static ApiException Forbidden() => new(403, "forbidden", "Current authority does not permit this operation.");
    public static ApiException Unavailable() => new(503, "service_unavailable", "The operation is temporarily unavailable.", 5);
    public static ApiException Conflict() => new(409, "idempotency_conflict", "This operation key was used with different input.");

    public static Task WriteAsync(HttpContext context, int status, string code, string detail, int? retryAfter = null)
    {
        if (retryAfter is not null)
            context.Response.Headers.RetryAfter = retryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: status, title: Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status),
            type: "about:blank", detail: detail, instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier,
                ["retryAfterSeconds"] = retryAfter
            }.Where(item => item.Value is not null).ToDictionary()).ExecuteAsync(context);
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return true;

        var problem = exception switch
        {
            ApiException known => known,
            ContractValidationException validation => ApiProblems.Invalid(validation.Message),
            JsonException => ApiProblems.Invalid("The JSON body is invalid, incomplete or contains unsupported fields."),
            BadHttpRequestException bad => new ApiException(bad.StatusCode, "invalid_request", "The HTTP request is invalid."),
            CosmosException cosmos when cosmos.StatusCode == HttpStatusCode.TooManyRequests =>
                new ApiException(429, "rate_limited", "Persistence is throttled.",
                    Math.Max(1, (int)Math.Ceiling(cosmos.RetryAfter?.TotalSeconds ?? 1))),
            CosmosException cosmos when (int)cosmos.StatusCode >= 500 ||
                cosmos.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden or HttpStatusCode.NotFound => ApiProblems.Unavailable(),
            AuthenticationFailedException or HttpRequestException or TimeoutException => ApiProblems.Unavailable(),
            _ => new ApiException(500, "internal_error", "An unexpected server failure occurred.")
        };
        if (problem.Status >= 500)
            logger.LogError(exception, "API failure {Code}; correlation {CorrelationId}", problem.Code, context.TraceIdentifier);
        else
            logger.LogInformation("API rejected request with {Code}; correlation {CorrelationId}", problem.Code, context.TraceIdentifier);
        await ApiProblems.WriteAsync(context, problem.Status, problem.Code, problem.Message, problem.RetryAfter);
        return true;
    }
}

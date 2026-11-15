using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Factory.Plant.Service.Web;

public sealed record WebsiteError(int Status, string Detail, string CorrelationId);

public static class WebsiteErrors
{
    public static bool IsPage(PathString path) => path == "/" || path.StartsWithSegments("/plants") ||
        path.StartsWithSegments("/production-lines") ||
        path.StartsWithSegments("/account") || path == "/signin-oidc" || path == "/signout-callback-oidc";

    public static Task WriteAsync(HttpContext context, int status, string detail, int? retryAfter = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (retryAfter is not null)
            context.Response.Headers.RetryAfter = retryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var view = new ViewResult
        {
            ViewName = "~/Views/Shared/Error.cshtml",
            StatusCode = status,
            ViewData = new ViewDataDictionary<WebsiteError>(
                context.RequestServices.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
            {
                Model = new(status, detail, context.TraceIdentifier)
            }
        };
        return view.ExecuteResultAsync(new ActionContext(context, new(), new ActionDescriptor()));
    }
}

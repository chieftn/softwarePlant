using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

public sealed class AccountController : Controller
{
    [AllowAnonymous]
    [HttpGet("/account/signin")]
    public IActionResult SignIn(string? returnUrl = null)
    {
        if (returnUrl is not null && !Url.IsLocalUrl(returnUrl))
            throw ApiProblems.Invalid("The return URL must be local to Factory.");
        return Challenge(new AuthenticationProperties { RedirectUri = returnUrl ?? "/plants" }, WebsiteAuthentication.OidcScheme);
    }

    [Authorize(Policy = WebsiteAuthentication.UserPolicy)]
    [HttpPost("/account/signout")]
    public IActionResult SignOutUser() => SignOut(new AuthenticationProperties { RedirectUri = "/" },
        WebsiteAuthentication.CookieScheme, WebsiteAuthentication.OidcScheme);

    [AllowAnonymous]
    [HttpGet("/account/access-denied")]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = 403;
        return View("~/Views/Shared/Error.cshtml", new WebsiteError(403,
            "Your account does not have the required Factory role.", HttpContext.TraceIdentifier));
    }

    [AllowAnonymous]
    [HttpGet("/account/signin-error")]
    public IActionResult SignInError()
    {
        Response.StatusCode = 401;
        return View("~/Views/Shared/Error.cshtml", new WebsiteError(401,
            "Sign-in could not be completed. Use an account in the configured tenant with the Factory.User role, or contact the service administrator.",
            HttpContext.TraceIdentifier));
    }
}

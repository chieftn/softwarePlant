using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Factory.Plant.Service.Web;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View();
}

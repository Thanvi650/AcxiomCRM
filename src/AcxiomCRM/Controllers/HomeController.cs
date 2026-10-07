using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

public class HomeController : Controller
{
    private readonly DashboardService _dashboard;

    public HomeController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>Role-scoped dashboard: users land here after signing in.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(DashboardFilter filter)
    {
        if (filter.Range == "custom" && filter.From is not null && filter.To is not null && filter.From > filter.To)
        {
            ModelState.AddModelError(nameof(filter.To), "The end date must be on or after the start date.");
            filter.Range = "all";
        }
        return View(await _dashboard.GetAsync(filter));
    }

    // No [HttpGet]: error pages are re-executed with the original request's method.
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public IActionResult Error() => View();

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [ActionName("StatusCode")]
    public IActionResult HttpStatusPage(int code)
    {
        ViewBag.Code = code;
        return View("StatusCode");
    }
}

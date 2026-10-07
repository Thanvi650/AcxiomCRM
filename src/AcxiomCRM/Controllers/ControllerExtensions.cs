using AcxiomCRM.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AcxiomCRM.Controllers;

public static class ControllerExtensions
{
    public const string SuccessKey = "Success";
    public const string ErrorKey = "Error";

    /// <summary>Copies service-layer (business) errors into ModelState so they render next to fields.</summary>
    public static void AddServiceErrors<T>(this ModelStateDictionary modelState, ServiceResult<T> result, string prefix = "")
    {
        foreach (var error in result.Errors)
        {
            var key = string.IsNullOrEmpty(error.Field) ? string.Empty : prefix + error.Field;
            modelState.AddModelError(key, error.Message);
        }
    }

    public static void Success(this Controller controller, string message) => controller.TempData[SuccessKey] = message;

    public static void Error(this Controller controller, string message) => controller.TempData[ErrorKey] = message;

    public static string FirstError<T>(this ServiceResult<T> result) =>
        result.Errors.FirstOrDefault()?.Message ?? "The operation could not be completed.";

    /// <summary>Safe redirect: only local URLs are honoured (prevents open redirects).</summary>
    public static IActionResult RedirectToLocal(this Controller controller, string? returnUrl, string fallbackAction, string fallbackController) =>
        !string.IsNullOrEmpty(returnUrl) && controller.Url.IsLocalUrl(returnUrl)
            ? controller.LocalRedirect(returnUrl)
            : controller.RedirectToAction(fallbackAction, fallbackController);
}

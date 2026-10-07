using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcxiomCRM.Controllers;

public class AccountController : Controller
{
    private readonly AuthService _auth;
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IAuditService _audit;

    public AccountController(AuthService auth, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditService audit)
    {
        _auth = auth;
        _users = users;
        _signIn = signIn;
        _audit = audit;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _auth.LoginAsync(model.Login, model.Password, model.RememberMe);
        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                return this.RedirectToLocal(model.ReturnUrl, "Index", "Home");
            case LoginOutcome.LockedOut:
                var minutes = result.LockoutEnd is null
                    ? 15
                    : Math.Max(1, (int)Math.Ceiling((result.LockoutEnd.Value - DateTimeOffset.UtcNow).TotalMinutes));
                ModelState.AddModelError(string.Empty,
                    $"This account is locked after repeated failed sign-in attempts. Try again in about {minutes} minute(s) or contact an administrator.");
                break;
            case LoginOutcome.Inactive:
                ModelState.AddModelError(string.Empty, "This account is inactive. Contact your administrator.");
                break;
            default:
                ModelState.AddModelError(string.Empty, "Invalid email/username or password.");
                break;
        }
        model.Password = string.Empty;
        return View(model);
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new RegisterViewModel());
    }

    /// <summary>Self-registration creates a Sales Executive (least privilege). Admins grant other roles.</summary>
    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var email = model.Email.Trim().ToLowerInvariant();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = model.FullName.Trim(),
            IsActive = true,
            CreatedDate = DateTime.Now
        };

        var result = await _users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                var key = error.Code.Contains("Password") ? nameof(model.Password)
                    : error.Code.Contains("Email") || error.Code.Contains("UserName") ? nameof(model.Email) : string.Empty;
                var message = error.Code is "DuplicateUserName" or "DuplicateEmail"
                    ? "An account with this email already exists."
                    : error.Description;
                ModelState.AddModelError(key, message);
            }
            return View(model);
        }

        await _users.AddToRoleAsync(user, Roles.SalesExecutive);
        await _audit.LogAsync(AuditActions.Register, "Account", user.Id, null,
            new { user.Email, user.FullName, Role = Roles.SalesExecutive }, userId: user.Id, userName: user.UserName);

        await _signIn.SignInAsync(user, isPersistent: false);
        await _audit.LogAsync(AuditActions.Login, "Account", user.Id, userId: user.Id, userName: user.UserName);
        this.Success($"Welcome, {user.FullName}! Your account has been created.");
        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _auth.LogoutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToAction(nameof(Login));

        var result = await _users.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Code == "PasswordMismatch" ? nameof(model.CurrentPassword) : nameof(model.NewPassword),
                    error.Code == "PasswordMismatch" ? "Current password is incorrect." : error.Description);
            }
            await _audit.LogAsync(AuditActions.PasswordChange, "Account", user.Id, result: "Failure");
            return View(new ChangePasswordViewModel());
        }

        await _signIn.RefreshSignInAsync(user);
        await _audit.LogAsync(AuditActions.PasswordChange, "Account", user.Id);
        this.Success("Your password has been changed.");
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult AccessDenied() => View();
}

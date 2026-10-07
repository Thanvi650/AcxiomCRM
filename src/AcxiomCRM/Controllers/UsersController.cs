using AcxiomCRM.Data;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>User administration. Managers get a read-only view of their team; only Admins can change anything.</summary>
[Authorize(Roles = Roles.AdminOrManager)]
public class UsersController : Controller
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public UsersController(UserManager<ApplicationUser> users, ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _users = users;
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter)
    {
        var query = _db.Users.AsNoTracking().Include(u => u.Manager).AsQueryable();
        if (_scope.IsManager)
        {
            var me = _scope.UserId;
            query = query.Where(u => u.ManagerId == me || u.Id == me);
        }
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var term = filter.Q.Trim().ToLower();
            query = query.Where(u => u.FullName.ToLower().Contains(term) || u.Email!.ToLower().Contains(term));
        }
        if (filter.Status == "active") query = query.Where(u => u.IsActive);
        if (filter.Status == "inactive") query = query.Where(u => !u.IsActive);

        var users = await query.OrderBy(u => u.FullName).ToListAsync();
        var roles = await (from ur in _db.UserRoles
                           join r in _db.Roles on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Name }).ToListAsync();
        var now = DateTimeOffset.UtcNow;

        var rows = users.Select(u => new UserListItem
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email ?? string.Empty,
                Role = roles.FirstOrDefault(r => r.UserId == u.Id)?.Name ?? string.Empty,
                ManagerName = u.Manager?.FullName,
                IsActive = u.IsActive,
                IsLockedOut = u.LockoutEnd > now,
                LockoutEnd = u.LockoutEnd,
                AccessFailedCount = u.AccessFailedCount,
                CreatedDate = u.CreatedDate
            })
            .Where(r => string.IsNullOrEmpty(filter.Type) || r.Role == filter.Type)
            .Where(r => filter.Status != "locked" || r.IsLockedOut);

        return View(new UserListViewModel
        {
            Filter = filter,
            Items = PagedList<UserListItem>.Create(rows, filter.Page, 15),
            CanManage = _scope.IsAdmin
        });
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet]
    public async Task<IActionResult> Create() => View("Form", await PrepareAsync(new UserFormViewModel()));

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserFormViewModel model)
    {
        if (string.IsNullOrEmpty(model.Password))
            ModelState.AddModelError(nameof(model.Password), "An initial password is required.");
        await ValidateManagerAsync(model);
        if (!ModelState.IsValid) return View("Form", await PrepareAsync(model));

        var email = model.Email.Trim().ToLowerInvariant();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = model.FullName.Trim(),
            IsActive = model.IsActive,
            ManagerId = model.Role == Roles.SalesExecutive ? model.ManagerId : null,
            CreatedDate = DateTime.Now
        };
        var result = await _users.CreateAsync(user, model.Password!);
        if (!result.Succeeded)
        {
            AddIdentityErrors(result);
            return View("Form", await PrepareAsync(model));
        }
        await _users.AddToRoleAsync(user, model.Role);
        await _audit.LogAsync(AuditActions.Create, "User", user.Id, null,
            new { user.Email, user.FullName, Role = model.Role, user.IsActive, user.ManagerId });

        this.Success($"User {user.FullName} created as {Roles.Display(model.Role)}.");
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();

        return View("Form", await PrepareAsync(new UserFormViewModel
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            Role = (await _users.GetRolesAsync(user)).FirstOrDefault() ?? Roles.SalesExecutive,
            ManagerId = user.ManagerId,
            IsActive = user.IsActive
        }));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, UserFormViewModel model)
    {
        model.Id = id;
        ModelState.Remove(nameof(model.Password));
        ModelState.Remove(nameof(model.ConfirmPassword));
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();

        var oldRole = (await _users.GetRolesAsync(user)).FirstOrDefault();
        if (user.Id == _scope.UserId && (model.Role != Roles.Admin || !model.IsActive))
        {
            ModelState.AddModelError(string.Empty, "You cannot remove your own Admin role or deactivate your own account.");
        }
        await ValidateManagerAsync(model);
        if (!ModelState.IsValid) return View("Form", await PrepareAsync(model));

        var before = new { user.Email, user.FullName, Role = oldRole, user.IsActive, user.ManagerId };
        var wasActive = user.IsActive;
        var email = model.Email.Trim().ToLowerInvariant();

        user.FullName = model.FullName.Trim();
        user.Email = email;
        user.UserName = email;
        user.IsActive = model.IsActive;
        user.ManagerId = model.Role == Roles.SalesExecutive ? model.ManagerId : null;
        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            AddIdentityErrors(result);
            return View("Form", await PrepareAsync(model));
        }

        if (oldRole != model.Role)
        {
            if (oldRole is not null) await _users.RemoveFromRoleAsync(user, oldRole);
            await _users.AddToRoleAsync(user, model.Role);
            await _audit.LogAsync(AuditActions.RoleChange, "User", user.Id, new { Role = oldRole }, new { Role = model.Role });
        }
        if (wasActive != user.IsActive)
        {
            await _audit.LogAsync(user.IsActive ? AuditActions.Activate : AuditActions.Deactivate, "User", user.Id);
        }
        // Invalidate existing sessions so role/status changes apply immediately.
        await _users.UpdateSecurityStampAsync(user);
        await _audit.LogAsync(AuditActions.Update, "User", user.Id, before,
            new { user.Email, user.FullName, Role = model.Role, user.IsActive, user.ManagerId });

        this.Success("User updated.");
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == _scope.UserId)
        {
            this.Error("You cannot deactivate your own account.");
            return RedirectToAction(nameof(Index));
        }

        user.IsActive = !user.IsActive;
        await _users.UpdateAsync(user);
        await _users.UpdateSecurityStampAsync(user);
        await _audit.LogAsync(user.IsActive ? AuditActions.Activate : AuditActions.Deactivate, "User", user.Id,
            new { IsActive = !user.IsActive }, new { user.IsActive });

        this.Success($"{user.FullName} is now {(user.IsActive ? "active" : "inactive")}.");
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();

        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        await _audit.LogAsync(AuditActions.Unlock, "User", user.Id);

        this.Success($"{user.FullName} has been unlocked.");
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet]
    public async Task<IActionResult> ResetPassword(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();
        return View(new ResetPasswordViewModel { Id = id, Email = user.Email });
    }

    /// <summary>Admin password reset via Identity's token flow. The password itself is never logged.</summary>
    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        var user = await _users.FindByIdAsync(model.Id);
        if (user is null) return NotFound();
        model.Email = user.Email;
        if (!ModelState.IsValid) return View(model);

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(nameof(model.NewPassword), error.Description);
            return View(new ResetPasswordViewModel { Id = model.Id, Email = user.Email });
        }
        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        await _audit.LogAsync(AuditActions.PasswordReset, "User", user.Id);

        this.Success($"Password reset for {user.FullName}. Share it with them securely.");
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateManagerAsync(UserFormViewModel model)
    {
        if (model.Role != Roles.SalesExecutive || string.IsNullOrEmpty(model.ManagerId)) return;

        var manager = await _users.FindByIdAsync(model.ManagerId);
        if (manager is null || !await _users.IsInRoleAsync(manager, Roles.Manager) || manager.Id == model.Id)
        {
            ModelState.AddModelError(nameof(model.ManagerId), "Select a valid manager.");
        }
    }

    private void AddIdentityErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            var key = error.Code.Contains("Password") ? nameof(UserFormViewModel.Password)
                : error.Code.Contains("Email") || error.Code.Contains("UserName") ? nameof(UserFormViewModel.Email) : string.Empty;
            ModelState.AddModelError(key, error.Code is "DuplicateUserName" or "DuplicateEmail"
                ? "A user with this email already exists." : error.Description);
        }
    }

    private async Task<UserFormViewModel> PrepareAsync(UserFormViewModel model)
    {
        var managers = await _users.GetUsersInRoleAsync(Roles.Manager);
        model.Managers = managers.Where(m => m.IsActive).OrderBy(m => m.FullName)
            .Select(m => new SelectListItem(m.FullName, m.Id, m.Id == model.ManagerId)).ToList();
        model.RoleOptions = Roles.All.Select(r => new SelectListItem(Roles.Display(r), r, r == model.Role)).ToList();
        return model;
    }
}

[Authorize(Roles = Roles.Admin)]
public class RolesController : Controller
{
    private readonly ApplicationDbContext _db;

    public RolesController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var counts = await (from r in _db.Roles
                            join ur in _db.UserRoles on r.Id equals ur.RoleId into members
                            select new { r.Name, Count = members.Count() }).ToListAsync();

        string Scope(string role) => role switch
        {
            Roles.Admin => "Full administration: users, roles, audit logs, configuration, all CRM records and reports.",
            Roles.Manager => "Team customers, leads, opportunities, follow-ups and management reports. No security administration.",
            _ => "Own/assigned customers, leads, opportunities, follow-ups and activities; own reports."
        };

        return View(new RolesViewModel
        {
            Roles = Roles.All.Select(r => new RoleSummary
            {
                Name = r,
                UserCount = counts.FirstOrDefault(c => c.Name == r)?.Count ?? 0,
                Scope = Scope(r)
            }).ToList(),
            Matrix = new()
            {
                ("Dashboard", "Full", "Team", "Own/Assigned"),
                ("Customers", "Full", "Team", "Own/Assigned"),
                ("Leads", "Full", "Team", "Own/Assigned"),
                ("Follow-Ups", "Full", "Team", "Own/Assigned"),
                ("Opportunities", "Full", "Team", "Own/Assigned"),
                ("Activities", "Full", "Team", "Own/Assigned"),
                ("User Management", "Full", "View team (read-only)", "No"),
                ("Role Management", "Full", "No", "No"),
                ("Audit Log", "Full", "Limited: team business events", "No"),
                ("REST API", "Authorized endpoints", "Authorized endpoints", "Authorized endpoints (own data)"),
                ("Reports", "All", "Management/team reports", "Own/assigned reports")
            }
        });
    }
}

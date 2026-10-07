using AcxiomCRM.Data;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Read-only audit trail. There are deliberately no edit/delete actions; the DbContext also
/// rejects any modification of audit rows. Managers see only their team's business events.
/// </summary>
[Authorize(Roles = Roles.AdminOrManager)]
public class AuditLogsController : Controller
{
    private static readonly string[] SecurityEntities = { "Account", "User", "System", "Security", "AuditLog" };

    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public AuditLogsController(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(AuditListViewModel model, string? export)
    {
        var query = await FilteredAsync(model);

        if (export == "csv")
        {
            if (!_scope.IsAdmin) return Forbid();
            var rows = await query.OrderByDescending(a => a.AuditLogId).Take(10_000).ToListAsync();
            await _audit.LogAsync(AuditActions.Export, "AuditLog", null,
                newValue: new { Rows = rows.Count, Query = Request.QueryString.Value });
            return File(CsvExport.Build(
                    new[] { "Id", "Date", "User", "Action", "Entity", "Record", "Result", "IP", "Old Value", "New Value" },
                    rows.Select(a => new object?[] { a.AuditLogId, a.CreatedDate, a.UserName, a.Action, a.EntityName, a.RecordId, a.Result, a.IpAddress, a.OldValue, a.NewValue })),
                "text/csv", $"audit-log-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }

        IQueryable<AuditLog> sorted = model.Sort switch
        {
            "date" => query.OrderBy(a => a.AuditLogId),
            "user" => query.OrderBy(a => a.UserName).ThenByDescending(a => a.AuditLogId),
            "user_desc" => query.OrderByDescending(a => a.UserName).ThenByDescending(a => a.AuditLogId),
            "action" => query.OrderBy(a => a.Action).ThenByDescending(a => a.AuditLogId),
            "action_desc" => query.OrderByDescending(a => a.Action).ThenByDescending(a => a.AuditLogId),
            "entity" => query.OrderBy(a => a.EntityName).ThenByDescending(a => a.AuditLogId),
            "entity_desc" => query.OrderByDescending(a => a.EntityName).ThenByDescending(a => a.AuditLogId),
            _ => query.OrderByDescending(a => a.AuditLogId)
        };
        model.Items = await PagedList<AuditLog>.CreateAsync(sorted, model.Page, 20);
        model.IsLimitedView = !_scope.IsAdmin;

        var baseQuery = await ScopedAsync();
        model.Entities = await baseQuery.Select(a => a.EntityName).Distinct().OrderBy(e => e).ToListAsync();
        model.Actions = await baseQuery.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
        var ids = await _scope.VisibleUserIdsAsync();
        var users = _db.Users.AsNoTracking().AsQueryable();
        if (ids is not null)
        {
            var list = ids.ToList();
            users = users.Where(u => list.Contains(u.Id));
        }
        model.Users = await users.OrderBy(u => u.FullName)
            .Select(u => new SelectListItem(u.FullName, u.Id, u.Id == model.UserId)).ToListAsync();
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        var entry = await (await ScopedAsync()).FirstOrDefaultAsync(a => a.AuditLogId == id);
        return entry is null ? NotFound() : View(entry);
    }

    private async Task<IQueryable<AuditLog>> ScopedAsync()
    {
        var query = _db.AuditLogs.AsNoTracking();
        if (_scope.IsAdmin) return query;

        var ids = (await _scope.VisibleUserIdsAsync())!.ToList();
        return query.Where(a => a.UserId != null && ids.Contains(a.UserId) && !SecurityEntities.Contains(a.EntityName));
    }

    private async Task<IQueryable<AuditLog>> FilteredAsync(AuditListViewModel model)
    {
        var query = await ScopedAsync();
        if (!string.IsNullOrEmpty(model.UserId)) query = query.Where(a => a.UserId == model.UserId);
        if (!string.IsNullOrEmpty(model.Entity)) query = query.Where(a => a.EntityName == model.Entity);
        if (!string.IsNullOrEmpty(model.EventAction)) query = query.Where(a => a.Action == model.EventAction);
        if (model.From is not null) query = query.Where(a => a.CreatedDate >= model.From.Value.Date);
        if (model.To is not null)
        {
            var end = model.To.Value.Date.AddDays(1);
            query = query.Where(a => a.CreatedDate < end);
        }
        return query;
    }
}

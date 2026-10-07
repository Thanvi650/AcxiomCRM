using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.ViewComponents;

/// <summary>Header bell: the signed-in user's overdue and due-today follow-ups.</summary>
public class NotificationsViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;

    public NotificationsViewComponent(ApplicationDbContext db, IUserScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var userId = _scope.UserId;
        var items = await _db.FollowUps.AsNoTracking()
            .Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity)
            .Where(f => f.AssignedToId == userId && f.Status == FollowUpStatus.Planned && f.FollowUpDate < tomorrow)
            .OrderBy(f => f.FollowUpDate)
            .Take(6)
            .ToListAsync();
        return View(items);
    }
}

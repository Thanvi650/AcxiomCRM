using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

/// <summary>Drop-down options, restricted to records the current user is allowed to use.</summary>
public class LookupService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;

    public LookupService(ApplicationDbContext db, IUserScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<List<SelectListItem>> UsersAsync(string? selected = null)
    {
        var users = await _scope.AssignableUsersAsync();
        return users.Select(u => new SelectListItem(u.FullName, u.Id, u.Id == selected)).ToList();
    }

    public async Task<List<SelectListItem>> CustomersAsync(int? selected = null)
    {
        var query = await _scope.ApplyAsync(_db.Customers.AsNoTracking());
        return await query.OrderBy(c => c.CustomerName)
            .Select(c => new SelectListItem(
                c.CustomerName + (c.CompanyName != null ? " (" + c.CompanyName + ")" : ""),
                c.CustomerId.ToString(),
                c.CustomerId == selected))
            .ToListAsync();
    }

    public async Task<List<SelectListItem>> LeadsAsync(int? selected = null, bool openOnly = false)
    {
        var query = await _scope.ApplyAsync(_db.Leads.AsNoTracking());
        if (openOnly)
        {
            query = query.Where(l => l.LeadId == selected ||
                                     (l.Status != LeadStatus.Converted && l.Status != LeadStatus.Lost && l.Status != LeadStatus.Unqualified));
        }
        return await query.OrderBy(l => l.LeadName)
            .Select(l => new SelectListItem(
                l.LeadName + (l.CompanyName != null ? " (" + l.CompanyName + ")" : ""),
                l.LeadId.ToString(),
                l.LeadId == selected))
            .ToListAsync();
    }

    public async Task<List<SelectListItem>> OpportunitiesAsync(int? selected = null)
    {
        var query = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        return await query.Where(o => o.Status == OpportunityStatus.Open || o.OpportunityId == selected)
            .OrderBy(o => o.OpportunityName)
            .Select(o => new SelectListItem(o.OpportunityName, o.OpportunityId.ToString(), o.OpportunityId == selected))
            .ToListAsync();
    }
}

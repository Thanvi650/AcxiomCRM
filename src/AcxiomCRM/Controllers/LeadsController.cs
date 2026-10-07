using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class LeadsController : Controller
{
    private readonly LeadService _leads;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;
    private readonly ApplicationDbContext _db;

    public LeadsController(LeadService leads, LookupService lookup, IUserScope scope, ApplicationDbContext db)
    {
        _leads = leads;
        _lookup = lookup;
        _scope = scope;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter)
    {
        var query = LeadService.Search(await _leads.QueryAsync(), filter.Q, filter.Status, filter.AssignedTo);
        query = LeadService.Sort(query, filter.Sort);
        return View(new LeadListViewModel
        {
            Filter = filter,
            Items = await PagedList<Lead>.CreateAsync(query, filter.Page),
            Users = _scope.IsSalesExecutive ? new() : await _lookup.UsersAsync(filter.AssignedTo)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var lead = await _leads.GetAsync(id);
        if (lead is null) return NotFound();

        var key = id.ToString();
        return View(new LeadDetailsViewModel
        {
            Lead = lead,
            NextStatuses = LeadWorkflow.NextStatuses(lead.Status),
            FollowUps = await (await _scope.ApplyAsync(_db.FollowUps.AsNoTracking().Include(f => f.AssignedTo).AsQueryable()))
                .Where(f => f.LeadId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync(),
            Activities = await (await _scope.ApplyAsync(_db.Activities.AsNoTracking().Include(a => a.AssignedTo).AsQueryable()))
                .Where(a => a.LeadId == id).OrderByDescending(a => a.ActivityDate).ToListAsync(),
            Opportunities = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking()))
                .Where(o => o.LeadId == id).ToListAsync(),
            History = await _db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityName == nameof(Lead) && a.RecordId == key)
                .OrderByDescending(a => a.AuditLogId).Take(20).ToListAsync()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View("Form", await PrepareAsync(new LeadFormViewModel { AssignedToId = _scope.UserId, ExpectedValue = 0 }, null));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(LeadFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _leads.CreateAsync(model);
            if (result.Succeeded)
            {
                this.Success($"Lead {result.Value!.LeadName} ({result.Value.LeadCode}) created.");
                return RedirectToAction(nameof(Details), new { id = result.Value.LeadId });
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model, null));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var l = await _leads.GetAsync(id);
        if (l is null) return NotFound();

        return View("Form", await PrepareAsync(new LeadFormViewModel
        {
            LeadId = l.LeadId,
            LeadCode = l.LeadCode,
            LeadName = l.LeadName,
            Email = l.Email,
            Phone = l.Phone,
            CompanyName = l.CompanyName,
            Source = l.Source,
            Status = l.Status,
            Priority = l.Priority,
            ExpectedValue = l.ExpectedValue,
            Notes = l.Notes,
            AssignedToId = l.AssignedToId
        }, l.Status));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, LeadFormViewModel model)
    {
        model.LeadId = id;
        if (ModelState.IsValid)
        {
            var result = await _leads.UpdateAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success("Lead updated.");
                return RedirectToAction(nameof(Details), new { id });
            }
            ModelState.AddServiceErrors(result);
        }
        var current = await _db.Leads.Where(l => l.LeadId == id).Select(l => (LeadStatus?)l.Status).FirstOrDefaultAsync();
        return View("Form", await PrepareAsync(model, current));
    }

    /// <summary>Quick status change from the details page (same workflow rules as Edit).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(int id, LeadStatus status)
    {
        var lead = await _leads.GetAsync(id);
        if (lead is null) return NotFound();

        var input = new LeadInputDto
        {
            LeadName = lead.LeadName, Email = lead.Email, Phone = lead.Phone, CompanyName = lead.CompanyName,
            Source = lead.Source, Status = status, Priority = lead.Priority, ExpectedValue = lead.ExpectedValue,
            Notes = lead.Notes, AssignedToId = lead.AssignedToId
        };
        var result = await _leads.UpdateAsync(id, input);
        if (result.Succeeded) this.Success($"Lead status changed to {status.Label()}.");
        else this.Error(result.FirstError());
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _leads.DeleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        this.Success("Lead deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Convert(int id)
    {
        var lead = await _leads.GetAsync(id);
        if (lead is null) return NotFound();
        if (lead.Status != LeadStatus.Qualified)
        {
            this.Error("Only leads with status Qualified can be converted.");
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(new ConvertLeadViewModel
        {
            LeadId = id,
            Lead = lead,
            OpportunityName = $"{lead.CompanyName ?? lead.LeadName} – new deal",
            Amount = lead.ExpectedValue > 0 ? lead.ExpectedValue : null,
            Probability = 20,
            ExpectedCloseDate = DateTime.Today.AddDays(30)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Convert(int id, ConvertLeadViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _leads.ConvertAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                var (customer, opportunity) = result.Value;
                this.Success(opportunity is null
                    ? $"Lead converted to customer {customer.CustomerName}."
                    : $"Lead converted to customer {customer.CustomerName} with opportunity \"{opportunity.OpportunityName}\".");
                return RedirectToAction("Details", "Customers", new { id = customer.CustomerId });
            }
            ModelState.AddServiceErrors(result);
        }
        var lead = await _leads.GetAsync(id);
        if (lead is null) return NotFound();
        model.LeadId = id;
        model.Lead = lead;
        return View(model);
    }

    private async Task<LeadFormViewModel> PrepareAsync(LeadFormViewModel model, LeadStatus? currentStatus)
    {
        model.CanChooseOwner = !_scope.IsSalesExecutive;
        if (model.CanChooseOwner) model.Users = await _lookup.UsersAsync(model.AssignedToId);

        // Only offer statuses the workflow allows from the current one.
        var allowed = currentStatus is null
            ? LeadWorkflow.InitialStatuses.ToList()
            : new[] { currentStatus.Value }.Concat(LeadWorkflow.NextStatuses(currentStatus.Value)).ToList();
        model.AllowedStatuses = DisplayHelpers.EnumOptions<LeadStatus>(allowed);
        return model;
    }
}

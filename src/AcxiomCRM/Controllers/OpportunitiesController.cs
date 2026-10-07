using AcxiomCRM.Data;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class OpportunitiesController : Controller
{
    private readonly OpportunityService _opportunities;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;
    private readonly ApplicationDbContext _db;

    public OpportunitiesController(OpportunityService opportunities, LookupService lookup, IUserScope scope, ApplicationDbContext db)
    {
        _opportunities = opportunities;
        _lookup = lookup;
        _scope = scope;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter)
    {
        // "Type" carries the stage filter, "Status" the Open/Won/Lost filter.
        var query = OpportunityService.Search(await _opportunities.QueryAsync(), filter.Q, filter.Type, filter.Status, filter.AssignedTo);
        var totals = await query.Select(o => new { o.Amount, o.Probability }).ToListAsync();
        return View(new OpportunityListViewModel
        {
            Filter = filter,
            Items = await PagedList<Opportunity>.CreateAsync(OpportunityService.Sort(query, filter.Sort), filter.Page),
            FilteredAmount = totals.Sum(t => t.Amount),
            FilteredWeighted = totals.Sum(t => OpportunityRules.Weighted(t.Amount, t.Probability))
        });
    }

    /// <summary>Kanban-style sales pipeline board.</summary>
    [HttpGet]
    public async Task<IActionResult> Pipeline()
    {
        var items = await (await _opportunities.QueryAsync()).OrderBy(o => o.ExpectedCloseDate).ToListAsync();
        var vm = new PipelineBoardViewModel();
        foreach (var stage in Enum.GetValues<OpportunityStage>())
        {
            var column = items.Where(o => o.Stage == stage);
            // Closed columns only show the last 90 days to keep the board focused.
            if (!OpportunityRules.IsActive(stage)) column = column.Where(o => o.ClosedDate >= DateTime.Today.AddDays(-90));
            vm.Columns[stage] = column.ToList();
        }
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var opportunity = await _opportunities.GetAsync(id);
        if (opportunity is null) return NotFound();

        var key = id.ToString();
        ViewBag.FollowUps = await (await _scope.ApplyAsync(_db.FollowUps.AsNoTracking().Include(f => f.AssignedTo).AsQueryable()))
            .Where(f => f.OpportunityId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync();
        ViewBag.History = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityName == nameof(Opportunity) && a.RecordId == key)
            .OrderByDescending(a => a.AuditLogId).Take(20).ToListAsync();
        return View(opportunity);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId) =>
        View("Form", await PrepareAsync(new OpportunityFormViewModel
        {
            CustomerId = customerId,
            AssignedToId = _scope.UserId,
            Probability = 20,
            ExpectedCloseDate = DateTime.Today.AddDays(30)
        }));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(OpportunityFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _opportunities.CreateAsync(model);
            if (result.Succeeded)
            {
                this.Success($"Opportunity \"{result.Value!.OpportunityName}\" created.");
                return RedirectToAction(nameof(Details), new { id = result.Value.OpportunityId });
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var o = await _opportunities.GetAsync(id);
        if (o is null) return NotFound();

        var input = OpportunityService.ToInput(o);
        return View("Form", await PrepareAsync(new OpportunityFormViewModel
        {
            OpportunityId = id,
            OpportunityName = input.OpportunityName,
            OutcomeNotes = input.OutcomeNotes,
            CustomerId = input.CustomerId,
            LeadId = input.LeadId,
            Amount = input.Amount,
            Probability = input.Probability,
            ExpectedCloseDate = input.ExpectedCloseDate,
            Stage = input.Stage,
            Source = input.Source,
            Notes = input.Notes,
            AssignedToId = input.AssignedToId
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, OpportunityFormViewModel model)
    {
        model.OpportunityId = id;
        if (ModelState.IsValid)
        {
            var result = await _opportunities.UpdateAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success("Opportunity updated.");
                return RedirectToAction(nameof(Details), new { id });
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStage(int id, OpportunityStage stage, string? outcomeNotes, string? returnUrl)
    {
        var result = await _opportunities.ChangeStageAsync(id, stage, outcomeNotes);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        if (result.Succeeded) this.Success($"Moved to {stage.Label()}.");
        else this.Error(result.FirstError() + " Edit the opportunity to fix it.");
        return this.RedirectToLocal(returnUrl, nameof(Pipeline), "Opportunities");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _opportunities.DeleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        this.Success("Opportunity deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<OpportunityFormViewModel> PrepareAsync(OpportunityFormViewModel model)
    {
        model.CanChooseOwner = !_scope.IsSalesExecutive;
        if (model.CanChooseOwner) model.Users = await _lookup.UsersAsync(model.AssignedToId);
        model.Customers = await _lookup.CustomersAsync(model.CustomerId);
        model.Leads = await _lookup.LeadsAsync(model.LeadId);
        return model;
    }
}

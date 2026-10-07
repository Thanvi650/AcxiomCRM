using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class FollowUpsController : Controller
{
    private readonly FollowUpService _followUps;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;

    public FollowUpsController(FollowUpService followUps, LookupService lookup, IUserScope scope)
    {
        _followUps = followUps;
        _lookup = lookup;
        _scope = scope;
    }

    // view: all | pending | overdue | today | upcoming
    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter, string view = "pending")
    {
        var scoped = await _followUps.QueryAsync();
        var today = DateTime.Today;
        var query = FollowUpService.Search(scoped, filter.Q, filter.Status, filter.AssignedTo, filter.From, filter.To, view);
        return View(new FollowUpListViewModel
        {
            Filter = filter,
            View = view,
            Items = await PagedList<FollowUp>.CreateAsync(FollowUpService.Sort(query, filter.Sort), filter.Page),
            Users = _scope.IsSalesExecutive ? new() : await _lookup.UsersAsync(filter.AssignedTo),
            OverdueCount = await scoped.CountAsync(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today),
            DueTodayCount = await scoped.CountAsync(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today && f.FollowUpDate < today.AddDays(1)),
            UpcomingCount = await scoped.CountAsync(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId) =>
        View("Form", await PrepareAsync(new FollowUpFormViewModel
        {
            CustomerId = customerId,
            LeadId = leadId,
            OpportunityId = opportunityId,
            AssignedToId = _scope.UserId,
            FollowUpDate = DateTime.Today.AddDays(1).AddHours(10)
        }));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(FollowUpFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _followUps.CreateAsync(model);
            if (result.Succeeded)
            {
                this.Success("Follow-up scheduled.");
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var f = await _followUps.GetAsync(id);
        if (f is null) return NotFound();
        if (f.Status != FollowUpStatus.Planned)
        {
            this.Error("Only planned follow-ups can be edited.");
            return RedirectToAction(nameof(Index));
        }

        return View("Form", await PrepareAsync(new FollowUpFormViewModel
        {
            FollowUpId = id,
            Subject = f.Subject,
            FollowUpDate = f.FollowUpDate,
            FollowUpType = f.FollowUpType,
            Remarks = f.Remarks,
            CustomerId = f.CustomerId,
            LeadId = f.LeadId,
            OpportunityId = f.OpportunityId,
            AssignedToId = f.AssignedToId
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, FollowUpFormViewModel model)
    {
        model.FollowUpId = id;
        if (ModelState.IsValid)
        {
            var result = await _followUps.UpdateAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success("Follow-up updated.");
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, string? remarks, string? returnUrl)
    {
        var result = await _followUps.CompleteAsync(id, remarks);
        return Done(result, "Follow-up marked as completed.", returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Missed(int id, string? returnUrl) =>
        Done(await _followUps.MarkMissedAsync(id), "Follow-up marked as missed.", returnUrl);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? returnUrl) =>
        Done(await _followUps.CancelAsync(id), "Follow-up cancelled.", returnUrl);

    [HttpGet]
    public async Task<IActionResult> Reschedule(int id)
    {
        var f = await _followUps.GetAsync(id);
        if (f is null) return NotFound();
        return View(new RescheduleFollowUpViewModel
        {
            FollowUpId = id,
            FollowUp = f,
            NewDate = (f.FollowUpDate.Date < DateTime.Today ? DateTime.Today.AddDays(1) : f.FollowUpDate.AddDays(1)).Date.AddHours(f.FollowUpDate.Hour)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reschedule(int id, RescheduleFollowUpViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _followUps.RescheduleAsync(id, model.NewDate!.Value, model.Remarks);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success($"Follow-up rescheduled to {result.Value!.FollowUpDate:dd MMM yyyy, hh:mm tt}.");
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddServiceErrors(result);
        }
        model.FollowUpId = id;
        model.FollowUp = await _followUps.GetAsync(id);
        return model.FollowUp is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _followUps.DeleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        this.Success("Follow-up deleted.");
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Done<T>(ServiceResult<T> result, string message, string? returnUrl)
    {
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        if (result.Succeeded) this.Success(message);
        else this.Error(result.FirstError());
        return this.RedirectToLocal(returnUrl, nameof(Index), "FollowUps");
    }

    private async Task<FollowUpFormViewModel> PrepareAsync(FollowUpFormViewModel model)
    {
        model.CanChooseOwner = !_scope.IsSalesExecutive;
        if (model.CanChooseOwner) model.Users = await _lookup.UsersAsync(model.AssignedToId);
        model.Customers = await _lookup.CustomersAsync(model.CustomerId);
        model.Leads = await _lookup.LeadsAsync(model.LeadId, openOnly: true);
        model.Opportunities = await _lookup.OpportunitiesAsync(model.OpportunityId);
        return model;
    }
}

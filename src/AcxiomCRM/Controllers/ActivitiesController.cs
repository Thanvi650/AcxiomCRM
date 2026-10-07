using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

public class ActivitiesController : Controller
{
    private readonly ActivityService _activities;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;

    public ActivitiesController(ActivityService activities, LookupService lookup, IUserScope scope)
    {
        _activities = activities;
        _lookup = lookup;
        _scope = scope;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter)
    {
        var query = ActivityService.Search(await _activities.QueryAsync(), filter.Q, filter.Type, filter.Status,
            filter.AssignedTo, filter.From, filter.To);
        return View(new ActivityListViewModel
        {
            Filter = filter,
            Items = await PagedList<Activity>.CreateAsync(ActivityService.Sort(query, filter.Sort), filter.Page),
            Users = _scope.IsSalesExecutive ? new() : await _lookup.UsersAsync(filter.AssignedTo)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, int? leadId, ActivityType? type) =>
        View("Form", await PrepareAsync(new ActivityFormViewModel
        {
            CustomerId = customerId,
            LeadId = leadId,
            ActivityType = type ?? ActivityType.Call,
            ActivityDate = DateTime.Now.Date.AddHours(DateTime.Now.Hour),
            Status = ActivityStatus.Completed,
            AssignedToId = _scope.UserId
        }));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ActivityFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _activities.CreateAsync(model);
            if (result.Succeeded)
            {
                this.Success($"{result.Value!.ActivityType.Label()} activity logged.");
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await _activities.GetAsync(id);
        if (a is null) return NotFound();

        return View("Form", await PrepareAsync(new ActivityFormViewModel
        {
            ActivityId = id,
            ActivityType = a.ActivityType,
            Subject = a.Subject,
            Description = a.Description,
            ActivityDate = a.ActivityDate,
            CustomerId = a.CustomerId,
            LeadId = a.LeadId,
            Status = a.Status,
            AssignedToId = a.AssignedToId
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ActivityFormViewModel model)
    {
        model.ActivityId = id;
        if (ModelState.IsValid)
        {
            var result = await _activities.UpdateAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success("Activity updated.");
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, string? returnUrl)
    {
        var result = await _activities.CompleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        if (result.Succeeded) this.Success($"\"{result.Value!.Subject}\" marked as completed.");
        else this.Error(result.FirstError());
        return this.RedirectToLocal(returnUrl, nameof(Index), "Activities");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _activities.DeleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        this.Success("Activity deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<ActivityFormViewModel> PrepareAsync(ActivityFormViewModel model)
    {
        model.CanChooseOwner = !_scope.IsSalesExecutive;
        if (model.CanChooseOwner) model.Users = await _lookup.UsersAsync(model.AssignedToId);
        model.Customers = await _lookup.CustomersAsync(model.CustomerId);
        model.Leads = await _lookup.LeadsAsync(model.LeadId);
        return model;
    }
}

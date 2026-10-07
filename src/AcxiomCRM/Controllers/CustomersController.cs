using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class CustomersController : Controller
{
    private readonly CustomerService _customers;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;
    private readonly ApplicationDbContext _db;

    public CustomersController(CustomerService customers, LookupService lookup, IUserScope scope, ApplicationDbContext db)
    {
        _customers = customers;
        _lookup = lookup;
        _scope = scope;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ListFilter filter)
    {
        var query = CustomerService.Search(await _customers.QueryAsync(), filter.Q, filter.Status, filter.AssignedTo);
        query = CustomerService.Sort(query, filter.Sort);
        return View(new CustomerListViewModel
        {
            Filter = filter,
            Items = await PagedList<Customer>.CreateAsync(query, filter.Page),
            Users = _scope.IsSalesExecutive ? new() : await _lookup.UsersAsync(filter.AssignedTo)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var customer = await _customers.GetAsync(id);
        if (customer is null) return NotFound();

        var key = id.ToString();
        return View(new CustomerDetailsViewModel
        {
            Customer = customer,
            CreatedByName = await _db.Users.Where(u => u.Id == customer.CreatedBy).Select(u => u.FullName).FirstOrDefaultAsync(),
            Opportunities = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking()))
                .Where(o => o.CustomerId == id).OrderByDescending(o => o.CreatedDate).ToListAsync(),
            FollowUps = await (await _scope.ApplyAsync(_db.FollowUps.AsNoTracking().Include(f => f.AssignedTo).AsQueryable()))
                .Where(f => f.CustomerId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync(),
            Activities = await (await _scope.ApplyAsync(_db.Activities.AsNoTracking().Include(a => a.AssignedTo).AsQueryable()))
                .Where(a => a.CustomerId == id).OrderByDescending(a => a.ActivityDate).ToListAsync(),
            History = await _db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityName == nameof(Customer) && a.RecordId == key)
                .OrderByDescending(a => a.AuditLogId).Take(20).ToListAsync()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View("Form", await PrepareAsync(new CustomerFormViewModel { AssignedToId = _scope.UserId }));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CustomerFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _customers.CreateAsync(model);
            if (result.Succeeded)
            {
                this.Success($"Customer {result.Value!.CustomerName} ({result.Value.CustomerCode}) created.");
                return RedirectToAction(nameof(Details), new { id = result.Value.CustomerId });
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await _customers.GetAsync(id);
        if (c is null) return NotFound();

        return View("Form", await PrepareAsync(new CustomerFormViewModel
        {
            CustomerId = c.CustomerId,
            CustomerCode = c.CustomerCode,
            CustomerName = c.CustomerName,
            Email = c.Email,
            Phone = c.Phone,
            CompanyName = c.CompanyName,
            Address = c.Address,
            City = c.City,
            State = c.State,
            Status = c.Status,
            Notes = c.Notes,
            AssignedToId = c.AssignedToId
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CustomerFormViewModel model)
    {
        model.CustomerId = id;
        if (ModelState.IsValid)
        {
            var result = await _customers.UpdateAsync(id, model);
            if (result.Status == ServiceStatus.NotFound) return NotFound();
            if (result.Succeeded)
            {
                this.Success("Customer updated.");
                return RedirectToAction(nameof(Details), new { id });
            }
            ModelState.AddServiceErrors(result);
        }
        return View("Form", await PrepareAsync(model));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _customers.DeleteAsync(id);
        if (result.Status == ServiceStatus.NotFound) return NotFound();
        if (!result.Succeeded)
        {
            this.Error(result.FirstError());
            return RedirectToAction(nameof(Details), new { id });
        }
        this.Success("Customer deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<CustomerFormViewModel> PrepareAsync(CustomerFormViewModel model)
    {
        model.CanChooseOwner = !_scope.IsSalesExecutive;
        if (model.CanChooseOwner) model.Users = await _lookup.UsersAsync(model.AssignedToId);
        if (model.CustomerId is not null && model.CustomerCode is null)
        {
            model.CustomerCode = await _db.Customers.Where(c => c.CustomerId == model.CustomerId)
                .Select(c => c.CustomerCode).FirstOrDefaultAsync();
        }
        return model;
    }
}

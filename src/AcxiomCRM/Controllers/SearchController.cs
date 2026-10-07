using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>Global search (spec 4.1): one box that searches customers, leads and opportunities in the user's scope.</summary>
public class SearchController : Controller
{
    private const int PerGroup = 8;

    private readonly CustomerService _customers;
    private readonly LeadService _leads;
    private readonly OpportunityService _opportunities;

    public SearchController(CustomerService customers, LeadService leads, OpportunityService opportunities)
    {
        _customers = customers;
        _leads = leads;
        _opportunities = opportunities;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q)
    {
        var vm = new GlobalSearchViewModel { Q = q?.Trim() ?? string.Empty };
        if (vm.Q.Length < 2) return View(vm);

        var customers = CustomerService.Search(await _customers.QueryAsync(), vm.Q, null, null);
        vm.CustomerCount = await customers.CountAsync();
        vm.Customers = await CustomerService.Sort(customers, "name").Take(PerGroup).ToListAsync();

        var leads = LeadService.Search(await _leads.QueryAsync(), vm.Q, null, null);
        vm.LeadCount = await leads.CountAsync();
        vm.Leads = await LeadService.Sort(leads, "name").Take(PerGroup).ToListAsync();

        var opportunities = OpportunityService.Search(await _opportunities.QueryAsync(), vm.Q, null, null, null);
        vm.OpportunityCount = await opportunities.CountAsync();
        vm.Opportunities = await OpportunityService.Sort(opportunities, "name").Take(PerGroup).ToListAsync();

        return View(vm);
    }
}

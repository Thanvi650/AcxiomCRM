using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Reports with filters, sorting, pagination and CSV export. Data always comes from the
/// scope-filtered service queries, so each role only ever reports on what it may see.
/// </summary>
public class ReportsController : Controller
{
    private readonly CustomerService _customers;
    private readonly LeadService _leads;
    private readonly OpportunityService _opportunities;
    private readonly FollowUpService _followUps;
    private readonly ReportService _reports;
    private readonly LookupService _lookup;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public ReportsController(CustomerService customers, LeadService leads, OpportunityService opportunities,
        FollowUpService followUps, ReportService reports, LookupService lookup, IUserScope scope, IAuditService audit)
    {
        _audit = audit;
        _customers = customers;
        _leads = leads;
        _opportunities = opportunities;
        _followUps = followUps;
        _reports = reports;
        _lookup = lookup;
        _scope = scope;
    }

    /// <summary>A reversed date range (From after To) is reported and ignored instead of returning nothing.</summary>
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        if (context.ActionArguments.Values.OfType<ListFilter>().FirstOrDefault() is { From: not null, To: not null } filter
            && filter.From > filter.To)
        {
            ViewData["RangeError"] = $"The end date ({filter.To:dd MMM yyyy}) is before the start date ({filter.From:dd MMM yyyy}). The date filter was ignored.";
            filter.From = null;
            filter.To = null;
        }
        base.OnActionExecuting(context);
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Customers(ListFilter filter, string? export)
    {
        var query = CustomerService.Search(await _customers.QueryAsync(), filter.Q, filter.Status, filter.AssignedTo);
        query = ApplyDates(query, filter, c => c.CreatedDate);
        query = CustomerService.Sort(query, filter.Sort);

        if (export == "csv")
        {
            var rows = await query.ToListAsync();
            return await CsvAsync("customers", new[] { "Code", "Customer", "Company", "Email", "Phone", "City", "Status", "Owner", "Created" },
                rows.Select(c => new object?[] { c.CustomerCode, c.CustomerName, c.CompanyName, c.Email, c.Phone, c.City, c.Status, c.AssignedTo?.FullName, c.CreatedDate }));
        }

        var all = await query.Select(c => c.Status).ToListAsync();
        return View(new ReportViewModel<Customer>
        {
            Title = "Customer Report",
            Filter = filter,
            Items = await PagedList<Customer>.CreateAsync(query, filter.Page, 15),
            Users = await UsersAsync(filter),
            Summary = new()
            {
                ["Total"] = all.Count.ToString(),
                ["Active"] = all.Count(s => s == CustomerStatus.Active).ToString(),
                ["Prospect"] = all.Count(s => s == CustomerStatus.Prospect).ToString(),
                ["Inactive"] = all.Count(s => s == CustomerStatus.Inactive).ToString()
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> Leads(ListFilter filter, string? export)
    {
        var query = LeadService.Search(await _leads.QueryAsync(), filter.Q, filter.Status, filter.AssignedTo);
        if (Enum.TryParse<LeadSource>(filter.Type, out var source)) query = query.Where(l => l.Source == source);
        query = ApplyDates(query, filter, l => l.CreatedDate);
        query = LeadService.Sort(query, filter.Sort);

        if (export == "csv")
        {
            var rows = await query.ToListAsync();
            return await CsvAsync("leads", new[] { "Code", "Lead", "Company", "Source", "Status", "Priority", "Expected Value", "Owner", "Created", "Converted", "Converted To" },
                rows.Select(l => new object?[] { l.LeadCode, l.LeadName, l.CompanyName, l.Source, l.Status, l.Priority, l.ExpectedValue, l.AssignedTo?.FullName, l.CreatedDate, l.ConvertedDate, l.ConvertedCustomer?.CustomerName }));
        }

        var all = await query.Select(l => new { l.Status, l.ExpectedValue }).ToListAsync();
        var converted = all.Count(l => l.Status == LeadStatus.Converted);
        return View(new ReportViewModel<Lead>
        {
            Title = "Lead Report",
            Filter = filter,
            Items = await PagedList<Lead>.CreateAsync(query, filter.Page, 15),
            Users = await UsersAsync(filter),
            Summary = new()
            {
                ["Total"] = all.Count.ToString(),
                ["Open"] = all.Count(l => LeadWorkflow.IsOpen(l.Status)).ToString(),
                ["Converted"] = converted.ToString(),
                ["Conversion rate"] = all.Count == 0 ? "0%" : $"{Math.Round(converted * 100m / all.Count, 1)}%",
                ["Expected value"] = all.Sum(l => l.ExpectedValue).Money()
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> FollowUps(ListFilter filter, string? export, string? view)
    {
        var query = FollowUpService.Search(await _followUps.QueryAsync(), filter.Q, filter.Status, filter.AssignedTo, filter.From, filter.To, view);
        query = FollowUpService.Sort(query, filter.Sort);

        if (export == "csv")
        {
            var rows = await query.ToListAsync();
            return await CsvAsync("follow-ups", new[] { "Date", "Subject", "Type", "Status", "Overdue", "Related To", "Assigned To", "Remarks" },
                rows.Select(f => new object?[] { f.FollowUpDate, f.Subject, f.FollowUpType, f.Status, f.IsOverdue ? "Yes" : "No", f.RelatedTo, f.AssignedTo?.FullName, f.Remarks }));
        }

        var today = DateTime.Today;
        var all = await query.Select(f => new { f.Status, f.FollowUpDate }).ToListAsync();
        ViewBag.View = view;
        return View(new ReportViewModel<FollowUp>
        {
            Title = "Follow-Up Report",
            Filter = filter,
            Items = await PagedList<FollowUp>.CreateAsync(query, filter.Page, 15),
            Users = await UsersAsync(filter),
            Summary = new()
            {
                ["Planned"] = all.Count(f => f.Status == FollowUpStatus.Planned).ToString(),
                ["Completed"] = all.Count(f => f.Status == FollowUpStatus.Completed).ToString(),
                ["Missed"] = all.Count(f => f.Status == FollowUpStatus.Missed).ToString(),
                ["Overdue"] = all.Count(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today).ToString()
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> Opportunities(ListFilter filter, string? export)
    {
        var query = OpportunityService.Search(await _opportunities.QueryAsync(), filter.Q, filter.Type, filter.Status, filter.AssignedTo);
        if (filter.From is not null) query = query.Where(o => o.ExpectedCloseDate >= filter.From.Value.Date);
        if (filter.To is not null) query = query.Where(o => o.ExpectedCloseDate <= filter.To.Value.Date);
        query = OpportunityService.Sort(query, filter.Sort);

        if (export == "csv")
        {
            var rows = await query.ToListAsync();
            return await CsvAsync("opportunities", new[] { "Opportunity", "Customer", "Stage", "Status", "Amount", "Probability %", "Weighted", "Expected Close", "Owner", "Closed", "Outcome" },
                rows.Select(o => new object?[] { o.OpportunityName, o.Customer?.CustomerName, o.Stage, o.Status, o.Amount, o.Probability, o.WeightedAmount, o.ExpectedCloseDate, o.AssignedTo?.FullName, o.ClosedDate, o.OutcomeNotes }));
        }

        var all = await query.Select(o => new { o.Status, o.Amount, o.Probability }).ToListAsync();
        return View(new ReportViewModel<Opportunity>
        {
            Title = "Opportunity Report",
            Filter = filter,
            Items = await PagedList<Opportunity>.CreateAsync(query, filter.Page, 15),
            Users = await UsersAsync(filter),
            Summary = new()
            {
                ["Count"] = all.Count.ToString(),
                ["Total amount"] = all.Sum(o => o.Amount).Money(),
                ["Weighted"] = all.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)).Money(),
                ["Won"] = all.Where(o => o.Status == OpportunityStatus.Won).Sum(o => o.Amount).Money()
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> Pipeline(ListFilter filter, string? export)
    {
        var report = await _reports.PipelineAsync(filter.AssignedTo);
        if (export == "csv")
        {
            // One sheet with both views of the pipeline: by stage, then by owner.
            var rows = report.ByStage
                .Select(s => new object?[] { "Stage", s.Stage, s.Count, s.Amount, s.WeightedAmount, null })
                .Concat(report.ByOwner.Select(o => new object?[] { "Owner", o.OwnerName, o.OpenCount, o.OpenAmount, o.WeightedAmount, o.WonAmount }));
            return await CsvAsync("pipeline",
                new[] { "View", "Stage / Owner", "Opportunities", "Amount", "Weighted Amount", "Won Amount" }, rows);
        }
        return View(new PipelineReportViewModel { Filter = filter, Report = report, Users = await UsersAsync(filter) });
    }

    [HttpGet]
    public async Task<IActionResult> Conversion(ListFilter filter, string? export)
    {
        var vm = await _reports.ConversionAsync(filter);
        if (export == "csv")
        {
            return await CsvAsync("conversion", new[] { "Group", "Type", "Total Leads", "Converted", "Lost/Unqualified", "Open", "Conversion %" },
                vm.BySource.Select(r => new object?[] { r.Group, "Source", r.TotalLeads, r.Converted, r.Lost, r.Open, r.ConversionRate })
                    .Concat(vm.ByOwner.Select(r => new object?[] { r.Group, "Owner", r.TotalLeads, r.Converted, r.Lost, r.Open, r.ConversionRate })));
        }
        ViewBag.Users = await UsersAsync(filter);
        return View(vm);
    }

    [Authorize(Roles = Roles.AdminOrManager)]
    [HttpGet]
    public async Task<IActionResult> UserActivity(ListFilter filter, string? export)
    {
        var rows = await _reports.UserActivityAsync(filter);
        if (export == "csv")
        {
            return await CsvAsync("user-activity", new[] { "User", "Logins", "Failed Logins", "Creates", "Updates", "Deletes", "Total", "Last Activity" },
                rows.Select(r => new object?[] { r.UserName, r.Logins, r.FailedLogins, r.Creates, r.Updates, r.Deletes, r.Total, r.LastActivity }));
        }
        return View(new ReportViewModel<UserActivityRow>
        {
            Title = "User Activity Report",
            Filter = filter,
            Items = PagedList<UserActivityRow>.Create(rows, filter.Page, 15),
            Users = await UsersAsync(filter),
            Summary = new()
            {
                ["Users"] = rows.Count.ToString(),
                ["Actions"] = rows.Sum(r => r.Total).ToString(),
                ["Logins"] = rows.Sum(r => r.Logins).ToString(),
                ["Failed logins"] = rows.Sum(r => r.FailedLogins).ToString()
            }
        });
    }

    /// <summary>The audit report is the audit log screen (Admin full view; Manager limited).</summary>
    [Authorize(Roles = Roles.AdminOrManager)]
    [HttpGet]
    public IActionResult Audit() => RedirectToAction("Index", "AuditLogs");

    private static IQueryable<T> ApplyDates<T>(IQueryable<T> query, ListFilter filter, System.Linq.Expressions.Expression<Func<T, DateTime>> date)
    {
        if (filter.From is not null)
        {
            var from = filter.From.Value.Date;
            var parameter = date.Parameters[0];
            var body = System.Linq.Expressions.Expression.GreaterThanOrEqual(date.Body, System.Linq.Expressions.Expression.Constant(from));
            query = query.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter));
        }
        if (filter.To is not null)
        {
            var end = filter.To.Value.Date.AddDays(1);
            var parameter = date.Parameters[0];
            var body = System.Linq.Expressions.Expression.LessThan(date.Body, System.Linq.Expressions.Expression.Constant(end));
            query = query.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter));
        }
        return query;
    }

    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> UsersAsync(ListFilter filter) =>
        _scope.IsSalesExecutive ? new() : await _lookup.UsersAsync(filter.AssignedTo);

    private async Task<FileContentResult> CsvAsync(string name, IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var list = rows.ToList();
        // Who exported what (and how many rows) is part of the audit trail.
        await _audit.LogAsync(AuditActions.Export, "Report", name,
            newValue: new { Report = name, Rows = list.Count, Query = Request.QueryString.Value });
        return File(CsvExport.Build(headers, list), "text/csv", $"{name}-report-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }
}

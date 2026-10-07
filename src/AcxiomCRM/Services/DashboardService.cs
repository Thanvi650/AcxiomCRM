using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

/// <summary>Dashboard KPIs and chart data — always computed from scope-filtered (authorized) queries.</summary>
public class DashboardService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly UserManager<ApplicationUser> _users;

    public DashboardService(ApplicationDbContext db, IUserScope scope, UserManager<ApplicationUser> users)
    {
        _db = db;
        _scope = scope;
        _users = users;
    }

    public async Task<DashboardViewModel> GetAsync(DashboardFilter filter)
    {
        var (start, end) = filter.Resolve();
        var vm = new DashboardViewModel
        {
            Filter = filter,
            ScopeLabel = _scope.IsAdmin ? "Organisation-wide" : _scope.IsManager ? "My team" : "My assigned records"
        };

        var customers = await _scope.ApplyAsync(_db.Customers.AsNoTracking());
        var leads = await _scope.ApplyAsync(_db.Leads.AsNoTracking());
        var opportunities = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        var followUps = await _scope.ApplyAsync(_db.FollowUps.AsNoTracking());

        if (start is not null)
        {
            customers = customers.Where(c => c.CreatedDate >= start);
            leads = leads.Where(l => l.CreatedDate >= start);
            opportunities = opportunities.Where(o => o.CreatedDate >= start);
        }
        if (end is not null)
        {
            customers = customers.Where(c => c.CreatedDate < end);
            leads = leads.Where(l => l.CreatedDate < end);
            opportunities = opportunities.Where(o => o.CreatedDate < end);
        }

        vm.TotalCustomers = await customers.CountAsync();

        // Small projections pulled into memory keep aggregation simple and SQLite-friendly.
        var leadRows = await leads.Select(l => new { l.Status, l.AssignedToId }).ToListAsync();
        vm.TotalLeads = leadRows.Count;
        vm.OpenLeads = leadRows.Count(l => LeadWorkflow.IsOpen(l.Status));
        var converted = leadRows.Count(l => l.Status == LeadStatus.Converted);
        vm.ConversionRate = vm.TotalLeads == 0 ? 0 : Math.Round(converted * 100m / vm.TotalLeads, 1);

        var oppRows = await opportunities
            .Select(o => new { o.Stage, o.Status, o.Amount, o.Probability, o.ClosedDate, o.AssignedToId })
            .ToListAsync();
        vm.TotalOpportunities = oppRows.Count;
        vm.OpenOpportunities = oppRows.Count(o => o.Status == OpportunityStatus.Open);
        vm.WonOpportunities = oppRows.Count(o => o.Status == OpportunityStatus.Won);
        vm.LostOpportunities = oppRows.Count(o => o.Status == OpportunityStatus.Lost);
        vm.TotalPipelineValue = oppRows.Where(o => o.Status == OpportunityStatus.Open).Sum(o => o.Amount);
        vm.WeightedPipelineValue = oppRows.Where(o => o.Status == OpportunityStatus.Open)
            .Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability));
        vm.WonValue = oppRows.Where(o => o.Status == OpportunityStatus.Won).Sum(o => o.Amount);
        var closed = vm.WonOpportunities + vm.LostOpportunities;
        vm.WinRate = closed == 0 ? 0 : Math.Round(vm.WonOpportunities * 100m / closed, 1);

        var today = DateTime.Today;
        vm.PendingFollowUps = await followUps.CountAsync(f => f.Status == FollowUpStatus.Planned);
        vm.OverdueFollowUps = await followUps.CountAsync(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today);

        // Chart: lead status
        foreach (var status in new[] { LeadStatus.New, LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost, LeadStatus.Converted })
        {
            vm.LeadStatusChart.Labels.Add(status.ToString());
            vm.LeadStatusChart.Values.Add(leadRows.Count(l => l.Status == status));
        }

        // Chart: opportunity pipeline (count + amount per stage)
        foreach (var stage in Enum.GetValues<OpportunityStage>())
        {
            vm.PipelineChart.Labels.Add(stage.ToString());
            vm.PipelineChart.Values.Add(oppRows.Count(o => o.Stage == stage));
            vm.PipelineChart.SecondaryValues.Add(oppRows.Where(o => o.Stage == stage).Sum(o => o.Amount));
        }

        // Chart: monthly sales — won amount (and lost count) per month, last 12 months
        // (or the selected custom range), keyed by the date the deal closed.
        var allClosed = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking()))
            .Where(o => o.ClosedDate != null)
            .Select(o => new { o.Status, o.Amount, o.ClosedDate })
            .ToListAsync();
        var firstMonth = start is not null && filter.Range == "custom"
            ? new DateTime(start.Value.Year, start.Value.Month, 1)
            : new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        var lastMonth = end is not null && filter.Range == "custom"
            ? new DateTime(end.Value.AddDays(-1).Year, end.Value.AddDays(-1).Month, 1)
            : new DateTime(today.Year, today.Month, 1);
        if ((lastMonth.Year - firstMonth.Year) * 12 + lastMonth.Month - firstMonth.Month > 36) firstMonth = lastMonth.AddMonths(-36);
        for (var month = firstMonth; month <= lastMonth; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            var inMonth = allClosed.Where(o => o.ClosedDate >= month && o.ClosedDate < next).ToList();
            vm.MonthlySalesChart.Labels.Add(month.ToString("MMM yy"));
            vm.MonthlySalesChart.Values.Add(inMonth.Where(o => o.Status == OpportunityStatus.Won).Sum(o => o.Amount));
            vm.MonthlySalesChart.SecondaryValues.Add(inMonth.Count(o => o.Status == OpportunityStatus.Won));
        }

        vm.UpcomingFollowUps = await followUps
            .Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity).Include(f => f.AssignedTo)
            .Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today.AddDays(8))
            .OrderBy(f => f.FollowUpDate)
            .Take(8)
            .ToListAsync();

        vm.ClosingSoon = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking()))
            .Include(o => o.Customer)
            .Where(o => o.Status == OpportunityStatus.Open)
            .OrderBy(o => o.ExpectedCloseDate)
            .Take(6)
            .ToListAsync();

        // Team performance (Admin & Manager)
        if (!_scope.IsSalesExecutive)
        {
            var people = await _scope.AssignableUsersAsync();
            var pendingByOwner = await followUps.Where(f => f.Status == FollowUpStatus.Planned)
                .GroupBy(f => f.AssignedToId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
            vm.Team = people.Select(p => new TeamPerformanceRow
                {
                    OwnerName = p.FullName,
                    OpenOpportunities = oppRows.Count(o => o.AssignedToId == p.Id && o.Status == OpportunityStatus.Open),
                    PipelineAmount = oppRows.Where(o => o.AssignedToId == p.Id && o.Status == OpportunityStatus.Open).Sum(o => o.Amount),
                    WonAmount = oppRows.Where(o => o.AssignedToId == p.Id && o.Status == OpportunityStatus.Won).Sum(o => o.Amount),
                    OpenLeads = leadRows.Count(l => l.AssignedToId == p.Id && LeadWorkflow.IsOpen(l.Status)),
                    PendingFollowUps = pendingByOwner.FirstOrDefault(x => x.Key == p.Id)?.Count ?? 0
                })
                .Where(r => r.OpenOpportunities + r.OpenLeads + r.PendingFollowUps > 0 || r.WonAmount > 0)
                .OrderByDescending(r => r.PipelineAmount)
                .ToList();
        }

        if (_scope.IsAdmin)
        {
            vm.ShowSecurityStats = true;
            vm.TotalUsers = await _db.Users.CountAsync();
            vm.ActiveUsers = await _db.Users.CountAsync(u => u.IsActive);
            var now = DateTimeOffset.UtcNow;
            vm.LockedUsers = (await _db.Users.Where(u => u.LockoutEnd != null).Select(u => u.LockoutEnd).ToListAsync())
                .Count(l => l > now);
            vm.FailedLoginsToday = await _db.AuditLogs.CountAsync(a =>
                (a.Action == AuditActions.FailedLogin || a.Action == AuditActions.Lockout) && a.CreatedDate >= today);
            vm.RecentAudit = await _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.AuditLogId).Take(8).ToListAsync();
        }

        return vm;
    }
}

/// <summary>Report queries. Every report starts from scope-filtered data.</summary>
public class ReportService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;

    public ReportService(ApplicationDbContext db, IUserScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<PipelineReportDto> PipelineAsync(string? assignedTo = null)
    {
        var query = (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking())).Include(o => o.AssignedTo).AsQueryable();
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(o => o.AssignedToId == assignedTo);

        var rows = await query
            .Select(o => new { o.Stage, o.Status, o.Amount, o.Probability, o.AssignedToId, Owner = o.AssignedTo!.FullName })
            .ToListAsync();

        var byStage = Enum.GetValues<OpportunityStage>()
            .Select(stage =>
            {
                var items = rows.Where(r => r.Stage == stage).ToList();
                return new PipelineStageDto(stage, items.Count, items.Sum(i => i.Amount),
                    items.Sum(i => OpportunityRules.Weighted(i.Amount, i.Probability)));
            })
            .ToList();

        var byOwner = rows
            .GroupBy(r => new { r.AssignedToId, r.Owner })
            .Select(g => new PipelineOwnerDto(
                g.Key.AssignedToId,
                g.Key.Owner ?? "Unassigned",
                g.Count(r => r.Status == OpportunityStatus.Open),
                g.Where(r => r.Status == OpportunityStatus.Open).Sum(r => r.Amount),
                g.Where(r => r.Status == OpportunityStatus.Open).Sum(r => OpportunityRules.Weighted(r.Amount, r.Probability)),
                g.Where(r => r.Status == OpportunityStatus.Won).Sum(r => r.Amount)))
            .OrderByDescending(o => o.OpenAmount)
            .ToList();

        var open = rows.Where(r => r.Status == OpportunityStatus.Open).ToList();
        return new PipelineReportDto(byStage, byOwner, open.Sum(r => r.Amount),
            open.Sum(r => OpportunityRules.Weighted(r.Amount, r.Probability)));
    }

    public async Task<ConversionReportViewModel> ConversionAsync(ListFilter filter)
    {
        var leads = await _scope.ApplyAsync(_db.Leads.AsNoTracking().Include(l => l.AssignedTo).AsQueryable());
        var opportunities = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        if (filter.From is not null)
        {
            leads = leads.Where(l => l.CreatedDate >= filter.From.Value.Date);
            opportunities = opportunities.Where(o => o.CreatedDate >= filter.From.Value.Date);
        }
        if (filter.To is not null)
        {
            var end = filter.To.Value.Date.AddDays(1);
            leads = leads.Where(l => l.CreatedDate < end);
            opportunities = opportunities.Where(o => o.CreatedDate < end);
        }
        if (!string.IsNullOrEmpty(filter.AssignedTo))
        {
            leads = leads.Where(l => l.AssignedToId == filter.AssignedTo);
            opportunities = opportunities.Where(o => o.AssignedToId == filter.AssignedTo);
        }

        var leadRows = await leads.Select(l => new { l.Source, l.Status, Owner = l.AssignedTo!.FullName }).ToListAsync();
        var oppRows = await opportunities.Select(o => new { o.Status, o.Amount }).ToListAsync();

        ConversionReportRow Row(string group, IEnumerable<LeadStatus> statuses)
        {
            var list = statuses.ToList();
            return new ConversionReportRow
            {
                Group = group,
                TotalLeads = list.Count,
                Converted = list.Count(s => s == LeadStatus.Converted),
                Lost = list.Count(s => s is LeadStatus.Lost or LeadStatus.Unqualified),
                Open = list.Count(LeadWorkflow.IsOpen)
            };
        }

        return new ConversionReportViewModel
        {
            Filter = filter,
            BySource = leadRows.GroupBy(l => l.Source)
                .Select(g => Row(Helpers.DisplayHelpers.Label(g.Key), g.Select(x => x.Status)))
                .OrderByDescending(r => r.TotalLeads).ToList(),
            ByOwner = leadRows.GroupBy(l => l.Owner ?? "Unassigned")
                .Select(g => Row(g.Key, g.Select(x => x.Status)))
                .OrderByDescending(r => r.TotalLeads).ToList(),
            OpportunitiesWon = oppRows.Count(o => o.Status == OpportunityStatus.Won),
            OpportunitiesLost = oppRows.Count(o => o.Status == OpportunityStatus.Lost),
            WonAmount = oppRows.Where(o => o.Status == OpportunityStatus.Won).Sum(o => o.Amount),
            LostAmount = oppRows.Where(o => o.Status == OpportunityStatus.Lost).Sum(o => o.Amount)
        };
    }

    /// <summary>Action counts per user from the audit log (Admin: everyone; Manager: own team).</summary>
    public async Task<List<UserActivityRow>> UserActivityAsync(ListFilter filter)
    {
        var logs = _db.AuditLogs.AsNoTracking().AsQueryable();
        var ids = await _scope.VisibleUserIdsAsync();
        if (ids is not null)
        {
            var list = ids.ToList();
            logs = logs.Where(a => a.UserId != null && list.Contains(a.UserId));
        }
        if (filter.From is not null) logs = logs.Where(a => a.CreatedDate >= filter.From.Value.Date);
        if (filter.To is not null)
        {
            var end = filter.To.Value.Date.AddDays(1);
            logs = logs.Where(a => a.CreatedDate < end);
        }
        if (!string.IsNullOrEmpty(filter.AssignedTo)) logs = logs.Where(a => a.UserId == filter.AssignedTo);

        var rows = await logs.Where(a => a.UserId != null)
            .Select(a => new { a.UserId, a.UserName, a.Action, a.CreatedDate })
            .ToListAsync();
        var names = await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.FullName);

        var result = rows.GroupBy(r => r.UserId!)
            .Select(g => new UserActivityRow
            {
                UserId = g.Key,
                UserName = names.TryGetValue(g.Key, out var name) ? name : g.First().UserName ?? g.Key,
                Logins = g.Count(r => r.Action == AuditActions.Login),
                FailedLogins = g.Count(r => r.Action is AuditActions.FailedLogin or AuditActions.Lockout),
                Creates = g.Count(r => r.Action == AuditActions.Create),
                Updates = g.Count(r => r.Action is AuditActions.Update or AuditActions.StatusChange or AuditActions.Complete
                    or AuditActions.Reschedule or AuditActions.Convert),
                Deletes = g.Count(r => r.Action == AuditActions.Delete),
                Total = g.Count(),
                LastActivity = g.Max(r => r.CreatedDate)
            });

        return (filter.Sort switch
        {
            "name" => result.OrderBy(r => r.UserName),
            "logins" => result.OrderByDescending(r => r.Logins),
            "last" => result.OrderByDescending(r => r.LastActivity),
            _ => result.OrderByDescending(r => r.Total)
        }).ToList();
    }
}

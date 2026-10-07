using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 4 — Dashboard. Every KPI is recomputed independently from the database and
/// compared with what the dashboard reports, for each role and date range.
/// </summary>
public class DashboardTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public DashboardTests(CrmFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Runs DashboardService as the given user and returns the user's visible owner ids.</summary>
    private async Task<(DashboardViewModel Vm, HashSet<string> Owners, ApplicationDbContext Db)> DashboardAsync(
        IServiceScope scope, string email, DashboardFilter filter)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var role = (await users.GetRolesAsync(user)).Single();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.Role, role)
        }, "Test"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var userScope = new UserScope(accessor, db);

        var vm = await new DashboardService(db, userScope, users).GetAsync(filter);
        var owners = role switch
        {
            Roles.Admin => (await db.Users.Select(u => u.Id).ToListAsync()).ToHashSet(),
            Roles.Manager => (await db.Users.Where(u => u.ManagerId == user.Id).Select(u => u.Id).ToListAsync())
                .Append(user.Id).ToHashSet(),
            _ => new HashSet<string> { user.Id }
        };
        return (vm, owners, db);
    }

    [Theory]
    [InlineData("admin@acxiomcrm.local", "all")]
    [InlineData("priya.manager@acxiomcrm.local", "all")]
    [InlineData("rahul.sales@acxiomcrm.local", "all")]
    [InlineData("admin@acxiomcrm.local", "today")]
    [InlineData("admin@acxiomcrm.local", "week")]
    [InlineData("admin@acxiomcrm.local", "month")]
    [InlineData("priya.manager@acxiomcrm.local", "month")]
    public async Task Kpi_cards_match_the_database_for_each_role_and_range(string email, string range)
    {
        using var scope = _factory.Services.CreateScope();
        var filter = new DashboardFilter { Range = range };
        var (vm, owners, db) = await DashboardAsync(scope, email, filter);
        var (start, end) = filter.Resolve();
        bool InRange(DateTime? d) => d is not null && (start is null || d >= start) && (end is null || d < end);

        var customers = (await db.Customers.ToListAsync()).Where(c => owners.Contains(c.AssignedToId!) && (range == "all" || InRange(c.CreatedDate))).ToList();
        var leads = (await db.Leads.ToListAsync()).Where(l => owners.Contains(l.AssignedToId!) && (range == "all" || InRange(l.CreatedDate))).ToList();
        var allOpps = (await db.Opportunities.ToListAsync()).Where(o => owners.Contains(o.AssignedToId!)).ToList();
        var createdOpps = allOpps.Where(o => range == "all" || InRange(o.CreatedDate)).ToList();
        var closedOpps = allOpps.Where(o => o.Status != OpportunityStatus.Open && (range == "all" || InRange(o.ClosedDate))).ToList();

        Assert.Equal(customers.Count, vm.TotalCustomers);
        Assert.Equal(leads.Count, vm.TotalLeads);
        Assert.Equal(leads.Count(l => l.Status is not (LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Unqualified)), vm.OpenLeads);
        Assert.Equal(createdOpps.Count, vm.TotalOpportunities);
        Assert.Equal(createdOpps.Count(o => o.Status == OpportunityStatus.Open), vm.OpenOpportunities);
        Assert.Equal(closedOpps.Count(o => o.Status == OpportunityStatus.Won), vm.WonOpportunities);
        Assert.Equal(closedOpps.Count(o => o.Status == OpportunityStatus.Lost), vm.LostOpportunities);
        Assert.Equal(createdOpps.Where(o => o.Status == OpportunityStatus.Open).Sum(o => o.Amount), vm.TotalPipelineValue);
        Assert.Equal(closedOpps.Where(o => o.Status == OpportunityStatus.Won).Sum(o => o.Amount), vm.WonValue);
    }

    [Fact]
    public async Task Won_deals_are_counted_by_close_date_not_created_date()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // An old deal (created 6 months ago) that was won today must count as "won today".
        var old = await db.Opportunities.FirstAsync(o => o.Status == OpportunityStatus.Open && o.AssignedTo!.Email == "kiran.sales@acxiomcrm.local");
        old.CreatedDate = DateTime.Today.AddMonths(-6);
        old.Stage = OpportunityStage.Won;
        old.Status = OpportunityStatus.Won;
        old.Probability = 100;
        old.ClosedDate = DateTime.Now;
        await db.SaveChangesAsync();

        var (vm, _, _) = await DashboardAsync(scope, "admin@acxiomcrm.local", new DashboardFilter { Range = "today" });
        Assert.True(vm.WonOpportunities >= 1);
        Assert.True(vm.WonValue >= old.Amount);
    }

    [Fact]
    public async Task Required_charts_have_the_required_categories()
    {
        using var scope = _factory.Services.CreateScope();
        var (vm, _, _) = await DashboardAsync(scope, "admin@acxiomcrm.local", new DashboardFilter());

        foreach (var status in new[] { "New", "Contacted", "Qualified", "Lost", "Converted" })
        {
            Assert.Contains(status, vm.LeadStatusChart.Labels);
        }
        Assert.Equal(new[] { "Qualification", "Proposal", "Negotiation", "Won", "Lost" }, vm.PipelineChart.Labels);
        Assert.Equal(12, vm.MonthlySalesChart.Labels.Count);
        Assert.Equal(vm.MonthlySalesChart.Labels.Count, vm.MonthlySalesChart.Values.Count);
        Assert.True(vm.MonthlySalesChart.Values.Sum() > 0, "Seeded won deals should appear in monthly sales");
    }

    [Fact]
    public async Task Security_stats_and_team_table_depend_on_role()
    {
        using var scope = _factory.Services.CreateScope();
        var (admin, _, _) = await DashboardAsync(scope, "admin@acxiomcrm.local", new DashboardFilter());
        var (manager, _, _) = await DashboardAsync(scope, "priya.manager@acxiomcrm.local", new DashboardFilter());
        var (sales, _, _) = await DashboardAsync(scope, "rahul.sales@acxiomcrm.local", new DashboardFilter());

        Assert.True(admin.ShowSecurityStats);
        Assert.True(admin.TotalUsers > 0);
        Assert.False(manager.ShowSecurityStats);
        Assert.False(sales.ShowSecurityStats);

        Assert.NotEmpty(manager.Team);
        Assert.All(manager.Team, t => Assert.Contains(t.OwnerName, new[] { "Priya Sharma", "Rahul Verma", "Sneha Reddy" }));
        Assert.Empty(sales.Team);
        Assert.NotEmpty(sales.UpcomingFollowUps);
    }

    [Theory]
    [InlineData("admin@acxiomcrm.local")]
    [InlineData("priya.manager@acxiomcrm.local")]
    [InlineData("rahul.sales@acxiomcrm.local")]
    public async Task Dashboard_page_renders_cards_and_charts_for_every_role(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Account/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = email, ["Password"] = CrmFactory.Password, ["__RequestVerificationToken"] = token
        }));
        Assert.Equal("/", post.Headers.Location!.OriginalString); // lands on the dashboard after login

        foreach (var range in new[] { "", "?Range=today", "?Range=week", "?Range=month", $"?Range=custom&From={DateTime.Today.AddDays(-30):yyyy-MM-dd}&To={DateTime.Today:yyyy-MM-dd}" })
        {
            var response = await client.GetAsync("/" + range);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            foreach (var card in new[] { "Total Customers", "Total Leads", "Open Leads", "Total Opportunities", "Open Opportunities", "Won Opportunities", "Lost Opportunities", "Total Pipeline Value" })
            {
                Assert.Contains(card, html);
            }
            Assert.Contains("id=\"leadStatusChart\"", html);
            Assert.Contains("id=\"pipelineChart\"", html);
            Assert.Contains("id=\"monthlyChart\"", html);
            Assert.Contains("chart.umd.min.js", html);
        }
    }

    [Fact]
    public async Task Custom_range_with_end_before_start_shows_an_error()
    {
        var client = await _factory.LoginAsync("admin@acxiomcrm.local");
        var html = await client.GetStringAsync($"/?Range=custom&From={DateTime.Today:yyyy-MM-dd}&To={DateTime.Today.AddDays(-5):yyyy-MM-dd}");
        Assert.Contains("The end date must be on or after the start date.", html);
    }
}

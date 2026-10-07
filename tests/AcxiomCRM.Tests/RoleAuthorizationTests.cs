using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 3 — Admin / Manager / SalesExecutive. Verifies the permission matrix (spec 7.1)
/// is enforced on the server, record scope per role, and assignment rules.
/// </summary>
public class RoleAuthorizationTests : IClassFixture<CrmFactory>
{
    private const string Admin = "admin@acxiomcrm.local";
    private const string Manager = "priya.manager@acxiomcrm.local";   // team: Rahul, Sneha
    private const string OtherManager = "arjun.manager@acxiomcrm.local"; // team: Kiran
    private const string Sales = "rahul.sales@acxiomcrm.local";

    private readonly CrmFactory _factory;

    public RoleAuthorizationTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> MvcLoginAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await TokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = email,
            ["Password"] = CrmFactory.Password,
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private async Task<string> UserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(email))!.Id;
    }

    // ---------- Permission matrix (spec 7.1) ----------

    public static IEnumerable<object[]> Matrix()
    {
        // page, Admin, Manager, Sales Executive
        var rows = new (string Page, bool Admin, bool Manager, bool Sales)[]
        {
            ("/", true, true, true),
            ("/Customers", true, true, true),
            ("/Leads", true, true, true),
            ("/Opportunities", true, true, true),
            ("/FollowUps", true, true, true),
            ("/Activities", true, true, true),
            ("/Reports", true, true, true),
            ("/Reports/Pipeline", true, true, true),           // sales get a scoped view
            ("/Users", true, true, false),                     // manager: read-only team view
            ("/Users/Create", true, false, false),             // user administration: Admin only
            ("/Roles", true, false, false),                    // role management: Admin only
            ("/AuditLogs", true, true, false),                 // manager: limited view
            ("/AuditLogs?export=csv", true, false, false),     // full audit export: Admin only
            ("/Reports/UserActivity", true, true, false),
        };
        foreach (var r in rows)
        {
            yield return new object[] { Admin, r.Page, r.Admin };
            yield return new object[] { Manager, r.Page, r.Manager };
            yield return new object[] { Sales, r.Page, r.Sales };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Permission_matrix_is_enforced_on_the_server(string user, string page, bool allowed)
    {
        var client = await MvcLoginAsync(user);
        var response = await client.GetAsync(page);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Menu_items_match_the_role()
    {
        var admin = await (await MvcLoginAsync(Admin)).GetStringAsync("/");
        Assert.Contains("Roles &amp; Permissions", admin);
        Assert.Contains("Audit Log", admin);

        var manager = await (await MvcLoginAsync(Manager)).GetStringAsync("/");
        Assert.Contains("My Team", manager);
        Assert.Contains("Audit Log", manager);
        Assert.DoesNotContain("Roles &amp; Permissions", manager);

        var sales = await (await MvcLoginAsync(Sales)).GetStringAsync("/");
        Assert.DoesNotContain("Audit Log", sales);
        Assert.DoesNotContain("My Team", sales);
        Assert.DoesNotContain("Roles &amp; Permissions", sales);
    }

    // ---------- Record scope ----------

    private static readonly string[] PriyaTeam = { "Priya Sharma", "Rahul Verma", "Sneha Reddy" };

    [Theory]
    [InlineData("/api/customers?pageSize=100")]
    [InlineData("/api/leads?pageSize=100")]
    [InlineData("/api/opportunities?pageSize=100")]
    [InlineData("/api/followups?view=all&pageSize=100")]
    public async Task Each_role_only_sees_records_in_its_scope(string url)
    {
        async Task<HashSet<string>> OwnersAsync(string user)
        {
            var client = await _factory.LoginAsync(user);
            var body = await client.GetFromJsonAsync<JsonElement>(url);
            return body.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("assignedToName").GetString()!)
                .ToHashSet();
        }

        var sales = await OwnersAsync(Sales);
        Assert.NotEmpty(sales);
        Assert.All(sales, owner => Assert.Equal("Rahul Verma", owner));

        var manager = await OwnersAsync(Manager);
        Assert.All(manager, owner => Assert.Contains(owner, PriyaTeam));
        Assert.True(manager.Count > 1, "Manager should see team members' records, not just their own");

        var admin = await OwnersAsync(Admin);
        Assert.Contains("Kiran Kumar", admin); // another team's records
    }

    [Fact]
    public async Task Manager_cannot_open_or_change_another_teams_record()
    {
        var admin = await _factory.LoginAsync(Admin);
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/customers?pageSize=100");
        var foreign = all.GetProperty("items").EnumerateArray()
            .First(c => c.GetProperty("assignedToName").GetString() == "Kiran Kumar");
        var id = foreign.GetProperty("customerId").GetInt32();

        var manager = await _factory.LoginAsync(Manager);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/customers/{id}")).StatusCode);

        var update = await manager.PutAsJsonAsync($"/api/customers/{id}", new
        {
            customerName = "Hijacked", email = "hijack@example.com", phone = "9000022222", status = "Active"
        });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var mvc = await MvcLoginAsync(Manager);
        Assert.Equal(HttpStatusCode.NotFound, (await mvc.GetAsync($"/Customers/Edit/{id}")).StatusCode);
    }

    // ---------- Assignment rules ----------

    [Fact]
    public async Task Manager_can_assign_to_own_team_but_not_to_another_team()
    {
        var manager = await _factory.LoginAsync(Manager);

        var toTeam = await manager.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Team Assigned", email = "team.assigned@example.com", phone = "9000033333",
            status = "Active", assignedToId = await UserIdAsync("sneha.sales@acxiomcrm.local")
        });
        Assert.Equal(HttpStatusCode.Created, toTeam.StatusCode);

        var toOtherTeam = await manager.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Other Team", email = "other.team@example.com", phone = "9000044444",
            status = "Active", assignedToId = await UserIdAsync("kiran.sales@acxiomcrm.local")
        });
        Assert.Equal(HttpStatusCode.BadRequest, toOtherTeam.StatusCode);
        var errors = (await toOtherTeam.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("AssignedToId", out _));
    }

    [Fact]
    public async Task Sales_executive_records_are_always_assigned_to_themselves()
    {
        var sales = await _factory.LoginAsync(Sales);
        var response = await sales.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Self Owned", email = "self.owned@example.com", phone = "9000055555",
            status = "Active", assignedToId = await UserIdAsync("sneha.sales@acxiomcrm.local") // tampered
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(await UserIdAsync(Sales), created.GetProperty("assignedToId").GetString());
    }

    [Fact]
    public async Task Manager_audit_view_hides_security_events()
    {
        var html = await (await MvcLoginAsync(Manager)).GetStringAsync("/AuditLogs");
        Assert.Contains("Limited view", html);
        Assert.DoesNotContain("badge text-bg-light border\">Login<", html);

        var adminHtml = await (await MvcLoginAsync(Admin)).GetStringAsync("/AuditLogs?eventAction=Login");
        Assert.Contains("badge text-bg-light border\">Login<", adminHtml);
    }

    // ---------- Role administration ----------

    [Fact]
    public async Task Each_user_has_exactly_one_role()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var user in users.Users.ToList())
        {
            Assert.Single(await users.GetRolesAsync(user));
        }
    }

    [Fact]
    public async Task Demoting_a_manager_releases_their_team()
    {
        var arjunId = await UserIdAsync(OtherManager);
        var admin = await MvcLoginAsync(Admin);
        var token = await TokenAsync(admin, $"/Users/Edit/{arjunId}");

        var response = await admin.PostAsync($"/Users/Edit/{arjunId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FullName"] = "Arjun Mehta",
            ["Email"] = OtherManager,
            ["Role"] = Roles.SalesExecutive,
            ["IsActive"] = "true",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var arjun = (await users.FindByIdAsync(arjunId))!;
        Assert.Equal(new[] { Roles.SalesExecutive }, await users.GetRolesAsync(arjun));

        var kiran = (await users.FindByEmailAsync("kiran.sales@acxiomcrm.local"))!;
        Assert.Null(kiran.ManagerId);
    }

    [Fact]
    public async Task Admin_cannot_remove_their_own_admin_role()
    {
        var adminId = await UserIdAsync(Admin);
        var admin = await MvcLoginAsync(Admin);
        var token = await TokenAsync(admin, $"/Users/Edit/{adminId}");

        var response = await admin.PostAsync($"/Users/Edit/{adminId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FullName"] = "System Administrator",
            ["Email"] = Admin,
            ["Role"] = Roles.Manager,
            ["IsActive"] = "true",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form re-displayed with an error
        Assert.Contains("You cannot remove your own Admin role", await response.Content.ReadAsStringAsync());
    }
}

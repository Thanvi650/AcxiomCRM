using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 9 — Activity Management (Call, Meeting, Email, Task) through the real MVC pages:
/// logging, validation, completing tasks, edit/delete, scope, search and filters (17.13).
/// </summary>
public class ActivityTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public ActivityTests(CrmFactory factory)
    {
        _factory = factory;
    }

    // ---------- helpers ----------

    private async Task<HttpClient> LoginAsync(string email = "rahul.sales@acxiomcrm.local")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await TokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", Form(token, ("Login", email), ("Password", CrmFactory.Password)));
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token)));

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<int> CustomerOfAsync(string email) =>
        WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == email).Select(c => c.CustomerId).FirstAsync());

    private static (string, string)[] Fields(string subject, string type = "Call", string status = "Completed",
        DateTime? date = null, int? customerId = null, int? leadId = null) =>
        new[]
        {
            ("ActivityType", type), ("Subject", subject), ("Description", "Notes"),
            ("ActivityDate", (date ?? DateTime.Today.AddHours(9)).ToString("yyyy-MM-ddTHH:mm")),
            ("Status", status), ("CustomerId", customerId?.ToString() ?? ""), ("LeadId", leadId?.ToString() ?? "")
        };

    private async Task<HttpResponseMessage> LogAsync(HttpClient client, (string, string)[] fields)
    {
        var token = await TokenAsync(client, "/Activities/Create");
        return await client.PostAsync("/Activities/Create", Form(token, fields));
    }

    private async Task<int> LogActivityAsync(HttpClient client, string subject, string type = "Call", string status = "Completed", DateTime? date = null)
    {
        var response = await LogAsync(client, Fields(subject, type, status, date, await CustomerOfAsync("rahul.sales@acxiomcrm.local")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await WithDbAsync(db => db.Activities.Where(a => a.Subject == subject).Select(a => a.ActivityId).SingleAsync());
    }

    private Task<Activity> ActivityAsync(int id) => WithDbAsync(db => db.Activities.AsNoTracking().SingleAsync(a => a.ActivityId == id));

    // ---------- logging (17.2: Call, Meeting, Email, Task) ----------

    [Theory]
    [InlineData("Call")]
    [InlineData("Meeting")]
    [InlineData("Email")]
    [InlineData("Task")]
    public async Task Each_activity_type_can_be_logged_and_is_audited(string type)
    {
        var client = await LoginAsync();
        var id = await LogActivityAsync(client, $"Logged {type}", type);

        var activity = await ActivityAsync(id);
        Assert.Equal(Enum.Parse<ActivityType>(type), activity.ActivityType);
        Assert.Equal(ActivityStatus.Completed, activity.Status);
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Activity" && a.Action == "Create" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Internal_task_without_a_customer_or_lead_is_allowed()
    {
        var client = await LoginAsync();
        var response = await LogAsync(client, Fields("Prepare quarterly forecast", "Task", "Planned", DateTime.Today.AddDays(2)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    // ---------- validation ----------

    [Theory]
    [InlineData("", "Call", "Completed", 0, "Subject is required.")]
    [InlineData("Hi", "Call", "Completed", 0, "Subject must be between 3 and 150 characters.")]
    [InlineData("Valid subject", "Fax", "Completed", 0, "'Fax' is not a valid value for Type.")]
    [InlineData("Valid subject", "Call", "Planned", -1, "A planned activity cannot be dated in the past.")]
    [InlineData("Valid subject", "Call", "Completed", 2, "A completed activity cannot be dated in the future.")]
    public async Task Invalid_activities_are_rejected(string subject, string type, string status, int days, string message)
    {
        var client = await LoginAsync();
        var before = await WithDbAsync(db => db.Activities.CountAsync());

        var response = await LogAsync(client, Fields(subject, type, status, DateTime.Today.AddDays(days).AddHours(10)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(before, await WithDbAsync(db => db.Activities.CountAsync()));
    }

    [Fact]
    public async Task Activity_for_another_teams_customer_or_lead_is_rejected()
    {
        var client = await LoginAsync();
        var kiransCustomer = await CustomerOfAsync("kiran.sales@acxiomcrm.local");
        var kiransLead = await WithDbAsync(db => db.Leads.Where(l => l.AssignedTo!.Email == "kiran.sales@acxiomcrm.local").Select(l => l.LeadId).FirstAsync());

        var customer = await LogAsync(client, Fields("Foreign customer", customerId: kiransCustomer));
        Assert.Contains("Select a valid customer.", await customer.Content.ReadAsStringAsync());

        var lead = await LogAsync(client, Fields("Foreign lead", leadId: kiransLead));
        Assert.Contains("Select a valid lead.", await lead.Content.ReadAsStringAsync());
    }

    // ---------- complete, edit, delete ----------

    [Fact]
    public async Task Planned_task_can_be_completed_once()
    {
        var client = await LoginAsync();
        var id = await LogActivityAsync(client, "Send brochure", "Task", "Planned", DateTime.Today.AddDays(1).AddHours(10));

        var token = await TokenAsync(client, "/Activities");
        await client.PostAsync($"/Activities/Complete/{id}", Form(token, ("returnUrl", "/Activities")));

        var activity = await ActivityAsync(id);
        Assert.Equal(ActivityStatus.Completed, activity.Status);
        Assert.True(activity.ActivityDate <= DateTime.Now); // finished early -> recorded as done now
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Activity" && a.Action == "Complete" && a.RecordId == id.ToString())));

        token = await TokenAsync(client, "/Activities");
        var again = await client.PostAsync($"/Activities/Complete/{id}", Form(token, ("returnUrl", "/Activities")));
        Assert.Contains("Only planned activities can be completed.", await client.GetStringAsync(again.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task Edit_and_delete_are_saved_and_audited()
    {
        var client = await LoginAsync();
        var id = await LogActivityAsync(client, "Editable call");

        var token = await TokenAsync(client, $"/Activities/Edit/{id}");
        var edit = await client.PostAsync($"/Activities/Edit/{id}", Form(token, Fields("Edited call subject", "Meeting", customerId: await CustomerOfAsync("rahul.sales@acxiomcrm.local"))));
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        var activity = await ActivityAsync(id);
        Assert.Equal("Edited call subject", activity.Subject);
        Assert.Equal(ActivityType.Meeting, activity.ActivityType);

        token = await TokenAsync(client, "/Activities");
        await client.PostAsync($"/Activities/Delete/{id}", Form(token));
        Assert.False(await WithDbAsync(db => db.Activities.AnyAsync(a => a.ActivityId == id)));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Activity" && a.Action == "Update" && a.RecordId == id.ToString())));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Activity" && a.Action == "Delete" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Activities_appear_on_the_customer_page()
    {
        var client = await LoginAsync();
        await LogActivityAsync(client, "Visible on customer page", "Meeting");
        var customerId = await CustomerOfAsync("rahul.sales@acxiomcrm.local");
        Assert.Contains("Visible on customer page", await client.GetStringAsync($"/Customers/Details/{customerId}"));
    }

    // ---------- scope, search and filters (17.13: type, date, status, assigned user) ----------

    [Fact]
    public async Task Sales_executive_only_sees_own_activities()
    {
        var client = await LoginAsync();
        var html = await client.GetStringAsync("/Activities");
        Assert.Contains("Pricing discussion", html);          // Rahul's
        Assert.DoesNotContain("Requirements workshop", html); // Sneha's
        Assert.DoesNotContain("Shared case studies", html);   // Kiran's
    }

    [Fact]
    public async Task Activities_filter_by_type_status_user_date_and_search()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");

        var meetings = await client.GetStringAsync("/Activities?type=Meeting");
        Assert.Contains("Requirements workshop", meetings);
        Assert.DoesNotContain("Shared case studies", meetings); // email

        var planned = await client.GetStringAsync("/Activities?status=Planned");
        Assert.Contains("Prepare phase-2 estimate", planned);
        Assert.DoesNotContain("Pricing discussion", planned);   // completed

        var snehaId = await WithDbAsync(db => db.Users.Where(u => u.Email == "sneha.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var sneha = await client.GetStringAsync($"/Activities?assignedTo={snehaId}");
        Assert.Contains("Requirements workshop", sneha);
        Assert.DoesNotContain("Pricing discussion", sneha);

        // Seeded: "Pricing discussion" 3 days ago, "Requirements workshop" 7 days ago.
        var from = DateTime.Today.AddDays(-4).ToString("yyyy-MM-dd");
        var to = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd");
        var range = await client.GetStringAsync($"/Activities?from={from}&to={to}");
        Assert.Contains("Pricing discussion", range);
        Assert.DoesNotContain("Requirements workshop", range);

        var search = await client.GetStringAsync("/Activities?q=Vikram");
        Assert.Contains("Pricing discussion", search);          // related customer name
    }
}

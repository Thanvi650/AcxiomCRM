using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 8 — Follow-Up Management through the real MVC pages: schedule, date rule (17.7),
/// related records, complete / missed / reschedule / cancel, views and filters (17.13),
/// reminders, audit.
/// </summary>
public class FollowUpTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public FollowUpTests(CrmFactory factory)
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

    private static string When(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm");

    private Task<int> RahulsCustomerAsync(int skip = 0) =>
        WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == "rahul.sales@acxiomcrm.local")
            .OrderBy(c => c.CustomerId).Skip(skip).Select(c => c.CustomerId).FirstAsync());

    private async Task<HttpResponseMessage> ScheduleAsync(HttpClient client, DateTime date, int? customerId = null,
        int? leadId = null, int? opportunityId = null, string subject = "Follow-up call")
    {
        var token = await TokenAsync(client, "/FollowUps/Create");
        return await client.PostAsync("/FollowUps/Create", Form(token,
            ("Subject", subject), ("FollowUpType", "Call"), ("FollowUpDate", When(date)),
            ("CustomerId", customerId?.ToString() ?? ""), ("LeadId", leadId?.ToString() ?? ""),
            ("OpportunityId", opportunityId?.ToString() ?? ""), ("Remarks", "")));
    }

    private async Task<int> ScheduleForCustomerAsync(HttpClient client, string subject, DateTime? date = null)
    {
        var response = await ScheduleAsync(client, date ?? DateTime.Today.AddDays(1).AddHours(10), await RahulsCustomerAsync(), subject: subject);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await WithDbAsync(db => db.FollowUps.Where(f => f.Subject == subject).Select(f => f.FollowUpId).SingleAsync());
    }

    private async Task<HttpResponseMessage> ActionAsync(HttpClient client, string action, int id, params (string, string)[] extra)
    {
        var token = await TokenAsync(client, "/FollowUps?view=all");
        return await client.PostAsync($"/FollowUps/{action}/{id}", Form(token, extra.Append(("returnUrl", "/FollowUps?view=all")).ToArray()));
    }

    private Task<FollowUp> FollowUpAsync(int id) => WithDbAsync(db => db.FollowUps.AsNoTracking().SingleAsync(f => f.FollowUpId == id));

    // ---------- schedule & date rule ----------

    [Fact]
    public async Task Scheduling_creates_a_planned_follow_up_and_audits_it()
    {
        var client = await LoginAsync();
        var id = await ScheduleForCustomerAsync(client, "Kick-off call");

        var f = await FollowUpAsync(id);
        Assert.Equal(FollowUpStatus.Planned, f.Status);
        Assert.Equal(await WithDbAsync(db => db.Users.Where(u => u.Email == "rahul.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync()), f.AssignedToId);
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "FollowUp" && a.Action == "Create" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Follow_up_date_before_today_is_rejected_but_today_is_allowed()
    {
        var client = await LoginAsync();
        var yesterday = await ScheduleAsync(client, DateTime.Today.AddDays(-1).AddHours(10), await RahulsCustomerAsync(), subject: "Yesterday");
        Assert.Equal(HttpStatusCode.OK, yesterday.StatusCode);
        Assert.Contains("Follow-up date cannot be earlier than today.", await yesterday.Content.ReadAsStringAsync());

        var earlyToday = await ScheduleAsync(client, DateTime.Today.AddMinutes(1), await RahulsCustomerAsync(), subject: "Early today");
        Assert.Equal(HttpStatusCode.Redirect, earlyToday.StatusCode);
    }

    [Fact]
    public async Task Follow_up_must_be_linked_to_a_record_in_scope()
    {
        var client = await LoginAsync();
        var unlinked = await ScheduleAsync(client, DateTime.Today.AddDays(1), subject: "Unlinked");
        Assert.Contains("Link the follow-up to a customer, lead or opportunity.", await unlinked.Content.ReadAsStringAsync());

        var kiransCustomer = await WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == "kiran.sales@acxiomcrm.local").Select(c => c.CustomerId).FirstAsync());
        var foreign = await ScheduleAsync(client, DateTime.Today.AddDays(1), kiransCustomer, subject: "Foreign");
        Assert.Contains("Select a valid customer.", await foreign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Opportunity_follow_up_is_linked_to_that_opportunitys_customer()
    {
        var client = await LoginAsync();
        var opp = await WithDbAsync(db => db.Opportunities.Where(o => o.AssignedTo!.Email == "rahul.sales@acxiomcrm.local" && o.Status == OpportunityStatus.Open)
            .Select(o => new { o.OpportunityId, o.CustomerId }).FirstAsync());

        var ok = await ScheduleAsync(client, DateTime.Today.AddDays(2), opportunityId: opp.OpportunityId, subject: "Opp only");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(opp.CustomerId, await WithDbAsync(db => db.FollowUps.Where(f => f.Subject == "Opp only").Select(f => f.CustomerId).SingleAsync()));

        var otherCustomer = await WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == "rahul.sales@acxiomcrm.local" && c.CustomerId != opp.CustomerId).Select(c => c.CustomerId).FirstAsync());
        var mismatch = await ScheduleAsync(client, DateTime.Today.AddDays(2), otherCustomer, opportunityId: opp.OpportunityId, subject: "Mismatch");
        Assert.Contains("The selected opportunity belongs to a different customer.", await mismatch.Content.ReadAsStringAsync());
    }

    // ---------- complete / missed / reschedule / cancel ----------

    [Fact]
    public async Task Completing_records_the_outcome_and_advances_a_new_lead()
    {
        var client = await LoginAsync();
        var leadId = await WithDbAsync(async db =>
        {
            var rahul = await db.Users.SingleAsync(u => u.Email == "rahul.sales@acxiomcrm.local");
            var lead = new Lead { LeadCode = $"T-{Guid.NewGuid():N}"[..12], LeadName = "Brand New Lead", Source = LeadSource.Website, Status = LeadStatus.New, AssignedToId = rahul.Id };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            return lead.LeadId;
        });
        await ScheduleAsync(client, DateTime.Today.AddHours(18), leadId: leadId, subject: "First contact");
        var id = await WithDbAsync(db => db.FollowUps.Where(f => f.Subject == "First contact").Select(f => f.FollowUpId).SingleAsync());

        await ActionAsync(client, "Complete", id, ("remarks", "Spoke to the buyer"));

        var f = await FollowUpAsync(id);
        Assert.Equal(FollowUpStatus.Completed, f.Status);
        Assert.NotNull(f.CompletedDate);
        Assert.Contains("Spoke to the buyer", f.Remarks);
        Assert.Equal(LeadStatus.Contacted, await WithDbAsync(db => db.Leads.Where(l => l.LeadId == leadId).Select(l => l.Status).SingleAsync()));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "FollowUp" && a.Action == "Complete" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Missed_follow_up_can_be_rescheduled_to_a_future_date_only()
    {
        var client = await LoginAsync();
        var id = await ScheduleForCustomerAsync(client, "Missed then moved");
        await ActionAsync(client, "Missed", id);
        Assert.Equal(FollowUpStatus.Missed, (await FollowUpAsync(id)).Status);

        var token = await TokenAsync(client, $"/FollowUps/Reschedule/{id}");
        var past = await client.PostAsync($"/FollowUps/Reschedule/{id}", Form(token, ("NewDate", When(DateTime.Today.AddDays(-1).AddHours(9))), ("Remarks", "")));
        Assert.Contains("Follow-up date cannot be earlier than today.", await past.Content.ReadAsStringAsync());

        var newDate = DateTime.Today.AddDays(3).AddHours(11);
        token = await TokenAsync(client, $"/FollowUps/Reschedule/{id}");
        var ok = await client.PostAsync($"/FollowUps/Reschedule/{id}", Form(token, ("NewDate", When(newDate)), ("Remarks", "Customer travelling")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var f = await FollowUpAsync(id);
        Assert.Equal(FollowUpStatus.Planned, f.Status);
        Assert.Equal(newDate, f.FollowUpDate);
        var audit = await WithDbAsync(db => db.AuditLogs.SingleAsync(a => a.EntityName == "FollowUp" && a.Action == "Reschedule" && a.RecordId == id.ToString()));
        Assert.Contains("Missed", audit.OldValue);
    }

    [Fact]
    public async Task Finished_follow_ups_cannot_change_status_or_be_edited()
    {
        var client = await LoginAsync();
        var id = await ScheduleForCustomerAsync(client, "Done deal call");
        await ActionAsync(client, "Complete", id);

        var missed = await ActionAsync(client, "Missed", id);
        var page = await client.GetStringAsync(missed.Headers.Location!.OriginalString);
        Assert.Contains("A Completed follow-up cannot be marked Missed.", page);
        Assert.Equal(FollowUpStatus.Completed, (await FollowUpAsync(id)).Status);

        var edit = await client.GetAsync($"/FollowUps/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
    }

    [Fact]
    public async Task Cancel_and_delete_work_and_are_audited()
    {
        var client = await LoginAsync();
        var cancelId = await ScheduleForCustomerAsync(client, "To cancel");
        await ActionAsync(client, "Cancel", cancelId);
        Assert.Equal(FollowUpStatus.Cancelled, (await FollowUpAsync(cancelId)).Status);

        var deleteId = await ScheduleForCustomerAsync(client, "To delete");
        var token = await TokenAsync(client, "/FollowUps");
        await client.PostAsync($"/FollowUps/Delete/{deleteId}", Form(token));
        Assert.False(await WithDbAsync(db => db.FollowUps.AnyAsync(f => f.FollowUpId == deleteId)));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "FollowUp" && a.Action == "Delete" && a.RecordId == deleteId.ToString())));
    }

    // ---------- views, filters, reminders ----------

    [Fact]
    public async Task Overdue_today_and_upcoming_views_show_the_right_items()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");

        var overdue = await client.GetStringAsync("/FollowUps?view=overdue");
        Assert.Contains("Send proposal revision", overdue);    // seeded 2 days ago, still planned
        Assert.DoesNotContain("Intro call", overdue);          // in 2 days

        var today = await client.GetStringAsync("/FollowUps?view=today");
        Assert.Contains("Call to discuss pricing", today);     // seeded for today
        Assert.DoesNotContain("Send proposal revision", today);

        var upcoming = await client.GetStringAsync("/FollowUps?view=upcoming");
        Assert.Contains("Intro call", upcoming);
        Assert.DoesNotContain("Send proposal revision", upcoming);
        Assert.DoesNotContain("Kick-off review", upcoming);    // completed
    }

    [Fact]
    public async Task Follow_ups_filter_by_status_user_date_and_related_record()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");

        var completed = await client.GetStringAsync("/FollowUps?view=all&status=Completed");
        Assert.Contains("Kick-off review", completed);
        Assert.DoesNotContain("Intro call", completed);

        var snehaId = await WithDbAsync(db => db.Users.Where(u => u.Email == "sneha.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var sneha = await client.GetStringAsync($"/FollowUps?view=all&assignedTo={snehaId}");
        Assert.Contains("Intro call", sneha);
        Assert.DoesNotContain("Site visit", sneha);            // Kiran's

        var from = DateTime.Today.AddDays(3).ToString("yyyy-MM-dd");
        var to = DateTime.Today.AddDays(4).ToString("yyyy-MM-dd");
        var range = await client.GetStringAsync($"/FollowUps?view=all&from={from}&to={to}");
        Assert.Contains("Site visit", range);                  // in 4 days
        Assert.DoesNotContain("Intro call", range);            // in 2 days

        var byCustomer = await client.GetStringAsync("/FollowUps?view=all&q=Fatima");
        Assert.Contains("Contract negotiation", byCustomer);
        var byLead = await client.GetStringAsync("/FollowUps?view=all&q=Sanjana");
        Assert.Contains("Intro call", byLead);
    }

    [Fact]
    public async Task Reminder_bell_lists_own_due_items_and_overdue_links_to_reschedule()
    {
        var client = await LoginAsync("sneha.sales@acxiomcrm.local");
        var html = await client.GetStringAsync("/");
        var bell = Regex.Match(html, "notif-menu[\\s\\S]*?View all follow-ups").Value;

        Assert.Contains("Send proposal revision", bell);       // Sneha's, overdue
        Assert.Contains("Overdue", bell);
        Assert.Matches("/FollowUps/Reschedule/\\d+", bell);
        Assert.DoesNotContain("Contract negotiation", bell);   // Kiran's
    }
}

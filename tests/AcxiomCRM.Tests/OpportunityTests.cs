using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 7 — Opportunity Management through the real MVC pages: business rules (17.7),
/// pipeline stages, Won/Lost outcome capture, search/filter (17.13), pipeline board, delete.
/// </summary>
public class OpportunityTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public OpportunityTests(CrmFactory factory)
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

    private Task<int> RahulsCustomerAsync() =>
        WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == "rahul.sales@acxiomcrm.local").Select(c => c.CustomerId).FirstAsync());

    private static (string, string)[] Fields(int customerId, string name = "New deal", string amount = "100000",
        string probability = "40", int closeInDays = 30, string stage = "Proposal", string outcome = "") =>
        new[]
        {
            ("OpportunityName", name), ("CustomerId", customerId.ToString()), ("LeadId", ""), ("Amount", amount),
            ("Probability", probability), ("ExpectedCloseDate", DateTime.Today.AddDays(closeInDays).ToString("yyyy-MM-dd")),
            ("Stage", stage), ("Source", "Referral"), ("Notes", ""), ("OutcomeNotes", outcome)
        };

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, (string, string)[] fields)
    {
        var token = await TokenAsync(client, "/Opportunities/Create");
        return await client.PostAsync("/Opportunities/Create", Form(token, fields));
    }

    private async Task<int> CreateOpportunityAsync(HttpClient client, string name, string stage = "Proposal")
    {
        var response = await CreateAsync(client, Fields(await RahulsCustomerAsync(), name, stage: stage));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private async Task<HttpResponseMessage> ChangeStageAsync(HttpClient client, int id, string stage, string? outcome = null)
    {
        var token = await TokenAsync(client, $"/Opportunities/Details/{id}");
        var fields = new List<(string, string)> { ("stage", stage), ("returnUrl", $"/Opportunities/Details/{id}") };
        if (outcome is not null) fields.Add(("outcomeNotes", outcome));
        return await client.PostAsync($"/Opportunities/ChangeStage/{id}", Form(token, fields.ToArray()));
    }

    private Task<Opportunity> OpportunityAsync(int id) =>
        WithDbAsync(db => db.Opportunities.AsNoTracking().SingleAsync(o => o.OpportunityId == id));

    // ---------- create & business rules (17.7) ----------

    [Fact]
    public async Task Create_saves_opportunity_with_weighted_value_and_audit()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Weighted Deal");

        var o = await OpportunityAsync(id);
        Assert.Equal(OpportunityStatus.Open, o.Status);
        Assert.Equal(40000m, o.WeightedAmount);   // 100,000 × 40 / 100
        Assert.Null(o.ClosedDate);
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Opportunity" && a.Action == "Create" && a.RecordId == id.ToString())));
    }

    [Theory]
    [InlineData("0", "40", 30, "Proposal", "Opportunity Amount must be greater than 0.")]
    [InlineData("-10", "40", 30, "Proposal", "Opportunity Amount cannot be negative.")]
    [InlineData("-10", "0", 30, "Lost", "Opportunity Amount cannot be negative.")]
    [InlineData("1000", "101", 30, "Proposal", "Probability must be between 0 and 100.")]
    [InlineData("1000", "-1", 30, "Proposal", "Probability must be between 0 and 100.")]
    [InlineData("1000", "40", -1, "Negotiation", "Expected Close Date cannot be in the past.")]
    public async Task Create_enforces_business_rules(string amount, string probability, int closeInDays, string stage, string message)
    {
        var client = await LoginAsync();
        var before = await WithDbAsync(db => db.Opportunities.CountAsync());

        var response = await CreateAsync(client, Fields(await RahulsCustomerAsync(), "Rule check", amount, probability, closeInDays, stage, "n/a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await WithDbAsync(db => db.Opportunities.CountAsync()));
    }

    [Fact]
    public async Task Closed_opportunities_may_have_past_date_and_zero_amount()
    {
        var client = await LoginAsync();
        var response = await CreateAsync(client, Fields(await RahulsCustomerAsync(), "Historic loss", "0", "0", -20, "Lost", "Budget cancelled"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Customer_from_another_team_is_rejected()
    {
        var kiransCustomer = await WithDbAsync(db => db.Customers.Where(c => c.AssignedTo!.Email == "kiran.sales@acxiomcrm.local").Select(c => c.CustomerId).FirstAsync());
        var client = await LoginAsync();
        var response = await CreateAsync(client, Fields(kiransCustomer, "Poaching"));
        Assert.Contains("Select a valid customer.", await response.Content.ReadAsStringAsync());
    }

    // ---------- pipeline stages & outcome (workflow 8) ----------

    [Fact]
    public async Task Moving_through_stages_is_saved_and_audited()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Stage Walker", "Qualification");

        await ChangeStageAsync(client, id, "Proposal");
        await ChangeStageAsync(client, id, "Negotiation");

        Assert.Equal(OpportunityStage.Negotiation, (await OpportunityAsync(id)).Stage);
        Assert.Equal(2, await WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityName == "Opportunity" && a.Action == "StatusChange" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Marking_won_captures_the_final_outcome()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Winner");

        await ChangeStageAsync(client, id, "Won", "Signed 2-year contract");

        var o = await OpportunityAsync(id);
        Assert.Equal(OpportunityStatus.Won, o.Status);
        Assert.Equal(100, o.Probability);
        Assert.NotNull(o.ClosedDate);
        Assert.Equal("Signed 2-year contract", o.OutcomeNotes);
        Assert.Contains("Signed 2-year contract", await client.GetStringAsync($"/Opportunities/Details/{id}"));
    }

    [Fact]
    public async Task Marking_lost_requires_a_reason()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Loser");

        var noReason = await ChangeStageAsync(client, id, "Lost");
        Assert.Equal(OpportunityStatus.Open, (await OpportunityAsync(id)).Status);
        var page = await client.GetStringAsync(noReason.Headers.Location!.OriginalString);
        Assert.Contains("Enter the reason the opportunity was lost.", page);

        await ChangeStageAsync(client, id, "Lost", "Went with a competitor");
        var o = await OpportunityAsync(id);
        Assert.Equal(OpportunityStatus.Lost, o.Status);
        Assert.Equal(0, o.Probability);
        Assert.NotNull(o.ClosedDate);
        Assert.Equal("Went with a competitor", o.OutcomeNotes);
    }

    [Fact]
    public async Task Edit_form_requires_reason_when_stage_is_lost()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Edit to lost");
        var token = await TokenAsync(client, $"/Opportunities/Edit/{id}");

        var response = await client.PostAsync($"/Opportunities/Edit/{id}", Form(token, Fields(await RahulsCustomerAsync(), "Edit to lost", stage: "Lost")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter the reason the opportunity was lost.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reopening_a_closed_deal_clears_the_close_date()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Reopened");
        await ChangeStageAsync(client, id, "Won");
        Assert.NotNull((await OpportunityAsync(id)).ClosedDate);

        await ChangeStageAsync(client, id, "Negotiation");
        var o = await OpportunityAsync(id);
        Assert.Equal(OpportunityStatus.Open, o.Status);
        Assert.Null(o.ClosedDate);
    }

    [Fact]
    public async Task Reopening_with_a_past_close_date_is_rejected()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Stale reopen");
        await ChangeStageAsync(client, id, "Won");
        await WithDbAsync(async db =>
        {
            var o = await db.Opportunities.SingleAsync(x => x.OpportunityId == id);
            o.ExpectedCloseDate = DateTime.Today.AddDays(-10);
            return await db.SaveChangesAsync();
        });

        var response = await ChangeStageAsync(client, id, "Proposal");
        var page = await client.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Equal(OpportunityStatus.Won, (await OpportunityAsync(id)).Status);
        Assert.Contains("Expected Close Date cannot be in the past.", page);
    }

    [Fact]
    public async Task Api_requires_a_reason_for_lost_opportunities()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var response = await api.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "API lost", customerId = await RahulsCustomerAsync(), amount = 0, probability = 0,
            expectedCloseDate = DateTime.Today.ToString("yyyy-MM-dd"), stage = "Lost"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("OutcomeNotes", out _));
    }

    // ---------- list, search, board, delete ----------

    [Fact]
    public async Task Opportunities_can_be_searched_by_name_customer_stage_and_status()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");

        var byName = await client.GetStringAsync("/Opportunities?q=fleet");
        Assert.Contains("Singh Logistics – fleet tracking", byName);
        Assert.DoesNotContain("Khan Pharma – compliance suite", byName);

        var byCustomer = await client.GetStringAsync("/Opportunities?q=Fatima");
        Assert.Contains("Khan Pharma – compliance suite", byCustomer);

        var negotiation = await client.GetStringAsync("/Opportunities?type=Negotiation&q=warehouse");
        Assert.Contains("Singh Logistics – warehouse module", negotiation);
        Assert.DoesNotContain("Rao Constructions – phase 2", await client.GetStringAsync("/Opportunities?type=Negotiation&q=Rao"));

        var lost = await client.GetStringAsync("/Opportunities?status=Lost&q=POS");
        Assert.Contains("Gupta Electronics – POS upgrade", lost);
    }

    [Fact]
    public async Task Pipeline_board_shows_all_five_stages()
    {
        var client = await LoginAsync();
        var html = await client.GetStringAsync("/Opportunities/Pipeline");
        foreach (var stage in new[] { "Qualification", "Proposal", "Negotiation", "Won", "Lost" })
        {
            Assert.Contains($"aria-label=\"{stage}\"", html);
        }
    }

    [Fact]
    public async Task Delete_removes_opportunity_and_its_follow_ups()
    {
        var client = await LoginAsync();
        var id = await CreateOpportunityAsync(client, "Temporary deal");
        await WithDbAsync(async db =>
        {
            var o = await db.Opportunities.SingleAsync(x => x.OpportunityId == id);
            db.FollowUps.Add(new FollowUp { OpportunityId = id, Subject = "Check in", FollowUpType = FollowUpType.Call, FollowUpDate = DateTime.Today.AddDays(1), AssignedToId = o.AssignedToId });
            return await db.SaveChangesAsync();
        });

        var token = await TokenAsync(client, $"/Opportunities/Details/{id}");
        var response = await client.PostAsync($"/Opportunities/Delete/{id}", Form(token));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await WithDbAsync(db => db.Opportunities.AnyAsync(o => o.OpportunityId == id)));
        Assert.False(await WithDbAsync(db => db.FollowUps.AnyAsync(f => f.OpportunityId == id)));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Opportunity" && a.Action == "Delete" && a.RecordId == id.ToString())));
    }
}

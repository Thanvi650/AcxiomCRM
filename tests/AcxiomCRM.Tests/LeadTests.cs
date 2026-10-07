using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 6 — Lead Management through the real MVC pages: create/edit/delete, validation,
/// status workflow, Lead-to-Customer conversion, search and filters.
/// </summary>
public class LeadTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public LeadTests(CrmFactory factory)
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

    private static (string, string)[] LeadFields(string name, string status = "New", string email = "", string phone = "",
        string value = "50000", string source = "Website") =>
        new[]
        {
            ("LeadName", name), ("Email", email), ("Phone", phone), ("CompanyName", name + " Pvt Ltd"),
            ("Source", source), ("Status", status), ("Priority", "Medium"), ("ExpectedValue", value), ("Notes", "")
        };

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, params (string, string)[] fields)
    {
        var token = await TokenAsync(client, "/Leads/Create");
        return await client.PostAsync("/Leads/Create", Form(token, fields));
    }

    private async Task<int> CreateLeadAsync(HttpClient client, string name, string status = "New", string email = "", string phone = "")
    {
        var response = await CreateAsync(client, LeadFields(name, status, email, phone));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private async Task<HttpResponseMessage> ChangeStatusAsync(HttpClient client, int id, string status)
    {
        var token = await TokenAsync(client, $"/Leads/Details/{id}");
        return await client.PostAsync($"/Leads/ChangeStatus/{id}", Form(token, ("status", status)));
    }

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<Lead> LeadAsync(int id) => WithDbAsync(db => db.Leads.AsNoTracking().SingleAsync(l => l.LeadId == id));

    // ---------- create & validation (17.6) ----------

    [Fact]
    public async Task Create_saves_lead_with_code_owner_and_audit()
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, "Meena Joshi", email: "Meena@Joshi.example", phone: "9822200001");

        var lead = await WithDbAsync(db => db.Leads.Include(l => l.AssignedTo).SingleAsync(l => l.LeadId == id));
        Assert.Equal($"LEAD-{id:D5}", lead.LeadCode);
        Assert.Equal("meena@joshi.example", lead.Email);
        Assert.Equal("Rahul Verma", lead.AssignedTo!.FullName);
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Lead" && a.Action == "Create" && a.RecordId == id.ToString())));
    }

    [Theory]
    [InlineData("", "New", "", "", "1000", "Lead Name is required.")]
    [InlineData("Valid Lead", "New", "bad-email", "", "1000", "Enter a valid email address.")]
    [InlineData("Valid Lead", "New", "", "123", "1000", "Enter a valid phone number.")]
    [InlineData("Valid Lead", "New", "", "", "-1", "Expected Value must be between 0 and 1,000,000,000.")]
    [InlineData("Valid Lead", "New", "", "", "2000000000", "Expected Value must be between 0 and 1,000,000,000.")]
    [InlineData("Valid Lead", "Bogus", "", "", "1000", "is not valid for Status")]
    [InlineData("Valid Lead", "Converted", "", "", "1000", "A new lead must start as New, Contacted, Qualified or Unqualified.")]
    [InlineData("Valid Lead", "Lost", "", "", "1000", "A new lead must start as New, Contacted, Qualified or Unqualified.")]
    public async Task Create_rejects_invalid_input(string name, string status, string email, string phone, string value, string message)
    {
        var client = await LoginAsync();
        var before = await WithDbAsync(db => db.Leads.CountAsync());

        var response = await CreateAsync(client, LeadFields(name, status, email, phone, value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await WithDbAsync(db => db.Leads.CountAsync()));
    }

    // ---------- status workflow ----------

    [Fact]
    public async Task Valid_status_changes_are_saved_and_audited()
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, "Workflow Lead");

        await ChangeStatusAsync(client, id, "Contacted");
        await ChangeStatusAsync(client, id, "Qualified");

        Assert.Equal(LeadStatus.Qualified, (await LeadAsync(id)).Status);
        Assert.Equal(2, await WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityName == "Lead" && a.Action == "StatusChange" && a.RecordId == id.ToString())));
    }

    [Theory]
    [InlineData("New", "Converted")]   // only reachable through Convert
    [InlineData("Lost", "Qualified")]  // a lost lead must be reopened as New first
    [InlineData("Unqualified", "Qualified")]
    public async Task Invalid_status_changes_are_rejected(string from, string to)
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, $"Bad Move {from}->{to}");
        if (from == "Lost") await ChangeStatusAsync(client, id, "Lost");
        if (from == "Unqualified") await ChangeStatusAsync(client, id, "Unqualified");

        var response = await ChangeStatusAsync(client, id, to);
        var page = await client.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Equal(Enum.Parse<LeadStatus>(from), (await LeadAsync(id)).Status);
        Assert.Contains("alert-danger", page);
    }

    [Fact]
    public async Task Edit_form_only_offers_allowed_next_statuses()
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, "Dropdown Lead", "Contacted");
        var html = await client.GetStringAsync($"/Leads/Edit/{id}");
        var select = Regex.Match(html, "<select[^>]*id=\"Status\"[\\s\\S]*?</select>").Value;

        foreach (var allowed in new[] { "Contacted", "Qualified", "Unqualified", "Lost" }) Assert.Contains($"value=\"{allowed}\"", select);
        Assert.DoesNotContain("value=\"Converted\"", select);
        Assert.DoesNotContain("value=\"New\"", select);
    }

    // ---------- Lead-to-Customer conversion (workflow 8) ----------

    private async Task<HttpResponseMessage> ConvertAsync(HttpClient client, int id, bool createOpportunity = true,
        string amount = "250000", string probability = "30", int closeInDays = 30)
    {
        var token = await TokenAsync(client, $"/Leads/Details/{id}");
        var fields = new List<(string, string)> { ("CreateOpportunity", createOpportunity ? "true" : "false") };
        if (createOpportunity)
        {
            fields.AddRange(new[]
            {
                ("OpportunityName", "Converted deal"), ("Amount", amount), ("Probability", probability),
                ("ExpectedCloseDate", DateTime.Today.AddDays(closeInDays).ToString("yyyy-MM-dd"))
            });
        }
        return await client.PostAsync($"/Leads/Convert/{id}", Form(token, fields.ToArray()));
    }

    [Fact]
    public async Task Qualified_lead_converts_to_customer_and_opportunity()
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, "Convertible Lead", "Qualified", "convert@lead.example", "9822200002");

        // A follow-up scheduled against the lead should follow it to the customer.
        await WithDbAsync(async db =>
        {
            var lead = await db.Leads.SingleAsync(l => l.LeadId == id);
            db.FollowUps.Add(new FollowUp { LeadId = id, Subject = "Pre-sale call", FollowUpType = FollowUpType.Call, FollowUpDate = DateTime.Today.AddDays(2), AssignedToId = lead.AssignedToId });
            return await db.SaveChangesAsync();
        });

        var response = await ConvertAsync(client, id);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Customers/Details/", response.Headers.Location!.OriginalString);

        var lead = await LeadAsync(id);
        Assert.Equal(LeadStatus.Converted, lead.Status);
        Assert.NotNull(lead.ConvertedDate);
        var customer = await WithDbAsync(db => db.Customers.SingleAsync(c => c.CustomerId == lead.ConvertedCustomerId));
        Assert.Equal("convert@lead.example", customer.Email);
        Assert.Equal(lead.AssignedToId, customer.AssignedToId);

        var opportunity = await WithDbAsync(db => db.Opportunities.SingleAsync(o => o.LeadId == id));
        Assert.Equal(customer.CustomerId, opportunity.CustomerId);
        Assert.Equal(OpportunityStage.Qualification, opportunity.Stage);
        Assert.Equal(250000m, opportunity.Amount);

        Assert.True(await WithDbAsync(db => db.FollowUps.AnyAsync(f => f.LeadId == id && f.CustomerId == customer.CustomerId)));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Lead" && a.Action == "Convert" && a.RecordId == id.ToString())));

        var page = await client.GetStringAsync(response.Headers.Location!.OriginalString);
        Assert.Contains("Pre-sale call", page);
        Assert.Contains("Converted deal", page);
    }

    [Fact]
    public async Task Conversion_reuses_an_existing_customer_with_the_same_email()
    {
        var client = await LoginAsync();
        // Ananya Iyer (Rahul's customer) already exists with this email.
        var id = await CreateLeadAsync(client, "Ananya Again", "Qualified", "ananya@iyertextiles.example", "9822200003");
        var customersBefore = await WithDbAsync(db => db.Customers.CountAsync());

        var response = await ConvertAsync(client, id, createOpportunity: false);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(customersBefore, await WithDbAsync(db => db.Customers.CountAsync()));
        var lead = await LeadAsync(id);
        Assert.Equal(await WithDbAsync(db => db.Customers.Where(c => c.Email == "ananya@iyertextiles.example").Select(c => c.CustomerId).SingleAsync()),
            lead.ConvertedCustomerId);
    }

    [Theory]
    [InlineData("0", "30", 30, "Opportunity Amount must be greater than 0.")]
    [InlineData("1000", "101", 30, "Probability must be between 0 and 100.")]
    [InlineData("1000", "30", -1, "Expected Close Date cannot be in the past.")]
    public async Task Conversion_applies_opportunity_business_rules(string amount, string probability, int closeInDays, string message)
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, $"Rules Lead {amount}-{probability}-{closeInDays}", "Qualified",
            $"rules{Guid.NewGuid():N}@lead.example", $"98223{Random.Shared.Next(10000, 99999)}");

        var response = await ConvertAsync(client, id, amount: amount, probability: probability, closeInDays: closeInDays);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(LeadStatus.Qualified, (await LeadAsync(id)).Status);
    }

    [Fact]
    public async Task Only_qualified_leads_with_contact_details_can_be_converted()
    {
        var client = await LoginAsync();
        var newLead = await CreateLeadAsync(client, "Too Early", "New", "early@lead.example", "9822200004");
        var notQualified = await ConvertAsync(client, newLead, createOpportunity: false);
        Assert.Equal(HttpStatusCode.OK, notQualified.StatusCode);
        Assert.Contains("Only leads with status Qualified can be converted.", await notQualified.Content.ReadAsStringAsync());

        var noPhone = await CreateLeadAsync(client, "No Phone", "Qualified", "nophone@lead.example");
        var missing = await ConvertAsync(client, noPhone, createOpportunity: false);
        Assert.Contains("needs a valid email and phone number", await missing.Content.ReadAsStringAsync());
        Assert.Equal(LeadStatus.Qualified, (await LeadAsync(noPhone)).Status);
    }

    [Fact]
    public async Task Another_teams_lead_cannot_be_opened_or_converted()
    {
        var kiranLead = await WithDbAsync(db => db.Leads.Where(l => l.AssignedTo!.Email == "kiran.sales@acxiomcrm.local").Select(l => l.LeadId).FirstAsync());
        var client = await LoginAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Leads/Details/{kiranLead}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Leads/Convert/{kiranLead}")).StatusCode);
    }

    // ---------- delete ----------

    [Fact]
    public async Task Delete_removes_lead_with_its_follow_ups_and_detaches_opportunities()
    {
        var client = await LoginAsync();
        var id = await CreateLeadAsync(client, "Disposable Lead", "Qualified", "dispose@lead.example", "9822200005");
        await ConvertAsync(client, id); // creates an opportunity linked to the lead
        var opportunityId = await WithDbAsync(db => db.Opportunities.Where(o => o.LeadId == id).Select(o => o.OpportunityId).SingleAsync());

        var token = await TokenAsync(client, $"/Leads/Details/{id}");
        var response = await client.PostAsync($"/Leads/Delete/{id}", Form(token));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await WithDbAsync(db => db.Leads.AnyAsync(l => l.LeadId == id)));
        Assert.Null(await WithDbAsync(db => db.Opportunities.Where(o => o.OpportunityId == opportunityId).Select(o => o.LeadId).SingleAsync()));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Lead" && a.Action == "Delete" && a.RecordId == id.ToString())));
    }

    // ---------- search & filters (17.13: name, company, status, assigned user) ----------

    [Fact]
    public async Task Leads_can_be_searched_and_filtered()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");

        var byName = await client.GetStringAsync("/Leads?q=Harish");
        Assert.Contains("Harish Pillai", byName);
        Assert.DoesNotContain("Pooja Desai", byName);

        var byCompany = await client.GetStringAsync("/Leads?q=Desai%20Exports");
        Assert.Contains("Pooja Desai", byCompany);

        var lost = await client.GetStringAsync("/Leads?status=Lost");
        Assert.Contains("Manoj Tiwari", lost);
        Assert.DoesNotContain("Pooja Desai", lost);

        // Search within "open" so leads created by other tests don't push these onto page 2.
        Assert.DoesNotContain("Manoj Tiwari", await client.GetStringAsync("/Leads?status=open&q=Manoj"));  // lost
        Assert.DoesNotContain("Imran Sheikh", await client.GetStringAsync("/Leads?status=open&q=Imran"));  // unqualified
        Assert.Contains("Pooja Desai", await client.GetStringAsync("/Leads?status=open&q=Pooja"));

        var kiranId = await WithDbAsync(db => db.Users.Where(u => u.Email == "kiran.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var kirans = await client.GetStringAsync($"/Leads?assignedTo={kiranId}");
        Assert.Contains("Ritu Agarwal", kirans);
        Assert.DoesNotContain("Pooja Desai", kirans);
    }
}

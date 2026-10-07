using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 13 — Reports (spec 4.11 and 11): every report, role access and scope, filters,
/// sorting, paging, CSV export, and figures that match the database.
/// </summary>
public class ReportTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public ReportTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/Account/Login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = email, ["Password"] = CrmFactory.Password, ["__RequestVerificationToken"] = token
        }));
        return client;
    }

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static string Body(string html) => Regex.Match(html, "<tbody>[\\s\\S]*?</tbody>").Value;

    /// <summary>Downloads a CSV export and returns its rows (header first), BOM stripped.</summary>
    private static async Task<List<string>> CsvAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).SelectMany(l => l.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();
    }

    // ---------- every report exists and is reachable per spec 11 ----------

    [Theory]
    [InlineData("admin@acxiomcrm.local")]
    [InlineData("priya.manager@acxiomcrm.local")]
    [InlineData("rahul.sales@acxiomcrm.local")]
    public async Task Each_role_can_open_its_reports(string email)
    {
        var client = await LoginAsync(email);
        foreach (var report in new[] { "Customers", "Leads", "FollowUps", "Opportunities", "Pipeline", "Conversion" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Reports/{report}")).StatusCode);
        }

        var index = await client.GetStringAsync("/Reports");
        var isSales = email.StartsWith("rahul");
        Assert.Equal(!isSales, index.Contains("User Activity Report"));
        Assert.Equal(!isSales, index.Contains("Audit Report"));
        Assert.Equal(isSales ? HttpStatusCode.Redirect : HttpStatusCode.OK, (await client.GetAsync("/Reports/UserActivity")).StatusCode);
    }

    [Fact]
    public async Task Reports_only_contain_the_users_scope()
    {
        var sales = await LoginAsync("rahul.sales@acxiomcrm.local");
        var customers = Body(await sales.GetStringAsync("/Reports/Customers"));
        Assert.Contains("Ananya Iyer", customers);      // Rahul's
        Assert.DoesNotContain("Meera Nair", customers);  // Sneha's

        var csv = string.Join("\n", await CsvAsync(sales, "/Reports/Opportunities?export=csv"));
        Assert.Contains("Rahul Verma", csv);
        Assert.DoesNotContain("Kiran Kumar", csv);
        Assert.DoesNotContain("Sneha Reddy", csv);
    }

    // ---------- filters, sorting, paging ----------

    [Fact]
    public async Task Customer_report_filters_by_status_and_created_date_and_sorts()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");

        var inactive = Body(await admin.GetStringAsync("/Reports/Customers?status=Inactive"));
        Assert.Contains("Arvind Joshi", inactive);
        Assert.DoesNotContain("Ananya Iyer", inactive);

        // Seeded: Deepa Menon created 12 days ago, Ananya Iyer 210 days ago.
        var from = DateTime.Today.AddDays(-20).ToString("yyyy-MM-dd");
        var recent = Body(await admin.GetStringAsync($"/Reports/Customers?from={from}"));
        Assert.Contains("Deepa Menon", recent);
        Assert.DoesNotContain("Ananya Iyer", recent);

        var sorted = Body(await admin.GetStringAsync("/Reports/Customers?sort=name"));
        Assert.True(sorted.IndexOf("Ananya Iyer", StringComparison.Ordinal) < sorted.IndexOf("Vikram Singh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lead_report_shows_source_status_owner_and_conversion()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");

        var referral = Body(await admin.GetStringAsync("/Reports/Leads?type=Referral"));
        Assert.Contains("Pooja Desai", referral);
        Assert.DoesNotContain("Nikhil Bose", referral);  // Website

        var converted = await admin.GetStringAsync("/Reports/Leads?status=Converted");
        Assert.Contains("Converted to", converted);
        Assert.Matches("/Customers/Details/\\d+\">Ananya Iyer<", converted);

        var csv = await CsvAsync(admin, "/Reports/Leads?export=csv&status=Converted");
        Assert.EndsWith("Converted To", csv[0]);
        Assert.Contains(csv, row => row.Contains("Ananya Iyer") && row.EndsWith("Ananya Iyer"));
    }

    [Fact]
    public async Task Follow_up_report_summarises_and_filters_overdue()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync("/Reports/FollowUps?view=overdue");

        foreach (var label in new[] { "Planned", "Completed", "Missed", "Overdue" }) Assert.Contains($">{label}<", html);
        var body = Body(html);
        Assert.Contains("Send proposal revision", body);
        Assert.DoesNotContain("Intro call", body);  // future
    }

    [Fact]
    public async Task Opportunity_report_filters_by_stage_and_close_date_and_pages()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");

        var negotiation = Body(await admin.GetStringAsync("/Reports/Opportunities?type=Negotiation"));
        Assert.Contains("Singh Logistics – warehouse module", negotiation);
        Assert.DoesNotContain("Patel Agro – supply-chain pilot", negotiation); // qualification

        var to = DateTime.Today.AddDays(15).ToString("yyyy-MM-dd");
        var from = DateTime.Today.ToString("yyyy-MM-dd");
        var closingSoon = Body(await admin.GetStringAsync($"/Reports/Opportunities?from={from}&to={to}"));
        Assert.Contains("Khan Pharma – field-force app", closingSoon);       // closes in 10 days
        Assert.DoesNotContain("Rao Constructions – phase 2", closingSoon);    // closes in 60 days

        // Reports show 15 rows per page; make sure there are more than 15 opportunities.
        var api = await _factory.LoginAsync("admin@acxiomcrm.local");
        var customerId = await WithDbAsync(db => db.Customers.Select(c => c.CustomerId).FirstAsync());
        for (var i = 0; i < 2; i++)
        {
            await api.PostAsync("/api/opportunities", System.Net.Http.Json.JsonContent.Create(new
            {
                opportunityName = $"Paging deal {i}", customerId, amount = 1000, probability = 10,
                expectedCloseDate = DateTime.Today.AddDays(90).ToString("yyyy-MM-dd"), stage = "Qualification"
            }));
        }
        var page2 = await admin.GetStringAsync("/Reports/Opportunities?sort=name&page=2");
        Assert.Contains("Showing 16–", page2);
        Assert.Contains("aria-label=\"Pagination\"", page2);
    }

    [Fact]
    public async Task Reversed_date_range_is_explained_not_silently_empty()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync($"/Reports/Customers?from={DateTime.Today:yyyy-MM-dd}&to={DateTime.Today.AddDays(-30):yyyy-MM-dd}");
        Assert.Contains("is before the start date", html);
        Assert.Contains("Ananya Iyer", Body(html)); // filter ignored, data still shown
    }

    // ---------- figures match the database ----------

    [Fact]
    public async Task Pipeline_report_matches_the_database_and_exports_both_views()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync("/Reports/Pipeline");

        var opps = await WithDbAsync(db => db.Opportunities.AsNoTracking().ToListAsync());
        var open = opps.Where(o => o.Status == OpportunityStatus.Open).ToList();
        Assert.Contains(open.Sum(o => o.Amount).ToString("N0", new System.Globalization.CultureInfo("en-IN")), html);
        Assert.Contains(open.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)).ToString("N0", new System.Globalization.CultureInfo("en-IN")), html);

        var csv = await CsvAsync(admin, "/Reports/Pipeline?export=csv");
        Assert.Equal("View,Stage / Owner,Opportunities,Amount,Weighted Amount,Won Amount", csv[0]);
        Assert.Equal(5, csv.Count(r => r.StartsWith("Stage,")));
        Assert.Contains(csv, r => r.StartsWith("Owner,Rahul Verma,"));
        var negotiation = csv.Single(r => r.StartsWith("Stage,Negotiation,")).Split(',');
        Assert.Equal(opps.Count(o => o.Stage == OpportunityStage.Negotiation).ToString(), negotiation[2]);
    }

    [Fact]
    public async Task Conversion_report_counts_outcomes_by_close_date()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");

        // An old deal (created a year ago) won today must count in a "today" range.
        var amount = await WithDbAsync(async db =>
        {
            // A deal no other test in this class looks at, so test order doesn't matter.
            var o = await db.Opportunities.SingleAsync(x => x.OpportunityName == "Malhotra Retail – loyalty program");
            o.CreatedDate = DateTime.Today.AddYears(-1);
            o.Stage = OpportunityStage.Won;
            o.Status = OpportunityStatus.Won;
            o.ClosedDate = DateTime.Now;
            await db.SaveChangesAsync();
            return o.Amount;
        });

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var html = await admin.GetStringAsync($"/Reports/Conversion?from={today}&to={today}");
        var wonCard = Regex.Match(html, "Opportunities won</div><div class=\"kpi-value[^\"]*\">([^<]+)<").Groups[1].Value;
        Assert.StartsWith("1 ·", wonCard.Trim());
        Assert.Contains(amount.ToString("N0", new System.Globalization.CultureInfo("en-IN")), wonCard);
    }

    [Fact]
    public async Task User_activity_report_counts_match_the_audit_log()
    {
        var sales = await LoginAsync("rahul.sales@acxiomcrm.local");
        await sales.GetAsync("/Customers");
        var admin = await LoginAsync("admin@acxiomcrm.local");

        var rahulId = await WithDbAsync(db => db.Users.Where(u => u.Email == "rahul.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var logins = await WithDbAsync(db => db.AuditLogs.CountAsync(a => a.UserId == rahulId && a.Action == "Login"));
        var total = await WithDbAsync(db => db.AuditLogs.CountAsync(a => a.UserId == rahulId));

        var csv = await CsvAsync(admin, $"/Reports/UserActivity?export=csv&assignedTo={rahulId}");
        var row = csv.Single(r => r.StartsWith("Rahul Verma,")).Split(',');
        Assert.Equal(logins.ToString(), row[1]);
        Assert.Equal(total.ToString(), row[6]);
    }

    // ---------- export safety ----------

    [Fact]
    public async Task Csv_export_respects_filters_and_neutralises_formulas()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        await api.PostAsync("/api/customers", System.Net.Http.Json.JsonContent.Create(new
        {
            customerName = "=HYPERLINK(\"http://evil.example\")", email = "formula@csv.example", phone = "9811199901", status = "Prospect"
        }));

        var sales = await LoginAsync("rahul.sales@acxiomcrm.local");
        var rows = await CsvAsync(sales, "/Reports/Customers?export=csv&status=Prospect");
        Assert.StartsWith("Code,Customer,", rows[0]);
        Assert.All(rows.Skip(1), r => Assert.Contains("Prospect", r));
        var evil = rows.Single(r => r.Contains("HYPERLINK"));
        Assert.Contains("\"'=HYPERLINK(", evil);  // leading quote stops Excel evaluating it
    }
}

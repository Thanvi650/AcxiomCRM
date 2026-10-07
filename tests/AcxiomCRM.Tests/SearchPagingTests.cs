using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 14 — Search, filtering and pagination (spec 4.1, 12, 17.13): global search,
/// required filters per page, sorting on every list, and robust paging.
/// </summary>
public class SearchPagingTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public SearchPagingTests(CrmFactory factory)
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

    private static string Section(string html, string heading) =>
        Regex.Match(html, $"{heading} \\(\\d+\\)[\\s\\S]*?</ul>").Value;

    // ---------- global search (4.1) ----------

    [Fact]
    public async Task Global_search_finds_customers_leads_and_opportunities()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync("/Search?q=Singh");

        Assert.Contains("Vikram Singh", Section(html, "Customers"));
        Assert.Contains("Singh Logistics – fleet tracking", Section(html, "Opportunities"));

        var leads = await admin.GetStringAsync("/Search?q=Pillai");
        Assert.Contains("Harish Pillai", Section(leads, "Leads"));
    }

    [Fact]
    public async Task Global_search_respects_the_users_scope()
    {
        var sales = await LoginAsync("rahul.sales@acxiomcrm.local");
        var html = await sales.GetStringAsync("/Search?q=Khan");     // Kiran's customer and deals
        Assert.Contains("Nothing matches", html);

        var own = await sales.GetStringAsync("/Search?q=Iyer");      // Rahul's customer
        Assert.Contains("Ananya Iyer", own);
    }

    [Theory]
    [InlineData("%%")]
    [InlineData("__")]
    [InlineData("'; DROP TABLE Customers; --")]
    [InlineData("[a-z]")]
    public async Task Special_characters_are_searched_literally(string q)
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var response = await admin.GetAsync($"/Search?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Nothing matches", await response.Content.ReadAsStringAsync()); // not "match everything"
        Assert.Contains("Ananya Iyer", await admin.GetStringAsync("/Customers"));       // table still there
    }

    [Fact]
    public async Task Global_search_needs_two_characters_and_links_from_every_page()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        Assert.Contains("Type at least 2 characters", await admin.GetStringAsync("/Search?q=a"));

        var dashboard = await admin.GetStringAsync("/");
        Assert.Matches("<form class=\"topbar-search[^>]*action=\"/Search\"", dashboard);
    }

    // ---------- required filters per page (17.13) ----------

    [Theory]
    [InlineData("/Customers", new[] { "q", "status", "assignedTo" })]                    // name, email, phone, company via q
    [InlineData("/Leads", new[] { "q", "status", "assignedTo" })]                        // name, company via q
    [InlineData("/Opportunities", new[] { "q", "type", "status" })]                      // name, customer via q; stage; status
    [InlineData("/FollowUps", new[] { "from", "to", "status", "assignedTo", "q" })]      // date, status, user, related customer/lead
    [InlineData("/Activities", new[] { "type", "from", "to", "status", "assignedTo" })]  // type, date, status, user
    public async Task Each_list_page_offers_the_required_filters(string page, string[] fields)
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync(page);
        var form = Regex.Match(html, "<form method=\"get\" class=\"filter-bar[\\s\\S]*?</form>").Value;
        foreach (var field in fields)
        {
            Assert.Matches($"name=\"{field}\"", form);
        }
        Assert.Contains(">Clear<", form);
    }

    // ---------- sorting ----------

    [Theory]
    [InlineData("/Customers", "name")]
    [InlineData("/Leads", "name")]
    [InlineData("/Opportunities", "amount")]
    [InlineData("/FollowUps?view=all", "subject")]
    [InlineData("/Activities", "subject")]
    [InlineData("/Users", "name")]
    [InlineData("/AuditLogs", "user")]
    public async Task Every_list_page_has_sortable_columns(string page, string key)
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync(page);
        Assert.Matches($"class=\"sort-link\" href=\"[^\"]*sort={key}", html);

        var sorted = await admin.GetAsync(page + (page.Contains('?') ? "&" : "?") + $"sort={key}_desc");
        Assert.Equal(HttpStatusCode.OK, sorted.StatusCode);
        Assert.Contains("bi-caret-down-fill", await sorted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Users_and_audit_log_sort_in_the_requested_order()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");

        var users = Regex.Match(await admin.GetStringAsync("/Users?sort=name_desc"), "<tbody>[\\s\\S]*</tbody>").Value;
        Assert.True(users.IndexOf("System Administrator", StringComparison.Ordinal) < users.IndexOf("Arjun Mehta", StringComparison.Ordinal));

        var oldestFirst = Regex.Match(await admin.GetStringAsync("/AuditLogs?sort=date"), "<tbody>[\\s\\S]*</tbody>").Value;
        Assert.Contains(">Seed<", oldestFirst.Substring(0, Math.Min(oldestFirst.Length, 1500)));
    }

    [Fact]
    public async Task Filtering_keeps_the_chosen_sort_order()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        foreach (var page in new[] { "/Customers?sort=name_desc", "/FollowUps?view=all&sort=subject", "/Activities?sort=subject", "/Users?sort=role", "/AuditLogs?sort=user" })
        {
            var html = await admin.GetStringAsync(page);
            var form = Regex.Match(html, "<form method=\"get\" class=\"filter-bar[\\s\\S]*?</form>").Value;
            var expected = Regex.Match(page, "sort=([a-z_]+)").Groups[1].Value;
            Assert.Contains($"name=\"sort\" value=\"{expected}\"", form);
        }
    }

    // ---------- pagination ----------

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("9999")]
    [InlineData("abc")]
    public async Task Out_of_range_or_invalid_page_numbers_are_handled(string page)
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        foreach (var list in new[] { "/Customers", "/Leads", "/Opportunities", "/FollowUps?view=all", "/Activities", "/Users", "/AuditLogs", "/Reports/Customers" })
        {
            var response = await admin.GetAsync(list + (list.Contains('?') ? "&" : "?") + $"page={page}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Showing ", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Pager_links_keep_filters_and_sort_and_sorting_resets_to_page_one()
    {
        var admin = await LoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync("/AuditLogs?entity=Account&sort=user&page=1");

        var next = Regex.Match(html, "href=\"([^\"]*page=2[^\"]*)\"").Groups[1].Value;
        Assert.Contains("entity=Account", next);
        Assert.Contains("sort=user", next);

        var page2 = await admin.GetStringAsync("/Customers?status=Active&page=2");
        var sortLink = Regex.Match(page2, "class=\"sort-link\" href=\"([^\"]*sort=name[^\"]*)\"").Groups[1].Value;
        Assert.Contains("status=Active", sortLink);
        Assert.DoesNotContain("page=", sortLink);
    }
}

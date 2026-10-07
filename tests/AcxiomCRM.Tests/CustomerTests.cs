using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 5 — Customer Management through the real MVC pages: create, details, edit,
/// delete, search/filter/sort/paging, validation, duplicate prevention and history.
/// </summary>
public class CustomerTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public CustomerTests(CrmFactory factory)
    {
        _factory = factory;
    }

    // ---------- helpers ----------

    private async Task<HttpClient> LoginAsync(string email = "priya.manager@acxiomcrm.local")
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

    private static (string, string)[] CustomerFields(string name, string email, string phone, string? company = null, string status = "Active") =>
        new[]
        {
            ("CustomerName", name), ("Email", email), ("Phone", phone), ("CompanyName", company ?? ""),
            ("Address", "12 MG Road"), ("City", "Pune"), ("State", "Maharashtra"), ("Status", status), ("Notes", "")
        };

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, params (string, string)[] fields)
    {
        var token = await TokenAsync(client, "/Customers/Create");
        return await client.PostAsync("/Customers/Create", Form(token, fields));
    }

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static int IdFrom(HttpResponseMessage response) =>
        int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());

    // ---------- create ----------

    [Fact]
    public async Task Create_saves_customer_with_code_creator_and_audit_entry()
    {
        var client = await LoginAsync();
        var response = await CreateAsync(client, CustomerFields("Asha Kulkarni", "Asha.K@Kulkarni.example", "9811100001", "Kulkarni Tools"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = IdFrom(response);
        var customer = await WithDbAsync(db => db.Customers.Include(c => c.AssignedTo).SingleAsync(c => c.CustomerId == id));
        Assert.Equal($"CUS-{id:D5}", customer.CustomerCode);
        Assert.Equal("asha.k@kulkarni.example", customer.Email); // normalised
        Assert.Equal("Priya Sharma", customer.AssignedTo!.FullName);
        Assert.Equal(customer.AssignedToId, customer.CreatedBy);
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Customer" && a.Action == "Create" && a.RecordId == id.ToString())));

        var details = await client.GetStringAsync($"/Customers/Details/{id}");
        Assert.Contains("Asha Kulkarni", details);
        Assert.Contains("by Priya Sharma", details);
        Assert.Contains("Create</span> by priya.manager@acxiomcrm.local", details); // history panel
    }

    [Theory]
    [InlineData("", "valid@example.com", "9811100002", "Customer Name is required.")]
    [InlineData("Valid Name", "", "9811100002", "Email is required.")]
    [InlineData("Valid Name", "not-an-email", "9811100002", "Enter a valid email address.")]
    [InlineData("Valid Name", "valid@example.com", "", "Phone is required.")]
    [InlineData("Valid Name", "valid@example.com", "12345", "Enter a valid phone number.")]
    [InlineData("Valid Name", "valid@example.com", "98111000021", "Enter a valid phone number.")]
    public async Task Create_rejects_invalid_input_on_the_server(string name, string email, string phone, string message)
    {
        var client = await LoginAsync();
        var before = await WithDbAsync(db => db.Customers.CountAsync());

        var response = await CreateAsync(client, CustomerFields(name, email, phone));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form re-displayed
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await WithDbAsync(db => db.Customers.CountAsync()));
    }

    [Fact]
    public async Task Create_rejects_name_longer_than_the_maximum()
    {
        var client = await LoginAsync();
        var response = await CreateAsync(client, CustomerFields(new string('A', 101), "long.name@example.com", "9811100003"));
        Assert.Contains("Customer Name must be between 2 and 100 characters.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Duplicate_email_phone_or_company_contact_is_prevented()
    {
        var client = await LoginAsync();
        var first = await CreateAsync(client, CustomerFields("Dev Anand", "dev@anand.example", "9811100004", "Anand Films"));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        var sameEmail = await CreateAsync(client, CustomerFields("Someone Else", "DEV@ANAND.EXAMPLE", "9811100005"));
        Assert.Contains("A customer with this email already exists.", await sameEmail.Content.ReadAsStringAsync());

        var samePhone = await CreateAsync(client, CustomerFields("Someone Else", "other@anand.example", "9811100004"));
        Assert.Contains("A customer with this phone number already exists.", await samePhone.Content.ReadAsStringAsync());

        var sameCompanyContact = await CreateAsync(client, CustomerFields("dev anand", "dev2@anand.example", "9811100006", "ANAND FILMS"));
        Assert.Contains("This customer already exists for the same company.", await sameCompanyContact.Content.ReadAsStringAsync());

        Assert.Equal(1, await WithDbAsync(db => db.Customers.CountAsync(c => c.CompanyName == "Anand Films")));
    }

    // ---------- edit ----------

    [Fact]
    public async Task Edit_updates_customer_and_audits_status_change()
    {
        var client = await LoginAsync();
        var id = IdFrom(await CreateAsync(client, CustomerFields("Kavya Shetty", "kavya@shetty.example", "9811100007", "Shetty Prints", "Prospect")));

        var token = await TokenAsync(client, $"/Customers/Edit/{id}");
        var response = await client.PostAsync($"/Customers/Edit/{id}",
            Form(token, CustomerFields("Kavya Shetty", "kavya@shetty.example", "9811100008", "Shetty Prints", "Active")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var customer = await WithDbAsync(db => db.Customers.SingleAsync(c => c.CustomerId == id));
        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Equal("9811100008", customer.Phone);
        Assert.NotNull(customer.ModifiedDate);

        var change = await WithDbAsync(db => db.AuditLogs.SingleAsync(a => a.EntityName == "Customer" && a.Action == "StatusChange" && a.RecordId == id.ToString()));
        Assert.Contains("Prospect", change.OldValue);
        Assert.Contains("Active", change.NewValue);
    }

    [Fact]
    public async Task Edit_cannot_take_another_customers_email()
    {
        var client = await LoginAsync();
        var id = IdFrom(await CreateAsync(client, CustomerFields("Rohit Bhat", "rohit@bhat.example", "9811100009")));
        var token = await TokenAsync(client, $"/Customers/Edit/{id}");

        var response = await client.PostAsync($"/Customers/Edit/{id}",
            Form(token, CustomerFields("Rohit Bhat", "ananya@iyertextiles.example", "9811100009")));

        Assert.Contains("A customer with this email already exists.", await response.Content.ReadAsStringAsync());
        Assert.Equal("rohit@bhat.example", await WithDbAsync(db => db.Customers.Where(c => c.CustomerId == id).Select(c => c.Email).SingleAsync()));
    }

    // ---------- delete ----------

    [Fact]
    public async Task Delete_removes_a_customer_without_related_records_and_audits_it()
    {
        var client = await LoginAsync();
        var id = IdFrom(await CreateAsync(client, CustomerFields("Temp Customer", "temp@delete.example", "9811100010")));

        var token = await TokenAsync(client, $"/Customers/Details/{id}");
        var response = await client.PostAsync($"/Customers/Delete/{id}", Form(token));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await WithDbAsync(db => db.Customers.AnyAsync(c => c.CustomerId == id)));
        Assert.True(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == "Customer" && a.Action == "Delete" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Delete_is_blocked_while_customer_has_opportunities()
    {
        var client = await LoginAsync();
        var id = await WithDbAsync(db => db.Customers.Where(c => c.Opportunities.Any() && c.AssignedTo!.FullName == "Rahul Verma").Select(c => c.CustomerId).FirstAsync());

        var token = await TokenAsync(client, $"/Customers/Details/{id}");
        var response = await client.PostAsync($"/Customers/Delete/{id}", Form(token));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WithDbAsync(db => db.Customers.AnyAsync(c => c.CustomerId == id)));

        var details = await client.GetStringAsync(response.Headers.Location!.OriginalString);
        Assert.Contains("This customer has related opportunities", details);
    }

    // ---------- search, filter, sort, paging (spec 17.13) ----------

    [Theory]
    [InlineData("Rao")]                         // customer name
    [InlineData("lakshmi@raoconstructions")]    // email
    [InlineData("9876500007")]                  // phone
    [InlineData("Rao Constructions")]           // company
    [InlineData("CUS-00007")]                   // code
    public async Task Search_finds_customer_by_name_email_phone_company_or_code(string q)
    {
        var client = await LoginAsync("admin@acxiomcrm.local");
        var html = await client.GetStringAsync($"/Customers?q={Uri.EscapeDataString(q)}");
        Assert.Contains("Lakshmi Rao", html);
        Assert.DoesNotContain("Vikram Singh", html);
    }

    [Fact]
    public async Task Status_and_owner_filters_narrow_the_list()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");
        var inactive = await client.GetStringAsync("/Customers?status=Inactive");
        Assert.Contains("Arvind Joshi", inactive);
        Assert.DoesNotContain("Ananya Iyer", inactive);

        var kiranId = await WithDbAsync(db => db.Users.Where(u => u.Email == "kiran.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var kirans = await client.GetStringAsync($"/Customers?assignedTo={kiranId}");
        Assert.Contains("Fatima Khan", kirans);
        Assert.DoesNotContain("Ananya Iyer", kirans);
    }

    [Fact]
    public async Task List_sorts_by_name_and_pages_results()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");
        var sorted = await client.GetStringAsync("/Customers?sort=name");
        Assert.True(sorted.IndexOf("Ananya Iyer", StringComparison.Ordinal) < sorted.IndexOf("Vikram Singh", StringComparison.Ordinal));

        var desc = await client.GetStringAsync("/Customers?sort=name_desc");
        Assert.True(desc.IndexOf("Vikram Singh", StringComparison.Ordinal) < desc.IndexOf("Ananya Iyer", StringComparison.Ordinal));

        // Ensure more than one page exists, then check page 2 renders different rows.
        for (var i = 0; i < 11; i++)
        {
            await CreateAsync(client, CustomerFields($"Paging Person {i:D2}", $"paging{i}@example.com", $"97000000{i:D2}"));
        }
        var page1 = await client.GetStringAsync("/Customers?sort=name&page=1");
        var page2 = await client.GetStringAsync("/Customers?sort=name&page=2");
        Assert.Contains("aria-label=\"Pagination\"", page1);
        Assert.Contains("Showing 11–", page2);
        Assert.NotEqual(page1, page2);
    }

    [Fact]
    public async Task Details_show_related_opportunities_follow_ups_and_activities()
    {
        var client = await LoginAsync("admin@acxiomcrm.local");
        var id = await WithDbAsync(db => db.Customers.Where(c => c.CustomerName == "Vikram Singh").Select(c => c.CustomerId).SingleAsync());
        var html = await client.GetStringAsync($"/Customers/Details/{id}");

        Assert.Contains("Singh Logistics – fleet tracking", html);   // opportunity
        Assert.Contains("Demo of warehouse module", html);           // follow-up
        Assert.Contains("Pricing discussion", html);                 // activity
    }

    [Fact]
    public async Task Customer_text_is_html_encoded_but_unicode_is_kept()
    {
        var client = await LoginAsync();
        var id = IdFrom(await CreateAsync(client,
            ("CustomerName", "<script>alert(1)</script>"), ("Email", "xss@example.com"), ("Phone", "9811100011"),
            ("CompanyName", "శ్రీ Traders – ₹"), ("Status", "Active")));

        var html = await client.GetStringAsync($"/Customers/Details/{id}");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("శ్రీ Traders – ₹", html);
    }
}

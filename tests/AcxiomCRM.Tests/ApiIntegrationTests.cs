using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcxiomCRM.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>Runs the real app against a throw-away SQLite database seeded with demo data.</summary>
public class CrmFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test@Pass123";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"acxiomcrm-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Tests run against a throw-away SQLite file so they need no SQL Server.
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:SqliteConnection", $"Data Source={_dbPath}");
        builder.UseSetting("Seed:DefaultPassword", Password);
        builder.UseSetting("Seed:SampleData", "true");
        builder.UseSetting("Security:LoginRateLimitPerMinute", "1000");
    }

    public async Task<HttpClient> LoginAsync(string email)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = email, password = Password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public class ApiIntegrationTests : IClassFixture<CrmFactory>
{
    private const string Admin = "admin@acxiomcrm.local";
    private const string Manager = "priya.manager@acxiomcrm.local";
    private const string Sales = "rahul.sales@acxiomcrm.local";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly CrmFactory _factory;

    public ApiIntegrationTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private static string Day(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/customers")).StatusCode);

        var page = await client.GetAsync("/Customers");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/Account/Login", page.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Invalid_credentials_are_rejected()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = Sales, password = "Wrong@Pass1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Each_role_sees_a_different_scope_of_customers()
    {
        async Task<int> CountAsync(string user)
        {
            var client = await _factory.LoginAsync(user);
            var body = await client.GetFromJsonAsync<JsonElement>("/api/customers?pageSize=100");
            return body.GetProperty("totalCount").GetInt32();
        }

        var admin = await CountAsync(Admin);
        var manager = await CountAsync(Manager);
        var sales = await CountAsync(Sales);
        Assert.True(admin > manager && manager > sales && sales > 0, $"admin={admin} manager={manager} sales={sales}");
    }

    [Fact]
    public async Task Sales_executive_cannot_read_another_teams_record()
    {
        var admin = await _factory.LoginAsync(Admin);
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/customers?pageSize=100");
        var foreignId = all.GetProperty("items").EnumerateArray()
            .First(c => c.GetProperty("assignedToName").GetString() == "Kiran Kumar")
            .GetProperty("customerId").GetInt32();

        var sales = await _factory.LoginAsync(Sales);
        Assert.Equal(HttpStatusCode.NotFound, (await sales.GetAsync($"/api/customers/{foreignId}")).StatusCode);
    }

    [Fact]
    public async Task Pipeline_report_is_forbidden_for_sales_and_allowed_for_managers()
    {
        var sales = await _factory.LoginAsync(Sales);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/reports/pipeline")).StatusCode);

        var manager = await _factory.LoginAsync(Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/reports/pipeline")).StatusCode);
    }

    [Theory]
    [InlineData(0, 50, 10, "Amount")]
    [InlineData(1000, 101, 10, "Probability")]
    [InlineData(1000, 50, -1, "ExpectedCloseDate")]
    public async Task Opportunity_business_rules_are_enforced_by_the_api(decimal amount, int probability, int closeInDays, string field)
    {
        var client = await _factory.LoginAsync(Sales);
        var customerId = (await client.GetFromJsonAsync<JsonElement>("/api/customers")).GetProperty("items")[0].GetProperty("customerId").GetInt32();

        var response = await client.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "Rule test", customerId, amount, probability, expectedCloseDate = Day(closeInDays), stage = "Proposal"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Valid_opportunity_is_created_with_201_and_audited()
    {
        var client = await _factory.LoginAsync(Sales);
        var customerId = (await client.GetFromJsonAsync<JsonElement>("/api/customers")).GetProperty("items")[0].GetProperty("customerId").GetInt32();

        var response = await client.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "Audited deal", customerId, amount = 5000, probability = 40, expectedCloseDate = Day(20), stage = "Qualification"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2000m, created.GetProperty("weightedAmount").GetDecimal());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = created.GetProperty("opportunityId").GetInt32().ToString();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityName == "Opportunity" && a.Action == "Create" && a.RecordId == id));
    }

    [Fact]
    public async Task Follow_up_before_today_is_rejected()
    {
        var client = await _factory.LoginAsync(Sales);
        var response = await client.PostAsJsonAsync("/api/followups", new
        {
            subject = "Past call", followUpDate = Day(-1) + "T10:00", followUpType = "Call", customerId = 1
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_customer_email_returns_409()
    {
        var client = await _factory.LoginAsync(Admin);
        var existing = (await client.GetFromJsonAsync<JsonElement>("/api/customers")).GetProperty("items")[0];
        var response = await client.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Someone Else", email = existing.GetProperty("email").GetString(), phone = "9000011111", status = "Active"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_customer_payload_returns_400_with_field_errors()
    {
        var client = await _factory.LoginAsync(Sales);
        var response = await client.PostAsJsonAsync("/api/customers", new { customerName = "Bad", email = "nope", phone = "12", status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("Email", out _));
        Assert.True(errors.TryGetProperty("Phone", out _));
    }

    [Fact]
    public async Task Passwords_are_hashed_and_never_returned()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var hash = await db.Users.Where(u => u.Email == Sales).Select(u => u.PasswordHash).SingleAsync();
            Assert.NotNull(hash);
            Assert.DoesNotContain(CrmFactory.Password, hash);
        }

        var client = await _factory.LoginAsync(Sales);
        var me = await client.GetStringAsync("/api/auth/me");
        Assert.DoesNotContain("PasswordHash", me, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", me, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Lockout gets its own database so it doesn't affect other tests.</summary>
public class LockoutTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public LockoutTests(CrmFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Account_locks_after_repeated_failed_logins_and_audits_it()
    {
        const string user = "kiran.sales@acxiomcrm.local";
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { login = user, password = "Wrong@Pass1" });
        }

        // Even the correct password is refused while locked out.
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = user, password = CrmFactory.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("locked", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Lockout" && a.UserName == user));
        Assert.True(await db.AuditLogs.CountAsync(a => a.Action == "FailedLogin" && a.UserName == user) >= 4);
    }

    [Fact]
    public async Task Audit_log_entries_cannot_be_modified()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await db.AuditLogs.FirstAsync();
        entry.Action = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}

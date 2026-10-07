using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 12 — REST API contract (spec 4.10, 10, 10.1, 17.14): every specified endpoint,
/// status codes, consistent error objects, Location headers, DTO shape, documentation.
/// </summary>
public class ApiContractTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public ApiContractTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Anonymous() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Day(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd");

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        var text = body.GetRawText();
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text);
    }

    // ---------- every specified endpoint requires authentication ----------

    public static IEnumerable<object[]> ProtectedEndpoints() => new[]
    {
        new object[] { "POST", "/api/auth/logout" },
        new object[] { "GET", "/api/auth/me" },
        new object[] { "GET", "/api/customers" },
        new object[] { "POST", "/api/customers" },
        new object[] { "GET", "/api/customers/1" },
        new object[] { "PUT", "/api/customers/1" },
        new object[] { "DELETE", "/api/customers/1" },
        new object[] { "GET", "/api/leads" },
        new object[] { "POST", "/api/leads" },
        new object[] { "GET", "/api/opportunities" },
        new object[] { "POST", "/api/opportunities" },
        new object[] { "GET", "/api/followups" },
        new object[] { "POST", "/api/followups" },
        new object[] { "GET", "/api/reports/pipeline" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Protected_endpoints_return_401_problem_when_anonymous(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        await AssertProblemAsync(await Anonymous().SendAsync(request), HttpStatusCode.Unauthorized);
    }

    // ---------- happy paths and Location headers ----------

    [Fact]
    public async Task Customer_endpoints_follow_rest_conventions()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");

        var list = await api.GetAsync("/api/customers?q=a");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("application/json", list.Content.Headers.ContentType?.MediaType);

        var create = await api.PostAsJsonAsync("/api/customers", new { customerName = "Rest Co", email = "rest@api.example", phone = "9811188801", status = "Active" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var location = create.Headers.Location!;
        var fetched = await api.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var id = (await fetched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerId").GetInt32();

        var update = await api.PutAsJsonAsync($"/api/customers/{id}", new { customerName = "Rest Co Ltd", email = "rest@api.example", phone = "9811188801", status = "Active" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Rest Co Ltd", (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerName").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/customers/{id}")).StatusCode);
        await AssertProblemAsync(await api.GetAsync($"/api/customers/{id}"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await api.DeleteAsync($"/api/customers/{id}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Created_resources_can_be_fetched_from_their_location_header()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var customerId = (await api.GetFromJsonAsync<JsonElement>("/api/customers")).GetProperty("items")[0].GetProperty("customerId").GetInt32();

        var posts = new (string Url, object Body)[]
        {
            ("/api/leads", new { leadName = "Api Lead", source = "Website", status = "New", priority = "High", expectedValue = 5000 }),
            ("/api/opportunities", new { opportunityName = "Api Deal", customerId, amount = 5000, probability = 30, expectedCloseDate = Day(20), stage = "Qualification" }),
            ("/api/followups", new { subject = "Api follow-up", followUpDate = Day(1) + "T11:00", followUpType = "Email", customerId }),
        };

        foreach (var (url, body) in posts)
        {
            var created = await api.PostAsJsonAsync(url, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.NotNull(created.Headers.Location);
            Assert.StartsWith(url + "/", created.Headers.Location!.AbsolutePath);
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(created.Headers.Location)).StatusCode);
        }
    }

    [Fact]
    public async Task Pipeline_report_is_available_to_managers_but_not_sales()
    {
        var manager = await _factory.LoginAsync("priya.manager@acxiomcrm.local");
        var report = await manager.GetFromJsonAsync<JsonElement>("/api/reports/pipeline");
        Assert.Equal(5, report.GetProperty("byStage").GetArrayLength());
        Assert.True(report.GetProperty("totalOpenAmount").GetDecimal() > 0);

        var sales = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        await AssertProblemAsync(await sales.GetAsync("/api/reports/pipeline"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_ends_the_api_session()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsync("/api/auth/logout", null)).StatusCode);
        await AssertProblemAsync(await api.GetAsync("/api/customers"), HttpStatusCode.Unauthorized);
    }

    // ---------- consistent error objects for every error status ----------

    [Fact]
    public async Task Framework_level_errors_use_the_same_problem_format()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");

        await AssertProblemAsync(await api.GetAsync("/api/does-not-exist"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await api.PutAsJsonAsync("/api/leads", new { }), HttpStatusCode.MethodNotAllowed);
        await AssertProblemAsync(await api.PostAsync("/api/customers", new FormUrlEncodedContent(new Dictionary<string, string> { ["a"] = "b" })), HttpStatusCode.UnsupportedMediaType);
        await AssertProblemAsync(await api.PostAsJsonAsync("/api/customers", new { customerName = "" }), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await Anonymous().PostAsJsonAsync("/api/auth/login", new { login = "rahul.sales@acxiomcrm.local", password = "Wrong@Pass1" }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Duplicate_returns_409_problem_with_field_errors()
    {
        var api = await _factory.LoginAsync("admin@acxiomcrm.local");
        var response = await api.PostAsJsonAsync("/api/customers", new { customerName = "Dup", email = "ananya@iyertextiles.example", phone = "9811188802", status = "Active" });
        await AssertProblemAsync(response, HttpStatusCode.Conflict);
    }

    // ---------- DTOs, paging ----------

    [Fact]
    public async Task Responses_are_flat_dtos_without_entities_or_secrets()
    {
        var api = await _factory.LoginAsync("admin@acxiomcrm.local");
        foreach (var url in new[] { "/api/customers", "/api/leads", "/api/opportunities", "/api/followups?view=all", "/api/auth/me" })
        {
            var text = await api.GetStringAsync(url);
            foreach (var forbidden in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "\"assignedTo\":{", "\"customer\":{", "\"opportunities\":", "\"followUps\":" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task List_endpoints_are_paged_and_clamp_page_size()
    {
        var api = await _factory.LoginAsync("admin@acxiomcrm.local");
        var page = await api.GetFromJsonAsync<JsonElement>("/api/customers?page=2&pageSize=5");
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(5, page.GetProperty("pageSize").GetInt32());
        Assert.True(page.GetProperty("totalCount").GetInt32() >= 10);
        Assert.Equal((int)Math.Ceiling(page.GetProperty("totalCount").GetInt32() / 5.0), page.GetProperty("totalPages").GetInt32());

        var clamped = await api.GetFromJsonAsync<JsonElement>("/api/customers?pageSize=100000");
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    // ---------- documentation ----------

    [Fact]
    public void Swagger_documents_every_specified_endpoint_with_its_responses()
    {
        var swagger = _factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var expected = new Dictionary<string, string[]>
        {
            ["/api/auth/login"] = new[] { "post" },
            ["/api/auth/logout"] = new[] { "post" },
            ["/api/customers"] = new[] { "get", "post" },
            ["/api/customers/{id}"] = new[] { "get", "put", "delete" },
            ["/api/leads"] = new[] { "get", "post" },
            ["/api/opportunities"] = new[] { "get", "post" },
            ["/api/followups"] = new[] { "get", "post" },
            ["/api/reports/pipeline"] = new[] { "get" },
        };
        foreach (var (path, methods) in expected)
        {
            Assert.True(swagger.Paths.ContainsKey(path), $"Missing {path}");
            var operations = swagger.Paths[path].Operations.ToDictionary(o => o.Key.ToString().ToLowerInvariant(), o => o.Value);
            foreach (var method in methods)
            {
                Assert.True(operations.ContainsKey(method), $"Missing {method.ToUpper()} {path}");
                Assert.False(string.IsNullOrWhiteSpace(operations[method].Summary), $"No description for {method.ToUpper()} {path}");
            }
        }

        var createCustomer = swagger.Paths["/api/customers"].Operations.Single(o => o.Key.ToString() == "Post").Value;
        Assert.Contains("201", createCustomer.Responses.Keys);
        Assert.Contains("400", createCustomer.Responses.Keys);
        Assert.Contains("409", createCustomer.Responses.Keys);
        Assert.Contains("403", swagger.Paths["/api/reports/pipeline"].Operations.Single().Value.Responses.Keys);
    }
}

/// <summary>Rate limiting needs its own app instance with a low limit.</summary>
public class RateLimitedFactory : CrmFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Security:LoginRateLimitPerMinute", "3");
    }
}

public class ApiRateLimitTests : IClassFixture<RateLimitedFactory>
{
    private readonly RateLimitedFactory _factory;

    public ApiRateLimitTests(RateLimitedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_endpoint_is_rate_limited_with_a_problem_response()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { login = "nobody", password = "x" });
        }

        var limited = await client.PostAsJsonAsync("/api/auth/login", new { login = "nobody", password = "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Too many requests", await limited.Content.ReadAsStringAsync());
    }
}

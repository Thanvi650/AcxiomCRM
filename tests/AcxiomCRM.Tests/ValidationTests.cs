using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 10 — Validation (spec section 5 and 17.8): client-side rules are emitted on every
/// form, the server repeats them, business rules use the exact 5.4 messages, numeric
/// precision is enforced, and error responses never expose internals.
/// </summary>
public class ValidationTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public ValidationTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> MvcLoginAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await TokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", Form(token, ("Login", "rahul.sales@acxiomcrm.local"), ("Password", CrmFactory.Password)));
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

    private async Task<int> RahulsCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Customers.Where(c => c.AssignedTo!.Email == "rahul.sales@acxiomcrm.local").Select(c => c.CustomerId).FirstAsync();
    }

    /// <summary>The opening tag of the input/select with the given id.</summary>
    private static string Field(string html, string id)
    {
        var match = Regex.Match(html, $"<(input|select|textarea)[^>]*\\bid=\"{id}\"[^>]*>");
        Assert.True(match.Success, $"Field {id} not found");
        return match.Value;
    }

    // ---------- 5.1 client-side validation is present on the forms ----------

    [Fact]
    public async Task Customer_form_has_required_email_phone_and_length_rules()
    {
        var html = await (await MvcLoginAsync()).GetStringAsync("/Customers/Create");

        Assert.Contains("data-val-required=\"Customer Name is required.\"", Field(html, "CustomerName"));
        Assert.Contains("data-val-length-max=\"100\"", Field(html, "CustomerName"));
        Assert.Contains("data-val-regex=\"Enter a valid email address.\"", Field(html, "Email"));
        Assert.Contains("data-val-required=\"Email is required.\"", Field(html, "Email"));
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", Field(html, "Phone"));
        Assert.Contains("data-val-length-max=\"1000\"", Field(html, "Notes"));
        Assert.Contains("jquery.validate.unobtrusive.min.js", html);
        Assert.Contains("/js/validation.js", html);
    }

    [Fact]
    public async Task Opportunity_form_has_numeric_date_and_business_rules()
    {
        var html = await (await MvcLoginAsync()).GetStringAsync("/Opportunities/Create");

        var amount = Field(html, "amount");
        Assert.Contains("data-val-number=", amount);
        Assert.Contains("data-val-range=\"Opportunity Amount cannot be negative.\"", amount);
        Assert.Contains("data-val-positive=\"Opportunity Amount must be greater than 0.\"", amount);
        Assert.Contains("data-val-positive-exempt=\"Won,Lost\"", amount);
        Assert.Contains("data-val-decimalplaces-places=\"2\"", amount);
        // HTML min/step become jQuery Validate rules too; they must use the spec's wording.
        Assert.Contains("data-msg-min=\"Opportunity Amount cannot be negative.\"", amount);
        Assert.Contains("data-msg-step=\"Amount can have at most 2 decimal places.\"", amount);

        var probability = Field(html, "probability");
        Assert.Contains("data-val-range=\"Probability must be between 0 and 100.\"", probability);
        Assert.Contains("data-val-range-min=\"0\"", probability);
        Assert.Contains("data-val-range-max=\"100\"", probability);
        Assert.Contains("data-msg-max=\"Probability must be between 0 and 100.\"", probability);

        var close = Field(html, "ExpectedCloseDate");
        Assert.Contains("type=\"date\"", close);
        Assert.Contains("data-val-notinpast=\"Expected Close Date cannot be in the past.\"", close);
        Assert.Contains("data-val-notinpast-other=\"Stage\"", close);
    }

    [Fact]
    public async Task Follow_up_and_lead_forms_have_their_rules()
    {
        var client = await MvcLoginAsync();
        var followUp = await client.GetStringAsync("/FollowUps/Create");
        Assert.Contains("data-val-notinpast=\"Follow-up date cannot be earlier than today.\"", Field(followUp, "FollowUpDate"));
        Assert.Contains("data-msg-min=\"Follow-up date cannot be earlier than today.\"", Field(followUp, "FollowUpDate"));
        Assert.Contains("data-val-required=\"Subject is required.\"", Field(followUp, "Subject"));

        var lead = await client.GetStringAsync("/Leads/Create");
        Assert.Contains("data-val-required=\"Lead Name is required.\"", Field(lead, "LeadName"));
        Assert.Contains("data-val-decimalplaces-places=\"2\"", Field(lead, "ExpectedValue"));
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", Field(lead, "Phone"));
    }

    [Fact]
    public async Task Invalid_fields_render_inline_messages_next_to_the_field()
    {
        var client = await MvcLoginAsync();
        var token = await TokenAsync(client, "/Customers/Create");
        var response = await client.PostAsync("/Customers/Create", Form(token,
            ("CustomerName", "Valid Name"), ("Email", "bad"), ("Phone", "9876"), ("Status", "Active")));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Matches("data-valmsg-for=\"Email\"[^>]*>Enter a valid email address.<", html);
        Assert.Matches("data-valmsg-for=\"Phone\"[^>]*>Enter a valid phone number.<", html);
        Assert.Contains("input-validation-error", Field(html, "Email"));
    }

    // ---------- 5.2 / 5.4 server-side with the exact messages ----------

    public static IEnumerable<object[]> BusinessRules() => new[]
    {
        new object[] { 0m, 50, 10, "Amount", "Opportunity Amount must be greater than 0." },
        new object[] { 1000m, 101, 10, "Probability", "Probability must be between 0 and 100." },
        new object[] { 1000m, 50, -1, "ExpectedCloseDate", "Expected Close Date cannot be in the past." },
        new object[] { 1000.555m, 50, 10, "Amount", "Amount can have at most 2 decimal places." },
    };

    [Theory]
    [MemberData(nameof(BusinessRules))]
    public async Task Api_returns_the_spec_messages_for_business_rules(decimal amount, int probability, int closeInDays, string field, string message)
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var response = await api.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "Rule check", customerId = await RahulsCustomerAsync(), amount, probability,
            expectedCloseDate = DateTime.Today.AddDays(closeInDays).ToString("yyyy-MM-dd"), stage = "Proposal"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains(message, errors.GetProperty(field).EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Two_decimal_places_are_accepted()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var response = await api.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "Precise deal", customerId = await RahulsCustomerAsync(), amount = 1000.55m, probability = 50,
            expectedCloseDate = DateTime.Today.AddDays(10).ToString("yyyy-MM-dd"), stage = "Proposal"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1000.55m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Whitespace_only_and_over_length_text_is_rejected_before_saving()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");

        var blank = await api.PostAsJsonAsync("/api/customers", new { customerName = "   ", email = "blank@example.com", phone = "9800000001", status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Customer Name is required.", await blank.Content.ReadAsStringAsync());

        var longNotes = await api.PostAsJsonAsync("/api/customers", new { customerName = "Long Notes", email = "long@example.com", phone = "9800000002", status = "Active", notes = new string('x', 1001) });
        Assert.Equal(HttpStatusCode.BadRequest, longNotes.StatusCode);
        Assert.Contains("Notes cannot exceed 1000 characters.", await longNotes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mvc_form_shows_friendly_message_for_text_in_a_number_field()
    {
        var client = await MvcLoginAsync();
        var token = await TokenAsync(client, "/Opportunities/Create");
        var response = await client.PostAsync("/Opportunities/Create", Form(token,
            ("OpportunityName", "Typo deal"), ("CustomerId", (await RahulsCustomerAsync()).ToString()), ("Amount", "lots"),
            ("Probability", "50"), ("ExpectedCloseDate", DateTime.Today.AddDays(5).ToString("yyyy-MM-dd")), ("Stage", "Proposal")));

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("'lots' is not a valid value for Amount.", html);
    }

    // ---------- error responses never expose internals ----------

    [Theory]
    [InlineData("{\"leadName\":\"Zed\",\"source\":\"Website\",\"status\":\"Bogus\",\"priority\":\"Low\",\"expectedValue\":10}", "Status", "Enter a valid value for Status.")]
    [InlineData("{\"leadName\":\"Zed\",\"source\":\"Website\",\"status\":\"New\",\"priority\":\"Low\",\"expectedValue\":\"lots\"}", "ExpectedValue", "Enter a valid value for ExpectedValue.")]
    [InlineData("{\"leadName\": ", "", "The request body is not valid JSON.")]
    public async Task Api_bad_input_gets_friendly_errors_without_internals(string body, string field, string message)
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var response = await api.PostAsync("/api/leads", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("System.", text);
        Assert.DoesNotContain("AcxiomCRM.", text);
        Assert.DoesNotContain("LineNumber", text);
        Assert.DoesNotContain("The input field is required.", text);

        var errors = JsonDocument.Parse(text).RootElement.GetProperty("errors");
        Assert.Contains(message, errors.GetProperty(field).EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Api_missing_body_is_reported_plainly()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var response = await api.PostAsync("/api/leads", new StringContent("", Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("System.", text);
        Assert.Contains("A request body is required.", text);
    }
}

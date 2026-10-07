using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

/// <summary>
/// Phase 2 — ASP.NET Core Identity authentication through the real MVC pages
/// (login, register, logout), including anti-forgery tokens and cookie settings.
/// </summary>
public class AuthTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public AuthTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Loads a page and returns its anti-forgery token (the client keeps the matching cookie).</summary>
    private static async Task<string> GetTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No anti-forgery token on {url}");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string token, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = token;
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private async Task<HttpResponseMessage> MvcLoginAsync(HttpClient client, string login, string password, string? returnUrl = null)
    {
        var token = await GetTokenAsync(client, "/Account/Login");
        var fields = new Dictionary<string, string> { ["Login"] = login, ["Password"] = password };
        if (returnUrl is not null) fields["ReturnUrl"] = returnUrl;
        return await PostFormAsync(client, "/Account/Login", token, fields);
    }

    [Fact]
    public async Task Login_with_valid_credentials_redirects_to_dashboard()
    {
        var client = NewClient();
        var response = await MvcLoginAsync(client, "rahul.sales@acxiomcrm.local", CrmFactory.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Auth_cookie_is_httponly_and_samesite_strict()
    {
        var client = NewClient();
        var response = await MvcLoginAsync(client, "rahul.sales@acxiomcrm.local", CrmFactory.Password);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("AcxiomCRM.Auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_with_wrong_password_shows_generic_error()
    {
        var client = NewClient();
        var response = await MvcLoginAsync(client, "rahul.sales@acxiomcrm.local", "Wrong@Pass1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email/username or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_post_without_antiforgery_token_is_rejected()
    {
        var client = NewClient();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = "rahul.sales@acxiomcrm.local",
            ["Password"] = CrmFactory.Password
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ignores_external_return_url()
    {
        var client = NewClient();
        var response = await MvcLoginAsync(client, "rahul.sales@acxiomcrm.local", CrmFactory.Password, "https://evil.example.com/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_ends_the_session_and_is_audited()
    {
        var client = NewClient();
        await MvcLoginAsync(client, "sneha.sales@acxiomcrm.local", CrmFactory.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Customers")).StatusCode);

        var token = await GetTokenAsync(client, "/");
        var logout = await PostFormAsync(client, "/Account/Logout", token, new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var after = await client.GetAsync("/Customers");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login", after.Headers.Location!.ToString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Logout" && a.UserName == "sneha.sales@acxiomcrm.local"));
    }

    [Fact]
    public async Task Register_creates_a_sales_executive_with_a_hashed_password()
    {
        var client = NewClient();
        var token = await GetTokenAsync(client, "/Account/Register");
        const string password = "New@User2026";
        var response = await PostFormAsync(client, "/Account/Register", token, new Dictionary<string, string>
        {
            ["FullName"] = "New Starter",
            ["Email"] = "New.Starter@Example.com",
            ["Password"] = password,
            ["ConfirmPassword"] = password
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(Roles.SalesExecutive, me.GetProperty("role").GetString());

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync("new.starter@example.com");
        Assert.NotNull(user);
        Assert.NotNull(user!.PasswordHash);
        Assert.DoesNotContain(password, user.PasswordHash!);
        Assert.True(await users.CheckPasswordAsync(user, password));

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Register" && a.RecordId == user.Id));
    }

    [Theory]
    [InlineData("alllowercase1!")] // no upper case
    [InlineData("NoDigitsHere!")]  // no digit
    [InlineData("NoSymbol123")]    // no symbol
    [InlineData("Ab1!")]           // too short
    public async Task Register_enforces_the_password_policy(string password)
    {
        var client = NewClient();
        var token = await GetTokenAsync(client, "/Account/Register");
        var email = $"weak{Guid.NewGuid():N}@example.com";
        var response = await PostFormAsync(client, "/Account/Register", token, new Dictionary<string, string>
        {
            ["FullName"] = "Weak Password",
            ["Email"] = email,
            ["Password"] = password,
            ["ConfirmPassword"] = password
        });

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var created = await users.FindByEmailAsync(email) is not null;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form re-displayed with the error
        Assert.False(created);
    }

    [Fact]
    public async Task Register_rejects_an_email_that_is_already_used()
    {
        var client = NewClient();
        var token = await GetTokenAsync(client, "/Account/Register");
        var response = await PostFormAsync(client, "/Account/Register", token, new Dictionary<string, string>
        {
            ["FullName"] = "Copy Cat",
            ["Email"] = "rahul.sales@acxiomcrm.local",
            ["Password"] = "Copy@Cat2026",
            ["ConfirmPassword"] = "Copy@Cat2026"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("An account with this email already exists.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Inactive_account_is_only_revealed_after_a_correct_password()
    {
        const string email = "arjun.manager@acxiomcrm.local";
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            user.IsActive = false;
            await users.UpdateAsync(user);
        }

        var client = NewClient();
        var wrong = await (await MvcLoginAsync(client, email, "Wrong@Pass1")).Content.ReadAsStringAsync();
        Assert.Contains("Invalid email/username or password.", wrong);
        Assert.DoesNotContain("inactive", wrong, StringComparison.OrdinalIgnoreCase);

        var right = await MvcLoginAsync(client, email, CrmFactory.Password);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode); // stays on the login page, not signed in
        Assert.Contains("This account is inactive.", await right.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Customers")).StatusCode);
    }
}

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
/// Phase 11 — Audit Log (spec 4.9, 17.16): every required event is captured with its fields,
/// secrets never reach the log, the log is append-only even below the application, and the
/// audit screens filter, compare and export correctly.
/// </summary>
public class AuditTests : IClassFixture<CrmFactory>
{
    private readonly CrmFactory _factory;

    public AuditTests(CrmFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> MvcLoginAsync(string email)
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

    private Task<AuditLog> LatestAsync(string action, string entity) =>
        WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.EntityName == entity)
            .OrderByDescending(a => a.AuditLogId).FirstAsync());

    // ---------- required events and fields (17.16) ----------

    [Fact]
    public async Task Login_success_failure_and_logout_are_recorded_with_user_ip_and_result()
    {
        var client = await MvcLoginAsync("kiran.sales@acxiomcrm.local");
        var login = await LatestAsync("Login", "Account");
        Assert.Equal("kiran.sales@acxiomcrm.local", login.UserName);
        Assert.False(string.IsNullOrEmpty(login.UserId));
        Assert.Equal("Success", login.Result);
        Assert.Equal("10.0.0.5", login.IpAddress);
        Assert.True((DateTime.Now - login.CreatedDate).TotalMinutes < 1);

        await MvcLoginAsync("nobody@example.com");
        Assert.Equal("UnknownUser", (await LatestAsync("FailedLogin", "Account")).Result);

        var token = await TokenAsync(client, "/");
        await client.PostAsync("/Account/Logout", Form(token));
        Assert.Equal("kiran.sales@acxiomcrm.local", (await LatestAsync("Logout", "Account")).UserName);
    }

    [Fact]
    public async Task Create_update_and_delete_record_old_and_new_values()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var created = await (await api.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Audit Trail Co", email = "trail@audit.example", phone = "9811177701", status = "Prospect"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("customerId").GetInt32();

        await api.PutAsJsonAsync($"/api/customers/{id}", new
        {
            customerName = "Audit Trail Co", email = "trail@audit.example", phone = "9811177702", status = "Active"
        });
        await api.DeleteAsync($"/api/customers/{id}");

        var entries = await WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString()).OrderBy(a => a.AuditLogId).ToListAsync());
        Assert.Equal(new[] { "Create", "Update", "StatusChange", "Delete" }, entries.Select(e => e.Action));

        var create = entries[0];
        Assert.Null(create.OldValue);
        Assert.Contains("9811177701", create.NewValue);

        var update = entries[1];
        Assert.Contains("9811177701", update.OldValue);
        Assert.Contains("9811177702", update.NewValue);

        var delete = entries[3];
        Assert.Contains("9811177702", delete.OldValue);
        Assert.Null(delete.NewValue);
        Assert.All(entries, e => Assert.Equal("rahul.sales@acxiomcrm.local", e.UserName));
    }

    [Fact]
    public async Task Role_change_and_password_reset_are_recorded_without_secrets()
    {
        var admin = await MvcLoginAsync("admin@acxiomcrm.local");
        var snehaId = await WithDbAsync(db => db.Users.Where(u => u.Email == "sneha.sales@acxiomcrm.local").Select(u => u.Id).SingleAsync());

        var token = await TokenAsync(admin, $"/Users/Edit/{snehaId}");
        await admin.PostAsync($"/Users/Edit/{snehaId}", Form(token,
            ("FullName", "Sneha Reddy"), ("Email", "sneha.sales@acxiomcrm.local"), ("Role", "Manager"), ("IsActive", "true")));
        var roleChange = await LatestAsync("RoleChange", "User");
        Assert.Equal(snehaId, roleChange.RecordId);
        Assert.Contains("SalesExecutive", roleChange.OldValue);
        Assert.Contains("Manager", roleChange.NewValue);

        const string newPassword = "Reset@Secret99";
        token = await TokenAsync(admin, $"/Users/ResetPassword/{snehaId}");
        await admin.PostAsync("/Users/ResetPassword", Form(token, ("Id", snehaId), ("NewPassword", newPassword), ("ConfirmPassword", newPassword)));
        Assert.Equal(snehaId, (await LatestAsync("PasswordReset", "User")).RecordId);

        // Nothing secret anywhere in the audit table.
        var all = await WithDbAsync(db => db.AuditLogs.AsNoTracking().ToListAsync());
        var text = string.Join("\n", all.Select(a => $"{a.OldValue} {a.NewValue} {a.UserName}"));
        Assert.DoesNotContain(newPassword, text);
        Assert.DoesNotContain(CrmFactory.Password, text);
        Assert.DoesNotContain("PasswordHash", text);
        Assert.DoesNotContain("SecurityStamp", text);
    }

    [Fact]
    public async Task Access_denied_attempts_are_recorded()
    {
        var sales = await MvcLoginAsync("rahul.sales@acxiomcrm.local");
        await sales.GetAsync("/AuditLogs");
        var mvc = await LatestAsync("AccessDenied", "Security");
        Assert.Equal("rahul.sales@acxiomcrm.local", mvc.UserName);
        Assert.Equal("Denied", mvc.Result);
        Assert.Contains("/AuditLogs", mvc.NewValue);

        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/reports/pipeline")).StatusCode);
        Assert.Contains("/api/reports/pipeline", (await LatestAsync("AccessDenied", "Security")).NewValue);
    }

    [Fact]
    public async Task Data_exports_are_recorded()
    {
        var admin = await MvcLoginAsync("admin@acxiomcrm.local");
        var csv = await admin.GetAsync("/Reports/Customers?export=csv&status=Active");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var report = await LatestAsync("Export", "Report");
        Assert.Equal("customers", report.RecordId);
        Assert.Contains("status=Active", report.NewValue);

        await admin.GetAsync("/AuditLogs?export=csv");
        Assert.Equal("admin@acxiomcrm.local", (await LatestAsync("Export", "AuditLog")).UserName);
    }

    // ---------- append-only protection ----------

    [Fact]
    public async Task Application_cannot_modify_audit_rows()
    {
        await WithDbAsync<int>(async db =>
        {
            var entry = await db.AuditLogs.FirstAsync();
            entry.Result = "Tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            return 0;
        });
        await WithDbAsync<int>(async db =>
        {
            db.AuditLogs.Remove(await db.AuditLogs.FirstAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            return 0;
        });
    }

    [Fact]
    public async Task Database_rejects_bulk_or_direct_changes_to_audit_rows()
    {
        var before = await WithDbAsync(db => db.AuditLogs.CountAsync());

        // These bypass the application's change tracking; the database trigger must stop them.
        var update = await Record.ExceptionAsync(() => WithDbAsync(db => db.AuditLogs.ExecuteUpdateAsync(s => s.SetProperty(a => a.Result, "Tampered"))));
        var delete = await Record.ExceptionAsync(() => WithDbAsync(db => db.AuditLogs.ExecuteDeleteAsync()));
        var raw = await Record.ExceptionAsync(() => WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM AuditLogs")));

        Assert.NotNull(update);
        Assert.NotNull(delete);
        Assert.NotNull(raw);
        Assert.Contains("append-only", delete!.Message + delete.InnerException?.Message);
        Assert.Equal(before, await WithDbAsync(db => db.AuditLogs.CountAsync()));
        Assert.False(await WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Result == "Tampered")));
    }

    // ---------- audit screens ----------

    [Fact]
    public async Task Audit_log_filters_by_user_module_action_and_date()
    {
        var admin = await MvcLoginAsync("admin@acxiomcrm.local");
        var adminId = await WithDbAsync(db => db.Users.Where(u => u.Email == "admin@acxiomcrm.local").Select(u => u.Id).SingleAsync());
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        var html = await admin.GetStringAsync($"/AuditLogs?userId={adminId}&entity=Account&eventAction=Login&from={today}&to={today}");
        var rows = Regex.Matches(html, "<tr>\\s*<td class=\"text-nowrap\">").Count;
        Assert.True(rows > 0);
        Assert.DoesNotContain(">Logout<", html);
        Assert.DoesNotContain("rahul.sales@acxiomcrm.local", Regex.Match(html, "<tbody>[\\s\\S]*</tbody>").Value);

        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var none = await admin.GetStringAsync($"/AuditLogs?eventAction=Login&from={yesterday}&to={yesterday}");
        Assert.Contains("No audit entries match your filters.", none);
    }

    [Fact]
    public async Task Audit_details_show_only_the_fields_that_changed()
    {
        var api = await _factory.LoginAsync("rahul.sales@acxiomcrm.local");
        var created = await (await api.PostAsJsonAsync("/api/customers", new
        {
            customerName = "Diff Co", email = "diff@audit.example", phone = "9811177703", status = "Active", city = "Pune"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("customerId").GetInt32();
        await api.PutAsJsonAsync($"/api/customers/{id}", new
        {
            customerName = "Diff Co", email = "diff@audit.example", phone = "9811177703", status = "Active", city = "Mumbai"
        });
        var updateId = await WithDbAsync(db => db.AuditLogs.Where(a => a.EntityName == "Customer" && a.Action == "Update" && a.RecordId == id.ToString()).Select(a => a.AuditLogId).SingleAsync());

        var admin = await MvcLoginAsync("admin@acxiomcrm.local");
        var html = await admin.GetStringAsync($"/AuditLogs/Details/{updateId}");
        var changes = Regex.Match(html, "Changes \\(\\d+\\)[\\s\\S]*?</table>").Value;

        Assert.Contains("Changes (1)", changes);
        Assert.Contains("City", changes);
        Assert.Contains("Pune", changes);
        Assert.Contains("Mumbai", changes);
        Assert.DoesNotContain("diff@audit.example", changes); // unchanged fields are not listed
    }

    [Fact]
    public async Task Manager_view_excludes_security_events_and_admin_only_export()
    {
        var manager = await MvcLoginAsync("priya.manager@acxiomcrm.local");
        var html = await manager.GetStringAsync("/AuditLogs");
        var body = Regex.Match(html, "<tbody>[\\s\\S]*</tbody>").Value;
        Assert.DoesNotContain(">Login<", body);
        Assert.DoesNotContain(">AccessDenied<", body);
        Assert.DoesNotContain("Export CSV", html);
        Assert.Equal(HttpStatusCode.Redirect, (await manager.GetAsync("/AuditLogs?export=csv")).StatusCode);
    }
}

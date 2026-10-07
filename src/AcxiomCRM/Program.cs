using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AcxiomCRM.Data;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.WebEncoders;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var isProduction = builder.Environment.IsProduction();

// ---------- Data ----------
// SQL Server is the application database. "Sqlite" is only for the automated tests
// (or running without SQL Server): set Database:Provider=Sqlite to use it.
var databaseProvider = config.GetValue("Database:Provider", DatabaseProviders.SqlServer)!;
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (databaseProvider.Equals(DatabaseProviders.Sqlite, StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
        options.UseSqlite(config.GetConnectionString("SqliteConnection") ?? "Data Source=App_Data/acxiomcrm.db");
    }
    else
    {
        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        options.UseSqlServer(connectionString);
    }
});

// ---------- Identity: hashing, password policy, lockout ----------
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 4;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = config.GetValue("Security:Lockout:MaxFailedAttempts", 5);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(config.GetValue("Security:Lockout:LockoutMinutes", 15));

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

// Re-validate the security stamp often so deactivation / role changes take effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.Name = "AcxiomCRM.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // APIs get status codes, not redirects to the login page.
    options.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.IsApiRequest()) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = async ctx =>
    {
        // A signed-in user tried to reach something their role doesn't allow: a security event.
        var audit = ctx.HttpContext.RequestServices.GetRequiredService<IAuditService>();
        await audit.LogAsync(AuditActions.AccessDenied, "Security", null,
            newValue: new { Path = ctx.Request.Path.Value, Method = ctx.Request.Method }, result: "Denied");

        if (ctx.Request.IsApiRequest()) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else ctx.Response.Redirect(ctx.RedirectUri);
    };
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "AcxiomCRM.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

// Every endpoint requires an authenticated user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// ---------- MVC + API ----------
builder.Services
    .AddControllersWithViews(options =>
    {
        // Anti-forgery validation for every state-changing MVC request (API controllers opt out).
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        FriendlyValidation.Configure(options.ModelBindingMessageProvider);
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Never echo JSON parser exception text (it contains .NET type names and positions).
        options.AllowInputFormatterExceptionMessages = false;
    })
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = FriendlyValidation.ApiResponse);

// Write Unicode text (₹, –, Indian-language names) as-is in HTML instead of &#x..; codes.
// HTML-sensitive characters (<, >, &, quotes) are still escaped.
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

// Throttle authentication endpoints (per client IP).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Security:LoginRateLimitPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AcxiomCRM API",
        Version = "v1",
        Description = "Secured REST API for AcxiomCRM. Authenticate with POST /api/auth/login (cookie-based); " +
                      "every other endpoint requires an authenticated user and applies role/ownership scope."
    });
    c.DocInclusionPredicate((_, api) => api.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true);
    var xml = Path.Combine(AppContext.BaseDirectory, "AcxiomCRM.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml);
});

// ---------- Application services ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserScope, UserScope>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<LeadService>();
builder.Services.AddScoped<OpportunityService>();
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<LookupService>();

var culture = CultureInfo.GetCultureInfo("en-IN");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var app = builder.Build();

await DbSeeder.SeedAsync(app.Services);

// ---------- Pipeline ----------
// Friendly errors only: stack traces and database details are logged, never shown.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerPathFeature>();
    app.Logger.LogError(feature?.Error, "Unhandled exception for {Path}", feature?.Path);

    if (feature?.Path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 500,
            Title = "An unexpected error occurred.",
            Detail = "The request could not be completed. Please try again later."
        }, options: null, contentType: "application/problem+json");
    }
    else
    {
        context.Response.Redirect("/Home/Error");
    }
}));

if (isProduction)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseWhen(ctx => !ctx.Request.IsApiRequest(),
    branch => branch.UseStatusCodePagesWithReExecute("/Home/StatusCode", "?code={0}"));

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DocumentTitle = "AcxiomCRM API");
}

app.UseStaticFiles();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(culture),
    SupportedCultures = new[] { culture },
    SupportedUICultures = new[] { culture }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }

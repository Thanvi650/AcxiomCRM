using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcxiomCRM.Data;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcxiomCRM.Services;

// ---------- Service result ----------

public enum ServiceStatus
{
    Success,
    NotFound,
    Invalid,
    Conflict,
    Forbidden
}

public record ServiceError(string Field, string Message);

public class ServiceResult<T>
{
    public ServiceStatus Status { get; private init; }
    public T? Value { get; private init; }
    public IReadOnlyList<ServiceError> Errors { get; private init; } = Array.Empty<ServiceError>();
    public bool Succeeded => Status == ServiceStatus.Success;

    public static ServiceResult<T> Ok(T value) => new() { Status = ServiceStatus.Success, Value = value };
    public static ServiceResult<T> NotFound() => new() { Status = ServiceStatus.NotFound };
    public static ServiceResult<T> Forbidden(string message) =>
        new() { Status = ServiceStatus.Forbidden, Errors = new[] { new ServiceError(string.Empty, message) } };
    public static ServiceResult<T> Invalid(IEnumerable<ServiceError> errors) =>
        new() { Status = ServiceStatus.Invalid, Errors = errors.ToList() };
    public static ServiceResult<T> Invalid(string field, string message) => Invalid(new[] { new ServiceError(field, message) });
    public static ServiceResult<T> Conflict(IEnumerable<ServiceError> errors) =>
        new() { Status = ServiceStatus.Conflict, Errors = errors.ToList() };
    public static ServiceResult<T> Conflict(string field, string message) => Conflict(new[] { new ServiceError(field, message) });

    /// <summary>Collects DataAnnotations + business errors; duplicates become a 409 Conflict.</summary>
    public static ServiceResult<T> FromErrors(List<ServiceError> errors, bool isConflict) =>
        isConflict ? Conflict(errors) : Invalid(errors);
}

internal static class ValidationExtensions
{
    public static List<ServiceError> AnnotationErrors(object input) =>
        ObjectValidator.Validate(input)
            .Select(r => new ServiceError(r.MemberNames.FirstOrDefault() ?? string.Empty, r.ErrorMessage ?? "Invalid value."))
            .ToList();

    public static string? Clean(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// ---------- Current user scope (row-level authorization) ----------

/// <summary>
/// Resolves what the signed-in user may see: Admin = everything, Manager = own + team,
/// Sales Executive = own/assigned records. Every CRM query goes through this.
/// </summary>
public interface IUserScope
{
    string UserId { get; }
    string UserName { get; }
    bool IsAdmin { get; }
    bool IsManager { get; }
    bool IsSalesExecutive { get; }
    string RoleName { get; }

    /// <summary>User ids whose records are visible; null means unrestricted.</summary>
    Task<IReadOnlySet<string>?> VisibleUserIdsAsync();

    Task<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query) where T : class, IAssignable;
    Task<bool> CanAccessAsync(IAssignable entity);
    Task<bool> CanAssignToAsync(string? userId);
    Task<List<ApplicationUser>> AssignableUsersAsync();
}

public class UserScope : IUserScope
{
    private readonly IHttpContextAccessor _http;
    private readonly ApplicationDbContext _db;
    private IReadOnlySet<string>? _visible;
    private bool _loaded;

    public UserScope(IHttpContextAccessor http, ApplicationDbContext db)
    {
        _http = http;
        _db = db;
    }

    private ClaimsPrincipal User => _http.HttpContext?.User ?? new ClaimsPrincipal();

    public string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    public string UserName => User.Identity?.Name ?? string.Empty;
    public bool IsAdmin => User.IsInRole(Roles.Admin);
    public bool IsManager => !IsAdmin && User.IsInRole(Roles.Manager);
    public bool IsSalesExecutive => !IsAdmin && !IsManager;
    public string RoleName => IsAdmin ? Roles.Admin : IsManager ? Roles.Manager : Roles.SalesExecutive;

    public async Task<IReadOnlySet<string>?> VisibleUserIdsAsync()
    {
        if (_loaded) return _visible;
        _loaded = true;

        if (IsAdmin)
        {
            _visible = null;
        }
        else if (IsManager)
        {
            var team = await _db.Users.Where(u => u.ManagerId == UserId).Select(u => u.Id).ToListAsync();
            team.Add(UserId);
            _visible = team.ToHashSet();
        }
        else
        {
            _visible = new HashSet<string> { UserId };
        }
        return _visible;
    }

    public async Task<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query) where T : class, IAssignable
    {
        var ids = await VisibleUserIdsAsync();
        if (ids is null) return query;

        // Build e => ids.Contains(e.AssignedToId) against the concrete entity type so EF can translate it.
        var parameter = Expression.Parameter(typeof(T), "e");
        var property = Expression.Property(parameter, nameof(IAssignable.AssignedToId));
        var contains = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(string) },
            Expression.Constant(ids.ToList()), property);
        return query.Where(Expression.Lambda<Func<T, bool>>(contains, parameter));
    }

    public async Task<bool> CanAccessAsync(IAssignable entity)
    {
        var ids = await VisibleUserIdsAsync();
        return ids is null || (entity.AssignedToId is not null && ids.Contains(entity.AssignedToId));
    }

    public async Task<bool> CanAssignToAsync(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        var ids = await VisibleUserIdsAsync();
        if (ids is not null && !ids.Contains(userId)) return false;
        return await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive);
    }

    public async Task<List<ApplicationUser>> AssignableUsersAsync()
    {
        var ids = await VisibleUserIdsAsync();
        var query = _db.Users.AsNoTracking().Where(u => u.IsActive);
        if (ids is not null)
        {
            var list = ids.ToList();
            query = query.Where(u => list.Contains(u.Id));
        }
        return await query.OrderBy(u => u.FullName).ToListAsync();
    }
}

// ---------- Audit ----------

public static class AuditActions
{
    public const string Login = "Login";
    public const string FailedLogin = "FailedLogin";
    public const string Lockout = "Lockout";
    public const string Logout = "Logout";
    public const string Register = "Register";
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string Convert = "Convert";
    public const string StatusChange = "StatusChange";
    public const string Complete = "Complete";
    public const string Reschedule = "Reschedule";
    public const string RoleChange = "RoleChange";
    public const string Activate = "Activate";
    public const string Deactivate = "Deactivate";
    public const string PasswordReset = "PasswordReset";
    public const string PasswordChange = "PasswordChange";
    public const string Unlock = "Unlock";
    public const string AccessDenied = "AccessDenied";
    public const string Export = "Export";
}

public interface IAuditService
{
    Task LogAsync(
        string action,
        string entityName,
        string? recordId = null,
        object? oldValue = null,
        object? newValue = null,
        string result = "Success",
        string? userId = null,
        string? userName = null);
}

public class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<Type> ScalarTypes = new()
    {
        typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(double), typeof(bool),
        typeof(DateTime), typeof(DateTimeOffset), typeof(Guid)
    };

    /// <summary>Property names that must never reach the audit log.</summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "NewPassword",
        "ConfirmPassword", "CurrentPassword", "Token"
    };

    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditService(ApplicationDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task LogAsync(
        string action, string entityName, string? recordId = null, object? oldValue = null, object? newValue = null,
        string result = "Success", string? userId = null, string? userName = null)
    {
        var context = _http.HttpContext;
        var principal = context?.User;

        _db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            EntityName = entityName,
            RecordId = recordId,
            OldValue = Serialize(oldValue),
            NewValue = Serialize(newValue),
            Result = result,
            UserId = userId ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier),
            UserName = userName ?? principal?.Identity?.Name,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            CreatedDate = DateTime.Now
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Flat snapshot of an entity's scalar fields (no navigation properties, no secrets).</summary>
    public static Dictionary<string, object?> Snapshot(object entity)
    {
        var values = new Dictionary<string, object?>();
        foreach (var property in entity.GetType().GetProperties())
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!property.CanRead || SensitiveNames.Contains(property.Name)) continue;
            if (!type.IsEnum && !ScalarTypes.Contains(type)) continue;
            if (property.GetIndexParameters().Length > 0) continue;
            values[property.Name] = property.GetValue(entity);
        }
        return values;
    }

    private static string? Serialize(object? value) => value switch
    {
        null => null,
        string s => s,
        _ when value.GetType().IsClass && value is not System.Collections.IDictionary && !value.GetType().Name.Contains("AnonymousType")
            => JsonSerializer.Serialize(Snapshot(value), JsonOptions),
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };
}

// ---------- Authentication ----------

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    LockedOut,
    Inactive
}

public record LoginResult(LoginOutcome Outcome, ApplicationUser? User, DateTimeOffset? LockoutEnd = null);

/// <summary>Login logic shared by the MVC Account controller and the REST auth endpoint.</summary>
public class AuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IAuditService _audit;

    public AuthService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditService audit)
    {
        _users = users;
        _signIn = signIn;
        _audit = audit;
    }

    public async Task<LoginResult> LoginAsync(string login, string password, bool rememberMe)
    {
        login = login.Trim();
        var user = await _users.FindByEmailAsync(login) ?? await _users.FindByNameAsync(login);

        if (user is null)
        {
            // Never log the attempted password; only the identifier supplied.
            await _audit.LogAsync(AuditActions.FailedLogin, "Account", null, result: "UnknownUser", userName: login);
            return new LoginResult(LoginOutcome.InvalidCredentials, null);
        }

        // Verify the password first (counting failures towards lockout) WITHOUT signing in.
        // Checking IsActive only after a correct password means an attacker can't use the
        // "inactive" message to discover which accounts exist.
        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (!user.IsActive)
            {
                await _audit.LogAsync(AuditActions.FailedLogin, "Account", user.Id, result: "Inactive", userId: user.Id, userName: user.UserName);
                return new LoginResult(LoginOutcome.Inactive, user);
            }

            await _signIn.SignInAsync(user, isPersistent: rememberMe);
            await _audit.LogAsync(AuditActions.Login, "Account", user.Id, userId: user.Id, userName: user.UserName);
            return new LoginResult(LoginOutcome.Success, user);
        }

        if (result.IsLockedOut)
        {
            var end = await _users.GetLockoutEndDateAsync(user);
            await _audit.LogAsync(AuditActions.Lockout, "Account", user.Id, result: "LockedOut", userId: user.Id, userName: user.UserName);
            return new LoginResult(LoginOutcome.LockedOut, user, end);
        }

        await _audit.LogAsync(AuditActions.FailedLogin, "Account", user.Id, result: "InvalidPassword", userId: user.Id, userName: user.UserName);
        return new LoginResult(LoginOutcome.InvalidCredentials, user);
    }

    public async Task LogoutAsync()
    {
        await _audit.LogAsync(AuditActions.Logout, "Account", null);
        await _signIn.SignOutAsync();
    }
}

/// <summary>Adds the user's display name to the auth cookie.</summary>
public class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public const string FullNameClaim = "full_name";

    public AppClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(FullNameClaim, user.FullName));
        return identity;
    }
}

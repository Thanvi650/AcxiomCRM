using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Dtos;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Validation;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// ---------- Account ----------

public class LoginViewModel
{
    [Required(ErrorMessage = "Email or username is required.")]
    [StringLength(256)]
    [Display(Name = "Email or Username")]
    public string Login { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required(ErrorMessage = "Full Name is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Full Name must be between 2 and 100 characters.")]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [StringLength(150)]
    [RegularExpression(ValidationPatterns.Email, ErrorMessage = ValidationPatterns.EmailMessage)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [RegularExpression(ValidationPatterns.Password, ErrorMessage = ValidationPatterns.PasswordMessage)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm your password.")]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    [Display(Name = "Current Password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required.")]
    [RegularExpression(ValidationPatterns.Password, ErrorMessage = ValidationPatterns.PasswordMessage)]
    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm the new password.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm New Password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

// ---------- Shared list filter ----------

public class ListFilter
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public string? AssignedTo { get; set; }
    public string? Sort { get; set; }
    public int Page { get; set; } = 1;

    [DataType(DataType.Date)]
    public DateTime? From { get; set; }

    [DataType(DataType.Date)]
    public DateTime? To { get; set; }

    public string? Type { get; set; }
}

// ---------- Customers ----------

public class CustomerFormViewModel : CustomerInputDto
{
    public int? CustomerId { get; set; }
    public string? CustomerCode { get; set; }

    [ValidateNever]
    public List<SelectListItem> Users { get; set; } = new();

    public bool CanChooseOwner { get; set; }
}

public class CustomerListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<Customer> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
}

public class CustomerDetailsViewModel
{
    public Customer Customer { get; set; } = null!;
    public string? CreatedByName { get; set; }
    public List<Opportunity> Opportunities { get; set; } = new();
    public List<FollowUp> FollowUps { get; set; } = new();
    public List<Activity> Activities { get; set; } = new();
    public List<AuditLog> History { get; set; } = new();
}

// ---------- Leads ----------

public class LeadFormViewModel : LeadInputDto
{
    public int? LeadId { get; set; }
    public string? LeadCode { get; set; }

    [ValidateNever]
    public List<SelectListItem> Users { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> AllowedStatuses { get; set; } = new();

    public bool CanChooseOwner { get; set; }
}

public class LeadListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<Lead> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
}

public class LeadDetailsViewModel
{
    public Lead Lead { get; set; } = null!;
    public List<FollowUp> FollowUps { get; set; } = new();
    public List<Activity> Activities { get; set; } = new();
    public List<Opportunity> Opportunities { get; set; } = new();
    public List<AuditLog> History { get; set; } = new();
    public IReadOnlyList<LeadStatus> NextStatuses { get; set; } = Array.Empty<LeadStatus>();
}

public class ConvertLeadViewModel : ConvertLeadInputDto
{
    public int LeadId { get; set; }

    [ValidateNever]
    public Lead Lead { get; set; } = null!;
}

// ---------- Opportunities ----------

public class OpportunityFormViewModel : OpportunityInputDto
{
    public int? OpportunityId { get; set; }

    [ValidateNever]
    public List<SelectListItem> Customers { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Leads { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Users { get; set; } = new();

    public bool CanChooseOwner { get; set; }
}

public class OpportunityListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<Opportunity> Items { get; set; } = null!;
    public decimal FilteredAmount { get; set; }
    public decimal FilteredWeighted { get; set; }
}

public class PipelineBoardViewModel
{
    public Dictionary<OpportunityStage, List<Opportunity>> Columns { get; set; } = new();
}

// ---------- Follow-ups ----------

public class FollowUpFormViewModel : FollowUpInputDto
{
    public int? FollowUpId { get; set; }

    [ValidateNever]
    public List<SelectListItem> Customers { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Leads { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Opportunities { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Users { get; set; } = new();

    public bool CanChooseOwner { get; set; }
}

public class RescheduleFollowUpViewModel
{
    public int FollowUpId { get; set; }

    [ValidateNever]
    public FollowUp? FollowUp { get; set; }

    [Required(ErrorMessage = "New follow-up date is required.")]
    [DataType(DataType.DateTime)]
    [NotInPast(ErrorMessage = "Follow-up date cannot be earlier than today.")]
    [Display(Name = "New Follow-Up Date")]
    public DateTime? NewDate { get; set; }

    [StringLength(1000, ErrorMessage = "Remarks cannot exceed 1000 characters.")]
    public string? Remarks { get; set; }
}

public class FollowUpListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<FollowUp> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
    public int OverdueCount { get; set; }
    public int DueTodayCount { get; set; }
    public int UpcomingCount { get; set; }
    public string View { get; set; } = "all";
}

// ---------- Activities ----------

public class ActivityFormViewModel : ActivityInputDto
{
    public int? ActivityId { get; set; }

    [ValidateNever]
    public List<SelectListItem> Customers { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Leads { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> Users { get; set; } = new();

    public bool CanChooseOwner { get; set; }
}

public class ActivityListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<Activity> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
}

// ---------- Users & roles ----------

public class UserListItem
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? ManagerName { get; set; }
    public bool IsActive { get; set; }
    public bool IsLockedOut { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class UserListViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PagedList<UserListItem> Items { get; set; } = null!;
    public bool CanManage { get; set; }
}

public class UserFormViewModel
{
    public string? Id { get; set; }

    [Required(ErrorMessage = "Full Name is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Full Name must be between 2 and 100 characters.")]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [StringLength(150)]
    [RegularExpression(ValidationPatterns.Email, ErrorMessage = ValidationPatterns.EmailMessage)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Role is required.")]
    [ValidRole]
    public string Role { get; set; } = Roles.SalesExecutive;

    [Display(Name = "Reports To (Manager)")]
    public string? ManagerId { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    /// <summary>Only used when creating a user.</summary>
    [RegularExpression(ValidationPatterns.Password, ErrorMessage = ValidationPatterns.PasswordMessage)]
    [DataType(DataType.Password)]
    [Display(Name = "Initial Password")]
    public string? Password { get; set; }

    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    public string? ConfirmPassword { get; set; }

    [ValidateNever]
    public List<SelectListItem> Managers { get; set; } = new();

    [ValidateNever]
    public List<SelectListItem> RoleOptions { get; set; } = new();
}

public class ResetPasswordViewModel
{
    public string Id { get; set; } = string.Empty;
    public string? Email { get; set; }

    [Required(ErrorMessage = "New password is required.")]
    [RegularExpression(ValidationPatterns.Password, ErrorMessage = ValidationPatterns.PasswordMessage)]
    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm the new password.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class RoleSummary
{
    public string Name { get; set; } = string.Empty;
    public int UserCount { get; set; }
    public string Scope { get; set; } = string.Empty;
}

public class RolesViewModel
{
    public List<RoleSummary> Roles { get; set; } = new();

    /// <summary>Module → (Admin, Manager, Sales Executive) access.</summary>
    public List<(string Module, string Admin, string Manager, string Sales)> Matrix { get; set; } = new();
}

// ---------- Audit ----------

public class AuditListViewModel
{
    public string? UserId { get; set; }
    public string? Entity { get; set; }

    /// <summary>Audit action filter (not named "Action" to avoid clashing with the MVC route value).</summary>
    public string? EventAction { get; set; }

    /// <summary>date (oldest first) | user | action | entity; default newest first.</summary>
    public string? Sort { get; set; }

    [DataType(DataType.Date)]
    public DateTime? From { get; set; }

    [DataType(DataType.Date)]
    public DateTime? To { get; set; }

    public int Page { get; set; } = 1;

    public PagedList<AuditLog> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public bool IsLimitedView { get; set; }
}

// ---------- Dashboard ----------

public class DashboardFilter
{
    /// <summary>all | today | week | month | custom</summary>
    public string Range { get; set; } = "all";

    [DataType(DataType.Date)]
    public DateTime? From { get; set; }

    [DataType(DataType.Date)]
    public DateTime? To { get; set; }

    /// <summary>Inclusive start / exclusive end, or nulls for all time.</summary>
    public (DateTime? Start, DateTime? End) Resolve()
    {
        var today = DateTime.Today;
        return Range switch
        {
            "today" => (today, today.AddDays(1)),
            "week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(1)),
            "month" => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            "custom" => (From?.Date, To?.Date.AddDays(1)),
            _ => (null, null)
        };
    }

    public string Description
    {
        get
        {
            var (start, end) = Resolve();
            if (start is null && end is null) return "All time";
            var s = start?.ToString("dd MMM yyyy") ?? "beginning";
            var e = end?.AddDays(-1).ToString("dd MMM yyyy") ?? "today";
            return $"{s} – {e}";
        }
    }
}

public class TeamPerformanceRow
{
    public string OwnerName { get; set; } = string.Empty;
    public int OpenOpportunities { get; set; }
    public decimal PipelineAmount { get; set; }
    public decimal WonAmount { get; set; }
    public int OpenLeads { get; set; }
    public int PendingFollowUps { get; set; }
}

public class ChartSeries
{
    public List<string> Labels { get; set; } = new();
    public List<decimal> Values { get; set; } = new();
    public List<decimal> SecondaryValues { get; set; } = new();
}

public class DashboardViewModel
{
    public DashboardFilter Filter { get; set; } = new();
    public string ScopeLabel { get; set; } = string.Empty;

    public int TotalCustomers { get; set; }
    public int TotalLeads { get; set; }
    public int OpenLeads { get; set; }
    public int TotalOpportunities { get; set; }
    public int OpenOpportunities { get; set; }
    public int WonOpportunities { get; set; }
    public int LostOpportunities { get; set; }
    public decimal TotalPipelineValue { get; set; }
    public decimal WeightedPipelineValue { get; set; }
    public decimal WonValue { get; set; }
    public int PendingFollowUps { get; set; }
    public int OverdueFollowUps { get; set; }
    public decimal ConversionRate { get; set; }
    public decimal WinRate { get; set; }

    public ChartSeries LeadStatusChart { get; set; } = new();
    public ChartSeries PipelineChart { get; set; } = new();
    public ChartSeries MonthlySalesChart { get; set; } = new();

    public List<FollowUp> UpcomingFollowUps { get; set; } = new();
    public List<Opportunity> ClosingSoon { get; set; } = new();
    public List<TeamPerformanceRow> Team { get; set; } = new();
    public List<AuditLog> RecentAudit { get; set; } = new();

    // Admin-only security statistics
    public bool ShowSecurityStats { get; set; }
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int LockedUsers { get; set; }
    public int FailedLoginsToday { get; set; }
}

// ---------- Reports ----------

public class ReportViewModel<T>
{
    public string Title { get; set; } = string.Empty;
    public ListFilter Filter { get; set; } = new();
    public PagedList<T> Items { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
    public Dictionary<string, string> Summary { get; set; } = new();
}

public class PipelineReportViewModel
{
    public ListFilter Filter { get; set; } = new();
    public PipelineReportDto Report { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
}

public class ConversionReportRow
{
    public string Group { get; set; } = string.Empty;
    public int TotalLeads { get; set; }
    public int Converted { get; set; }
    public int Lost { get; set; }
    public int Open { get; set; }
    public decimal ConversionRate => TotalLeads == 0 ? 0 : Math.Round(Converted * 100m / TotalLeads, 1);
}

public class ConversionReportViewModel
{
    public ListFilter Filter { get; set; } = new();
    public List<ConversionReportRow> BySource { get; set; } = new();
    public List<ConversionReportRow> ByOwner { get; set; } = new();
    public int OpportunitiesWon { get; set; }
    public int OpportunitiesLost { get; set; }
    public decimal WonAmount { get; set; }
    public decimal LostAmount { get; set; }
    public decimal WinRate => OpportunitiesWon + OpportunitiesLost == 0
        ? 0 : Math.Round(OpportunitiesWon * 100m / (OpportunitiesWon + OpportunitiesLost), 1);
}

public class UserActivityRow
{
    public string? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int Logins { get; set; }
    public int FailedLogins { get; set; }
    public int Creates { get; set; }
    public int Updates { get; set; }
    public int Deletes { get; set; }
    public int Total { get; set; }
    public DateTime? LastActivity { get; set; }
}

// ---------- Global search ----------

public class GlobalSearchViewModel
{
    public string Q { get; set; } = string.Empty;
    public List<Customer> Customers { get; set; } = new();
    public int CustomerCount { get; set; }
    public List<Lead> Leads { get; set; } = new();
    public int LeadCount { get; set; }
    public List<Opportunity> Opportunities { get; set; } = new();
    public int OpportunityCount { get; set; }
    public int Total => CustomerCount + LeadCount + OpportunityCount;
}

using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using AcxiomCRM.Validation;

namespace AcxiomCRM.Dtos;

// Input models are shared by MVC forms (view models inherit them) and the REST API,
// so the same DataAnnotations drive client-side, MVC server-side and API validation.

public class CustomerInputDto
{
    [Required(ErrorMessage = "Customer Name is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Customer Name must be between 2 and 100 characters.")]
    [Display(Name = "Customer Name")]
    public string CustomerName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [StringLength(150, ErrorMessage = "Email cannot exceed 150 characters.")]
    [RegularExpression(ValidationPatterns.Email, ErrorMessage = ValidationPatterns.EmailMessage)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Phone is required.")]
    [RegularExpression(ValidationPatterns.Phone, ErrorMessage = ValidationPatterns.PhoneMessage)]
    [DataType(DataType.PhoneNumber)]
    public string Phone { get; set; } = string.Empty;

    [StringLength(150, ErrorMessage = "Company Name cannot exceed 150 characters.")]
    [Display(Name = "Company Name")]
    public string? CompanyName { get; set; }

    [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters.")]
    public string? Address { get; set; }

    [StringLength(80, ErrorMessage = "City cannot exceed 80 characters.")]
    public string? City { get; set; }

    [StringLength(80, ErrorMessage = "State cannot exceed 80 characters.")]
    public string? State { get; set; }

    [Required(ErrorMessage = "Status is required.")]
    [EnumDataType(typeof(CustomerStatus), ErrorMessage = "Select a valid status.")]
    public CustomerStatus? Status { get; set; } = CustomerStatus.Active;

    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters.")]
    public string? Notes { get; set; }

    /// <summary>Owner (sales executive). Sales Executives can only assign to themselves.</summary>
    [Display(Name = "Assigned To")]
    public string? AssignedToId { get; set; }
}

public class LeadInputDto
{
    [Required(ErrorMessage = "Lead Name is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Lead Name must be between 2 and 100 characters.")]
    [Display(Name = "Lead Name")]
    public string LeadName { get; set; } = string.Empty;

    [StringLength(150, ErrorMessage = "Email cannot exceed 150 characters.")]
    [RegularExpression(ValidationPatterns.Email, ErrorMessage = ValidationPatterns.EmailMessage)]
    public string? Email { get; set; }

    [RegularExpression(ValidationPatterns.Phone, ErrorMessage = ValidationPatterns.PhoneMessage)]
    [DataType(DataType.PhoneNumber)]
    public string? Phone { get; set; }

    [StringLength(150, ErrorMessage = "Company Name cannot exceed 150 characters.")]
    [Display(Name = "Company Name")]
    public string? CompanyName { get; set; }

    [Required(ErrorMessage = "Lead Source is required.")]
    [EnumDataType(typeof(LeadSource), ErrorMessage = "Select a valid lead source.")]
    public LeadSource? Source { get; set; }

    [Required(ErrorMessage = "Lead Status is required.")]
    [EnumDataType(typeof(LeadStatus), ErrorMessage = "Select a valid lead status.")]
    public LeadStatus? Status { get; set; } = LeadStatus.New;

    [Required(ErrorMessage = "Priority is required.")]
    [EnumDataType(typeof(Models.Priority), ErrorMessage = "Select a valid priority.")]
    public Priority? Priority { get; set; } = Models.Priority.Medium;

    [Required(ErrorMessage = "Expected Value is required.")]
    [Range(0d, 1_000_000_000d, ErrorMessage = "Expected Value must be between 0 and 1,000,000,000.")]
    [Display(Name = "Expected Value")]
    public decimal? ExpectedValue { get; set; }

    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters.")]
    public string? Notes { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedToId { get; set; }
}

public class OpportunityInputDto
{
    [Required(ErrorMessage = "Opportunity Name is required.")]
    [StringLength(150, MinimumLength = 3, ErrorMessage = "Opportunity Name must be between 3 and 150 characters.")]
    [Display(Name = "Opportunity Name")]
    public string OpportunityName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Customer is required.")]
    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Source Lead")]
    public int? LeadId { get; set; }

    [Required(ErrorMessage = "Opportunity Amount is required.")]
    [Range(0d, 1_000_000_000_000d, ErrorMessage = "Opportunity Amount cannot be negative.")]
    [Positive(nameof(Stage), nameof(OpportunityStage.Won), nameof(OpportunityStage.Lost),
        ErrorMessage = "Opportunity Amount must be greater than 0.")]
    [Display(Name = "Amount")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Probability is required.")]
    [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    public int? Probability { get; set; }

    [Required(ErrorMessage = "Expected Close Date is required.")]
    [DataType(DataType.Date)]
    [NotInPast(nameof(Stage), nameof(OpportunityStage.Won), nameof(OpportunityStage.Lost),
        ErrorMessage = "Expected Close Date cannot be in the past.")]
    [Display(Name = "Expected Close Date")]
    public DateTime? ExpectedCloseDate { get; set; }

    [Required(ErrorMessage = "Stage is required.")]
    [EnumDataType(typeof(OpportunityStage), ErrorMessage = "Select a valid stage.")]
    public OpportunityStage? Stage { get; set; } = OpportunityStage.Qualification;

    [StringLength(100, ErrorMessage = "Source cannot exceed 100 characters.")]
    public string? Source { get; set; }

    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters.")]
    public string? Notes { get; set; }

    [Display(Name = "Owner")]
    public string? AssignedToId { get; set; }
}

public class FollowUpInputDto
{
    [Required(ErrorMessage = "Subject is required.")]
    [StringLength(150, MinimumLength = 3, ErrorMessage = "Subject must be between 3 and 150 characters.")]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "Follow-up date is required.")]
    [DataType(DataType.DateTime)]
    [NotInPast(ErrorMessage = "Follow-up date cannot be earlier than today.")]
    [Display(Name = "Follow-Up Date")]
    public DateTime? FollowUpDate { get; set; }

    [Required(ErrorMessage = "Follow-up type is required.")]
    [EnumDataType(typeof(Models.FollowUpType), ErrorMessage = "Select a valid follow-up type.")]
    [Display(Name = "Type")]
    public FollowUpType? FollowUpType { get; set; } = Models.FollowUpType.Call;

    [StringLength(1000, ErrorMessage = "Remarks cannot exceed 1000 characters.")]
    public string? Remarks { get; set; }

    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    [Display(Name = "Opportunity")]
    public int? OpportunityId { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedToId { get; set; }
}

public class ActivityInputDto
{
    [Required(ErrorMessage = "Activity type is required.")]
    [EnumDataType(typeof(Models.ActivityType), ErrorMessage = "Select a valid activity type.")]
    [Display(Name = "Type")]
    public ActivityType? ActivityType { get; set; } = Models.ActivityType.Call;

    [Required(ErrorMessage = "Subject is required.")]
    [StringLength(150, MinimumLength = 3, ErrorMessage = "Subject must be between 3 and 150 characters.")]
    public string Subject { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "Description cannot exceed 1000 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Activity date is required.")]
    [DataType(DataType.DateTime)]
    [Display(Name = "Activity Date")]
    public DateTime? ActivityDate { get; set; }

    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    [Required(ErrorMessage = "Status is required.")]
    [EnumDataType(typeof(ActivityStatus), ErrorMessage = "Select a valid status.")]
    public ActivityStatus? Status { get; set; } = ActivityStatus.Planned;

    [Display(Name = "Assigned To")]
    public string? AssignedToId { get; set; }
}

public class ConvertLeadInputDto
{
    [Display(Name = "Also create an opportunity")]
    public bool CreateOpportunity { get; set; } = true;

    [StringLength(150, ErrorMessage = "Opportunity Name cannot exceed 150 characters.")]
    [Display(Name = "Opportunity Name")]
    public string? OpportunityName { get; set; }

    [Range(0d, 1_000_000_000_000d, ErrorMessage = "Opportunity Amount cannot be negative.")]
    [Display(Name = "Amount")]
    public decimal? Amount { get; set; }

    [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    public int? Probability { get; set; } = 20;

    [DataType(DataType.Date)]
    [NotInPast(ErrorMessage = "Expected Close Date cannot be in the past.")]
    [Display(Name = "Expected Close Date")]
    public DateTime? ExpectedCloseDate { get; set; }
}

public class LoginRequestDto
{
    [Required(ErrorMessage = "Email or username is required.")]
    [StringLength(256)]
    public string Login { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100)]
    public string Password { get; set; } = string.Empty;
}

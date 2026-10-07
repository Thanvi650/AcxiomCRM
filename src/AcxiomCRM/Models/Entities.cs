using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Models;

/// <summary>A CRM record owned by (assigned to) a user. Used for row-level authorization.</summary>
public interface IAssignable
{
    string? AssignedToId { get; }
}

/// <summary>
/// Application user. Passwords, hashes, lockout counters and security stamps are managed
/// by ASP.NET Core Identity — never by application code.
/// </summary>
public class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Manager this user reports to (defines a Manager's team).</summary>
    public string? ManagerId { get; set; }
    public ApplicationUser? Manager { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
}

public class Customer : IAssignable
{
    public int CustomerId { get; set; }

    [MaxLength(20)]
    public string CustomerCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string CustomerName { get; set; } = string.Empty;

    [MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(15)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(150)]
    public string? CompanyName { get; set; }

    [MaxLength(250)]
    public string? Address { get; set; }

    [MaxLength(80)]
    public string? City { get; set; }

    [MaxLength(80)]
    public string? State { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public string? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    /// <summary>User id of the creator.</summary>
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
    public ICollection<FollowUp> FollowUps { get; set; } = new List<FollowUp>();
    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
}

public class Lead : IAssignable
{
    public int LeadId { get; set; }

    [MaxLength(20)]
    public string LeadCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LeadName { get; set; } = string.Empty;

    [MaxLength(150)]
    public string? Email { get; set; }

    [MaxLength(15)]
    public string? Phone { get; set; }

    [MaxLength(150)]
    public string? CompanyName { get; set; }

    public LeadSource Source { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public Priority Priority { get; set; } = Priority.Medium;

    public decimal ExpectedValue { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public string? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }
    public DateTime? ConvertedDate { get; set; }

    public ICollection<FollowUp> FollowUps { get; set; } = new List<FollowUp>();
    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
}

public class Opportunity : IAssignable
{
    public int OpportunityId { get; set; }

    [MaxLength(150)]
    public string OpportunityName { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public decimal Amount { get; set; }
    public OpportunityStage Stage { get; set; } = OpportunityStage.Qualification;

    /// <summary>0–100.</summary>
    public int Probability { get; set; }

    public DateTime ExpectedCloseDate { get; set; }

    /// <summary>Derived from <see cref="Stage"/>: Open, Won or Lost.</summary>
    public OpportunityStatus Status { get; set; } = OpportunityStatus.Open;

    [MaxLength(100)]
    public string? Source { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public string? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Set when the opportunity is marked Won or Lost.</summary>
    public DateTime? ClosedDate { get; set; }

    /// <summary>Final outcome: why it was won or lost (required when Lost).</summary>
    [MaxLength(500)]
    public string? OutcomeNotes { get; set; }

    [NotMapped]
    public decimal WeightedAmount => Amount * Probability / 100m;

    public ICollection<FollowUp> FollowUps { get; set; } = new List<FollowUp>();
}

public class FollowUp : IAssignable
{
    public int FollowUpId { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    public DateTime FollowUpDate { get; set; }
    public FollowUpType FollowUpType { get; set; }

    [MaxLength(150)]
    public string Subject { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Remarks { get; set; }

    public FollowUpStatus Status { get; set; } = FollowUpStatus.Planned;

    public string? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? CompletedDate { get; set; }

    [NotMapped]
    public bool IsOverdue => Status == FollowUpStatus.Planned && FollowUpDate.Date < DateTime.Today;

    [NotMapped]
    public string RelatedTo =>
        Customer?.CustomerName ?? Lead?.LeadName ?? Opportunity?.OpportunityName ?? "—";
}

public class Activity : IAssignable
{
    public int ActivityId { get; set; }

    public ActivityType ActivityType { get; set; }

    [MaxLength(150)]
    public string Subject { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public DateTime ActivityDate { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public string? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;

    public DateTime CreatedDate { get; set; } = DateTime.Now;
}

/// <summary>Append-only audit record. Updates and deletes are rejected by the DbContext.</summary>
public class AuditLog
{
    public long AuditLogId { get; set; }

    [MaxLength(450)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string? UserName { get; set; }

    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    /// <summary>Module / entity the event relates to (Customer, Lead, Account, User ...).</summary>
    [MaxLength(50)]
    public string EntityName { get; set; } = string.Empty;

    [MaxLength(450)]
    public string? RecordId { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>Success, Failure, LockedOut ...</summary>
    [MaxLength(30)]
    public string Result { get; set; } = "Success";

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    [MaxLength(64)]
    public string? IpAddress { get; set; }
}

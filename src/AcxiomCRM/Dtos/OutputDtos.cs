using AcxiomCRM.Models;

namespace AcxiomCRM.Dtos;

// Response DTOs: only business fields are exposed. No entity, password hash,
// security stamp or other Identity internals ever leave the server.

public record CustomerDto(
    int CustomerId,
    string CustomerCode,
    string CustomerName,
    string Email,
    string Phone,
    string? CompanyName,
    string? Address,
    string? City,
    string? State,
    CustomerStatus Status,
    string? Notes,
    string? AssignedToId,
    string? AssignedToName,
    DateTime CreatedDate,
    DateTime? ModifiedDate)
{
    public static CustomerDto From(Customer c) => new(
        c.CustomerId, c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName, c.Address,
        c.City, c.State, c.Status, c.Notes, c.AssignedToId, c.AssignedTo?.FullName, c.CreatedDate, c.ModifiedDate);
}

public record LeadDto(
    int LeadId,
    string LeadCode,
    string LeadName,
    string? Email,
    string? Phone,
    string? CompanyName,
    LeadSource Source,
    LeadStatus Status,
    Priority Priority,
    decimal ExpectedValue,
    string? Notes,
    string? AssignedToId,
    string? AssignedToName,
    DateTime CreatedDate,
    int? ConvertedCustomerId,
    DateTime? ConvertedDate)
{
    public static LeadDto From(Lead l) => new(
        l.LeadId, l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source, l.Status, l.Priority,
        l.ExpectedValue, l.Notes, l.AssignedToId, l.AssignedTo?.FullName, l.CreatedDate, l.ConvertedCustomerId, l.ConvertedDate);
}

public record OpportunityDto(
    int OpportunityId,
    string OpportunityName,
    int CustomerId,
    string? CustomerName,
    int? LeadId,
    decimal Amount,
    int Probability,
    decimal WeightedAmount,
    OpportunityStage Stage,
    OpportunityStatus Status,
    DateTime ExpectedCloseDate,
    string? Source,
    string? Notes,
    string? AssignedToId,
    string? AssignedToName,
    DateTime CreatedDate,
    DateTime? ClosedDate,
    string? OutcomeNotes)
{
    public static OpportunityDto From(Opportunity o) => new(
        o.OpportunityId, o.OpportunityName, o.CustomerId, o.Customer?.CustomerName, o.LeadId, o.Amount, o.Probability,
        o.WeightedAmount, o.Stage, o.Status, o.ExpectedCloseDate, o.Source, o.Notes, o.AssignedToId,
        o.AssignedTo?.FullName, o.CreatedDate, o.ClosedDate, o.OutcomeNotes);
}

public record FollowUpDto(
    int FollowUpId,
    string Subject,
    DateTime FollowUpDate,
    FollowUpType FollowUpType,
    FollowUpStatus Status,
    bool IsOverdue,
    string? Remarks,
    int? CustomerId,
    int? LeadId,
    int? OpportunityId,
    string RelatedTo,
    string? AssignedToId,
    string? AssignedToName)
{
    public static FollowUpDto From(FollowUp f) => new(
        f.FollowUpId, f.Subject, f.FollowUpDate, f.FollowUpType, f.Status, f.IsOverdue, f.Remarks, f.CustomerId,
        f.LeadId, f.OpportunityId, f.RelatedTo, f.AssignedToId, f.AssignedTo?.FullName);
}

public record PipelineStageDto(OpportunityStage Stage, int Count, decimal Amount, decimal WeightedAmount);

public record PipelineOwnerDto(string? OwnerId, string OwnerName, int OpenCount, decimal OpenAmount, decimal WeightedAmount, decimal WonAmount);

public record PipelineReportDto(
    IReadOnlyList<PipelineStageDto> ByStage,
    IReadOnlyList<PipelineOwnerDto> ByOwner,
    decimal TotalOpenAmount,
    decimal TotalWeightedAmount);

public record CurrentUserDto(string UserId, string UserName, string FullName, string Role);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

/// <summary>Opportunity business rules, shared by MVC, API and lead conversion.</summary>
public static class OpportunityRules
{
    public static bool IsActive(OpportunityStage stage) => stage is not (OpportunityStage.Won or OpportunityStage.Lost);

    public static OpportunityStatus StatusFor(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Won => OpportunityStatus.Won,
        OpportunityStage.Lost => OpportunityStatus.Lost,
        _ => OpportunityStatus.Open
    };

    public static decimal Weighted(decimal amount, int probability) => amount * probability / 100m;

    public static IEnumerable<ServiceError> Validate(decimal amount, int probability, DateTime expectedCloseDate, OpportunityStage stage)
    {
        if (amount < 0)
            yield return new ServiceError("Amount", "Opportunity Amount cannot be negative.");
        else if (IsActive(stage) && amount <= 0)
            yield return new ServiceError("Amount", "Opportunity Amount must be greater than 0.");

        if (probability is < 0 or > 100)
            yield return new ServiceError("Probability", "Probability must be between 0 and 100.");

        if (IsActive(stage) && expectedCloseDate.Date < DateTime.Today)
            yield return new ServiceError("ExpectedCloseDate", "Expected Close Date cannot be in the past.");
    }
}

public class OpportunityService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public OpportunityService(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    public Task<IQueryable<Opportunity>> QueryAsync() =>
        _scope.ApplyAsync(_db.Opportunities.AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.AssignedTo)
            .AsQueryable());

    public static IQueryable<Opportunity> Search(IQueryable<Opportunity> query, string? q, string? stage, string? status, string? assignedTo)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(o =>
                o.OpportunityName.ToLower().Contains(term) ||
                (o.Customer != null && o.Customer.CustomerName.ToLower().Contains(term)) ||
                (o.Customer != null && o.Customer.CompanyName != null && o.Customer.CompanyName.ToLower().Contains(term)));
        }
        if (Enum.TryParse<OpportunityStage>(stage, out var st)) query = query.Where(o => o.Stage == st);
        if (Enum.TryParse<OpportunityStatus>(status, out var s)) query = query.Where(o => o.Status == s);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(o => o.AssignedToId == assignedTo);
        return query;
    }

    public static IQueryable<Opportunity> Sort(IQueryable<Opportunity> query, string? sort) => sort switch
    {
        "name" => query.OrderBy(o => o.OpportunityName),
        "name_desc" => query.OrderByDescending(o => o.OpportunityName),
        "amount" => query.OrderBy(o => o.Amount),
        "amount_desc" => query.OrderByDescending(o => o.Amount),
        "probability" => query.OrderBy(o => o.Probability),
        "probability_desc" => query.OrderByDescending(o => o.Probability),
        "close" => query.OrderBy(o => o.ExpectedCloseDate),
        "close_desc" => query.OrderByDescending(o => o.ExpectedCloseDate),
        "stage" => query.OrderBy(o => o.Stage),
        "stage_desc" => query.OrderByDescending(o => o.Stage),
        _ => query.OrderByDescending(o => o.CreatedDate)
    };

    public async Task<Opportunity?> GetAsync(int id)
    {
        var opportunity = await _db.Opportunities
            .Include(o => o.Customer)
            .Include(o => o.Lead)
            .Include(o => o.AssignedTo)
            .FirstOrDefaultAsync(o => o.OpportunityId == id);
        return opportunity is not null && await _scope.CanAccessAsync(opportunity) ? opportunity : null;
    }

    public async Task<ServiceResult<Opportunity>> CreateAsync(OpportunityInputDto input)
    {
        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<Opportunity>.Invalid(errors);

        var opportunity = new Opportunity { CreatedDate = DateTime.Now };
        Apply(input, opportunity);
        _db.Opportunities.Add(opportunity);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Create, nameof(Opportunity), opportunity.OpportunityId.ToString(), null, opportunity);
        return ServiceResult<Opportunity>.Ok(opportunity);
    }

    public async Task<ServiceResult<Opportunity>> UpdateAsync(int id, OpportunityInputDto input)
    {
        var opportunity = await GetAsync(id);
        if (opportunity is null) return ServiceResult<Opportunity>.NotFound();

        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<Opportunity>.Invalid(errors);

        var before = AuditService.Snapshot(opportunity);
        var oldStage = opportunity.Stage;
        Apply(input, opportunity);
        opportunity.ModifiedDate = DateTime.Now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Update, nameof(Opportunity), id.ToString(), before, opportunity);
        if (oldStage != opportunity.Stage)
        {
            await _audit.LogAsync(AuditActions.StatusChange, nameof(Opportunity), id.ToString(),
                new { Stage = oldStage }, new { opportunity.Stage, opportunity.Status });
        }
        return ServiceResult<Opportunity>.Ok(opportunity);
    }

    /// <summary>Moves an opportunity to a new stage (pipeline board), re-checking all business rules.</summary>
    public async Task<ServiceResult<Opportunity>> ChangeStageAsync(int id, OpportunityStage stage, string? outcomeNotes = null)
    {
        var opportunity = await GetAsync(id);
        if (opportunity is null) return ServiceResult<Opportunity>.NotFound();

        var input = ToInput(opportunity);
        input.Stage = stage;
        if (!string.IsNullOrWhiteSpace(outcomeNotes)) input.OutcomeNotes = outcomeNotes;
        return await UpdateAsync(id, input);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var opportunity = await GetAsync(id);
        if (opportunity is null) return ServiceResult<bool>.NotFound();

        var before = AuditService.Snapshot(opportunity);
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            // Follow-ups belong to the opportunity, so they are removed with it.
            await _db.FollowUps.Where(f => f.OpportunityId == id).ExecuteDeleteAsync();
            _db.Opportunities.Remove(opportunity);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await _audit.LogAsync(AuditActions.Delete, nameof(Opportunity), id.ToString(), before);
        return ServiceResult<bool>.Ok(true);
    }

    public static OpportunityInputDto ToInput(Opportunity o) => new()
    {
        OpportunityName = o.OpportunityName,
        CustomerId = o.CustomerId,
        LeadId = o.LeadId,
        Amount = o.Amount,
        Probability = o.Probability,
        ExpectedCloseDate = o.ExpectedCloseDate,
        Stage = o.Stage,
        Source = o.Source,
        Notes = o.Notes,
        OutcomeNotes = o.OutcomeNotes,
        AssignedToId = o.AssignedToId
    };

    private async Task<List<ServiceError>> ValidateAsync(OpportunityInputDto input)
    {
        var errors = ValidationExtensions.AnnotationErrors(input);

        if (input.Amount is not null && input.Probability is not null && input.ExpectedCloseDate is not null && input.Stage is not null)
        {
            foreach (var error in OpportunityRules.Validate(input.Amount.Value, input.Probability.Value, input.ExpectedCloseDate.Value, input.Stage.Value))
            {
                if (!errors.Any(e => e.Field == error.Field)) errors.Add(error);
            }
        }

        // Foreign keys must exist AND be inside the user's scope (prevents tampered ids).
        if (input.CustomerId is not null)
        {
            var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == input.CustomerId);
            if (customer is null || !await _scope.CanAccessAsync(customer))
                errors.Add(new ServiceError(nameof(input.CustomerId), "Select a valid customer."));
        }
        if (input.LeadId is not null)
        {
            var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.LeadId == input.LeadId);
            if (lead is null || !await _scope.CanAccessAsync(lead))
                errors.Add(new ServiceError(nameof(input.LeadId), "Select a valid lead."));
        }
        if (!await _scope.CanAssignToAsync(input.AssignedToId))
        {
            errors.Add(new ServiceError(nameof(input.AssignedToId), "Select a valid user within your scope."));
        }
        return errors;
    }

    private void Normalize(OpportunityInputDto input)
    {
        input.OpportunityName = input.OpportunityName?.Trim() ?? string.Empty;
        input.Source = input.Source.Clean();
        input.Notes = input.Notes.Clean();
        input.OutcomeNotes = input.OutcomeNotes.Clean();
        if (input.ExpectedCloseDate is not null) input.ExpectedCloseDate = input.ExpectedCloseDate.Value.Date;
        if (_scope.IsSalesExecutive || string.IsNullOrEmpty(input.AssignedToId)) input.AssignedToId = _scope.UserId;
    }

    private static void Apply(OpportunityInputDto input, Opportunity opportunity)
    {
        var stage = input.Stage!.Value;
        var wasClosed = !OpportunityRules.IsActive(opportunity.Stage) && opportunity.OpportunityId != 0;

        opportunity.OpportunityName = input.OpportunityName;
        opportunity.CustomerId = input.CustomerId!.Value;
        opportunity.LeadId = input.LeadId;
        opportunity.Amount = input.Amount!.Value;
        opportunity.Probability = input.Probability!.Value;
        opportunity.ExpectedCloseDate = input.ExpectedCloseDate!.Value;
        opportunity.Stage = stage;
        opportunity.Status = OpportunityRules.StatusFor(stage);
        opportunity.Source = input.Source;
        opportunity.Notes = input.Notes;
        opportunity.OutcomeNotes = input.OutcomeNotes;
        opportunity.AssignedToId = input.AssignedToId;

        // Capture final outcome: Won = 100%, Lost = 0%, with the date it closed.
        if (stage == OpportunityStage.Won) opportunity.Probability = 100;
        if (stage == OpportunityStage.Lost) opportunity.Probability = 0;
        if (OpportunityRules.IsActive(stage)) opportunity.ClosedDate = null;
        else if (!wasClosed || opportunity.ClosedDate is null) opportunity.ClosedDate = DateTime.Now;
    }
}

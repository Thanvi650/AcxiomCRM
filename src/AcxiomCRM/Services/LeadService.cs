using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

/// <summary>Lead status workflow. "Converted" is only reachable through the Convert action.</summary>
public static class LeadWorkflow
{
    private static readonly Dictionary<LeadStatus, LeadStatus[]> Transitions = new()
    {
        [LeadStatus.New] = new[] { LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Contacted] = new[] { LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Qualified] = new[] { LeadStatus.Contacted, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Unqualified] = new[] { LeadStatus.Contacted, LeadStatus.Lost },
        [LeadStatus.Lost] = new[] { LeadStatus.New },
        [LeadStatus.Converted] = Array.Empty<LeadStatus>()
    };

    /// <summary>Statuses a brand-new lead may start in.</summary>
    public static readonly LeadStatus[] InitialStatuses =
        { LeadStatus.New, LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified };

    public static IReadOnlyList<LeadStatus> NextStatuses(LeadStatus current) => Transitions[current];

    public static bool CanTransition(LeadStatus from, LeadStatus to) => from == to || Transitions[from].Contains(to);

    public static bool IsOpen(LeadStatus status) =>
        status is not (LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Unqualified);
}

public class LeadService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public LeadService(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    public Task<IQueryable<Lead>> QueryAsync() =>
        _scope.ApplyAsync(_db.Leads.AsNoTracking().Include(l => l.AssignedTo).AsQueryable());

    public static IQueryable<Lead> Search(IQueryable<Lead> query, string? q, string? status, string? assignedTo)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(l =>
                l.LeadName.ToLower().Contains(term) ||
                l.LeadCode.ToLower().Contains(term) ||
                (l.CompanyName != null && l.CompanyName.ToLower().Contains(term)) ||
                (l.Email != null && l.Email.ToLower().Contains(term)) ||
                (l.Phone != null && l.Phone.Contains(term)));
        }
        if (status == "open")
        {
            query = query.Where(l => l.Status != LeadStatus.Converted && l.Status != LeadStatus.Lost && l.Status != LeadStatus.Unqualified);
        }
        else if (Enum.TryParse<LeadStatus>(status, out var s))
        {
            query = query.Where(l => l.Status == s);
        }
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(l => l.AssignedToId == assignedTo);
        return query;
    }

    public static IQueryable<Lead> Sort(IQueryable<Lead> query, string? sort) => sort switch
    {
        "name" => query.OrderBy(l => l.LeadName),
        "name_desc" => query.OrderByDescending(l => l.LeadName),
        "company" => query.OrderBy(l => l.CompanyName),
        "company_desc" => query.OrderByDescending(l => l.CompanyName),
        "status" => query.OrderBy(l => l.Status),
        "status_desc" => query.OrderByDescending(l => l.Status),
        "value" => query.OrderBy(l => l.ExpectedValue),
        "value_desc" => query.OrderByDescending(l => l.ExpectedValue),
        "created" => query.OrderBy(l => l.CreatedDate),
        _ => query.OrderByDescending(l => l.CreatedDate)
    };

    public async Task<Lead?> GetAsync(int id)
    {
        var lead = await _db.Leads.Include(l => l.AssignedTo).Include(l => l.ConvertedCustomer)
            .FirstOrDefaultAsync(l => l.LeadId == id);
        return lead is not null && await _scope.CanAccessAsync(lead) ? lead : null;
    }

    public async Task<ServiceResult<Lead>> CreateAsync(LeadInputDto input)
    {
        Normalize(input);
        var errors = await ValidateAsync(input);
        if (input.Status is { } status && !LeadWorkflow.InitialStatuses.Contains(status))
        {
            errors.Add(new ServiceError(nameof(input.Status),
                "A new lead must start as New, Contacted, Qualified or Unqualified. Use Convert to convert a lead."));
        }
        if (errors.Count > 0) return ServiceResult<Lead>.Invalid(errors);

        var lead = new Lead { LeadCode = "TMP-" + Guid.NewGuid().ToString("N")[..12], CreatedDate = DateTime.Now };
        Apply(input, lead);
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();

        lead.LeadCode = $"LEAD-{lead.LeadId:D5}";
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Create, nameof(Lead), lead.LeadId.ToString(), null, lead);
        return ServiceResult<Lead>.Ok(lead);
    }

    public async Task<ServiceResult<Lead>> UpdateAsync(int id, LeadInputDto input)
    {
        var lead = await GetAsync(id);
        if (lead is null) return ServiceResult<Lead>.NotFound();

        Normalize(input);
        var errors = await ValidateAsync(input);
        if (input.Status is { } next && !LeadWorkflow.CanTransition(lead.Status, next))
        {
            errors.Add(new ServiceError(nameof(input.Status),
                next == LeadStatus.Converted
                    ? "Use the Convert action to convert a qualified lead."
                    : $"Lead status cannot change from {lead.Status} to {next}."));
        }
        if (errors.Count > 0) return ServiceResult<Lead>.Invalid(errors);

        var before = AuditService.Snapshot(lead);
        var oldStatus = lead.Status;
        Apply(input, lead);
        lead.ModifiedDate = DateTime.Now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Update, nameof(Lead), id.ToString(), before, lead);
        if (oldStatus != lead.Status)
        {
            await _audit.LogAsync(AuditActions.StatusChange, nameof(Lead), id.ToString(),
                new { Status = oldStatus }, new { lead.Status });
        }
        return ServiceResult<Lead>.Ok(lead);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var lead = await GetAsync(id);
        if (lead is null) return ServiceResult<bool>.NotFound();

        var before = AuditService.Snapshot(lead);
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            // Dependent rows are handled here because the foreign keys use NO ACTION.
            await _db.FollowUps.Where(f => f.LeadId == id).ExecuteDeleteAsync();
            await _db.Activities.Where(a => a.LeadId == id).ExecuteDeleteAsync();
            await _db.Opportunities.Where(o => o.LeadId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeadId, (int?)null));
            _db.Leads.Remove(lead);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await _audit.LogAsync(AuditActions.Delete, nameof(Lead), id.ToString(), before);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Converts a qualified lead into a customer (re-using an existing customer with the same
    /// email/phone when the user can see it) and optionally an opportunity, in one transaction.
    /// </summary>
    public async Task<ServiceResult<(Customer Customer, Opportunity? Opportunity)>> ConvertAsync(int id, ConvertLeadInputDto input)
    {
        var lead = await GetAsync(id);
        if (lead is null) return ServiceResult<(Customer, Opportunity?)>.NotFound();

        var errors = ValidationExtensions.AnnotationErrors(input);
        if (lead.Status != LeadStatus.Qualified)
        {
            errors.Add(new ServiceError(string.Empty, "Only leads with status Qualified can be converted."));
        }
        if (string.IsNullOrEmpty(lead.Email) || string.IsNullOrEmpty(lead.Phone))
        {
            errors.Add(new ServiceError(string.Empty, "The lead needs a valid email and phone number before it can become a customer. Edit the lead first."));
        }
        if (input.CreateOpportunity)
        {
            if (string.IsNullOrWhiteSpace(input.OpportunityName))
                errors.Add(new ServiceError(nameof(input.OpportunityName), "Opportunity Name is required."));
            if (input.Amount is null)
                errors.Add(new ServiceError(nameof(input.Amount), "Opportunity Amount is required."));
            if (input.Probability is null)
                errors.Add(new ServiceError(nameof(input.Probability), "Probability is required."));
            if (input.ExpectedCloseDate is null)
                errors.Add(new ServiceError(nameof(input.ExpectedCloseDate), "Expected Close Date is required."));
            if (input.Amount is not null && input.Probability is not null && input.ExpectedCloseDate is not null)
            {
                errors.AddRange(OpportunityRules.Validate(input.Amount.Value, input.Probability.Value,
                    input.ExpectedCloseDate.Value, OpportunityStage.Qualification));
            }
        }
        if (errors.Count > 0) return ServiceResult<(Customer, Opportunity?)>.Invalid(errors);

        Customer? customer;
        bool reused;
        Opportunity? opportunity = null;
        var before = new { lead.Status };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == lead.Email || c.Phone == lead.Phone);
            reused = customer is not null;
            if (customer is not null && !await _scope.CanAccessAsync(customer))
            {
                return ServiceResult<(Customer, Opportunity?)>.Conflict(string.Empty,
                    "A customer with this email or phone already exists and belongs to another team. Ask a manager to reassign it.");
            }

            if (customer is null)
            {
                customer = new Customer
                {
                    CustomerCode = "TMP-" + Guid.NewGuid().ToString("N")[..12],
                    CustomerName = lead.LeadName,
                    Email = lead.Email!.ToLowerInvariant(),
                    Phone = lead.Phone!,
                    CompanyName = lead.CompanyName,
                    Status = CustomerStatus.Active,
                    Notes = $"Converted from lead {lead.LeadCode}.",
                    AssignedToId = lead.AssignedToId,
                    CreatedBy = _scope.UserId,
                    CreatedDate = DateTime.Now
                };
                _db.Customers.Add(customer);
                await _db.SaveChangesAsync();
                customer.CustomerCode = $"CUS-{customer.CustomerId:D5}";
            }

            if (input.CreateOpportunity)
            {
                opportunity = new Opportunity
                {
                    OpportunityName = input.OpportunityName!.Trim(),
                    CustomerId = customer.CustomerId,
                    LeadId = lead.LeadId,
                    Amount = input.Amount!.Value,
                    Probability = input.Probability!.Value,
                    ExpectedCloseDate = input.ExpectedCloseDate!.Value.Date,
                    Stage = OpportunityStage.Qualification,
                    Status = OpportunityStatus.Open,
                    Source = lead.Source.ToString(),
                    AssignedToId = lead.AssignedToId,
                    CreatedDate = DateTime.Now
                };
                _db.Opportunities.Add(opportunity);
            }

            lead.Status = LeadStatus.Converted;
            lead.ConvertedCustomerId = customer.CustomerId;
            lead.ConvertedDate = DateTime.Now;
            lead.ModifiedDate = DateTime.Now;
            await _db.SaveChangesAsync();

            // Carry the lead's follow-ups and activities over to the customer so the
            // salesperson keeps seeing them on the customer's page.
            await _db.FollowUps.Where(f => f.LeadId == lead.LeadId && f.CustomerId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CustomerId, customer.CustomerId));
            await _db.Activities.Where(a => a.LeadId == lead.LeadId && a.CustomerId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CustomerId, customer.CustomerId));

            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            // Another user created a customer with the same email/phone at the same moment.
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            return ServiceResult<(Customer, Opportunity?)>.Conflict(string.Empty,
                "A customer with this email or phone was just created by someone else. Refresh and try again.");
        }

        if (!reused) await _audit.LogAsync(AuditActions.Create, nameof(Customer), customer.CustomerId.ToString(), null, customer);
        if (opportunity is not null)
            await _audit.LogAsync(AuditActions.Create, nameof(Opportunity), opportunity.OpportunityId.ToString(), null, opportunity);
        await _audit.LogAsync(AuditActions.Convert, nameof(Lead), lead.LeadId.ToString(), before,
            new { lead.Status, lead.ConvertedCustomerId, OpportunityId = opportunity?.OpportunityId, ReusedExistingCustomer = reused });

        return ServiceResult<(Customer, Opportunity?)>.Ok((customer, opportunity));
    }

    private async Task<List<ServiceError>> ValidateAsync(LeadInputDto input)
    {
        var errors = ValidationExtensions.AnnotationErrors(input);
        if (!await _scope.CanAssignToAsync(input.AssignedToId))
        {
            errors.Add(new ServiceError(nameof(input.AssignedToId), "Select a valid user within your scope."));
        }
        return errors;
    }

    private void Normalize(LeadInputDto input)
    {
        input.LeadName = input.LeadName?.Trim() ?? string.Empty;
        input.Email = input.Email.Clean()?.ToLowerInvariant();
        input.Phone = input.Phone.Clean();
        input.CompanyName = input.CompanyName.Clean();
        input.Notes = input.Notes.Clean();
        if (_scope.IsSalesExecutive || string.IsNullOrEmpty(input.AssignedToId)) input.AssignedToId = _scope.UserId;
    }

    private static void Apply(LeadInputDto input, Lead lead)
    {
        lead.LeadName = input.LeadName;
        lead.Email = input.Email;
        lead.Phone = input.Phone;
        lead.CompanyName = input.CompanyName;
        lead.Source = input.Source!.Value;
        lead.Status = input.Status!.Value;
        lead.Priority = input.Priority!.Value;
        lead.ExpectedValue = input.ExpectedValue!.Value;
        lead.Notes = input.Notes;
        lead.AssignedToId = input.AssignedToId;
    }
}

using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

public class FollowUpService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public FollowUpService(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    public Task<IQueryable<FollowUp>> QueryAsync() =>
        _scope.ApplyAsync(_db.FollowUps.AsNoTracking()
            .Include(f => f.Customer)
            .Include(f => f.Lead)
            .Include(f => f.Opportunity)
            .Include(f => f.AssignedTo)
            .AsQueryable());

    public static IQueryable<FollowUp> Search(IQueryable<FollowUp> query, string? q, string? status, string? assignedTo,
        DateTime? from, DateTime? to, string? view)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        query = view switch
        {
            "overdue" => query.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today),
            "today" => query.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today && f.FollowUpDate < tomorrow),
            "upcoming" => query.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today),
            "pending" => query.Where(f => f.Status == FollowUpStatus.Planned),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(f =>
                f.Subject.ToLower().Contains(term) ||
                (f.Customer != null && f.Customer.CustomerName.ToLower().Contains(term)) ||
                (f.Lead != null && f.Lead.LeadName.ToLower().Contains(term)) ||
                (f.Opportunity != null && f.Opportunity.OpportunityName.ToLower().Contains(term)));
        }
        if (Enum.TryParse<FollowUpStatus>(status, out var s)) query = query.Where(f => f.Status == s);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(f => f.AssignedToId == assignedTo);
        if (from is not null) query = query.Where(f => f.FollowUpDate >= from.Value.Date);
        if (to is not null)
        {
            var end = to.Value.Date.AddDays(1);
            query = query.Where(f => f.FollowUpDate < end);
        }
        return query;
    }

    public static IQueryable<FollowUp> Sort(IQueryable<FollowUp> query, string? sort) => sort switch
    {
        "date_desc" => query.OrderByDescending(f => f.FollowUpDate),
        "subject" => query.OrderBy(f => f.Subject),
        "subject_desc" => query.OrderByDescending(f => f.Subject),
        "status" => query.OrderBy(f => f.Status),
        "status_desc" => query.OrderByDescending(f => f.Status),
        _ => query.OrderBy(f => f.FollowUpDate)
    };

    public async Task<FollowUp?> GetAsync(int id)
    {
        var followUp = await _db.FollowUps
            .Include(f => f.Customer)
            .Include(f => f.Lead)
            .Include(f => f.Opportunity)
            .Include(f => f.AssignedTo)
            .FirstOrDefaultAsync(f => f.FollowUpId == id);
        return followUp is not null && await _scope.CanAccessAsync(followUp) ? followUp : null;
    }

    public async Task<ServiceResult<FollowUp>> CreateAsync(FollowUpInputDto input)
    {
        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<FollowUp>.Invalid(errors);

        var followUp = new FollowUp { Status = FollowUpStatus.Planned, CreatedDate = DateTime.Now };
        Apply(input, followUp);
        _db.FollowUps.Add(followUp);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Create, nameof(FollowUp), followUp.FollowUpId.ToString(), null, followUp);
        return ServiceResult<FollowUp>.Ok(followUp);
    }

    public async Task<ServiceResult<FollowUp>> UpdateAsync(int id, FollowUpInputDto input)
    {
        var followUp = await GetAsync(id);
        if (followUp is null) return ServiceResult<FollowUp>.NotFound();
        if (followUp.Status != FollowUpStatus.Planned)
            return ServiceResult<FollowUp>.Invalid(string.Empty, "Only planned follow-ups can be edited.");

        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<FollowUp>.Invalid(errors);

        var before = AuditService.Snapshot(followUp);
        Apply(input, followUp);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Update, nameof(FollowUp), id.ToString(), before, followUp);
        return ServiceResult<FollowUp>.Ok(followUp);
    }

    /// <summary>Marks a planned/missed follow-up completed and moves a "New" lead to "Contacted".</summary>
    public async Task<ServiceResult<FollowUp>> CompleteAsync(int id, string? remarks)
    {
        var followUp = await GetAsync(id);
        if (followUp is null) return ServiceResult<FollowUp>.NotFound();
        if (followUp.Status is not (FollowUpStatus.Planned or FollowUpStatus.Missed))
            return ServiceResult<FollowUp>.Invalid(string.Empty, "Only planned or missed follow-ups can be completed.");

        var before = new { followUp.Status };
        followUp.Status = FollowUpStatus.Completed;
        followUp.CompletedDate = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(remarks))
            followUp.Remarks = Truncate(string.IsNullOrEmpty(followUp.Remarks) ? remarks.Trim() : followUp.Remarks + "\n" + remarks.Trim());

        // Update the related CRM record (workflow step 5).
        Lead? lead = followUp.Lead;
        var leadAdvanced = false;
        if (lead is not null && lead.Status == LeadStatus.New)
        {
            lead.Status = LeadStatus.Contacted;
            lead.ModifiedDate = DateTime.Now;
            leadAdvanced = true;
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Complete, nameof(FollowUp), id.ToString(), before,
            new { followUp.Status, followUp.CompletedDate });
        if (leadAdvanced)
        {
            await _audit.LogAsync(AuditActions.StatusChange, nameof(Lead), lead!.LeadId.ToString(),
                new { Status = LeadStatus.New }, new { lead.Status, Reason = $"Follow-up #{id} completed" });
        }
        return ServiceResult<FollowUp>.Ok(followUp);
    }

    public Task<ServiceResult<FollowUp>> MarkMissedAsync(int id) =>
        SetStatusAsync(id, FollowUpStatus.Missed, FollowUpStatus.Planned);

    public Task<ServiceResult<FollowUp>> CancelAsync(int id) =>
        SetStatusAsync(id, FollowUpStatus.Cancelled, FollowUpStatus.Planned, FollowUpStatus.Missed);

    public async Task<ServiceResult<FollowUp>> RescheduleAsync(int id, DateTime newDate, string? remarks)
    {
        var followUp = await GetAsync(id);
        if (followUp is null) return ServiceResult<FollowUp>.NotFound();
        if (followUp.Status is not (FollowUpStatus.Planned or FollowUpStatus.Missed))
            return ServiceResult<FollowUp>.Invalid(string.Empty, "Only planned or missed follow-ups can be rescheduled.");
        if (newDate.Date < DateTime.Today)
            return ServiceResult<FollowUp>.Invalid("NewDate", "Follow-up date cannot be earlier than today.");

        var before = new { followUp.FollowUpDate, followUp.Status };
        followUp.FollowUpDate = newDate;
        followUp.Status = FollowUpStatus.Planned;
        if (!string.IsNullOrWhiteSpace(remarks))
            followUp.Remarks = Truncate(string.IsNullOrEmpty(followUp.Remarks) ? remarks.Trim() : followUp.Remarks + "\n" + remarks.Trim());
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Reschedule, nameof(FollowUp), id.ToString(), before,
            new { followUp.FollowUpDate, followUp.Status });
        return ServiceResult<FollowUp>.Ok(followUp);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var followUp = await GetAsync(id);
        if (followUp is null) return ServiceResult<bool>.NotFound();

        var before = AuditService.Snapshot(followUp);
        _db.FollowUps.Remove(followUp);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Delete, nameof(FollowUp), id.ToString(), before);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<ServiceResult<FollowUp>> SetStatusAsync(int id, FollowUpStatus status, params FollowUpStatus[] allowedFrom)
    {
        var followUp = await GetAsync(id);
        if (followUp is null) return ServiceResult<FollowUp>.NotFound();
        if (!allowedFrom.Contains(followUp.Status))
            return ServiceResult<FollowUp>.Invalid(string.Empty, $"A {followUp.Status} follow-up cannot be marked {status}.");

        var before = new { followUp.Status };
        followUp.Status = status;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.StatusChange, nameof(FollowUp), id.ToString(), before, new { followUp.Status });
        return ServiceResult<FollowUp>.Ok(followUp);
    }

    private async Task<List<ServiceError>> ValidateAsync(FollowUpInputDto input)
    {
        var errors = ValidationExtensions.AnnotationErrors(input);

        if (input.FollowUpDate is not null && input.FollowUpDate.Value.Date < DateTime.Today &&
            !errors.Any(e => e.Field == nameof(input.FollowUpDate)))
        {
            errors.Add(new ServiceError(nameof(input.FollowUpDate), "Follow-up date cannot be earlier than today."));
        }

        if (input.CustomerId is null && input.LeadId is null && input.OpportunityId is null)
        {
            errors.Add(new ServiceError(string.Empty, "Link the follow-up to a customer, lead or opportunity."));
        }
        if (input.LeadId is not null)
        {
            var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.LeadId == input.LeadId);
            if (lead is null || !await _scope.CanAccessAsync(lead))
                errors.Add(new ServiceError(nameof(input.LeadId), "Select a valid lead."));
        }
        if (input.OpportunityId is not null)
        {
            var opportunity = await _db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.OpportunityId == input.OpportunityId);
            if (opportunity is null || !await _scope.CanAccessAsync(opportunity))
            {
                errors.Add(new ServiceError(nameof(input.OpportunityId), "Select a valid opportunity."));
            }
            else if (input.CustomerId is null)
            {
                // An opportunity always belongs to a customer: link that customer too so the
                // follow-up also appears on the customer's page.
                input.CustomerId = opportunity.CustomerId;
            }
            else if (input.CustomerId != opportunity.CustomerId)
            {
                errors.Add(new ServiceError(nameof(input.CustomerId), "The selected opportunity belongs to a different customer."));
            }
        }
        if (input.CustomerId is not null)
        {
            var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == input.CustomerId);
            if (customer is null || !await _scope.CanAccessAsync(customer))
                errors.Add(new ServiceError(nameof(input.CustomerId), "Select a valid customer."));
        }
        if (!await _scope.CanAssignToAsync(input.AssignedToId))
        {
            errors.Add(new ServiceError(nameof(input.AssignedToId), "Select a valid user within your scope."));
        }
        return errors;
    }

    private void Normalize(FollowUpInputDto input)
    {
        input.Subject = input.Subject?.Trim() ?? string.Empty;
        input.Remarks = input.Remarks.Clean();
        if (_scope.IsSalesExecutive || string.IsNullOrEmpty(input.AssignedToId)) input.AssignedToId = _scope.UserId;
    }

    private static void Apply(FollowUpInputDto input, FollowUp followUp)
    {
        followUp.Subject = input.Subject;
        followUp.FollowUpDate = input.FollowUpDate!.Value;
        followUp.FollowUpType = input.FollowUpType!.Value;
        followUp.Remarks = input.Remarks;
        followUp.CustomerId = input.CustomerId;
        followUp.LeadId = input.LeadId;
        followUp.OpportunityId = input.OpportunityId;
        followUp.AssignedToId = input.AssignedToId;
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}

public class ActivityService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public ActivityService(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    public Task<IQueryable<Activity>> QueryAsync() =>
        _scope.ApplyAsync(_db.Activities.AsNoTracking()
            .Include(a => a.Customer)
            .Include(a => a.Lead)
            .Include(a => a.AssignedTo)
            .AsQueryable());

    public static IQueryable<Activity> Search(IQueryable<Activity> query, string? q, string? type, string? status,
        string? assignedTo, DateTime? from, DateTime? to)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(a =>
                a.Subject.ToLower().Contains(term) ||
                (a.Customer != null && a.Customer.CustomerName.ToLower().Contains(term)) ||
                (a.Lead != null && a.Lead.LeadName.ToLower().Contains(term)));
        }
        if (Enum.TryParse<ActivityType>(type, out var t)) query = query.Where(a => a.ActivityType == t);
        if (Enum.TryParse<ActivityStatus>(status, out var s)) query = query.Where(a => a.Status == s);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(a => a.AssignedToId == assignedTo);
        if (from is not null) query = query.Where(a => a.ActivityDate >= from.Value.Date);
        if (to is not null)
        {
            var end = to.Value.Date.AddDays(1);
            query = query.Where(a => a.ActivityDate < end);
        }
        return query;
    }

    public static IQueryable<Activity> Sort(IQueryable<Activity> query, string? sort) => sort switch
    {
        "date" => query.OrderBy(a => a.ActivityDate),
        "type" => query.OrderBy(a => a.ActivityType),
        "type_desc" => query.OrderByDescending(a => a.ActivityType),
        "subject" => query.OrderBy(a => a.Subject),
        "subject_desc" => query.OrderByDescending(a => a.Subject),
        _ => query.OrderByDescending(a => a.ActivityDate)
    };

    public async Task<Activity?> GetAsync(int id)
    {
        var activity = await _db.Activities
            .Include(a => a.Customer)
            .Include(a => a.Lead)
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.ActivityId == id);
        return activity is not null && await _scope.CanAccessAsync(activity) ? activity : null;
    }

    public async Task<ServiceResult<Activity>> CreateAsync(ActivityInputDto input)
    {
        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<Activity>.Invalid(errors);

        var activity = new Activity { CreatedDate = DateTime.Now };
        Apply(input, activity);
        _db.Activities.Add(activity);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Create, nameof(Activity), activity.ActivityId.ToString(), null, activity);
        return ServiceResult<Activity>.Ok(activity);
    }

    public async Task<ServiceResult<Activity>> UpdateAsync(int id, ActivityInputDto input)
    {
        var activity = await GetAsync(id);
        if (activity is null) return ServiceResult<Activity>.NotFound();

        Normalize(input);
        var errors = await ValidateAsync(input);
        if (errors.Count > 0) return ServiceResult<Activity>.Invalid(errors);

        var before = AuditService.Snapshot(activity);
        Apply(input, activity);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Update, nameof(Activity), id.ToString(), before, activity);
        return ServiceResult<Activity>.Ok(activity);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var activity = await GetAsync(id);
        if (activity is null) return ServiceResult<bool>.NotFound();

        var before = AuditService.Snapshot(activity);
        _db.Activities.Remove(activity);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Delete, nameof(Activity), id.ToString(), before);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<List<ServiceError>> ValidateAsync(ActivityInputDto input)
    {
        var errors = ValidationExtensions.AnnotationErrors(input);
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
        if (input.Status == ActivityStatus.Planned && input.ActivityDate is not null && input.ActivityDate.Value.Date < DateTime.Today)
        {
            errors.Add(new ServiceError(nameof(input.ActivityDate), "A planned activity cannot be dated in the past. Mark it Completed to log a past activity."));
        }
        if (!await _scope.CanAssignToAsync(input.AssignedToId))
        {
            errors.Add(new ServiceError(nameof(input.AssignedToId), "Select a valid user within your scope."));
        }
        return errors;
    }

    private void Normalize(ActivityInputDto input)
    {
        input.Subject = input.Subject?.Trim() ?? string.Empty;
        input.Description = input.Description.Clean();
        if (_scope.IsSalesExecutive || string.IsNullOrEmpty(input.AssignedToId)) input.AssignedToId = _scope.UserId;
    }

    private static void Apply(ActivityInputDto input, Activity activity)
    {
        activity.ActivityType = input.ActivityType!.Value;
        activity.Subject = input.Subject;
        activity.Description = input.Description;
        activity.ActivityDate = input.ActivityDate!.Value;
        activity.CustomerId = input.CustomerId;
        activity.LeadId = input.LeadId;
        activity.Status = input.Status!.Value;
        activity.AssignedToId = input.AssignedToId;
    }
}

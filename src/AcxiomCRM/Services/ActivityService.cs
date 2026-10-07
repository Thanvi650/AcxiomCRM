using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

/// <summary>Activities: calls, meetings, emails and tasks logged against customers and leads.</summary>
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

    /// <summary>Marks a planned activity (e.g. a task) as done.</summary>
    public async Task<ServiceResult<Activity>> CompleteAsync(int id)
    {
        var activity = await GetAsync(id);
        if (activity is null) return ServiceResult<Activity>.NotFound();
        if (activity.Status != ActivityStatus.Planned)
            return ServiceResult<Activity>.Invalid(string.Empty, $"Only planned activities can be completed. This one is {activity.Status}.");

        var before = new { activity.Status, activity.ActivityDate };
        activity.Status = ActivityStatus.Completed;
        // A task finished earlier than planned is recorded as done now.
        if (activity.ActivityDate > DateTime.Now) activity.ActivityDate = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditActions.Complete, nameof(Activity), id.ToString(), before, new { activity.Status, activity.ActivityDate });
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
        if (input.Status == ActivityStatus.Completed && input.ActivityDate is not null && input.ActivityDate.Value.Date > DateTime.Today)
        {
            errors.Add(new ServiceError(nameof(input.ActivityDate), "A completed activity cannot be dated in the future. Set it to Planned instead."));
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

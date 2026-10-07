using AcxiomCRM.Data;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

public class CustomerService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserScope _scope;
    private readonly IAuditService _audit;

    public CustomerService(ApplicationDbContext db, IUserScope scope, IAuditService audit)
    {
        _db = db;
        _scope = scope;
        _audit = audit;
    }

    /// <summary>Customers the current user may see.</summary>
    public Task<IQueryable<Customer>> QueryAsync() =>
        _scope.ApplyAsync(_db.Customers.AsNoTracking().Include(c => c.AssignedTo).AsQueryable());

    public static IQueryable<Customer> Search(IQueryable<Customer> query, string? q, string? status, string? assignedTo)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(c =>
                c.CustomerName.ToLower().Contains(term) ||
                c.Email.ToLower().Contains(term) ||
                c.Phone.Contains(term) ||
                c.CustomerCode.ToLower().Contains(term) ||
                (c.CompanyName != null && c.CompanyName.ToLower().Contains(term)));
        }
        if (Enum.TryParse<CustomerStatus>(status, out var s)) query = query.Where(c => c.Status == s);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(c => c.AssignedToId == assignedTo);
        return query;
    }

    public static IQueryable<Customer> Sort(IQueryable<Customer> query, string? sort) => sort switch
    {
        "name" => query.OrderBy(c => c.CustomerName),
        "name_desc" => query.OrderByDescending(c => c.CustomerName),
        "company" => query.OrderBy(c => c.CompanyName),
        "company_desc" => query.OrderByDescending(c => c.CompanyName),
        "status" => query.OrderBy(c => c.Status),
        "status_desc" => query.OrderByDescending(c => c.Status),
        "created" => query.OrderBy(c => c.CreatedDate),
        _ => query.OrderByDescending(c => c.CreatedDate)
    };

    public async Task<Customer?> GetAsync(int id)
    {
        var customer = await _db.Customers.Include(c => c.AssignedTo).FirstOrDefaultAsync(c => c.CustomerId == id);
        return customer is not null && await _scope.CanAccessAsync(customer) ? customer : null;
    }

    public async Task<ServiceResult<Customer>> CreateAsync(CustomerInputDto input)
    {
        Normalize(input);
        var (errors, conflict) = await ValidateAsync(input, null);
        if (errors.Count > 0) return ServiceResult<Customer>.FromErrors(errors, conflict);

        var customer = new Customer
        {
            CustomerCode = "TMP-" + Guid.NewGuid().ToString("N")[..12],
            CreatedBy = _scope.UserId,
            CreatedDate = DateTime.Now
        };
        Apply(input, customer);
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        customer.CustomerCode = $"CUS-{customer.CustomerId:D5}";
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Create, nameof(Customer), customer.CustomerId.ToString(), null, customer);
        return ServiceResult<Customer>.Ok(customer);
    }

    public async Task<ServiceResult<Customer>> UpdateAsync(int id, CustomerInputDto input)
    {
        var customer = await GetAsync(id);
        if (customer is null) return ServiceResult<Customer>.NotFound();

        Normalize(input);
        var (errors, conflict) = await ValidateAsync(input, id);
        if (errors.Count > 0) return ServiceResult<Customer>.FromErrors(errors, conflict);

        var before = AuditService.Snapshot(customer);
        var oldStatus = customer.Status;
        Apply(input, customer);
        customer.ModifiedDate = DateTime.Now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditActions.Update, nameof(Customer), id.ToString(), before, customer);
        if (oldStatus != customer.Status)
        {
            await _audit.LogAsync(AuditActions.StatusChange, nameof(Customer), id.ToString(),
                new { Status = oldStatus }, new { customer.Status });
        }
        return ServiceResult<Customer>.Ok(customer);
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var customer = await GetAsync(id);
        if (customer is null) return ServiceResult<bool>.NotFound();

        var hasOpportunities = await _db.Opportunities.AnyAsync(o => o.CustomerId == id);
        var hasFollowUps = await _db.FollowUps.AnyAsync(f => f.CustomerId == id);
        var hasActivities = await _db.Activities.AnyAsync(a => a.CustomerId == id);
        if (hasOpportunities || hasFollowUps || hasActivities)
        {
            return ServiceResult<bool>.Conflict(string.Empty,
                "This customer has related opportunities, follow-ups or activities. Set the customer to Inactive instead, or remove the related records first.");
        }

        var before = AuditService.Snapshot(customer);
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            // Converted leads keep their history but no longer point at the deleted customer.
            await _db.Leads.Where(l => l.ConvertedCustomerId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConvertedCustomerId, (int?)null));
            _db.Customers.Remove(customer);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await _audit.LogAsync(AuditActions.Delete, nameof(Customer), id.ToString(), before);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<(List<ServiceError> Errors, bool Conflict)> ValidateAsync(CustomerInputDto input, int? existingId)
    {
        var errors = ValidationExtensions.AnnotationErrors(input);
        if (!await _scope.CanAssignToAsync(input.AssignedToId))
        {
            errors.Add(new ServiceError(nameof(input.AssignedToId), "Select a valid user within your scope."));
        }
        if (errors.Count > 0) return (errors, false);

        // Duplicate checks: only these produce a 409 Conflict.
        if (await _db.Customers.AnyAsync(c => c.Email == input.Email && c.CustomerId != existingId))
        {
            errors.Add(new ServiceError(nameof(input.Email), "A customer with this email already exists."));
        }
        if (await _db.Customers.AnyAsync(c => c.Phone == input.Phone && c.CustomerId != existingId))
        {
            errors.Add(new ServiceError(nameof(input.Phone), "A customer with this phone number already exists."));
        }
        if (!string.IsNullOrEmpty(input.CompanyName))
        {
            var name = input.CustomerName.ToLower();
            var company = input.CompanyName.ToLower();
            if (await _db.Customers.AnyAsync(c => c.CustomerName.ToLower() == name && c.CompanyName != null &&
                                                  c.CompanyName.ToLower() == company && c.CustomerId != existingId))
            {
                errors.Add(new ServiceError(nameof(input.CustomerName), "This customer already exists for the same company."));
            }
        }
        return (errors, errors.Count > 0);
    }

    private void Normalize(CustomerInputDto input)
    {
        input.CustomerName = input.CustomerName?.Trim() ?? string.Empty;
        input.Email = input.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        input.Phone = input.Phone?.Trim() ?? string.Empty;
        input.CompanyName = input.CompanyName.Clean();
        input.Address = input.Address.Clean();
        input.City = input.City.Clean();
        input.State = input.State.Clean();
        input.Notes = input.Notes.Clean();
        // Sales Executives always own what they create.
        if (_scope.IsSalesExecutive || string.IsNullOrEmpty(input.AssignedToId)) input.AssignedToId = _scope.UserId;
    }

    private static void Apply(CustomerInputDto input, Customer customer)
    {
        customer.CustomerName = input.CustomerName;
        customer.Email = input.Email;
        customer.Phone = input.Phone;
        customer.CompanyName = input.CompanyName;
        customer.Address = input.Address;
        customer.City = input.City;
        customer.State = input.State;
        customer.Status = input.Status!.Value;
        customer.Notes = input.Notes;
        customer.AssignedToId = input.AssignedToId;
    }
}

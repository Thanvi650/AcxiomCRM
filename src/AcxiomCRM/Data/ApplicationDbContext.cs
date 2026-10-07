using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Store enums as readable text (e.g. "Qualified") rather than numbers.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);

        if (Database.IsSqlite())
        {
            // SQLite (test/fallback only) cannot sort or sum decimals, so store money as REAL there.
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
        }
        else
        {
            // SQL Server: money as decimal(18,2).
            configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Every CRM foreign key uses NO ACTION (Restrict). SQL Server rejects "multiple cascade
        // paths" (e.g. Lead -> FollowUp directly and Lead -> Opportunity -> FollowUp), so the
        // services remove or detach dependent rows explicitly, inside a transaction, before a delete.

        builder.Entity<ApplicationUser>(e =>
        {
            e.HasOne(u => u.Manager)
                .WithMany()
                .HasForeignKey(u => u.ManagerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Customer>(e =>
        {
            e.HasIndex(c => c.CustomerCode).IsUnique();
            e.HasIndex(c => c.Email).IsUnique();
            e.HasIndex(c => c.Phone).IsUnique();
            e.HasIndex(c => c.CustomerName);
            e.HasIndex(c => c.AssignedToId);
            e.HasOne(c => c.AssignedTo).WithMany().HasForeignKey(c => c.AssignedToId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Lead>(e =>
        {
            e.HasIndex(l => l.LeadCode).IsUnique();
            e.HasIndex(l => l.Status);
            e.HasIndex(l => l.AssignedToId);
            e.HasOne(l => l.AssignedTo).WithMany().HasForeignKey(l => l.AssignedToId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.ConvertedCustomer).WithMany().HasForeignKey(l => l.ConvertedCustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Opportunity>(e =>
        {
            e.HasIndex(o => o.Stage);
            e.HasIndex(o => o.AssignedToId);
            e.HasOne(o => o.Customer).WithMany(c => c.Opportunities).HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Lead).WithMany().HasForeignKey(o => o.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.AssignedTo).WithMany().HasForeignKey(o => o.AssignedToId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Opportunity_Probability", "Probability BETWEEN 0 AND 100");
                t.HasCheckConstraint("CK_Opportunity_Amount", "Amount >= 0");
            });
        });

        builder.Entity<FollowUp>(e =>
        {
            e.HasIndex(f => new { f.Status, f.FollowUpDate });
            e.HasIndex(f => f.AssignedToId);
            e.HasOne(f => f.Customer).WithMany(c => c.FollowUps).HasForeignKey(f => f.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.Lead).WithMany(l => l.FollowUps).HasForeignKey(f => f.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.Opportunity).WithMany(o => o.FollowUps).HasForeignKey(f => f.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.AssignedTo).WithMany().HasForeignKey(f => f.AssignedToId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Activity>(e =>
        {
            e.HasIndex(a => a.ActivityDate);
            e.HasIndex(a => a.AssignedToId);
            e.HasOne(a => a.Customer).WithMany(c => c.Activities).HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Lead).WithMany(l => l.Activities).HasForeignKey(a => a.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.AssignedTo).WithMany().HasForeignKey(a => a.AssignedToId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.CreatedDate);
            e.HasIndex(a => new { a.EntityName, a.Action });
            e.HasIndex(a => a.UserId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditLog();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAuditLog();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Audit records are append-only: they can be inserted but never changed or removed.</summary>
    private void GuardAuditLog()
    {
        var tampered = ChangeTracker.Entries<AuditLog>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered)
        {
            throw new InvalidOperationException("Audit log entries are append-only and cannot be modified or deleted.");
        }
    }
}

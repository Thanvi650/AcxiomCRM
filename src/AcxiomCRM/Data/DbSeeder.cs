using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

/// <summary>
/// Creates the database, the three roles and — when Seed:DefaultPassword is configured
/// (Development only by default) — demo users and sample CRM data.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ApplicationDbContext>();
        var config = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbSeeder));

        if (db.Database.IsSqlServer())
        {
            // Applies any pending EF Core migrations (creates the database on first run).
            await db.Database.MigrateAsync();
        }
        else
        {
            // SQLite (tests / fallback): migrations are SQL Server-specific, so build the schema from the model.
            await db.Database.EnsureCreatedAsync();
            // Same append-only protection as the SQL Server migration's trigger.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS TR_AuditLogs_NoUpdate BEFORE UPDATE ON AuditLogs
                BEGIN SELECT RAISE(ABORT, 'Audit log entries are append-only and cannot be modified or deleted.'); END;
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS TR_AuditLogs_NoDelete BEFORE DELETE ON AuditLogs
                BEGIN SELECT RAISE(ABORT, 'Audit log entries are append-only and cannot be modified or deleted.'); END;
                """);
        }

        var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole(role));
        }

        var password = config["Seed:DefaultPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:DefaultPassword is not configured; demo users were not created. " +
                              "Set it with user-secrets or an environment variable to seed users.");
            return;
        }

        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await EnsureUserAsync(userManager, "admin@acxiomcrm.local", "System Administrator", Roles.Admin, null, password);
        var priya = await EnsureUserAsync(userManager, "priya.manager@acxiomcrm.local", "Priya Sharma", Roles.Manager, null, password);
        var arjun = await EnsureUserAsync(userManager, "arjun.manager@acxiomcrm.local", "Arjun Mehta", Roles.Manager, null, password);
        var rahul = await EnsureUserAsync(userManager, "rahul.sales@acxiomcrm.local", "Rahul Verma", Roles.SalesExecutive, priya.Id, password);
        var sneha = await EnsureUserAsync(userManager, "sneha.sales@acxiomcrm.local", "Sneha Reddy", Roles.SalesExecutive, priya.Id, password);
        var kiran = await EnsureUserAsync(userManager, "kiran.sales@acxiomcrm.local", "Kiran Kumar", Roles.SalesExecutive, arjun.Id, password);

        if (config.GetValue("Seed:SampleData", true) && !await db.Customers.AnyAsync())
        {
            await SeedSampleDataAsync(db, admin, priya, rahul, sneha, kiran);
            logger.LogInformation("Sample CRM data seeded.");
        }
    }

    private static async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> userManager, string email, string fullName, string role, string? managerId, string password)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null) return user;

        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName,
            ManagerId = managerId,
            IsActive = true,
            CreatedDate = DateTime.Now.AddMonths(-8)
        };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed user {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
        await userManager.AddToRoleAsync(user, role);
        return user;
    }

    private static async Task SeedSampleDataAsync(
        ApplicationDbContext db, ApplicationUser admin, ApplicationUser priya,
        ApplicationUser rahul, ApplicationUser sneha, ApplicationUser kiran)
    {
        var today = DateTime.Today;

        // ---- Customers ----
        var customerSeed = new (string Name, string Company, string City, string State, ApplicationUser Owner, CustomerStatus Status, int DaysAgo)[]
        {
            ("Ananya Iyer", "Iyer Textiles", "Chennai", "Tamil Nadu", rahul, CustomerStatus.Active, 210),
            ("Vikram Singh", "Singh Logistics", "New Delhi", "Delhi", rahul, CustomerStatus.Active, 180),
            ("Meera Nair", "Nair Foods", "Kochi", "Kerala", sneha, CustomerStatus.Active, 150),
            ("Rohan Gupta", "Gupta Electronics", "Mumbai", "Maharashtra", sneha, CustomerStatus.Active, 120),
            ("Fatima Khan", "Khan Pharma", "Hyderabad", "Telangana", kiran, CustomerStatus.Active, 95),
            ("Suresh Patel", "Patel Agro", "Ahmedabad", "Gujarat", kiran, CustomerStatus.Prospect, 60),
            ("Lakshmi Rao", "Rao Constructions", "Bengaluru", "Karnataka", rahul, CustomerStatus.Active, 45),
            ("Arvind Joshi", "Joshi Motors", "Pune", "Maharashtra", sneha, CustomerStatus.Inactive, 30),
            ("Deepa Menon", "Menon Healthcare", "Thiruvananthapuram", "Kerala", kiran, CustomerStatus.Active, 12),
            ("Karan Malhotra", "Malhotra Retail", "Chandigarh", "Punjab", priya, CustomerStatus.Prospect, 3)
        };
        var customers = customerSeed.Select((c, i) =>
        {
            var first = c.Name.Split(' ')[0].ToLowerInvariant();
            var domain = c.Company.Replace(" ", string.Empty).ToLowerInvariant();
            return new Customer
            {
                CustomerCode = $"CUS-{i + 1:D5}",
                CustomerName = c.Name,
                Email = $"{first}@{domain}.example",
                Phone = $"98765{i + 1:D5}",
                CompanyName = c.Company,
                Address = $"{10 + i * 7}, Main Road",
                City = c.City,
                State = c.State,
                Status = c.Status,
                AssignedToId = c.Owner.Id,
                CreatedBy = c.Owner.Id,
                CreatedDate = today.AddDays(-c.DaysAgo).AddHours(10)
            };
        }).ToList();
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        // ---- Leads ----
        var leadSeed = new (string Name, string Company, LeadSource Source, LeadStatus Status, Priority Priority, decimal Value, ApplicationUser Owner, int DaysAgo)[]
        {
            ("Nikhil Bose", "Bose Interiors", LeadSource.Website, LeadStatus.New, Priority.Medium, 150000, rahul, 2),
            ("Pooja Desai", "Desai Exports", LeadSource.Referral, LeadStatus.Contacted, Priority.High, 420000, rahul, 9),
            ("Harish Pillai", "Pillai Shipping", LeadSource.Event, LeadStatus.Qualified, Priority.High, 780000, rahul, 18),
            ("Sanjana Kapoor", "Kapoor Studios", LeadSource.SocialMedia, LeadStatus.New, Priority.Low, 60000, sneha, 1),
            ("Imran Sheikh", "Sheikh Traders", LeadSource.ColdCall, LeadStatus.Unqualified, Priority.Low, 25000, sneha, 40),
            ("Divya Krishnan", "Krishnan Labs", LeadSource.Campaign, LeadStatus.Qualified, Priority.Medium, 310000, sneha, 22),
            ("Manoj Tiwari", "Tiwari Steel", LeadSource.Referral, LeadStatus.Lost, Priority.Medium, 900000, kiran, 75),
            ("Ritu Agarwal", "Agarwal Jewels", LeadSource.Website, LeadStatus.Contacted, Priority.High, 520000, kiran, 6),
            ("Gaurav Saxena", "Saxena Solar", LeadSource.Event, LeadStatus.New, Priority.High, 1200000, kiran, 0),
            ("Ayesha Siddiqui", "Siddiqui Fashion", LeadSource.Campaign, LeadStatus.Contacted, Priority.Medium, 180000, priya, 14),
            ("Prakash Hegde", "Hegde Coffee", LeadSource.Other, LeadStatus.New, Priority.Low, 90000, sneha, 4)
        };
        var leads = leadSeed.Select((l, i) => new Lead
        {
            LeadCode = $"LEAD-{i + 1:D5}",
            LeadName = l.Name,
            Email = $"{l.Name.Split(' ')[0].ToLowerInvariant()}@{l.Company.Replace(" ", string.Empty).ToLowerInvariant()}.example",
            Phone = $"91234{i + 1:D5}",
            CompanyName = l.Company,
            Source = l.Source,
            Status = l.Status,
            Priority = l.Priority,
            ExpectedValue = l.Value,
            AssignedToId = l.Owner.Id,
            CreatedDate = today.AddDays(-l.DaysAgo).AddHours(11)
        }).ToList();

        // Two historical conversions so the conversion report has data.
        leads.Add(new Lead
        {
            LeadCode = "LEAD-00012", LeadName = "Ananya Iyer", Email = "ananya@iyertextiles.example", Phone = "9876500001",
            CompanyName = "Iyer Textiles", Source = LeadSource.Referral, Status = LeadStatus.Converted, Priority = Priority.High,
            ExpectedValue = 650000, AssignedToId = rahul.Id, CreatedDate = today.AddDays(-230),
            ConvertedCustomerId = customers[0].CustomerId, ConvertedDate = today.AddDays(-210)
        });
        leads.Add(new Lead
        {
            LeadCode = "LEAD-00013", LeadName = "Fatima Khan", Email = "fatima@khanpharma.example", Phone = "9876500005",
            CompanyName = "Khan Pharma", Source = LeadSource.Website, Status = LeadStatus.Converted, Priority = Priority.Medium,
            ExpectedValue = 400000, AssignedToId = kiran.Id, CreatedDate = today.AddDays(-110),
            ConvertedCustomerId = customers[4].CustomerId, ConvertedDate = today.AddDays(-95)
        });
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync();

        // ---- Opportunities ----
        Opportunity Opp(string name, int customer, decimal amount, OpportunityStage stage, int probability,
            int closeInDays, ApplicationUser owner, int createdDaysAgo, int? closedDaysAgo = null) => new()
        {
            OpportunityName = name,
            CustomerId = customers[customer].CustomerId,
            Amount = amount,
            Stage = stage,
            Status = stage switch
            {
                OpportunityStage.Won => OpportunityStatus.Won,
                OpportunityStage.Lost => OpportunityStatus.Lost,
                _ => OpportunityStatus.Open
            },
            Probability = probability,
            ExpectedCloseDate = today.AddDays(closeInDays),
            AssignedToId = owner.Id,
            Source = "Direct",
            CreatedDate = today.AddDays(-createdDaysAgo),
            ClosedDate = closedDaysAgo is null ? null : today.AddDays(-closedDaysAgo.Value).AddHours(15)
        };

        var opportunities = new List<Opportunity>
        {
            Opp("Iyer Textiles – ERP rollout", 0, 650000, OpportunityStage.Won, 100, -200, rahul, 205, 200),
            Opp("Singh Logistics – fleet tracking", 1, 480000, OpportunityStage.Won, 100, -150, rahul, 170, 150),
            Opp("Nair Foods – cold-chain sensors", 2, 320000, OpportunityStage.Won, 100, -115, sneha, 140, 115),
            Opp("Gupta Electronics – POS upgrade", 3, 275000, OpportunityStage.Lost, 0, -88, sneha, 110, 88),
            Opp("Khan Pharma – compliance suite", 4, 540000, OpportunityStage.Won, 100, -60, kiran, 90, 60),
            Opp("Rao Constructions – site CRM", 6, 390000, OpportunityStage.Won, 100, -25, rahul, 40, 25),
            Opp("Joshi Motors – dealer portal", 7, 210000, OpportunityStage.Lost, 0, -20, sneha, 28, 20),
            Opp("Menon Healthcare – patient app", 8, 860000, OpportunityStage.Won, 100, -5, kiran, 11, 5),
            Opp("Singh Logistics – warehouse module", 1, 350000, OpportunityStage.Negotiation, 70, 14, rahul, 20),
            Opp("Nair Foods – analytics add-on", 2, 180000, OpportunityStage.Proposal, 50, 30, sneha, 15),
            Opp("Patel Agro – supply-chain pilot", 5, 260000, OpportunityStage.Qualification, 20, 45, kiran, 10),
            Opp("Rao Constructions – phase 2", 6, 720000, OpportunityStage.Proposal, 40, 60, rahul, 8),
            Opp("Khan Pharma – field-force app", 4, 450000, OpportunityStage.Negotiation, 75, 10, kiran, 25),
            Opp("Malhotra Retail – loyalty program", 9, 300000, OpportunityStage.Qualification, 25, 50, priya, 3)
        };
        foreach (var o in opportunities.Where(o => o.Status == OpportunityStatus.Lost))
        {
            o.OutcomeNotes = "Customer chose a lower-priced competitor.";
        }
        foreach (var o in opportunities.Where(o => o.Status == OpportunityStatus.Won))
        {
            o.OutcomeNotes = "Signed after successful pilot.";
        }
        db.Opportunities.AddRange(opportunities);
        await db.SaveChangesAsync();

        // ---- Follow-ups (some deliberately overdue so the overdue view has data) ----
        db.FollowUps.AddRange(
            new FollowUp { Subject = "Demo of warehouse module", FollowUpType = FollowUpType.Meeting, FollowUpDate = today.AddDays(1).AddHours(11), OpportunityId = opportunities[8].OpportunityId, CustomerId = customers[1].CustomerId, AssignedToId = rahul.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Call to discuss pricing", FollowUpType = FollowUpType.Call, FollowUpDate = today.AddHours(16), LeadId = leads[1].LeadId, AssignedToId = rahul.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Send proposal revision", FollowUpType = FollowUpType.Email, FollowUpDate = today.AddDays(-2).AddHours(10), OpportunityId = opportunities[9].OpportunityId, CustomerId = customers[2].CustomerId, AssignedToId = sneha.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Intro call", FollowUpType = FollowUpType.Call, FollowUpDate = today.AddDays(2).AddHours(12), LeadId = leads[3].LeadId, AssignedToId = sneha.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Site visit", FollowUpType = FollowUpType.Visit, FollowUpDate = today.AddDays(4).AddHours(10), CustomerId = customers[5].CustomerId, AssignedToId = kiran.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Contract negotiation", FollowUpType = FollowUpType.Meeting, FollowUpDate = today.AddDays(-1).AddHours(15), OpportunityId = opportunities[12].OpportunityId, CustomerId = customers[4].CustomerId, AssignedToId = kiran.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Kick-off review", FollowUpType = FollowUpType.Meeting, FollowUpDate = today.AddDays(-10).AddHours(11), CustomerId = customers[8].CustomerId, AssignedToId = kiran.Id, Status = FollowUpStatus.Completed, CompletedDate = today.AddDays(-10).AddHours(12) },
            new FollowUp { Subject = "Quarterly check-in", FollowUpType = FollowUpType.Call, FollowUpDate = today.AddDays(-6).AddHours(14), CustomerId = customers[0].CustomerId, AssignedToId = rahul.Id, Status = FollowUpStatus.Missed },
            new FollowUp { Subject = "Discovery meeting", FollowUpType = FollowUpType.Meeting, FollowUpDate = today.AddDays(3).AddHours(15), LeadId = leads[9].LeadId, AssignedToId = priya.Id, Status = FollowUpStatus.Planned },
            new FollowUp { Subject = "Qualification call", FollowUpType = FollowUpType.Call, FollowUpDate = today.AddDays(5).AddHours(10), LeadId = leads[8].LeadId, AssignedToId = kiran.Id, Status = FollowUpStatus.Planned });

        // ---- Activities ----
        db.Activities.AddRange(
            new Activity { ActivityType = ActivityType.Call, Subject = "Pricing discussion", ActivityDate = today.AddDays(-3).AddHours(11), CustomerId = customers[1].CustomerId, AssignedToId = rahul.Id, Status = ActivityStatus.Completed, Description = "Customer wants a 3-year contract option." },
            new Activity { ActivityType = ActivityType.Meeting, Subject = "Requirements workshop", ActivityDate = today.AddDays(-7).AddHours(10), CustomerId = customers[2].CustomerId, AssignedToId = sneha.Id, Status = ActivityStatus.Completed },
            new Activity { ActivityType = ActivityType.Email, Subject = "Shared case studies", ActivityDate = today.AddDays(-1).AddHours(17), LeadId = leads[7].LeadId, AssignedToId = kiran.Id, Status = ActivityStatus.Completed },
            new Activity { ActivityType = ActivityType.Task, Subject = "Prepare phase-2 estimate", ActivityDate = today.AddDays(2).AddHours(9), CustomerId = customers[6].CustomerId, AssignedToId = rahul.Id, Status = ActivityStatus.Planned },
            new Activity { ActivityType = ActivityType.Meeting, Subject = "Team pipeline review", ActivityDate = today.AddDays(1).AddHours(16), AssignedToId = priya.Id, Status = ActivityStatus.Planned },
            new Activity { ActivityType = ActivityType.Call, Subject = "Follow up on demo feedback", ActivityDate = today.AddDays(-12).AddHours(12), LeadId = leads[5].LeadId, AssignedToId = sneha.Id, Status = ActivityStatus.Completed });

        db.AuditLogs.Add(new AuditLog
        {
            Action = "Seed",
            EntityName = "System",
            UserId = admin.Id,
            UserName = admin.UserName,
            NewValue = "Sample data created",
            CreatedDate = DateTime.Now
        });
        await db.SaveChangesAsync();
    }
}

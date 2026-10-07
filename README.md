# AcxiomCRM

A role-based CRM web application covering the full sales lifecycle: lead capture, qualification and conversion, opportunities and pipeline, follow-ups, activities, audit logging, a secured REST API, dashboards and reports.

**Stack:** ASP.NET Core 8 MVC · ASP.NET Core Identity · Entity Framework Core + SQL Server (migrations) · Bootstrap 5 · jQuery Unobtrusive Validation · Chart.js 4 · Swagger · xUnit

---

## Run it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and a **SQL Server** instance.

### 1. Get a SQL Server

**Windows:** SQL Server Express LocalDB (installed with Visual Studio). The default connection string in `appsettings.json` already points at `(localdb)\MSSQLLocalDB`, so skip to step 3.

**Mac (Apple Silicon or Intel) / Linux:** run SQL Server 2022 in Docker. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) first. On Apple Silicon, enable *Settings → General → "Use Rosetta for x86_64/amd64 emulation"*. Then choose your own strong SA password (8+ characters with upper case, lower case, a digit and a symbol) and run:

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<your-strong-password>" -p 1433:1433 --name acxiomcrm-sql --platform linux/amd64 -d mcr.microsoft.com/mssql/server:2022-latest
```

Afterwards, start it again with `docker start acxiomcrm-sql`.

### 2. Point the app at it (kept out of source control)

The connection string is stored with **user-secrets** so no database password is committed:

```bash
cd src/AcxiomCRM
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=AcxiomCRM;User Id=sa;Password=<your-strong-password>;TrustServerCertificate=True;MultipleActiveResultSets=True"
```

### 3. Create the database and run

```bash
dotnet tool restore                          # installs dotnet-ef (from .config/dotnet-tools.json)
cd src/AcxiomCRM
dotnet ef database update                    # applies the migrations in Data/Migrations
dotnet run
```

Open **http://localhost:5080**. On first run the app also applies any pending migrations itself, then seeds the three roles, demo users and sample data.

> If `dotnet` is not on your PATH (it was installed to `~/.dotnet`), add `export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"` to `~/.zshrc` and open a new terminal.

**In VS Code:** open the `AcxiomCRM` folder, install the recommended C# Dev Kit extension, then press **F5** and pick *AcxiomCRM (SQL Server)*. *AcxiomCRM (SQLite fallback)* runs without SQL Server, for when it isn't available.

### Migrations

| Task | Command (from `src/AcxiomCRM`) |
|---|---|
| Add a migration after changing a model | `dotnet ef migrations add <Name> --output-dir Data/Migrations` |
| Apply migrations | `dotnet ef database update` |
| Generate a SQL script | `dotnet ef migrations script --idempotent -o ../../database/AcxiomCRM_schema.sql` |
| Reset the database | `dotnet ef database drop` then `dotnet ef database update` |

A ready-made schema script is in `database/AcxiomCRM_schema.sql`.

### Demo accounts (Development only)

The demo users are created on first run, but only if you choose a demo password first. It's kept in **user-secrets**, outside the repository, so no password is committed. Pick any password with 8+ characters, upper and lower case, a number and a symbol:

```bash
cd src/AcxiomCRM
dotnet user-secrets set "Seed:DefaultPassword" "<choose-a-demo-password>"
```

All demo users then sign in with that password. If it isn't set, the app still starts, but it only creates the roles; register an account at `/Account/Register` instead.

| Email | Role | Scope |
|---|---|---|
| admin@acxiomcrm.local | Admin | Everything, plus users, roles and the audit log |
| priya.manager@acxiomcrm.local | Manager | Own records plus team (Rahul, Sneha) |
| arjun.manager@acxiomcrm.local | Manager | Own records plus team (Kiran) |
| rahul.sales@acxiomcrm.local | Sales Executive | Own/assigned records only |
| sneha.sales@acxiomcrm.local | Sales Executive | Own/assigned records only |
| kiran.sales@acxiomcrm.local | Sales Executive | Own/assigned records only |

Self-registration (`/Account/Register`) always creates a **Sales Executive** (least privilege). An Admin can change the role afterwards.


### Tests

```bash
dotnet test
```

There are 33 tests: unit tests for the business rules, plus integration tests that run the real app against a temporary SQLite database, so no SQL Server is needed (API status codes, role scoping, lockout, audit, password hashing).

### API docs

The full API reference (endpoints, status codes, error format, business rules, curl examples) is in **[docs/API.md](docs/API.md)**. In Development, Swagger UI is at **http://localhost:5080/swagger**. Authenticate with `POST /api/auth/login` (`{ "login": "...", "password": "..." }`). This sets the auth cookie, which the browser then sends on the other calls.

---

## Project structure

```
src/AcxiomCRM/
├── Controllers/          MVC controllers (one per module)
│   └── Api/              REST API controllers (DTOs only)
├── Data/                 ApplicationDbContext (EF Core + Identity), DbSeeder, design-time factory
│   └── Migrations/       EF Core SQL Server migrations
├── Dtos/                 API/form input models (validation attributes) and response DTOs
├── Helpers/              Paging, CSV export, sorting/display helpers
├── Models/               Entities, enums, role names
├── Services/             Business logic: rules, row-level scope, audit, auth, reports
├── Validation/           Custom validation attributes (server + client)
├── ViewComponents/       Header notifications
├── ViewModels/           View-specific models
├── Views/                Razor views
└── wwwroot/              CSS, JS (validation adapters, dashboard charts), libraries
tests/AcxiomCRM.Tests/    xUnit unit + integration tests
```

**Layering:** Controllers → Services (business rules, authorization scope, auditing) → EF Core DbContext → SQL Server. MVC and the API share the same services and input DTOs, so every rule is enforced identically on both paths.

---

## How the requirements are met

### Validation (client + server + business)
- **Client:** DataAnnotations generate unobtrusive jQuery validation for Required, Email, Phone, Length, Date and Numeric/Range. `wwwroot/js/validation.js` adds browser-side versions of the custom rules (`NotInPast`, `Positive`), including "only while the stage is active".
- **Server:** `ModelState` is checked in every POST, `[ApiController]` returns 400 automatically, and **every service re-validates** the DataAnnotations plus the business rules. Tampered requests are rejected even when the browser is bypassed.
- **Business rules** (`Services/OpportunityService.cs` → `OpportunityRules`, `LeadWorkflow`, `FollowUpService`):
  - Opportunity Amount > 0 while active, and never negative.
  - Probability between 0 and 100.
  - Expected Close Date not in the past while the opportunity is active.
  - Follow-up date not earlier than today.
  - Customer email and phone are unique (409 Conflict); duplicate name + company is blocked.
  - Lead status must be a defined value and follow the allowed transitions; Converted is only reachable via Convert.
  - Foreign keys must exist and be inside the user's scope.
- Phone rule: 10-digit Indian mobile number (`^[6-9][0-9]{9}$`), defined once in `Validation/ValidationAttributes.cs`.

### Security
- ASP.NET Core Identity: hashed passwords, no custom password table.
- Password policy: 8+ characters with upper case, lower case, a digit and a symbol.
- Lockout: 5 failed attempts locks the account for 15 minutes (configurable in `appsettings.json`).
- A global `AutoValidateAntiforgeryToken` filter plus `[ValidateAntiForgeryToken]` protect every MVC POST.
- Every endpoint requires sign-in by default (fallback authorization policy).
- Cookies are HttpOnly and SameSite=Strict, and Secure in Production.
- The login endpoints are rate limited.
- HTTPS redirection and HSTS are on in Production.
- Security headers: `nosniff`, `X-Frame-Options`, `Referrer-Policy`.
- SQL injection: EF Core LINQ only, so every query is parameterized.
- Errors: users see friendly error pages and API clients get ProblemDetails. Stack traces only go to the server log.
- Deactivating a user or changing their role invalidates their existing sessions (security stamp checked every minute).
- Passwords, hashes and tokens are never logged or returned. DTOs expose business fields only.

### Role-based authorization
Authorization is enforced on the server, not just by hiding menu items.
- **Role checks:** `[Authorize(Roles = ...)]` on controllers and actions.
- **Row-level scope:** `IUserScope` (`Services/Core.cs`) filters every query.
  - Admin sees all records.
  - Manager sees their own records plus their team's (users whose `ManagerId` is the manager).
  - Sales Executive sees only records assigned to them.
- Records outside the user's scope return 404, and role-restricted endpoints return 403.
- The full permission matrix is at `/Roles` (Admin only).

### Audit log
- **What is recorded:** login, failed login, lockout, logout, register, create/update/delete, status changes, conversion, completion, rescheduling, role changes, activate/deactivate, password reset/change and unlock.
- **Fields:** UserId, UserName, Action, EntityName, RecordId, OldValue/NewValue (JSON), Result, CreatedDate and IpAddress.
- **Append-only:** the DbContext throws if an audit row is modified or deleted, and the app has no edit/delete actions for audit entries.
- **Filtering:** by user, module, action and date range. Admins can export to CSV. Managers see a limited view: their team's business events only.

### REST API
| Method | Endpoint | Notes |
|---|---|---|
| POST | /api/auth/login · /api/auth/logout · GET /api/auth/me | Login is rate limited |
| GET/POST | /api/customers | Paged search; 201 / 400 / 409 |
| GET/PUT/DELETE | /api/customers/{id} | 404 outside scope; 409 if related records exist |
| GET/POST | /api/leads, GET /api/leads/{id} | |
| GET/POST | /api/opportunities, GET /api/opportunities/{id} | Business rules → 400 |
| GET/POST | /api/followups | Past date → 400 |
| GET | /api/reports/pipeline | Admin/Manager only (Sales Executive → 403) |

### Dashboard and reports
- **Dashboard KPI cards:** Total Customers, Total Leads, Open Leads, Total/Open/Won/Lost Opportunities, Total Pipeline Value, plus weighted pipeline, win rate, conversion rate and pending/overdue follow-ups.
- **Dashboard charts (Chart.js):** Lead Status, Opportunity Pipeline and Monthly Sales.
- **Date filters:** Today, This Week, This Month and Custom.
- All dashboard data comes from the server and is limited to what the user is authorized to see.
- **Reports:** Customer, Lead, Follow-Up, Opportunity, Pipeline (by stage and owner, with weighted values), Sales/Conversion, User Activity and Audit. Each supports filters, sorting, paging and CSV export (with protection against spreadsheet formula injection).

### Workflows
- **Lead → Customer:** create → assign → contact → qualify → **Convert**. Conversion creates the customer (or reuses an existing one with the same email/phone) and, optionally, an opportunity, in a single transaction, and is audited.
- **Opportunity:** set amount, probability and close date → move through the stages (detail page or pipeline board) → Won/Lost. Closing records the closed date and sets probability to 100% for Won or 0% for Lost.
- **Follow-up:** schedule → complete / mark missed / reschedule / cancel. Completing a follow-up on a *New* lead moves the lead to *Contacted*. Every step is audited.
- **User administration:** create → assign role and manager → activate/deactivate → reset password / unlock. Every change is audited.

---

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | LocalDB (Windows) | SQL Server connection, overridden with user-secrets |
| `Database:Provider` | `SqlServer` | `Sqlite` = no-SQL-Server fallback (used by the tests) |
| `Security:Lockout:MaxFailedAttempts` | 5 | Failed sign-ins before lockout |
| `Security:Lockout:LockoutMinutes` | 15 | Lockout duration |
| `Security:LoginRateLimitPerMinute` | 10 | Login requests per IP per minute |
| `Seed:DefaultPassword` | *(not set; use user-secrets)* | Creates the demo users when set |
| `Seed:SampleData` | true | Seeds sample CRM data on an empty database |

Never put passwords or connection strings in `appsettings*.json`; that's why `Seed:DefaultPassword` isn't in the repository. For a production deployment, leave `Seed:DefaultPassword` unset. Provide secrets through environment variables or `dotnet user-secrets`.

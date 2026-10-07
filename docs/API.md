# AcxiomCRM REST API

Base URL (development): `http://localhost:5080`. Interactive documentation is available at **`/swagger`** while running in Development.

- **Format:** JSON in and out (`Content-Type: application/json`). Enum values are strings (`"Proposal"`, `"Active"` …). Dates use ISO 8601 (`2026-11-30`, `2026-11-30T10:00`).
- **Authentication:** cookie-based, using the same ASP.NET Core Identity account as the web app. Call `POST /api/auth/login` once; the response sets an HttpOnly cookie that the client sends on later calls.
- **Authorization:** every endpoint except login requires a signed-in user. Data is always limited to the caller's scope:
  - **Admin:** everything.
  - **Manager:** own records and their team's.
  - **Sales Executive:** own/assigned records only.

  A record outside the caller's scope returns `404`, the same as a missing record.
- **DTOs only:** responses never contain database entities, password hashes, security stamps or tokens.

## Endpoints

| Method | Endpoint | Purpose | Success | Errors |
|---|---|---|---|---|
| POST | `/api/auth/login` | Sign in (rate limited per IP) | 200 | 400, 401, 429 |
| POST | `/api/auth/logout` | Sign out | 204 | 401 |
| GET | `/api/auth/me` | Current user | 200 | 401 |
| GET | `/api/customers?q=&status=&page=&pageSize=` | List/search customers | 200 | 401 |
| POST | `/api/customers` | Create customer | 201 + `Location` | 400, 401, 409 |
| GET | `/api/customers/{id}` | Get customer | 200 | 401, 404 |
| PUT | `/api/customers/{id}` | Update customer (full replace) | 200 | 400, 401, 404, 409 |
| DELETE | `/api/customers/{id}` | Delete customer | 204 | 401, 404, 409 |
| GET | `/api/leads?q=&status=&page=&pageSize=` | List/search leads | 200 | 401 |
| POST | `/api/leads` | Create lead | 201 + `Location` | 400, 401 |
| GET | `/api/leads/{id}` | Get lead | 200 | 401, 404 |
| GET | `/api/opportunities?q=&stage=&status=&page=&pageSize=` | List/search opportunities | 200 | 401 |
| POST | `/api/opportunities` | Create opportunity | 201 + `Location` | 400, 401 |
| GET | `/api/opportunities/{id}` | Get opportunity | 200 | 401, 404 |
| GET | `/api/followups?view=&status=&page=&pageSize=` | List follow-ups (`view` = all, pending, overdue, today, upcoming) | 200 | 401 |
| POST | `/api/followups` | Schedule follow-up | 201 + `Location` | 400, 401 |
| GET | `/api/followups/{id}` | Get follow-up | 200 | 401, 404 |
| GET | `/api/reports/pipeline` | Stage-wise and owner-wise pipeline | 200 | 401, 403 (Sales Executive) |

**Paging:** list endpoints return `{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 42, "totalPages": 3 }`. `pageSize` is limited to 5–100.

## Status codes

| Code | Meaning |
|---|---|
| 200 / 201 / 204 | OK / created (with a `Location` header pointing to the new resource) / done, no body |
| 400 | Validation failed; see `errors` |
| 401 | Not signed in |
| 403 | Signed in, but your role is not allowed |
| 404 | Not found, or outside your scope |
| 405 | Wrong HTTP method for this endpoint |
| 409 | Conflicts with an existing record (duplicate email/phone; customer still has related records) |
| 415 | Body is not JSON |
| 429 | Too many login attempts; wait a minute |
| 500 | Unexpected error; details are logged on the server, never returned |

## Error format

Every error uses the same [Problem Details](https://www.rfc-editor.org/rfc/rfc9457) shape. Validation errors add an `errors` object keyed by field:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Amount": ["Opportunity Amount must be greater than 0."],
    "ExpectedCloseDate": ["Expected Close Date cannot be in the past."]
  }
}
```

```json
{ "title": "You do not have permission to perform this action.", "status": 403, "instance": "/api/reports/pipeline" }
```

## Business rules enforced by the API

Each rule returns 400 with the message shown:

- **Customer:**
  - Name is required (max 100 characters). → "Customer Name is required."
  - Email is required and must be valid. → "Enter a valid email address."
  - Phone must be a 10-digit mobile number starting with 6–9. → "Enter a valid phone number."
  - Email and phone must be unique. → **409**
- **Lead:**
  - Name is required.
  - Status must be a valid value.
  - A new lead can't start as Converted or Lost.
  - Expected value must be between 0 and 1,000,000,000, with at most 2 decimals.
- **Opportunity:**
  - Amount ≥ 0, and > 0 while open. → "Opportunity Amount must be greater than 0."
  - Amount can have at most 2 decimal places.
  - Probability between 0 and 100. → "Probability must be between 0 and 100."
  - The expected close date can't be in the past while the opportunity is open. → "Expected Close Date cannot be in the past."
  - A Lost opportunity requires `outcomeNotes`.
- **Follow-up:**
  - The date can't be earlier than today. → "Follow-up date cannot be earlier than today."
  - It must be linked to a customer, lead or opportunity in your scope.

## Examples (curl)

Sign in. The cookie is saved to `cookies.txt`:

```bash
curl -c cookies.txt -H "Content-Type: application/json" \
     -d '{"login":"rahul.sales@acxiomcrm.local","password":"<password>"}' \
     http://localhost:5080/api/auth/login
```

List customers:

```bash
curl -b cookies.txt "http://localhost:5080/api/customers?q=iyer&page=1&pageSize=20"
```

Create an opportunity:

```bash
curl -b cookies.txt -H "Content-Type: application/json" \
     -d '{"opportunityName":"Annual renewal","customerId":1,"amount":250000,"probability":40,
          "expectedCloseDate":"2026-12-15","stage":"Proposal"}' \
     http://localhost:5080/api/opportunities
```

Response: `201 Created`, `Location: /api/opportunities/15`, body:

```json
{
  "opportunityId": 15, "opportunityName": "Annual renewal", "customerId": 1, "customerName": "Ananya Iyer",
  "amount": 250000, "probability": 40, "weightedAmount": 100000, "stage": "Proposal", "status": "Open",
  "expectedCloseDate": "2026-12-15T00:00:00", "assignedToName": "Rahul Verma", "closedDate": null, "outcomeNotes": null
}
```

## Security notes

- The auth cookie is HttpOnly and `SameSite=Strict` (and `Secure` in production), so other sites can't use it. That is why the JSON API doesn't need anti-forgery tokens; the MVC forms do use them.
- `POST /api/auth/login` is rate limited (10 requests per minute per IP by default, `Security:LoginRateLimitPerMinute`). Accounts lock for 15 minutes after 5 wrong passwords.
- HTTPS and HSTS are enforced in Production.
- Calls your role isn't allowed to make (403) are recorded in the audit log.

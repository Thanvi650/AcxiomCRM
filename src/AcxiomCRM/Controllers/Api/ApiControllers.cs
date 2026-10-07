using AcxiomCRM.Dtos;
using AcxiomCRM.Helpers;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers.Api;

/// <summary>
/// Base for REST controllers: JSON only, authenticated by default (fallback policy),
/// [ApiController] automatic 400 responses for invalid payloads, and anti-forgery
/// disabled (cookie is SameSite=Strict; API clients do not post HTML forms).
/// </summary>
// No [Produces]: it would force errors to "application/json" too. Normal results are JSON
// via the default formatter; errors keep the standard "application/problem+json".
[ApiController]
[IgnoreAntiforgeryToken]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Maps a service result to the right HTTP status with a consistent problem body.</summary>
    protected ActionResult FromFailure<T>(ServiceResult<T> result)
    {
        switch (result.Status)
        {
            case ServiceStatus.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Resource not found.");
            case ServiceStatus.Forbidden:
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: result.FirstError());
            case ServiceStatus.Conflict:
            case ServiceStatus.Invalid:
            default:
                foreach (var error in result.Errors) ModelState.AddModelError(error.Field, error.Message);
                var status = result.Status == ServiceStatus.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
                return ValidationProblem(statusCode: status,
                    title: status == 409 ? "The resource conflicts with an existing record." : "One or more validation errors occurred.",
                    modelStateDictionary: ModelState);
        }
    }

    protected static PagedResponse<TOut> Page<TIn, TOut>(PagedList<TIn> list, Func<TIn, TOut> map) =>
        new(list.Select(map).ToList(), list.PageIndex, list.PageSize, list.TotalCount, list.TotalPages);
}

/// <summary>Authentication (cookie-based).</summary>
[Route("api/auth")]
public class AuthApiController : ApiControllerBase
{
    private readonly AuthService _auth;

    public AuthApiController(AuthService auth)
    {
        _auth = auth;
    }

    /// <summary>Sign in with email/username and password. Sets the auth cookie. Rate limited.</summary>
    /// <response code="200">Signed in.</response>
    /// <response code="400">Missing fields.</response>
    /// <response code="401">Invalid credentials, inactive or locked-out account.</response>
    /// <response code="429">Too many attempts.</response>
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CurrentUserDto>> Login(LoginRequestDto request)
    {
        var result = await _auth.LoginAsync(request.Login, request.Password, rememberMe: false);
        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(await ToDtoAsync(result.User!)),
            LoginOutcome.LockedOut => Problem(statusCode: 401, title: "Account locked.",
                detail: "The account is temporarily locked after repeated failed sign-in attempts."),
            LoginOutcome.Inactive => Problem(statusCode: 401, title: "Account inactive."),
            _ => Problem(statusCode: 401, title: "Invalid credentials.")
        };
    }

    /// <summary>Sign out and clear the auth cookie.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await _auth.LogoutAsync();
        return NoContent();
    }

    /// <summary>The signed-in user.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    public ActionResult<CurrentUserDto> Me()
    {
        var role = Roles.All.FirstOrDefault(User.IsInRole) ?? string.Empty;
        return Ok(new CurrentUserDto(
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
            User.Identity?.Name ?? string.Empty,
            User.FindFirst(AppClaimsPrincipalFactory.FullNameClaim)?.Value ?? string.Empty,
            role));
    }

    private async Task<CurrentUserDto> ToDtoAsync(ApplicationUser user)
    {
        var users = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;
        return new CurrentUserDto(user.Id, user.UserName ?? string.Empty, user.FullName, role);
    }
}

/// <summary>Customers within the caller's scope.</summary>
[Route("api/customers")]
public class CustomersApiController : ApiControllerBase
{
    private readonly CustomerService _customers;

    public CustomersApiController(CustomerService customers)
    {
        _customers = customers;
    }

    /// <summary>List/search customers (paged).</summary>
    /// <param name="q">Search name, email, phone, company or code.</param>
    /// <param name="status">Prospect | Active | Inactive</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">5–100, default 20.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<CustomerDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<CustomerDto>>> GetAll(string? q, string? status, int page = 1, int pageSize = 20)
    {
        var query = CustomerService.Sort(CustomerService.Search(await _customers.QueryAsync(), q, status, null), "name");
        return Ok(Page(await PagedList<Customer>.CreateAsync(query, page, pageSize), CustomerDto.From));
    }

    /// <summary>Get one customer.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(CustomerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerDto>> Get(int id)
    {
        var customer = await _customers.GetAsync(id);
        return customer is null ? Problem(statusCode: 404, title: "Resource not found.") : Ok(CustomerDto.From(customer));
    }

    /// <summary>Create a customer. Email and phone must be unique.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CustomerDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Create(CustomerInputDto input)
    {
        var result = await _customers.CreateAsync(input);
        if (!result.Succeeded) return FromFailure(result);
        var created = await _customers.GetAsync(result.Value!.CustomerId);
        return CreatedAtAction(nameof(Get), new { id = result.Value.CustomerId }, CustomerDto.From(created!));
    }

    /// <summary>Update a customer.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(CustomerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Update(int id, CustomerInputDto input)
    {
        var result = await _customers.UpdateAsync(id, input);
        if (!result.Succeeded) return FromFailure(result);
        var updated = await _customers.GetAsync(id);
        return Ok(CustomerDto.From(updated!));
    }

    /// <summary>Delete a customer (409 if it still has related records — deactivate it instead).</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _customers.DeleteAsync(id);
        return result.Succeeded ? NoContent() : FromFailure(result);
    }
}

/// <summary>Leads within the caller's scope.</summary>
[Route("api/leads")]
public class LeadsApiController : ApiControllerBase
{
    private readonly LeadService _leads;

    public LeadsApiController(LeadService leads)
    {
        _leads = leads;
    }

    /// <summary>List/search leads (paged).</summary>
    /// <param name="q">Search name, company, email, phone or code.</param>
    /// <param name="status">New | Contacted | Qualified | Unqualified | Converted | Lost | open</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">5–100, default 20.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<LeadDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<LeadDto>>> GetAll(string? q, string? status, int page = 1, int pageSize = 20)
    {
        var query = LeadService.Sort(LeadService.Search(await _leads.QueryAsync(), q, status, null), null);
        return Ok(Page(await PagedList<Lead>.CreateAsync(query, page, pageSize), LeadDto.From));
    }

    /// <summary>Get one lead.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(LeadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeadDto>> Get(int id)
    {
        var lead = await _leads.GetAsync(id);
        return lead is null ? Problem(statusCode: 404, title: "Resource not found.") : Ok(LeadDto.From(lead));
    }

    /// <summary>Create a lead. Status must be a valid initial status.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(LeadDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LeadDto>> Create(LeadInputDto input)
    {
        var result = await _leads.CreateAsync(input);
        if (!result.Succeeded) return FromFailure(result);
        var created = await _leads.GetAsync(result.Value!.LeadId);
        return CreatedAtAction(nameof(Get), new { id = result.Value.LeadId }, LeadDto.From(created!));
    }
}

/// <summary>Opportunities within the caller's scope.</summary>
[Route("api/opportunities")]
public class OpportunitiesApiController : ApiControllerBase
{
    private readonly OpportunityService _opportunities;

    public OpportunitiesApiController(OpportunityService opportunities)
    {
        _opportunities = opportunities;
    }

    /// <summary>List/search opportunities (paged).</summary>
    /// <param name="q">Search opportunity or customer name.</param>
    /// <param name="stage">Qualification | Proposal | Negotiation | Won | Lost</param>
    /// <param name="status">Open | Won | Lost</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">5–100, default 20.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<OpportunityDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<OpportunityDto>>> GetAll(string? q, string? stage, string? status, int page = 1, int pageSize = 20)
    {
        var query = OpportunityService.Sort(OpportunityService.Search(await _opportunities.QueryAsync(), q, stage, status, null), null);
        return Ok(Page(await PagedList<Opportunity>.CreateAsync(query, page, pageSize), OpportunityDto.From));
    }

    /// <summary>Get one opportunity.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OpportunityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpportunityDto>> Get(int id)
    {
        var opportunity = await _opportunities.GetAsync(id);
        return opportunity is null ? Problem(statusCode: 404, title: "Resource not found.") : Ok(OpportunityDto.From(opportunity));
    }

    /// <summary>
    /// Create an opportunity. Amount must be &gt; 0 and the expected close date today or later
    /// for active stages; probability must be 0–100.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OpportunityDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OpportunityDto>> Create(OpportunityInputDto input)
    {
        var result = await _opportunities.CreateAsync(input);
        if (!result.Succeeded) return FromFailure(result);
        var created = await _opportunities.GetAsync(result.Value!.OpportunityId);
        return CreatedAtAction(nameof(Get), new { id = result.Value.OpportunityId }, OpportunityDto.From(created!));
    }
}

/// <summary>Follow-ups within the caller's scope.</summary>
[Route("api/followups")]
public class FollowUpsApiController : ApiControllerBase
{
    private readonly FollowUpService _followUps;

    public FollowUpsApiController(FollowUpService followUps)
    {
        _followUps = followUps;
    }

    /// <summary>List follow-ups (paged).</summary>
    /// <param name="view">all | pending | overdue | today | upcoming</param>
    /// <param name="status">Planned | Completed | Missed | Cancelled</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">5–100, default 20.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<FollowUpDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<FollowUpDto>>> GetAll(string? view, string? status, int page = 1, int pageSize = 20)
    {
        var query = FollowUpService.Sort(FollowUpService.Search(await _followUps.QueryAsync(), null, status, null, null, null, view), null);
        return Ok(Page(await PagedList<FollowUp>.CreateAsync(query, page, pageSize), FollowUpDto.From));
    }

    /// <summary>Get one follow-up.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(FollowUpDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FollowUpDto>> Get(int id)
    {
        var followUp = await _followUps.GetAsync(id);
        return followUp is null ? Problem(statusCode: 404, title: "Resource not found.") : Ok(FollowUpDto.From(followUp));
    }

    /// <summary>Schedule a follow-up. The date cannot be earlier than today.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(FollowUpDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FollowUpDto>> Create(FollowUpInputDto input)
    {
        var result = await _followUps.CreateAsync(input);
        if (!result.Succeeded) return FromFailure(result);
        var created = await _followUps.GetAsync(result.Value!.FollowUpId);
        return CreatedAtAction(nameof(Get), new { id = result.Value.FollowUpId }, FollowUpDto.From(created!));
    }
}

/// <summary>Reporting data.</summary>
[Route("api/reports")]
public class ReportsApiController : ApiControllerBase
{
    private readonly ReportService _reports;

    public ReportsApiController(ReportService reports)
    {
        _reports = reports;
    }

    /// <summary>Stage-wise and owner-wise pipeline (Admin: all, Manager: team). Sales Executives get 403.</summary>
    [Authorize(Roles = Roles.AdminOrManager)]
    [HttpGet("pipeline")]
    [ProducesResponseType(typeof(PipelineReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PipelineReportDto>> Pipeline() => Ok(await _reports.PipelineAsync());
}

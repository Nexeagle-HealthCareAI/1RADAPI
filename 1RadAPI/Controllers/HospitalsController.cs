using _1Rad.Application.Features.Hospitals.Commands.CreateChain;
using _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;
using _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;
using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;
using _1Rad.Application.Features.Hospitals.Queries.GetAllHospitalsForAdmin;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class HospitalsController : ControllerBase
{
    private const string AdminRoles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}";
    // Accountant toggles auto-billing from the Billing page (useExpenseActions).
    private const string HospitalEditRoles = $"{AdminRoles},{RoleConstants.Accountant}";

    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;
    private readonly IUserContext _userContext;
    private readonly IApplicationDbContext _db;

    public HospitalsController(IMediator mediator, IConfiguration configuration, IUserContext userContext, IApplicationDbContext db)
    {
        _mediator = mediator;
        _configuration = configuration;
        _userContext = userContext;
        _db = db;
    }

    // Gap analysis P1-6: GET/PUT took any hospital id (cross-tenant read/write).
    // A caller may only touch centres they are mapped to. Checked against the DB rather
    // than the token's "hubs" claim so a centre created a moment ago (POST chain) is
    // editable before the access token is re-minted.
    private async Task<bool> CanAccessHospitalAsync(Guid id)
    {
        if (id == Guid.Empty || _userContext.UserId == Guid.Empty) return false;
        if (_userContext.AuthorizedHospitalIds.Contains(id)) return true;
        return await _db.UserHospitalMappings
            .AnyAsync(m => m.UserId == _userContext.UserId && m.HospitalId == id);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetHospital(Guid id)
    {
        if (!await CanAccessHospitalAsync(id)) return Forbid();
        var result = await _mediator.Send(new GetHospitalDetailsQuery(id));
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = HospitalEditRoles)]
    public async Task<IActionResult> UpdateHospital(Guid id, [FromBody] UpdateHospitalDetailsRequest request)
    {
        if (!await CanAccessHospitalAsync(id)) return Forbid();
        var result = await _mediator.Send(new UpdateHospitalDetailsCommand(
            id,
            request.HospitalName,
            request.HospitalAddress,
            request.GSTIN,
            request.RegistrationNumber,
            request.PAN,
            request.NABHNumber,
            request.IsAutoBillingEnabled,
            request.Latitude,
            request.Longitude));

        if (!result.Success) return BadRequest(new { message = result.Error });
        return Ok(new { message = "Hospital metadata updated successfully." });
    }

    // Review B9: the Billing page's auto-billing switch used to PUT the whole hospital
    // record, and when its GET failed it sent blank GSTIN/PAN/address/NABH — wiping the
    // centre's tax details. This endpoint changes only the flag.
    [HttpPut("{id}/auto-billing")]
    [Authorize(Roles = HospitalEditRoles)]
    public async Task<IActionResult> SetAutoBilling(Guid id, [FromBody] SetAutoBillingRequest request)
    {
        if (!await CanAccessHospitalAsync(id)) return Forbid();
        var hospital = await _db.Hospitals.FirstOrDefaultAsync(h => h.HospitalId == id);
        if (hospital == null) return NotFound();
        hospital.IsAutoBillingEnabled = request.Enabled;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { isAutoBillingEnabled = hospital.IsAutoBillingEnabled });
    }

    [HttpPost("chain")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> CreateChain([FromBody] CreateChainRequest request)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? Guid.Empty.ToString());
        if (userId == Guid.Empty) return Unauthorized();

        var result = await _mediator.Send(new CreateChainCommand(
            userId,
            request.ChainName,
            request.HospitalName,
            request.HospitalAddress,
            request.GSTIN,
            request.RegistrationNumber,
            request.PAN,
            request.NABHNumber,
            request.Modules));

        if (!result.Success) return BadRequest(new { success = false, message = result.Error, errorCode = result.ErrorCode });
        return Ok(result);
    }

    [HttpGet("group")]
    public async Task<IActionResult> GetGroupHospitals()
    {
        var result = await _mediator.Send(new GetGroupHospitalsQuery());
        return Ok(result);
    }

    // [SERVICE-TO-SERVICE] Every registered diagnostic center + active staff roster, platform-wide,
    // for CMS's admin console. Protected by a shared key header instead of a user JWT -- same
    // convention as CMSAPI's EasyHmsSubscriptionPlansController/SymptomRouterController "/service"
    // endpoints. Empty/missing configured key means this stays disabled (401 on every call).
    [HttpGet("admin/all")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAllForAdmin([FromHeader(Name = "X-Service-Key")] string? serviceKey)
    {
        var expectedKey = _configuration["ServiceAuth:CmsServiceKey"];
        if (string.IsNullOrEmpty(expectedKey) || serviceKey != expectedKey)
            return Unauthorized();

        var result = await _mediator.Send(new GetAllHospitalsForAdminQuery());
        return Ok(new { success = true, data = result });
    }
}

public record SetAutoBillingRequest(bool Enabled);

public class UpdateHospitalDetailsRequest
{
    public string HospitalName { get; set; } = string.Empty;
    public string HospitalAddress { get; set; } = string.Empty;
    public string? GSTIN { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? PAN { get; set; }
    public string? NABHNumber { get; set; }
    public bool IsAutoBillingEnabled { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}

public record CreateChainRequest(
    string ChainName,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN = null,
    string? RegistrationNumber = null,
    string? PAN = null,
    string? NABHNumber = null,
    // Chosen product package (SKU) for the new centre: "RIS" | "PACS" | "RIS,PACS".
    string? Modules = null);

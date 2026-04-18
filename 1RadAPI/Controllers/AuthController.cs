using _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;
using _1Rad.Application.Features.Auth.Commands.IdentitySetup;
using _1Rad.Application.Features.Auth.Commands.SendOTP;
using _1Rad.Application.Features.Auth.Commands.VerifyOTP;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace _1RadAPI.Controllers;

/// <summary>
/// Handles all Authentication and Registration flows for the 1Rad Clinical Hub.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuthController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Stage 1a: Dispatches a 6-digit OTP to the user's mobile number via WhatsApp.
    /// </summary>
    /// <response code="200">OTP dispatched successfully.</response>
    /// <response code="400">Invalid mobile number format.</response>
    /// <response code="429">Too many requests. Please try again later.</response>
    /// <response code="500">Internal server error during dispatch.</response>
    [EnableRateLimiting("OtpRateLimit")]
    [HttpPost("otp/send")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SendOTP([FromBody] SendOTPCommand command)
    {
        var result = await _mediator.Send(command);
        return result 
            ? Ok(new { message = "OTP sent successfully." }) 
            : StatusCode(StatusCodes.Status500InternalServerError, new { message = "Infrastructure failure while sending OTP." });
    }

    /// <summary>
    /// Stage 1b: Verifies the OTP and issues a short-lived Initiation Token.
    /// </summary>
    /// <response code="200">OTP verified. Returns an Initiation Token.</response>
    /// <response code="401">Invalid or expired OTP.</response>
    /// <response code="429">Too many verification attempts.</response>
    [EnableRateLimiting("OtpRateLimit")]
    [HttpPost("otp/verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> VerifyOTP([FromBody] VerifyOTPCommand command)
    {
        var token = await _mediator.Send(command);
        return token != null 
            ? Ok(new { token, message = "OTP verified. Proceed to identity setup." }) 
            : Unauthorized(new { message = "The code provided is invalid or has expired." });
    }

    /// <summary>
    /// Stage 2: Collects personnel details and secures the user account.
    /// </summary>
    /// <remarks>Requires a valid Initiation Token in the Authorization header.</remarks>
    /// <response code="200">Personnel identity established.</response>
    /// <response code="400">Duplicate email/mobile or validation error.</response>
    /// <response code="401">Missing or invalid Initiation Token.</response>
    [Authorize(Policy = "InitiationOnly")]
    [HttpPost("identity-setup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> IdentitySetup([FromBody] IdentitySetupCommand command)
    {
        var result = await _mediator.Send(command);
        
        if (result.Error != null) 
            return BadRequest(new { error = result.Error });
            
        return Ok(new { userId = result.UserId, token = result.Token, message = "Identity created. Proceed to infrastructure deployment." });
    }

    /// <summary>
    /// Stage 3: Deploys center infrastructure and activates the user account.
    /// </summary>
    /// <remarks>Requires a valid Initiation Token in the Authorization header.</remarks>
    /// <response code="200">Facility deployed and account activated.</response>
    /// <response code="400">Deployment data error or persistence failure.</response>
    /// <response code="401">Missing or invalid Initiation Token.</response>
    [Authorize(Policy = "InitiationOnly")]
    [HttpPost("deploy-infrastructure")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeployInfrastructure([FromBody] DeployInfrastructureCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success 
            ? Ok(new { message = "Clinical Hub infrastructure deployed successfully. Welcome aboard!" }) 
            : BadRequest(new { error = result.Error });
    }
}

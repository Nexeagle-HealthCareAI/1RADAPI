using _1Rad.Application.Features.Auth.Commands.SendOTP;
using _1Rad.Application.Features.Auth.Commands.VerifyOTP;
using _1Rad.Application.Features.Auth.Commands.IdentitySetup;
using _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;
using _1Rad.Application.Features.Auth.Commands.Login;
using _1Rad.Application.Features.Auth.Commands.TokenRefresh;
using _1Rad.Application.Features.Auth.Commands.SwitchContext;
using _1Rad.Application.Features.Auth.Commands.ForgotPassword;
using _1Rad.Application.Features.Auth.Commands.VerifyResetCode;
using _1Rad.Application.Features.Auth.Commands.ResetPassword;
using _1Rad.Application.Features.Auth.Queries.GetAuthorizedHospitals;
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

        return result.Success 
            ? Ok(new { 
                success = true,
                isAlreadyRegistered = result.IsAlreadyRegistered,
                message = result.IsAlreadyRegistered ? "Mobile registered. Proceeding with authentication." : "OTP sent successfully." 
              }) 
            : StatusCode(StatusCodes.Status500InternalServerError, new { message = result.Message ?? "Infrastructure failure while sending OTP." });
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
        var result = await _mediator.Send(command);
        return result.Success 
            ? Ok(result) 
            : Unauthorized(result);
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
            return BadRequest(result);
            
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
            : BadRequest(result);
    }

    /// <summary>
    /// Tactical Login: Authenticates user via Email or Mobile.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginCommand command)
    {
        // Server-side enrichment of the device context. We trust the client
        // for DeviceCategory + DeviceName (it knows itself best — touch /
        // screen size / installed-PWA signals are client-side) but the IP
        // and User-Agent MUST come from the request frame: anything
        // client-supplied here can be spoofed.
        var realIp =
            Request.Headers.TryGetValue("X-Forwarded-For", out var fwd) && !string.IsNullOrWhiteSpace(fwd)
                ? fwd.ToString().Split(',')[0].Trim()
                : HttpContext.Connection.RemoteIpAddress?.ToString();
        var realUa = Request.Headers.TryGetValue("User-Agent", out var ua) ? ua.ToString() : null;

        var enriched = command with { IpAddress = realIp, UserAgent = realUa };

        var result = await _mediator.Send(enriched);
        return result.Success ? Ok(result) : Unauthorized(result);
    }

    /// <summary>
    /// Refresh Token: Rotates the session for clinical continuity.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : Unauthorized(result);
    }

    /// <summary>
    /// Switch Context: Toggles active hospital context without full re-login.
    /// </summary>
    [Authorize]
    [HttpPost("switch-context")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SwitchContext([FromBody] SwitchContextCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Hub Synchronization: Fetches all authorized hospital nodes for the current user.
    /// </summary>
    [Authorize]
    [HttpGet("hubs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAuthorizedHospitals()
    {
        var userIdString = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value 
                           ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
                           
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId))
            return Unauthorized();

        var result = await _mediator.Send(new GetAuthorizedHospitalsQuery(userId));
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Recovery 1: Initiates password reset flow by sending an OTP.
    /// </summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordCommand command)
    {
        var (success, message) = await _mediator.Send(command);
        return Ok(new { message });
    }

    /// <summary>
    /// Recovery 2: Verifies reset OTP and issues a persistent Reset Token.
    /// </summary>
    [HttpPost("verify-reset-code")]
    public async Task<IActionResult> VerifyResetCode([FromBody] VerifyResetCodeCommand command)
    {
        var (success, resetToken, error) = await _mediator.Send(command);
        return success 
            ? Ok(new { success = true, resetToken }) 
            : BadRequest(new { error });
    }

    /// <summary>
    /// Recovery 3: Finalizes password update using the Reset Token.
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordCommand command)
    {
        var (success, error) = await _mediator.Send(command);
        return success 
            ? Ok(new { message = "Password reset successfully. You can now login with your new credentials." }) 
            : BadRequest(new { error });
    }
}

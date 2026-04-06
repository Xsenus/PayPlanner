using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ExternalAuthService _externalAuthService;

    public ProfileController(AuthService authService, ExternalAuthService externalAuthService)
    {
        _authService = authService;
        _externalAuthService = externalAuthService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await _authService.GetUserByIdAsync(userId.Value);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPut]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var user = await _authService.UpdateProfileAsync(userId.Value, request, cancellationToken);
            return user is null ? NotFound() : Ok(user);
        }
        catch (ArgumentException ex)
        {
            return Problem(title: "ProfileValidationFailed", detail: ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: ex.Message, detail: ex.Message, statusCode: 409);
        }
    }

    [HttpDelete("external/{provider}")]
    public async Task<IActionResult> DetachExternalIdentity([FromRoute] string provider, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var detached = await _externalAuthService.DetachExternalIdentityAsync(userId.Value, provider, cancellationToken);
            if (!detached)
            {
                return NotFound();
            }

            var user = await _authService.GetUserByIdAsync(userId.Value);
            return Ok(new
            {
                detached = true,
                user
            });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "ExternalIdentityDetachFailed", detail: ex.Message, statusCode: 400);
        }
    }
}

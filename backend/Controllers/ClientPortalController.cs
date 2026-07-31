using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("client-portal")]
[Route("api/portal")]
public sealed class ClientPortalController(IClientPortalService service, IWebHostEnvironment environment) : ControllerBase
{
    private const string CookieName = "pp_client_portal";

    [HttpPost("auth/{challengeId:guid}/preview")]
    public async Task<ActionResult<ClientPortalLoginPreviewDto>> Preview(
        Guid challengeId,
        [FromBody] ClientPortalTokenRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            return Ok(await service.PreviewLoginAsync(challengeId, request?.Token ?? string.Empty, ct));
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpPost("auth/{challengeId:guid}/verify")]
    public async Task<ActionResult<ClientPortalLoginResultDto>> Verify(
        Guid challengeId,
        [FromBody] ClientPortalVerifyRequest? request,
        CancellationToken ct)
    {
        ApplySecurityHeaders();
        if (request is null)
        {
            return BadRequest(new { code = "validation_error", message = "Введите код из письма." });
        }

        try
        {
            var result = await service.VerifyLoginAsync(
                challengeId,
                request.Token ?? string.Empty,
                request.Otp ?? string.Empty,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                ct);
            Response.Cookies.Append(CookieName, result.SessionToken, BuildCookieOptions(result.ExpiresAt));
            return Ok(new ClientPortalLoginResultDto { ExpiresAt = result.ExpiresAt });
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpGet("home")]
    public async Task<ActionResult<ClientPortalHomeDto>> Home(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ApplySecurityHeaders();
        try
        {
            var session = await ResolveSession(ct);
            return session is null
                ? Unauthorized(new { code = "session_unavailable", message = "Войдите в кабинет ещё раз." })
                : Ok(await service.GetHomeAsync(session, page, pageSize, ct));
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpGet("cases/{casePublicId:guid}")]
    public async Task<ActionResult<ClientPortalCaseDto>> GetCase(Guid casePublicId, CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            var session = await ResolveSession(ct);
            if (session is null)
            {
                return Unauthorized(new { code = "session_unavailable", message = "Войдите в кабинет ещё раз." });
            }
            var result = await service.GetCaseAsync(session, casePublicId, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpGet("documents/{documentPublicId:guid}")]
    public async Task<IActionResult> DownloadDocument(Guid documentPublicId, CancellationToken ct)
    {
        ApplySecurityHeaders();
        try
        {
            var session = await ResolveSession(ct);
            if (session is null)
            {
                return Unauthorized(new { code = "session_unavailable", message = "Войдите в кабинет ещё раз." });
            }
            var document = await service.GetDocumentAsync(session, documentPublicId, ct);
            return document is null
                ? NotFound()
                : PhysicalFile(document.FullPath, document.ContentType, document.FileName);
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromQuery] bool all = false, CancellationToken ct = default)
    {
        ApplySecurityHeaders();
        try
        {
            await service.RevokeSessionAsync(Request.Cookies[CookieName], all, ct);
        }
        catch (ClientPortalException ex)
        {
            return MapPublicError(ex);
        }
        Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps || !environment.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
        return NoContent();
    }

    private Task<ClientPortalSessionContext?> ResolveSession(CancellationToken ct)
        => service.ResolveSessionAsync(
            Request.Cookies[CookieName],
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);

    private CookieOptions BuildCookieOptions(DateTime expiresAt)
        => new()
        {
            HttpOnly = true,
            Secure = Request.IsHttps || !environment.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
            IsEssential = true,
        };

    private void ApplySecurityHeaders()
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    }

    private ActionResult MapPublicError(ClientPortalException ex)
        => ex.Code switch
        {
            "validation_error" or "invalid_confirmation" => BadRequest(new { ex.Code, message = ex.Message }),
            "feature_disabled" or "link_unavailable" or "account_unavailable" =>
                NotFound(new { code = "link_unavailable", message = "Ссылка недействительна или больше недоступна." }),
            _ => Conflict(new { ex.Code, message = ex.Message }),
        };
}

public sealed class ClientPortalTokenRequest
{
    public string? Token { get; set; }
}

public sealed class ClientPortalVerifyRequest
{
    public string? Token { get; set; }
    public string? Otp { get; set; }
}

public sealed class ClientPortalLoginResultDto
{
    public DateTime ExpiresAt { get; set; }
}

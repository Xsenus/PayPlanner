using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using System.Security.Claims;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _svc;

    public AuthController(AuthService svc) => _svc = svc;

    /// <summary>
    /// Проверяет пароль и выдаёт JWT только активному одобренному пользователю.
    /// Частота публичных попыток ограничена по IP-адресу.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.LoginPolicy)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        try
        {
            var res = await _svc.LoginAsync(req.Email, req.Password, ct);

            // Неверные логин/пароль
            if (res is null)
            {
                return Problem(
                    title: "InvalidCredentials",
                    detail: "Неверный email или пароль.",
                    statusCode: 401
                );
            }

            return Ok(res);
        }
        // Аккаунт ещё не одобрен
        catch (InvalidOperationException ex) when (ex.Message == "PendingApproval")
        {
            return Problem(
                title: "PendingApproval",
                detail: "Ваш аккаунт ожидает одобрения администратором.",
                statusCode: 403
            );
        }
        // Аккаунт отключён
        catch (InvalidOperationException ex) when (ex.Message == "UserInactive")
        {
            return Problem(
                title: "UserInactive",
                detail: "Ваш аккаунт отключён. Обратитесь к администратору.",
                statusCode: 403
            );
        }
    }

    [HttpPost("refresh")]
    [Authorize]
    public async Task<IActionResult> RefreshSession(CancellationToken ct)
    {
        var id = GetUserId(User);
        if (id is null || !AuthSessionClaims.TryGetTokenVersion(User, out var tokenVersion))
        {
            return Unauthorized();
        }

        try
        {
            var response = await _svc.RefreshSessionAsync(id.Value, tokenVersion, ct);
            return response is null ? Unauthorized() : Ok(response);
        }
        catch (InvalidOperationException ex) when (ex.Message == "PendingApproval")
        {
            return Problem(
                title: "PendingApproval",
                detail: "Ваш аккаунт ожидает одобрения администратором.",
                statusCode: 403
            );
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserInactive")
        {
            return Problem(
                title: "UserInactive",
                detail: "Ваш аккаунт отключён. Обратитесь к администратору.",
                statusCode: 403
            );
        }
    }

    /// <summary>
    /// Завершает все текущие сессии пользователя. Сервер атомарно меняет версию
    /// JWT, поэтому сохранённые на других устройствах токены сразу перестают работать.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var id = GetUserId(User);
        if (id is null || !AuthSessionClaims.TryGetTokenVersion(User, out var tokenVersion))
        {
            return Unauthorized();
        }

        var revoked = await _svc.RevokeSessionsAsync(id.Value, tokenVersion, ct);
        return revoked ? NoContent() : Unauthorized();
    }

    /// <summary>
    /// Создаёт не одобренную учётную запись, если самостоятельная регистрация разрешена.
    /// Частота публичных регистраций ограничена по IP-адресу.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.RegisterPolicy)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        try
        {
            var dto = await _svc.RegisterAsync(req, ct);
            if (dto is null)
            {
                return Problem(
                    title: "UserAlreadyExists",
                    detail: "Пользователь с таким email уже существует.",
                    statusCode: 409
                );
            }

            return Created($"/api/auth/users/{dto.Id}", dto);
        }
        catch (InvalidOperationException ex) when (ex.Message == "ProtectedUserEmailReserved")
        {
            return Problem(
                title: "ProtectedUserEmailReserved",
                detail: "Этот системный email зарезервирован и не может быть использован для регистрации.",
                statusCode: 409
            );
        }
        catch (InvalidOperationException ex) when (ex.Message == "RegistrationDisabled")
        {
            return Problem(
                title: "RegistrationDisabled",
                detail: "Самостоятельная регистрация сейчас отключена администратором.",
                statusCode: 403
            );
        }
        catch (InvalidOperationException)
        {
            return Problem(
                title: "RegistrationError",
                detail: "Не удалось завершить регистрацию. Обратитесь к администратору.",
                statusCode: 400
            );
        }
    }


    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var id = GetUserId(User);
        if (id is null) return Unauthorized();

        var user = await _svc.GetUserByIdAsync(id.Value, ct);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>Извлекает числовой идентификатор пользователя из проверенного JWT.</summary>
    private static int? GetUserId(ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;
}


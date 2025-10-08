using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        try
        {
            var res = await _svc.LoginAsync(req.Email, req.Password);

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

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        try
        {
            var dto = await _svc.RegisterAsync(req);
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
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: "RegistrationError",
                detail: ex.Message,
                statusCode: 400
            );
        }
    }


    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var id = GetUserId(User);
        if (id is null) return Unauthorized();

        var user = await _svc.GetUserByIdAsync(id.Value);
        return user is null ? NotFound() : Ok(user);
    }

    private static int? GetUserId(ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;
}

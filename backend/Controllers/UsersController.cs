using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Policy = "Admin")]
public class UsersController : ControllerBase, IAsyncActionFilter
{
    private readonly PaymentContext _db;
    private readonly AuthService _svc;

    public UsersController(AuthService svc, PaymentContext db)
    {
        _svc = svc;
        _db = db;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> ApproveUser(int id)
    {
        var s = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(s, out var approverId))
        {
            return Unauthorized();
        }

        var ok = await _svc.ApproveUserAsync(id, approverId);
        return ok ? Ok(new { approved = true }) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        try
        {
            var dto = await _svc.CreateUserAsync(req);
            return dto is null
                ? Conflict(new { message = "Пользователь с таким email уже существует." })
                : Created($"/api/users/{dto.Id}", dto);
        }
        catch (InvalidOperationException ex) when (ex.Message == "RoleNotFound")
        {
            return BadRequest(new { message = "Выбранная роль не найдена." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotAllowed")
        {
            return BadRequest(new { message = "Сотрудника можно привязать только к основным компаниям из настроек." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotFound")
        {
            return BadRequest(new { message = "Одна из выбранных компаний не найдена." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return await _svc.DeleteUserAsync(id, actorUserId.Value) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserInUseAsResponsible")
        {
            return Conflict(new { message = "Нельзя удалить пользователя: он назначен ответственным в документах." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "Системного администратора нельзя удалить." });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("ExternalCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("GoogleCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("YandexCalendarCleanupFailed", StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = "Не удалось удалить календарь PayPlanner из внешнего календаря. Пользователь не удален, повторите операцию после проверки подключений календарей.",
                detail = ex.Message
            });
        }
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await _svc.GetAllRolesAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetUser(int id)
        => (await _svc.GetUserByIdAsync(id)) is { } u ? Ok(u) : NotFound();

    [HttpGet("{id:int}/contractor-balance")]
    public async Task<ActionResult<ContractorBalanceDto>> GetUserContractorBalance(int id, CancellationToken cancellationToken)
    {
        var userExists = await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == id, cancellationToken);

        if (!userExists)
        {
            return NotFound();
        }

        var tasks = await _db.TaskJournalItems
            .AsNoTracking()
            .Include(task => task.ContractorPayment)
            .Include(task => task.Client)
            .Include(task => task.LegalEntity)
            .Include(task => task.ClientCase)
            .Where(task => task.ContractorPaymentEnabled)
            .Where(task => task.AssigneeUserId == id)
            .OrderByDescending(task => task.ContractorPaymentDueDate ?? task.UpdatedAt ?? task.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var items = tasks
            .Select(MapContractorBalanceItem)
            .Where(item => item is not null)
            .Cast<ContractorBalanceItemDto>()
            .ToList();

        return Ok(new ContractorBalanceDto
        {
            OwedToMe = items.Sum(item => item.OutstandingAmount),
            OwedByMe = 0m,
            PaidToMe = items.Sum(item => item.PaidAmount),
            PaidByMe = 0m,
            Queue = items
                .Where(item => item.OutstandingAmount > 0m && item.PaymentStatus != PaymentStatus.Cancelled.ToString())
                .OrderBy(item => item.PaymentDueDate ?? string.Empty)
                .ToList(),
            Paid = items
                .Where(item => item.PaidAmount > 0m || item.PaymentStatus == PaymentStatus.Completed.ToString())
                .OrderByDescending(item => item.PaidDate ?? item.PaymentDueDate ?? string.Empty)
                .ToList(),
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] string? status)
        => Ok(await _svc.GetAllUsersAsync(status));

    [HttpGet("paged")]
    public async Task<IActionResult> GetUsersPaged(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
        => Ok(await _svc.GetUsersPagedAsync(status, page, pageSize));

    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> RejectUser(int id, [FromBody] RejectUserRequest? request)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return await _svc.RejectUserAsync(id, actorUserId.Value, request?.Reason)
                ? Ok(new { rejected = true })
                : NotFound();
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserCannotBeDeleted")
        {
            return Conflict(new { message = "Системного администратора нельзя отклонить или удалить." });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("ExternalCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("GoogleCalendarCleanupFailed", StringComparison.Ordinal)
            || ex.Message.StartsWith("YandexCalendarCleanupFailed", StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = "Не удалось удалить календарь PayPlanner из внешнего календаря. Пользователь не отклонен, повторите операцию после проверки подключений календарей.",
                detail = ex.Message
            });
        }
    }

    [HttpPost("merge")]
    public async Task<IActionResult> MergeUsers([FromBody] MergeUsersRequest request, CancellationToken cancellationToken)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _svc.MergeUsersAsync(request, actorUserId.Value, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "UserMergeFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var actorUserId = User.GetUserId();
        if (!actorUserId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return (await _svc.UpdateUserAsync(id, req, actorUserId.Value)) is { } u ? Ok(u) : NotFound();
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "ProtectedUserManagedOnlyBySelf"
            || ex.Message == "ProtectedUserEmailChangeForbidden"
            || ex.Message == "ProtectedUserAdministrativeFieldsLocked"
            || ex.Message == "ProtectedUserEmailReserved")
        {
            return Conflict(new
            {
                message = "Изменение системного администратора ограничено: этот пользователь управляет собой только самостоятельно, а его email зарезервирован."
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "RoleNotFound")
        {
            return BadRequest(new { message = "Выбранная роль не найдена." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserAlreadyExists")
        {
            return Conflict(new { message = "Пользователь с таким email уже существует." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotAllowed")
        {
            return BadRequest(new { message = "Сотрудника можно привязать только к основным компаниям из настроек." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "UserCompanyAccessCompanyNotFound")
        {
            return BadRequest(new { message = "Одна из выбранных компаний не найдена." });
        }
    }

    private static ContractorBalanceItemDto? MapContractorBalanceItem(TaskJournalItem task)
    {
        if (!task.AssigneeUserId.HasValue || !task.ContractorPaymentAmount.HasValue)
        {
            return null;
        }

        var payment = task.ContractorPayment;
        var amount = Math.Round(task.ContractorPaymentAmount.Value, 2);
        var paidAmount = payment is null
            ? 0m
            : payment.Status == PaymentStatus.Completed && payment.PaidAmount <= 0m
                ? amount
                : Math.Min(Math.Round(payment.PaidAmount, 2), amount);
        var status = payment?.Status.ToString() ?? (task.ContractorPaymentOnResult && task.Status != "done" ? "WaitingForResult" : PaymentStatus.Pending.ToString());
        var outstandingAmount = status == PaymentStatus.Cancelled.ToString()
            ? 0m
            : Math.Max(amount - paidAmount, 0m);

        return new ContractorBalanceItemDto
        {
            TaskId = task.Id,
            TaskTitle = task.Title,
            PaymentId = payment?.Id,
            Direction = "toMe",
            AssigneeName = task.AssigneeName,
            AuthorName = task.AuthorName,
            ClientId = task.ClientId,
            ClientName = !string.IsNullOrWhiteSpace(task.Client?.Name) ? task.Client.Name : task.LegalEntity is null ? task.ObjectName : null,
            LegalEntityId = task.LegalEntityId,
            LegalEntityName = !string.IsNullOrWhiteSpace(task.LegalEntity?.ShortName) ? task.LegalEntity.ShortName : task.LegalEntity?.FullName,
            ClientCaseId = task.ClientCaseId,
            ClientCaseTitle = task.ClientCase?.Title,
            Amount = amount,
            PaidAmount = paidAmount,
            OutstandingAmount = outstandingAmount,
            PaymentStatus = status,
            TaskStatus = task.Status,
            PaymentDueDate = task.ContractorPaymentDueDate?.ToString("O"),
            PaidDate = payment?.PaidDate?.ToString("O"),
        };
    }
}

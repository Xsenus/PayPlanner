using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ExternalAuthService _externalAuthService;
    private readonly PaymentContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public ProfileController(
        AuthService authService,
        ExternalAuthService externalAuthService,
        PaymentContext db,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _authService = authService;
        _externalAuthService = externalAuthService;
        _db = db;
        _environment = environment;
        _configuration = configuration;
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

    [HttpGet("contractor-balance")]
    public async Task<ActionResult<ContractorBalanceDto>> GetContractorBalance(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var tasks = await _db.TaskJournalItems
            .AsNoTracking()
            .Include(task => task.ContractorPayment)
                .ThenInclude(payment => payment!.PaymentSource)
            .Include(task => task.Client)
            .Include(task => task.LegalEntity)
            .Include(task => task.ClientCase)
            .Where(task => task.ContractorPaymentEnabled)
            .Where(task => task.AssigneeUserId == userId.Value)
            .OrderByDescending(task => task.ContractorPaymentDueDate ?? task.UpdatedAt ?? task.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var items = tasks
            .Select(task => MapContractorBalanceItem(task, userId.Value))
            .Where(item => item is not null)
            .Cast<ContractorBalanceItemDto>()
            .ToList();

        return Ok(new ContractorBalanceDto
        {
            OwedToMe = items
                .Where(item => item.Direction == "toMe")
                .Sum(item => item.OutstandingAmount),
            OwedByMe = items
                .Where(item => item.Direction == "byMe")
                .Sum(item => item.OutstandingAmount),
            PaidToMe = items
                .Where(item => item.Direction == "toMe")
                .Sum(item => item.PaidAmount),
            PaidByMe = items
                .Where(item => item.Direction == "byMe")
                .Sum(item => item.PaidAmount),
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

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var user = await _authService.ChangePasswordAsync(
                userId.Value,
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);

            return user is null ? NotFound() : Ok(user);
        }
        catch (ArgumentException ex)
        {
            return Problem(title: "PasswordValidationFailed", detail: ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex) when (ex.Message == "CurrentPasswordRequired")
        {
            return Problem(title: ex.Message, detail: "Введите текущий пароль, чтобы подтвердить смену пароля.", statusCode: 400);
        }
        catch (InvalidOperationException ex) when (ex.Message == "InvalidCurrentPassword")
        {
            return Problem(title: ex.Message, detail: "Текущий пароль указан неверно.", statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "PasswordChangeFailed", detail: ex.Message, statusCode: 409);
        }
    }

    [HttpPost("photo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> UploadProfilePhoto(IFormFile? file, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (file is null || file.Length == 0)
        {
            return Problem(title: "ProfilePhotoMissing", detail: "Выберите файл изображения.", statusCode: 400);
        }

        var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant();
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
        if (string.IsNullOrWhiteSpace(extension) || !allowedExtensions.Contains(extension))
        {
            return Problem(title: "ProfilePhotoFormatUnsupported", detail: "Поддерживаются только JPG, PNG и WEBP.", statusCode: 400);
        }

        var contentType = file.ContentType?.Trim().ToLowerInvariant();
        var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp"
        };
        if (!string.IsNullOrWhiteSpace(contentType) && !allowedContentTypes.Contains(contentType))
        {
            return Problem(title: "ProfilePhotoFormatUnsupported", detail: "Поддерживаются только JPG, PNG и WEBP.", statusCode: 400);
        }

        var currentUser = await _authService.GetUserByIdAsync(userId.Value);
        if (currentUser is null)
        {
            return NotFound();
        }

        var photoStorageRoot = UserPhotoStoragePaths.ResolveRoot(_environment, _configuration);
        var userPhotoDirectory = Path.Combine(photoStorageRoot, userId.Value.ToString());
        Directory.CreateDirectory(userPhotoDirectory);

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}";
        var physicalPath = Path.Combine(userPhotoDirectory, fileName);

        await using (var stream = System.IO.File.Create(physicalPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var relativePath = $"/user-photos/{userId.Value}/{fileName}";
        var publicUrl = $"{GetRequestBaseUrl()}{relativePath}";

        try
        {
            var updatedUser = await _authService.UpdateProfilePhotoAsync(userId.Value, publicUrl, cancellationToken);
            if (updatedUser is null)
            {
                System.IO.File.Delete(physicalPath);
                return NotFound();
            }

            DeletePreviousUploadedPhoto(currentUser.PhotoUrl, publicUrl, photoStorageRoot);
            var legacyPhotoRoot = UserPhotoStoragePaths.ResolveLegacyRoot(_environment);
            if (!string.Equals(photoStorageRoot, legacyPhotoRoot, StringComparison.OrdinalIgnoreCase))
            {
                DeletePreviousUploadedPhoto(currentUser.PhotoUrl, publicUrl, legacyPhotoRoot);
            }
            return Ok(updatedUser);
        }
        catch
        {
            if (System.IO.File.Exists(physicalPath))
            {
                System.IO.File.Delete(physicalPath);
            }

            throw;
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

    private string GetRequestBaseUrl()
    {
        var forwardedProto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var forwardedHost = Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        var scheme = string.IsNullOrWhiteSpace(forwardedProto) ? Request.Scheme : forwardedProto;
        var host = string.IsNullOrWhiteSpace(forwardedHost) ? Request.Host.Value : forwardedHost;
        return $"{scheme}://{host}";
    }

    private static ContractorBalanceItemDto? MapContractorBalanceItem(TaskJournalItem task, int currentUserId)
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
            Direction = task.AssigneeUserId == currentUserId ? "toMe" : "byMe",
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
            PaymentDate = payment?.Date.ToString("O"),
            PaidDate = payment?.PaidDate?.ToString("O"),
            PaymentSourceName = payment?.PaymentSource?.Name,
            PaymentAccount = payment?.Account,
            PaymentAccountDate = payment?.AccountDate?.ToString("O"),
            PaymentOrderNumber = payment?.PaymentOrderNumber,
            PaymentDocumentDate = payment?.PaymentDocumentDate?.ToString("O"),
        };
    }

    private static void DeletePreviousUploadedPhoto(string? previousPhotoUrl, string currentPhotoUrl, string photoStorageRoot)
    {
        if (string.IsNullOrWhiteSpace(previousPhotoUrl)
            || string.Equals(previousPhotoUrl, currentPhotoUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var physicalPath = UserPhotoStoragePaths.ResolvePhotoPath(photoStorageRoot, previousPhotoUrl);
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            return;
        }

        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }
    }

}

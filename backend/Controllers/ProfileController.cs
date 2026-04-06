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
    private readonly IWebHostEnvironment _environment;

    public ProfileController(
        AuthService authService,
        ExternalAuthService externalAuthService,
        IWebHostEnvironment environment)
    {
        _authService = authService;
        _externalAuthService = externalAuthService;
        _environment = environment;
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

        var webRootPath = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRootPath))
        {
            webRootPath = Path.Combine(_environment.ContentRootPath, "wwwroot");
        }

        var userPhotoDirectory = Path.Combine(webRootPath, "user-photos", userId.Value.ToString());
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

            DeletePreviousUploadedPhoto(currentUser.PhotoUrl, publicUrl, webRootPath);
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

    private static void DeletePreviousUploadedPhoto(string? previousPhotoUrl, string currentPhotoUrl, string webRootPath)
    {
        if (string.IsNullOrWhiteSpace(previousPhotoUrl)
            || string.Equals(previousPhotoUrl, currentPhotoUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relativePath = ExtractRelativePhotoPath(previousPhotoUrl);
        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.StartsWith("/user-photos/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var physicalPath = Path.GetFullPath(Path.Combine(
            webRootPath,
            relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(webRootPath);
        if (!physicalPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }
    }

    private static string? ExtractRelativePhotoPath(string photoUrl)
    {
        if (Uri.TryCreate(photoUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.AbsolutePath;
        }

        return photoUrl.StartsWith('/') ? photoUrl : $"/{photoUrl}";
    }
}

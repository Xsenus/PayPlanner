using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/yandex-disk")]
public sealed class YandexDiskController : ControllerBase
{
    private readonly IYandexDiskService _service;

    public YandexDiskController(IYandexDiskService service)
    {
        _service = service;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<YandexDiskSettingsDto>> GetSettings(CancellationToken ct)
        => Ok(await _service.GetSettingsAsync(ct));

    [HttpPut("settings")]
    public async Task<ActionResult<YandexDiskSettingsDto>> UpdateSettings(
        [FromBody] UpdateYandexDiskSettingsRequest request,
        CancellationToken ct)
        => Ok(await _service.UpdateSettingsAsync(request, ct));

    [HttpGet("resources")]
    public async Task<ActionResult<YandexDiskListResponseDto>> List(
        [FromQuery] string? path,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
        => await ExecuteAsync(() => _service.ListAsync(path, limit, offset, ct));

    [HttpPost("folders")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> CreateFolder(
        [FromBody] CreateYandexDiskFolderRequest request,
        CancellationToken ct)
        => await ExecuteAsync(() => _service.CreateFolderAsync(request.Path, ct));

    [HttpPost("copy")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Copy(
        [FromBody] YandexDiskResourceOperationRequest request,
        CancellationToken ct)
        => await ExecuteAsync(() => _service.CopyAsync(request.FromPath, request.ToPath, request.Overwrite, ct));

    [HttpPost("move")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Move(
        [FromBody] YandexDiskResourceOperationRequest request,
        CancellationToken ct)
        => await ExecuteAsync(() => _service.MoveAsync(request.FromPath, request.ToPath, request.Overwrite, ct));

    [HttpDelete("resources")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Delete(
        [FromQuery] string? path,
        [FromQuery] bool permanently = false,
        CancellationToken ct = default)
        => await ExecuteAsync(() => _service.DeleteAsync(path, permanently, ct));

    [HttpPost("upload")]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? path,
        [FromForm] bool overwrite = true,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("Выберите файл для загрузки.");
        }

        await using var stream = file.OpenReadStream();
        return await ExecuteAsync(() => _service.UploadAsync(path, stream, file.ContentType, overwrite, ct));
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string? path, CancellationToken ct)
    {
        try
        {
            var file = await _service.DownloadAsync(path, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (YandexDiskOperationException ex)
        {
            return MapError(ex);
        }
    }

    [HttpGet("text")]
    public async Task<ActionResult<YandexDiskTextFileDto>> GetText([FromQuery] string? path, CancellationToken ct)
        => await ExecuteAsync(() => _service.GetTextFileAsync(path, ct));

    [HttpPut("text")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> SaveText(
        [FromBody] UpdateYandexDiskTextFileRequest request,
        CancellationToken ct)
        => await ExecuteAsync(() => _service.SaveTextFileAsync(request, ct));

    private static async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (YandexDiskOperationException ex)
        {
            return MapError(ex);
        }
    }

    private static ActionResult MapError(YandexDiskOperationException ex)
    {
        if (ex.StatusCode >= 400 && ex.StatusCode < 500)
        {
            return new BadRequestObjectResult(ex.Message);
        }

        return new ObjectResult(ex.Message)
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
    }
}

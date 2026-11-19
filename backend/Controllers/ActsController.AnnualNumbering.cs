using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

public partial class ActsController
{
    [HttpGet("next-number")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct)
    {
        if (await RequireActsPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden) return forbidden;
        var zone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);
        var target = date?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, zone).Date;
        try
        {
            var number = await new ActAnnualNumberingService(_db).PreviewNextAsync(target, ct);
            return Ok(new { year = target.Year, nextNumber = number });
        }
        catch (ActAnnualNumberExhaustedException failure)
        {
            return UnprocessableEntity(new { message = failure.Message });
        }
    }

    [HttpGet("create-attempts/{uploadKey:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ActDto>> GetCreateAttempt(Guid uploadKey, CancellationToken ct)
    {
        if (await RequireActsPermissionAsync(permission => permission.CanView, ct) is { } forbidden) return forbidden;
        var actorId = User.GetUserId();
        if (actorId is not > 0) return Unauthorized();
        var receipt = await FindActReceiptAsync(actorId.Value, uploadKey, ct);
        if (receipt is null) return NotFound();
        if (receipt.Operation == "work-order")
        {
            if (await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Cases, permission => permission.CanView, ct) is false)
                return Forbid();
            var cases = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
            if (cases is null || !await _db.ClientCases.ApplyClientCaseVisibility(cases)
                .AnyAsync(item => item.Id == receipt.CaseId && _db.ClientCaseWorkOrders.Any(order => order.Id == receipt.WorkOrderId && order.ClientCaseId == item.Id), ct))
                return NotFound();
        }
        return await GetById(receipt.ActId, ct);
    }

    private bool TryReadActUploadKey(out Guid? key)
    {
        key = null;
        var headers = HttpContext?.Request.Headers;
        if (headers is null || !headers.TryGetValue("X-Upload-Key", out var value)) return true;
        if (value.Count != 1 || !Guid.TryParse(value[0], out var parsed) || parsed == Guid.Empty) return false;
        key = parsed;
        return true;
    }

    private Task<ActCreateReceipt?> FindActReceiptAsync(int actorId, Guid uploadKey, CancellationToken ct)
        => _db.ActCreateReceipts.AsNoTracking().SingleOrDefaultAsync(receipt => receipt.ActorUserId == actorId && receipt.UploadKey == uploadKey, ct);

    private Task<bool> HasGeneratedActKeyAsync(int actorId, Guid uploadKey, CancellationToken ct)
        => _db.GeneratedFinancialDocumentAttempts.AsNoTracking().AnyAsync(attempt => attempt.ActorId == actorId && attempt.UploadKey == uploadKey, ct);

    private async Task<ActionResult<ActDto>> ReplayActCreateAsync(ActCreateReceipt receipt, string commandHash, CancellationToken ct)
    {
        if (receipt.Operation != "ordinary" || receipt.CommandSha256 != commandHash)
            return Conflict(new { message = "Эта попытка создания акта уже относится к другой команде." });
        if (await RequireActsPermissionAsync(permission => permission.CanView, ct) is { } forbidden) return forbidden;
        var result = await GetById(receipt.ActId, ct);
        if (result.Result is NotFoundResult)
        {
            var exists = await _db.Acts.AsNoTracking().AnyAsync(act => act.Id == receipt.ActId, ct);
            return exists ? NotFound() : StatusCode(410, new { message = "Акт этой попытки больше недоступен. Повторное создание не выполнено." });
        }
        if (result.Result is not OkObjectResult { Value: ActDto dto }) return result;
        Response.Headers["X-Upload-Key"] = receipt.UploadKey.ToString("D");
        return Created($"/api/acts/{receipt.ActId}", dto);
    }

    private static string ActCreateCommandHash(UpsertActRequest request)
    {
        var node = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node.Remove("number");
        node["date"] = request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node["amount"] = request.Amount.ToString("G29", CultureInfo.InvariantCulture);
        foreach (var line in node["lineItems"]?.AsArray() ?? [])
        {
            if (line is null) continue;
            line["quantity"] = line["quantity"]!.GetValue<decimal>().ToString("G29", CultureInfo.InvariantCulture);
            line["price"] = line["price"]!.GetValue<decimal>().ToString("G29", CultureInfo.InvariantCulture);
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node))).ToLowerInvariant();
    }
}

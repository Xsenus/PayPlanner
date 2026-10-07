using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers;

public sealed partial class InternalChatController
{
    private sealed class ChatSendAccessRevokedException : Exception { }
    private sealed class ChatSendNamespaceConflictException : Exception { }

    private Task<bool> HasManagedSendAsync(int threadId, int actorId, Guid key, CancellationToken ct)
        => _db.InternalChatSendAttempts.AsNoTracking().AnyAsync(item => item.ThreadId == threadId
            && item.ActorId == actorId && item.ClientRequestId == key, ct);

    private async Task<ActionResult<InternalChatMessageDto>> SendManagedMessageAsync(int threadId, int actorId,
        string text, SendInternalChatMessageRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (request.ClientRequestId is not { } key || key == Guid.Empty)
            return BadRequest(new { message = "Для отправки файлов нужен идентификатор отправки." });
        if (_chatWriter is null || _chatPolicies is null || _chatBindings is null)
            return StatusCode(503, new { message = "Отправка файлов временно недоступна." });
        var registry = new InternalChatSendAttemptRegistry(_db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return Forbid();
        var files = new List<InternalChatSendFileSnapshot>(request.Files.Count);
        // Bound and verify every uploaded stream before freezing metadata. This
        // pass uses no extra temporary file and precedes all provider writes.
        foreach (var file in request.Files)
        {
            string sha;
            try { sha = await UploadValidationHelper.CopyValidatedAndHashAsync(file, Stream.Null, MaximumFileSize, ct); }
            catch (InvalidDataException) { return BadRequest(new { message = "Не удалось проверить размер или содержимое файла." }); }
            files.Add(new(SafeFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream" : file.ContentType[..Math.Min(file.ContentType.Length, 160)].Trim(), file.Length, sha));
        }
        var snapshot = new InternalChatSendSnapshot(threadId, actorId, key, text,
            request.ReplyToMessageId is > 0 ? request.ReplyToMessageId : null,
            ParseMentionIds(text, request.MentionUserIds).Order().ToArray(), files.ToArray());
        var captured = await registry.CaptureAsync(snapshot, ct);
        if (captured.Status == "forbidden") return Forbid();
        if (captured.Status == "legacy")
        {
            var legacy = await FindExistingSendAsync(threadId, actorId, key, text, request, ct);
            if (legacy is not null) return legacy;
            return Conflict(new { message = "Не удалось подтвердить прежнюю отправку. Обновите сообщения." });
        }
        if (captured.Status == "invalid") return BadRequest(new { message = "Некорректные данные отправки файлов." });
        if (captured.Status != "recorded")
            return Conflict(new { message = "Этот идентификатор уже использован для другого сообщения или содержимого файлов." });
        if (_chatWriter.Enabled)
        {
            var written = await _chatWriter.WriteAsync(snapshot, request.Files, ct);
            if (written.Status == "forbidden") return Forbid();
            if (written.Status is "conflict" or "invalid")
                return Conflict(new { message = "Не удалось подтвердить исходное содержимое сообщения и файлов." });
            // The durable header survives admission failure; continuation can
            // distinguish missing input from recoverable reserved originals.
            await _chatWriter.ContinueAsync(threadId, actorId, key, _chatPolicies, _chatBindings, ct);
        }
        var state = await _chatWriter.ReadStatusAsync(threadId, actorId, key, _storageWorkerAvailability, ct);
        var response = await SendStateResponseAsync(threadId, actorId, key, state, true, ct);
        if (response.Result is not OkObjectResult { Value: InternalChatSendStateDto dto })
            return response.Result ?? StatusCode(503);
        if (dto.Status == "published" && dto.Message is not null)
            return CreatedAtAction(nameof(GetMessages), new { threadId }, dto.Message);
        if (dto.Status is "conflict" or "failed" or "deleted") return Conflict(new { message = dto.Description });
        return AcceptedAtAction(nameof(GetSendStatus), new { threadId, clientRequestId = key }, dto);
    }
}

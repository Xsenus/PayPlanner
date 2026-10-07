using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers;

public sealed record InternalChatSendStateDto(Guid ClientRequestId, string Status, bool AutomaticRecovery,
    DateTime? RetryAfter, InternalChatMessageDto? Message, string Description);

public sealed partial class InternalChatController
{
    [HttpGet("threads/{threadId:int}/sends/{clientRequestId:guid}")]
    public async Task<ActionResult<InternalChatSendStateDto>> GetSendStatus(int threadId, Guid clientRequestId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var actor = CurrentUserId();
        if (actor is null) return Unauthorized();
        if (_chatWriter is null) return StatusCode(503, new { message = "Просмотр отправки временно недоступен." });
        var state = await _chatWriter.ReadStatusAsync(threadId, actor.Value, clientRequestId, _storageWorkerAvailability, ct);
        return await SendStateResponseAsync(threadId, actor.Value, clientRequestId, state, false, ct);
    }

    [HttpGet("threads/{threadId:int}/sends")]
    public async Task<ActionResult<InternalChatSendDiscoveryResult>> GetPendingSends(int threadId, CancellationToken ct,
        [FromQuery] int limit = 20, [FromQuery] Guid? afterAttemptId = null)
    {
        Response.Headers.CacheControl = "no-store";
        var actor = CurrentUserId();
        if (actor is null) return Unauthorized();
        if (_chatWriter is null) return StatusCode(503, new { message = "Просмотр отправок временно недоступен." });
        var result = await _chatWriter.DiscoverAsync(threadId, actor.Value, limit, afterAttemptId, ct);
        return result.Status switch
        {
            "forbidden" => Forbid(),
            "invalid" or "invalidCursor" => BadRequest(new { message = "Некорректные параметры списка отправок." }),
            _ => Ok(result)
        };
    }

    [HttpPost("threads/{threadId:int}/sends/{clientRequestId:guid}/resume")]
    public async Task<ActionResult<InternalChatSendStateDto>> ResumeSend(int threadId, Guid clientRequestId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var actor = CurrentUserId();
        if (actor is null) return Unauthorized();
        if (_chatWriter is null || _chatPolicies is null || _chatBindings is null)
            return StatusCode(503, new { message = "Продолжение отправки временно недоступно." });
        // Read the owned state before continuation: disabled writes must not
        // disclose another actor's intent or bypass current thread/task access.
        var state = await _chatWriter.ReadStatusAsync(threadId, actor.Value, clientRequestId, _storageWorkerAvailability, ct);
        if (state.Status is "invalid" or "forbidden" or "notFound" or "deleted")
            return await SendStateResponseAsync(threadId, actor.Value, clientRequestId, state, false, ct);
        // A missing hosted worker pauses automatic work, not an explicit resume.
        if (state.Status != "published")
        {
            await _chatWriter.ContinueAsync(threadId, actor.Value, clientRequestId, _chatPolicies, _chatBindings, ct);
            state = await _chatWriter.ReadStatusAsync(threadId, actor.Value, clientRequestId, _storageWorkerAvailability, ct);
        }
        return await SendStateResponseAsync(threadId, actor.Value, clientRequestId, state, true, ct);
    }

    private async Task<ActionResult<InternalChatSendStateDto>> SendStateResponseAsync(int threadId, int actorId,
        Guid key, InternalChatSendStatusResult state, bool dispatch, CancellationToken ct)
    {
        if (state.Status == "invalid") return BadRequest(new { message = "Некорректный идентификатор отправки." });
        if (state.Status == "forbidden") return Forbid();
        if (state.Status == "notFound") return NotFound(new { message = "Отправка не найдена." });
        InternalChatMessageDto? dto = null;
        if (state.Status == "published" && state.MessageId is { } messageId)
        {
            if (dispatch)
            {
                try { await InternalChatNotificationRecoveryService.DispatchMessageAsync(_db, _notifications, messageId, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                { _logger.LogWarning(error, "Chat message {MessageId} was saved but recipient notification failed.", messageId); }
                try { await _realtime.PublishTaskJournalChangedAsync(ct); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                { _logger.LogWarning(error, "Chat message {MessageId} was saved but realtime update failed.", messageId); }
            }
            var message = await LoadMessageAsync(messageId, ct);
            if (message is null || message.DeletedAt.HasValue) state = new("deleted");
            else if (message.ThreadId != threadId || message.AuthorUserId != actorId || message.ClientRequestId != key)
                state = new("conflict");
            else dto = await MapMessageWithReadStateAsync(message, ct);
        }
        if (!await new InternalChatSendAttemptRegistry(_db).CanParticipateAsync(threadId, actorId, ct)) return Forbid();
        var description = state.Status switch
        {
            "published" => "Сообщение отправлено.",
            "processing" => "Отправка сообщения завершается.",
            "retrying" => "Не удалось завершить отправку. Система повторит попытку автоматически.",
            "paused" => "Сообщение ещё не опубликовано. Обработка приостановлена.",
            "needsInput" => "Чтобы завершить отправку, повторно выберите исходные файлы.",
            "failed" => "Отправка не завершена. Обратитесь к администратору перед повторной отправкой.",
            "conflict" => "Данные отправки изменились. Проверьте исходное сообщение и файлы.",
            "deleted" => "Опубликованное сообщение удалено.",
            _ => "Сообщение будет отправлено после подготовки копий файлов."
        };
        return Ok(new InternalChatSendStateDto(key, state.Status, state.AutomaticRecovery, state.RetryAfter, dto, description));
    }
}

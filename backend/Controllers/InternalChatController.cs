using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/internal-chat")]
public sealed partial class InternalChatController : ControllerBase
{
    private const int PageSize = 50;
    private const int MaximumFilesPerMessage = 10;
    private const long MaximumFileSize = 20 * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".cmd", ".bat", ".com", ".msi", ".ps1", ".js", ".vbs", ".scr"
    };

    private readonly PaymentContext _db;
    private readonly INotificationService _notifications;
    private readonly IRealtimeNotificationPublisher _realtime;
    private readonly string _filesRoot;

    public InternalChatController(
        PaymentContext db,
        INotificationService notifications,
        IRealtimeNotificationPublisher realtime,
        IWebHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _notifications = notifications;
        _realtime = realtime;
        _filesRoot = Path.Combine(
            ApplicationFileStoragePaths.ResolveRoot(environment, configuration, AppContext.BaseDirectory),
            "internal-chat-files");
    }

    [HttpGet("threads")]
    public async Task<ActionResult<IReadOnlyList<InternalChatThreadDto>>> GetThreads(CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var rows = await ActiveAuthorizedMembers()
            .Where(member => member.UserId == userId && member.IsActive && !member.Thread.IsArchived
                && (member.HiddenAfterMessageId == null || member.Thread.Messages.Any(message => message.Id > member.HiddenAfterMessageId)))
            .OrderByDescending(member => member.Thread.LastMessageAt ?? member.Thread.CreatedAt)
            .Select(member => new
            {
                member.Thread,
                member.Role,
                member.NotificationMode,
                member.IsArchived,
                member.LastReadMessageId,
                MemberUserIds = ActiveAuthorizedMembers()
                    .Where(item => item.ThreadId == member.ThreadId)
                    .Select(item => item.UserId)
                    .ToList(),
                GuestUserIds = ActiveAuthorizedMembers()
                    .Where(item => item.ThreadId == member.ThreadId && item.Role == "guest")
                    .Select(item => item.UserId)
                    .ToList(),
                UnreadCount = _db.InternalChatMessages.Count(message => message.ThreadId == member.ThreadId
                    && message.DeletedAt == null
                    && message.AuthorUserId != userId
                    && message.Id > (member.LastReadMessageId ?? 0)),
                FirstUnreadMessageId = _db.InternalChatMessages
                    .Where(message => message.ThreadId == member.ThreadId
                        && message.DeletedAt == null
                        && message.AuthorUserId != userId
                        && message.Id > (member.LastReadMessageId ?? 0))
                    .OrderBy(message => message.Id)
                    .Select(message => (int?)message.Id)
                    .FirstOrDefault(),
                LatestMessage = _db.InternalChatMessages
                    .Where(message => message.ThreadId == member.ThreadId && message.DeletedAt == null)
                    .OrderByDescending(message => message.Id)
                    .Select(message => new { message.Text, message.CreatedAt })
                    .FirstOrDefault(),
                DirectName = member.Thread.Kind == InternalChatThreadKinds.Direct
                    ? ActiveAuthorizedMembers()
                        .Where(other => other.ThreadId == member.ThreadId && other.UserId != userId)
                        .Select(other => other.User.FullName == "" ? other.User.Email : other.User.FullName)
                        .FirstOrDefault()
                    : null
            })
            .ToListAsync(ct);

        return Ok(rows.Select(row =>
        {
            var thread = row.Thread;
            return new InternalChatThreadDto(
                thread.Id,
                thread.Kind,
                thread.Kind == InternalChatThreadKinds.Direct && !string.IsNullOrWhiteSpace(row.DirectName) ? row.DirectName : thread.Name,
                thread.TaskJournalItemId,
                row.Role,
                row.NotificationMode,
                row.IsArchived,
                row.MemberUserIds,
                row.GuestUserIds,
                row.UnreadCount,
                row.LatestMessage is null ? null : MessagePreview(row.LatestMessage.Text),
                AsUtc(row.LatestMessage?.CreatedAt),
                AsUtc(thread.CreatedAt),
                row.LastReadMessageId,
                row.FirstUnreadMessageId);
        }).ToList());
    }

    [HttpGet("people")]
    public async Task<ActionResult<IReadOnlyList<InternalChatPersonDto>>> GetPeople(CancellationToken ct)
    {
        if (CurrentUserId() is null) return Unauthorized();
        var people = await _db.Users.AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new InternalChatPersonDto(user.Id, user.FullName == "" ? user.Email : user.FullName, user.Email, user.PhotoUrl))
            .ToListAsync(ct);
        return Ok(people);
    }

    [HttpPost("channels")]
    public async Task<ActionResult<InternalChatThreadDto>> CreateChannel([FromBody] CreateInternalChatChannelRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var name = NormalizeRequired(request.Name, 200);
        if (name is null) return BadRequest("Укажите название канала.");

        var requestedMembers = request.MemberUserIds.Where(id => id > 0).Append(userId.Value).Distinct().ToList();
        var activeMembers = await _db.Users.AsNoTracking()
            .Where(user => requestedMembers.Contains(user.Id) && user.IsActive)
            .Select(user => user.Id)
            .ToListAsync(ct);
        if (!activeMembers.Contains(userId.Value) || activeMembers.Count != requestedMembers.Count)
            return BadRequest("Один или несколько участников не найдены или отключены.");

        var now = DateTime.UtcNow;
        var thread = new InternalChatThread
        {
            Kind = InternalChatThreadKinds.Channel,
            Name = name,
            CreatedByUserId = userId,
            CreatedAt = now,
            Members = activeMembers.Select(id => new InternalChatMember
            {
                UserId = id,
                Role = id == userId ? "owner" : "member",
                JoinedAt = now
            }).ToList()
        };
        _db.InternalChatThreads.Add(thread);
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return CreatedAtAction(nameof(GetMessages), new { threadId = thread.Id }, ToThreadDto(thread, "owner", InternalChatNotificationModes.All));
    }

    [HttpPut("threads/{threadId:int}/members")]
    public async Task<IActionResult> UpdateChannelMembers(int threadId, [FromBody] UpdateInternalChatMembersRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var thread = await _db.InternalChatThreads.Include(item => item.Members).FirstOrDefaultAsync(item => item.Id == threadId, ct);
        if (thread is null) return NotFound();
        if (thread.Kind != InternalChatThreadKinds.Channel) return BadRequest("Состав участников изменяется только у командных каналов.");
        var actor = thread.Members.FirstOrDefault(member => member.UserId == userId && member.IsActive);
        if (actor?.Role != "owner" && !User.HasAdminRole()) return Forbid();

        var desiredIds = request.MemberUserIds.Where(id => id > 0).Append(thread.CreatedByUserId ?? userId.Value).Distinct().ToList();
        var validCount = await _db.Users.CountAsync(user => desiredIds.Contains(user.Id) && user.IsActive, ct);
        if (validCount != desiredIds.Count) return BadRequest("Один или несколько участников не найдены или отключены.");

        foreach (var member in thread.Members)
        {
            member.IsActive = desiredIds.Contains(member.UserId);
            member.UpdatedAt = DateTime.UtcNow;
        }
        foreach (var id in desiredIds.Where(id => thread.Members.All(member => member.UserId != id)))
        {
            thread.Members.Add(new InternalChatMember { UserId = id, Role = id == thread.CreatedByUserId ? "owner" : "member" });
        }
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPut("threads/{threadId:int}/task-guests")]
    public async Task<IActionResult> UpdateTaskGuests(int threadId, [FromBody] UpdateInternalChatGuestsRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var thread = await _db.InternalChatThreads.Include(item => item.Members).FirstOrDefaultAsync(item => item.Id == threadId, ct);
        if (thread?.Kind != InternalChatThreadKinds.Task || thread.TaskJournalItemId is null) return NotFound();
        var task = await _db.TaskJournalItems.Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == thread.TaskJournalItemId, ct);
        if (task is null) return NotFound();
        if (task.AuthorUserId != userId && !User.HasAdminRole()) return Forbid();

        var taskMembers = TaskMemberIds(task);
        var desiredGuests = request.GuestUserIds.Where(id => id > 0 && !taskMembers.Contains(id)).Distinct().ToHashSet();
        var activeCount = await _db.Users.CountAsync(user => desiredGuests.Contains(user.Id) && user.IsActive && user.IsApproved, ct);
        if (activeCount != desiredGuests.Count) return BadRequest("Один или несколько сотрудников не найдены или отключены.");
        var now = DateTime.UtcNow;
        SyncTaskMembers(thread, taskMembers, task.AuthorUserId, now);
        foreach (var member in thread.Members.Where(member => !taskMembers.Contains(member.UserId)))
        {
            member.IsActive = desiredGuests.Contains(member.UserId);
            if (member.IsActive) member.Role = "guest";
            member.UpdatedAt = now;
        }
        foreach (var guestId in desiredGuests.Where(id => thread.Members.All(member => member.UserId != id)))
            thread.Members.Add(new InternalChatMember { UserId = guestId, Role = "guest", JoinedAt = now });
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPut("threads/{threadId:int}/personal-state")]
    public async Task<IActionResult> SetPersonalState(int threadId, [FromBody] UpdateInternalChatPersonalStateRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var member = await _db.InternalChatMembers.FirstOrDefaultAsync(item => item.ThreadId == threadId && item.UserId == userId && item.IsActive, ct);
        if (member is null) return Forbid();
        if (!await IsMemberAsync(threadId, userId.Value, ct)) return Forbid();
        if (request.State is not ("visible" or "hidden" or "archived")) return BadRequest("Неизвестное состояние чата.");
        member.IsArchived = request.State == "archived";
        member.HiddenAfterMessageId = request.State == "hidden"
            ? await _db.InternalChatMessages.Where(message => message.ThreadId == threadId).MaxAsync(message => (int?)message.Id, ct) ?? 0
            : null;
        member.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("direct/{otherUserId:int}")]
    public async Task<ActionResult<InternalChatThreadDto>> OpenDirect(int otherUserId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (otherUserId <= 0 || otherUserId == userId) return BadRequest("Выберите другого сотрудника.");
        var other = await _db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == otherUserId && user.IsActive, ct);
        if (other is null) return NotFound("Сотрудник не найден.");

        var directKey = BuildDirectKey(userId.Value, otherUserId);
        var existing = await _db.InternalChatThreads
            .Include(thread => thread.Members)
            .FirstOrDefaultAsync(thread => thread.DirectKey == directKey, ct);
        if (existing is not null)
        {
            foreach (var member in existing.Members) member.IsActive = true;
            var mine = existing.Members.First(member => member.UserId == userId);
            mine.HiddenAfterMessageId = null;
            mine.IsArchived = false;
            await _db.SaveChangesAsync(ct);
            var (unreadCount, firstUnreadMessageId) = await GetUnreadSummaryAsync(existing.Id, userId.Value, mine.LastReadMessageId, ct);
            return Ok(ToThreadDto(existing, mine.Role, mine.NotificationMode, DisplayName(other),
                lastReadMessageId: mine.LastReadMessageId, unreadCount: unreadCount, firstUnreadMessageId: firstUnreadMessageId));
        }

        var current = await _db.Users.AsNoTracking().FirstAsync(user => user.Id == userId, ct);
        var thread = new InternalChatThread
        {
            Kind = InternalChatThreadKinds.Direct,
            Name = $"{DisplayName(current)} / {DisplayName(other)}",
            DirectKey = directKey,
            CreatedByUserId = userId,
            Members =
            [
                new InternalChatMember { UserId = userId.Value, Role = "member" },
                new InternalChatMember { UserId = otherUserId, Role = "member" }
            ]
        };
        _db.InternalChatThreads.Add(thread);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsDirectChatAlreadyCreated(exception))
        {
            // The other participant opened the same conversation after our lookup.
            _db.ChangeTracker.Clear();
            existing = await _db.InternalChatThreads.Include(item => item.Members)
                .FirstOrDefaultAsync(item => item.DirectKey == directKey, ct);
            if (existing is null) throw;
            foreach (var member in existing.Members) member.IsActive = true;
            var mine = existing.Members.First(member => member.UserId == userId);
            mine.HiddenAfterMessageId = null;
            mine.IsArchived = false;
            await _db.SaveChangesAsync(ct);
            var (unreadCount, firstUnreadMessageId) = await GetUnreadSummaryAsync(existing.Id, userId.Value, mine.LastReadMessageId, ct);
            return Ok(ToThreadDto(existing, mine.Role, mine.NotificationMode, DisplayName(other),
                lastReadMessageId: mine.LastReadMessageId, unreadCount: unreadCount, firstUnreadMessageId: firstUnreadMessageId));
        }
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return CreatedAtAction(nameof(GetMessages), new { threadId = thread.Id }, ToThreadDto(thread, "member", InternalChatNotificationModes.All, DisplayName(other)));
    }

    [HttpPost("tasks/{taskId:int}")]
    public async Task<ActionResult<InternalChatThreadDto>> OpenTaskChat(int taskId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var task = await _db.TaskJournalItems
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == taskId, ct);
        if (task is null) return NotFound();

        var memberIds = TaskMemberIds(task);
        var now = DateTime.UtcNow;
        var thread = await _db.InternalChatThreads.Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.TaskJournalItemId == taskId, ct);
        if (!memberIds.Contains(userId.Value) && thread?.Members.Any(member => member.UserId == userId && member.IsActive && member.Role == "guest") != true)
            return Forbid();
        var creatingThread = thread is null;
        if (thread is null)
        {
            thread = new InternalChatThread
            {
                Kind = InternalChatThreadKinds.Task,
                Name = task.Title,
                TaskJournalItemId = taskId,
                CreatedByUserId = task.AuthorUserId ?? userId,
                CreatedAt = now
            };
            _db.InternalChatThreads.Add(thread);
        }
        else
        {
            thread.Name = task.Title;
            thread.UpdatedAt = now;
        }

        SyncTaskMembers(thread, memberIds, task.AuthorUserId, now);
        var currentMember = thread.Members.First(member => member.UserId == userId);
        currentMember.HiddenAfterMessageId = null;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (creatingThread && IsTaskChatAlreadyCreated(exception))
        {
            // Another request created this task's chat after our initial lookup.
            _db.ChangeTracker.Clear();
            task = await _db.TaskJournalItems.Include(item => item.Participants)
                .FirstOrDefaultAsync(item => item.Id == taskId, ct);
            if (task is null) return NotFound();
            memberIds = TaskMemberIds(task);
            thread = await _db.InternalChatThreads.Include(item => item.Members)
                .FirstOrDefaultAsync(item => item.TaskJournalItemId == taskId, ct);
            if (thread is null) throw;
            if (!memberIds.Contains(userId.Value) && !thread.Members.Any(member => member.UserId == userId && member.IsActive && member.Role == "guest"))
                return Forbid();
            thread.Name = task.Title;
            var recoveredAt = DateTime.UtcNow;
            thread.UpdatedAt = recoveredAt;
            SyncTaskMembers(thread, memberIds, task.AuthorUserId, recoveredAt);
            currentMember = thread.Members.First(member => member.UserId == userId);
            currentMember.HiddenAfterMessageId = null;
            await _db.SaveChangesAsync(ct);
        }
        var (unreadCount, firstUnreadMessageId) = await GetUnreadSummaryAsync(thread.Id, userId.Value, currentMember.LastReadMessageId, ct);
        return Ok(ToThreadDto(thread, currentMember.Role, currentMember.NotificationMode, isArchived: currentMember.IsArchived,
            lastReadMessageId: currentMember.LastReadMessageId, unreadCount: unreadCount, firstUnreadMessageId: firstUnreadMessageId));
    }

    [HttpGet("threads/{threadId:int}/messages")]
    public async Task<ActionResult<InternalChatMessagePageDto>> GetMessages(
        int threadId,
        [FromQuery] int? beforeId,
        [FromQuery] string? search,
        [FromQuery] int? authorUserId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] bool mentionedMe = false,
        [FromQuery] int limit = PageSize,
        CancellationToken ct = default,
        [FromQuery] int? afterId = null)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (!await IsMemberAsync(threadId, userId.Value, ct)) return Forbid();

        var take = Math.Clamp(limit, 1, PageSize);
        var query = _db.InternalChatMessages.AsNoTracking().Where(message => message.ThreadId == threadId);
        if (beforeId is > 0 && afterId.HasValue) return BadRequest("Выберите одно направление загрузки сообщений.");
        if (beforeId is > 0) query = query.Where(message => message.Id < beforeId.Value);
        if (afterId.HasValue) query = query.Where(message => message.Id > Math.Max(afterId.Value, 0));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > 120) term = term[..120];
            query = query.Where(message => message.Text.Contains(term));
        }
        if (authorUserId is > 0) query = query.Where(message => message.AuthorUserId == authorUserId);
        if (from.HasValue) query = query.Where(message => message.CreatedAt >= from.Value.ToUniversalTime());
        if (to.HasValue) query = query.Where(message => message.CreatedAt <= to.Value.ToUniversalTime());
        if (mentionedMe) query = query.Where(message => message.Mentions.Any(mention => mention.UserId == userId));

        // Load the bounded page's collections separately to avoid multiplying rows
        // across attachments, reactions and mentions in a single joined result.
        var loaded = query.AsSplitQuery()
            .Include(message => message.AuthorUser)
            .Include(message => message.ReplyToMessage)!.ThenInclude(message => message!.AuthorUser)
            .Include(message => message.Attachments)
            .Include(message => message.Reactions).ThenInclude(reaction => reaction.User)
            .Include(message => message.Mentions);
        var rows = afterId.HasValue
            ? await loaded.OrderBy(message => message.Id).Take(take + 1).ToListAsync(ct)
            : await loaded.OrderByDescending(message => message.Id).Take(take + 1).ToListAsync(ct);
        var hasNextPage = rows.Count > take;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);
        if (!afterId.HasValue) rows.Reverse();
        var hasMore = afterId.HasValue && rows.Count > 0
            ? await _db.InternalChatMessages.AnyAsync(message => message.ThreadId == threadId && message.Id < rows[0].Id, ct)
            : hasNextPage;
        var readStates = await ActiveAuthorizedMembers()
            .Where(member => member.ThreadId == threadId && member.LastReadMessageId.HasValue)
            .Select(member => new { member.UserId, LastReadMessageId = member.LastReadMessageId!.Value })
            .ToListAsync(ct);
        return Ok(new InternalChatMessagePageDto(
            rows.Select(message => MapMessage(message, readStates
                .Where(state => state.UserId != message.AuthorUserId && state.LastReadMessageId >= message.Id)
                .Select(state => state.UserId)
                .ToList())).ToList(),
            hasMore,
            rows.FirstOrDefault()?.Id,
            afterId.HasValue && hasNextPage));
    }

    [HttpPost("threads/{threadId:int}/messages")]
    [RequestSizeLimit(MaximumFilesPerMessage * MaximumFileSize)]
    public async Task<ActionResult<InternalChatMessageDto>> SendMessage(int threadId, [FromForm] SendInternalChatMessageRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (!await IsMemberAsync(threadId, userId.Value, ct)) return Forbid();
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length > 8000) return BadRequest("Сообщение не может быть длиннее 8000 символов.");
        if (request.Files.Count > MaximumFilesPerMessage) return BadRequest($"Можно прикрепить не более {MaximumFilesPerMessage} файлов.");
        if (text.Length == 0 && request.Files.Count == 0) return BadRequest("Введите сообщение или прикрепите файл.");
        var fileProblem = ValidateFiles(request.Files);
        if (fileProblem is not null) return BadRequest(fileProblem);

        InternalChatMessage? reply = null;
        if (request.ReplyToMessageId is > 0)
        {
            reply = await _db.InternalChatMessages
                .FirstOrDefaultAsync(message => message.Id == request.ReplyToMessageId && message.ThreadId == threadId && message.DeletedAt == null, ct);
            if (reply is null) return BadRequest("Сообщение для ответа не найдено.");
        }

        var mentionIds = ParseMentionIds(text, request.MentionUserIds);
        if (mentionIds.Count > 0)
        {
            var validMentionCount = await ActiveAuthorizedMembers().CountAsync(member => member.ThreadId == threadId && mentionIds.Contains(member.UserId), ct);
            if (validMentionCount != mentionIds.Count) return BadRequest("Упоминать можно только участников этого чата.");
        }

        var now = DateTime.UtcNow;
        var message = new InternalChatMessage
        {
            ThreadId = threadId,
            AuthorUserId = userId,
            Text = text,
            ReplyToMessageId = reply?.Id,
            CreatedAt = now,
            Mentions = mentionIds.Select(id => new InternalChatMention { UserId = id }).ToList()
        };
        _db.InternalChatMessages.Add(message);
        var thread = await _db.InternalChatThreads.FirstAsync(item => item.Id == threadId, ct);
        thread.LastMessageAt = now;
        thread.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var savedPaths = new List<string>();
        try
        {
            if (request.Files.Count > 0)
            {
                var directory = Path.Combine(_filesRoot, threadId.ToString());
                Directory.CreateDirectory(directory);
                foreach (var file in request.Files)
                {
                    var extension = Path.GetExtension(file.FileName);
                    var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
                    var path = Path.Combine(directory, storedName);
                    await using (var stream = System.IO.File.Create(path)) await file.CopyToAsync(stream, ct);
                    savedPaths.Add(path);
                    message.Attachments.Add(new InternalChatAttachment
                    {
                        FileName = SafeFileName(file.FileName),
                        StoredFileName = storedName,
                        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType[..Math.Min(file.ContentType.Length, 160)],
                        Size = file.Length,
                        UploadedByUserId = userId.Value,
                        CreatedAt = now
                    });
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        catch
        {
            foreach (var path in savedPaths) if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            _db.InternalChatMessages.Remove(message);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (reply?.AuthorUserId is int replyAuthorId && replyAuthorId != userId && reply.NextNoReplyReminderAt.HasValue)
        {
            reply.NextNoReplyReminderAt = null;
            await _db.SaveChangesAsync(ct);
        }

        await NotifyMessageRecipientsAsync(thread, message, reply, mentionIds, ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        var loaded = await LoadMessageAsync(message.Id, ct);
        return CreatedAtAction(nameof(GetMessages), new { threadId }, await MapMessageWithReadStateAsync(loaded!, ct));
    }

    [HttpPut("messages/{messageId:int}")]
    public async Task<ActionResult<InternalChatMessageDto>> EditMessage(int messageId, [FromBody] EditInternalChatMessageRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var message = await _db.InternalChatMessages.Include(item => item.Mentions).FirstOrDefaultAsync(item => item.Id == messageId, ct);
        if (message is null) return NotFound();
        if (message.AuthorUserId != userId || message.DeletedAt.HasValue || message.Kind != InternalChatMessageKinds.User) return Forbid();
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 8000) return BadRequest("Введите сообщение длиной не более 8000 символов.");
        var mentionIds = ParseMentionIds(text, request.MentionUserIds);
        var validCount = await ActiveAuthorizedMembers().CountAsync(member => member.ThreadId == message.ThreadId && mentionIds.Contains(member.UserId), ct);
        if (validCount != mentionIds.Count) return BadRequest("Упоминать можно только участников этого чата.");
        _db.InternalChatMentions.RemoveRange(message.Mentions);
        message.Mentions = mentionIds.Select(id => new InternalChatMention { UserId = id }).ToList();
        message.Text = text;
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return Ok(await MapMessageWithReadStateAsync((await LoadMessageAsync(message.Id, ct))!, ct));
    }

    [HttpDelete("messages/{messageId:int}")]
    public async Task<IActionResult> DeleteMessage(int messageId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var message = await _db.InternalChatMessages.Include(item => item.Attachments).FirstOrDefaultAsync(item => item.Id == messageId, ct);
        if (message is null) return NotFound();
        if (message.AuthorUserId != userId || message.DeletedAt.HasValue || message.Kind != InternalChatMessageKinds.User) return Forbid();
        message.DeletedAt = DateTime.UtcNow;
        message.NextNoReplyReminderAt = null;
        message.Text = string.Empty;
        message.IsPinned = false;
        foreach (var attachment in message.Attachments)
        {
            var path = AttachmentPath(message.ThreadId, attachment.StoredFileName);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        _db.InternalChatAttachments.RemoveRange(message.Attachments);
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPost("messages/{messageId:int}/no-reply-reminder")]
    public async Task<ActionResult<InternalChatMessageDto>> SetNoReplyReminder(
        int messageId, [FromBody] SetInternalChatNoReplyReminderRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var message = await _db.InternalChatMessages.FirstOrDefaultAsync(item => item.Id == messageId, ct);
        if (message is null) return NotFound();
        if (message.AuthorUserId != userId || message.DeletedAt.HasValue || message.Kind != InternalChatMessageKinds.User)
            return Forbid();
        if (!await IsMemberAsync(message.ThreadId, userId.Value, ct)) return Forbid();

        if (request.Enabled)
        {
            var hasAnswer = await _db.InternalChatMessages.AnyAsync(item =>
                item.ReplyToMessageId == messageId && item.DeletedAt == null && item.AuthorUserId != userId, ct);
            if (hasAnswer) return BadRequest("На это сообщение уже ответили.");
            if (!message.NextNoReplyReminderAt.HasValue)
                message.NextNoReplyReminderAt = DateTime.UtcNow.AddDays(1);
        }
        else
        {
            message.NextNoReplyReminderAt = null;
        }

        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return Ok(await MapMessageWithReadStateAsync((await LoadMessageAsync(messageId, ct))!, ct));
    }

    [HttpPost("messages/{messageId:int}/reactions")]
    public async Task<IActionResult> ToggleReaction(int messageId, [FromBody] ToggleInternalChatReactionRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var emoji = (request.Emoji ?? string.Empty).Trim();
        if (emoji.Length == 0 || emoji.Length > 32) return BadRequest("Некорректная реакция.");
        var message = await _db.InternalChatMessages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == messageId && item.DeletedAt == null, ct);
        if (message is null) return NotFound();
        if (!await IsMemberAsync(message.ThreadId, userId.Value, ct)) return Forbid();
        var existing = await _db.InternalChatReactions.FirstOrDefaultAsync(item => item.MessageId == messageId && item.UserId == userId && item.Emoji == emoji, ct);
        if (existing is null) _db.InternalChatReactions.Add(new InternalChatReaction { MessageId = messageId, UserId = userId.Value, Emoji = emoji });
        else _db.InternalChatReactions.Remove(existing);
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPost("messages/{messageId:int}/pin")]
    public async Task<IActionResult> TogglePin(int messageId, [FromBody] PinInternalChatMessageRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var message = await _db.InternalChatMessages.FirstOrDefaultAsync(item => item.Id == messageId && item.DeletedAt == null, ct);
        if (message is null) return NotFound();
        if (!await IsMemberAsync(message.ThreadId, userId.Value, ct)) return Forbid();
        if (request.Pinned && !message.IsPinned)
        {
            var pinnedCount = await _db.InternalChatMessages.CountAsync(item => item.ThreadId == message.ThreadId && item.IsPinned && item.DeletedAt == null, ct);
            if (pinnedCount >= 3) return Conflict("В чате можно закрепить не более трех сообщений.");
        }
        message.IsPinned = request.Pinned;
        message.PinnedAt = request.Pinned ? DateTime.UtcNow : null;
        message.PinnedByUserId = request.Pinned ? userId : null;
        await _db.SaveChangesAsync(ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPost("threads/{threadId:int}/read")]
    public async Task<IActionResult> MarkRead(int threadId, [FromBody] MarkInternalChatReadRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var member = await _db.InternalChatMembers.FirstOrDefaultAsync(item => item.ThreadId == threadId && item.UserId == userId && item.IsActive, ct);
        if (member is null) return Forbid();
        if (request.MessageId is <= 0) return BadRequest("Укажите сообщение из этого чата.");
        var latestId = request.MessageId.HasValue
            ? await _db.InternalChatMessages
                .Where(item => item.ThreadId == threadId && item.Id == request.MessageId && item.DeletedAt == null)
                .Select(item => (int?)item.Id)
                .SingleOrDefaultAsync(ct)
            : await _db.InternalChatMessages
                .Where(item => item.ThreadId == threadId && item.DeletedAt == null)
                .MaxAsync(item => (int?)item.Id, ct);
        if (request.MessageId.HasValue && latestId is null) return BadRequest("Сообщение не найдено в этом чате.");
        var changed = latestId.HasValue && latestId > (member.LastReadMessageId ?? 0);
        if (latestId.HasValue && latestId > (member.LastReadMessageId ?? 0)) member.LastReadMessageId = latestId;
        member.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (changed) await _realtime.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    [HttpPut("threads/{threadId:int}/notification-mode")]
    public async Task<IActionResult> SetNotificationMode(int threadId, [FromBody] UpdateInternalChatNotificationModeRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not (InternalChatNotificationModes.All or InternalChatNotificationModes.Mentions or InternalChatNotificationModes.Muted))
            return BadRequest("Доступны режимы: all, mentions, muted.");
        var member = await _db.InternalChatMembers.FirstOrDefaultAsync(item => item.ThreadId == threadId && item.UserId == userId && item.IsActive, ct);
        if (member is null) return Forbid();
        member.NotificationMode = mode;
        member.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("attachments/{attachmentId:int}")]
    public async Task<IActionResult> DownloadAttachment(int attachmentId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var attachment = await _db.InternalChatAttachments.AsNoTracking()
            .Include(item => item.Message)
            .FirstOrDefaultAsync(item => item.Id == attachmentId && item.Message.DeletedAt == null, ct);
        if (attachment is null) return NotFound();
        if (!await IsMemberAsync(attachment.Message.ThreadId, userId.Value, ct)) return Forbid();
        var path = AttachmentPath(attachment.Message.ThreadId, attachment.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, attachment.ContentType, attachment.FileName, enableRangeProcessing: true);
    }

    [HttpPost("messages/{messageId:int}/task")]
    public async Task<ActionResult<InternalChatMessageDto>> CreateTaskFromMessage(int messageId, [FromBody] CreateTaskFromInternalChatMessageRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var source = await _db.InternalChatMessages.Include(item => item.Thread).FirstOrDefaultAsync(item => item.Id == messageId && item.DeletedAt == null, ct);
        if (source is null) return NotFound();
        if (!await IsMemberAsync(source.ThreadId, userId.Value, ct)) return Forbid();
        if (!await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Tasks, permission => permission.CanCreate, ct)) return Forbid();
        var title = NormalizeRequired(request.Title, 500);
        if (title is null) return BadRequest("Укажите название задачи.");
        if (request.DueAt <= DateTime.UtcNow.AddMinutes(-1)) return BadRequest("Срок задачи должен быть в будущем.");

        User? assignee = null;
        if (request.AssigneeUserId is > 0)
        {
            assignee = await _db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == request.AssigneeUserId && user.IsActive, ct);
            if (assignee is null) return BadRequest("Исполнитель не найден или отключен.");
        }
        var actor = await _db.Users.AsNoTracking().FirstAsync(user => user.Id == userId, ct);
        var now = DateTime.UtcNow;
        var task = new TaskJournalItem
        {
            ObjectName = source.Thread.Kind == InternalChatThreadKinds.Task ? source.Thread.Name : "Задача из чата",
            Title = title,
            Description = string.IsNullOrWhiteSpace(request.Description) ? source.Text : request.Description.Trim()[..Math.Min(request.Description.Trim().Length, 4000)],
            AssigneeUserId = assignee?.Id,
            AssigneeName = assignee is null ? string.Empty : DisplayName(assignee),
            AuthorUserId = userId,
            AuthorName = DisplayName(actor),
            DueAt = request.DueAt.ToUniversalTime(),
            Type = "task",
            Priority = "normal",
            Status = "new",
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            CreatedAt = now
        };
        _db.TaskJournalItems.Add(task);
        await _db.SaveChangesAsync(ct);

        var link = new InternalChatMessage
        {
            ThreadId = source.ThreadId,
            AuthorUserId = userId,
            Kind = InternalChatMessageKinds.TaskLink,
            Text = $"Создана задача «{task.Title}»",
            ReplyToMessageId = source.Id,
            CreatedTaskJournalItemId = task.Id,
            CreatedAt = now
        };
        _db.InternalChatMessages.Add(link);
        source.Thread.LastMessageAt = now;
        await _db.SaveChangesAsync(ct);
        if (assignee is not null && assignee.Id != userId)
            await _notifications.NotifyTaskAssignedAsync(task, DisplayName(actor), userId.Value, ct);
        await _realtime.PublishTaskJournalChangedAsync(ct);
        await NotifyTaskCreatedRecipientsAsync(source.Thread, link, task, userId.Value, ct);
        return Ok(await MapMessageWithReadStateAsync((await LoadMessageAsync(link.Id, ct))!, ct));
    }

    private int? CurrentUserId() => User.GetUserId();

    private IQueryable<InternalChatMember> ActiveAuthorizedMembers()
        => _db.InternalChatMembers.AsNoTracking().Where(member => member.IsActive
            && (member.Thread.Kind != InternalChatThreadKinds.Task || member.Role == "guest"
                || member.Thread.TaskJournalItem!.AuthorUserId == member.UserId
                || member.Thread.TaskJournalItem.AssigneeUserId == member.UserId
                || member.Thread.TaskJournalItem.Participants.Any(participant => participant.UserId == member.UserId)));

    private Task<bool> IsMemberAsync(int threadId, int userId, CancellationToken ct)
        => ActiveAuthorizedMembers().AnyAsync(member => member.ThreadId == threadId && member.UserId == userId, ct);

    private async Task<InternalChatMessage?> LoadMessageAsync(int messageId, CancellationToken ct)
        => await _db.InternalChatMessages.AsNoTracking()
            .Include(message => message.AuthorUser)
            .Include(message => message.ReplyToMessage)!.ThenInclude(message => message!.AuthorUser)
            .Include(message => message.Attachments)
            .Include(message => message.Reactions).ThenInclude(reaction => reaction.User)
            .Include(message => message.Mentions)
            .FirstOrDefaultAsync(message => message.Id == messageId, ct);

    private async Task<InternalChatMessageDto> MapMessageWithReadStateAsync(InternalChatMessage message, CancellationToken ct)
    {
        var readBy = await ActiveAuthorizedMembers()
            .Where(member => member.ThreadId == message.ThreadId
                && member.UserId != message.AuthorUserId
                && member.LastReadMessageId >= message.Id)
            .Select(member => member.UserId)
            .ToListAsync(ct);
        return MapMessage(message, readBy);
    }

    private async Task NotifyTaskCreatedRecipientsAsync(
        InternalChatThread thread,
        InternalChatMessage message,
        TaskJournalItem task,
        int actorUserId,
        CancellationToken ct)
    {
        var recipients = await ActiveAuthorizedMembers()
            .Where(member => member.ThreadId == thread.Id
                && member.UserId != actorUserId
                && member.NotificationMode != InternalChatNotificationModes.Muted)
            .Select(member => member.UserId)
            .ToListAsync(ct);
        foreach (var recipientId in recipients)
        {
            await _notifications.NotifyAsync(new NotificationEventEnvelope(
                recipientId,
                UserNotificationTypes.ChatTaskCreated,
                $"chat:{message.Id}:{recipientId}:task-created",
                $"Из чата создана задача: {task.Title}",
                $"Откройте задачу №{task.Id} или продолжите обсуждение в чате «{thread.Name}».",
                "internalChat",
                thread.Id,
                task.Id,
                Metadata: new { threadId = thread.Id, messageId = message.Id, taskId = task.Id },
                Channels: null,
                ActionUrl: $"/?ppTab=messenger&thread={thread.Id}"), ct);
        }
    }

    private async Task NotifyMessageRecipientsAsync(
        InternalChatThread thread,
        InternalChatMessage message,
        InternalChatMessage? reply,
        IReadOnlyCollection<int> mentionIds,
        CancellationToken ct)
    {
        var members = await ActiveAuthorizedMembers()
            .Where(member => member.ThreadId == thread.Id && member.UserId != message.AuthorUserId)
            .ToListAsync(ct);
        foreach (var member in members)
        {
            var isMention = mentionIds.Contains(member.UserId);
            var isReply = reply?.AuthorUserId == member.UserId;
            if (member.NotificationMode == InternalChatNotificationModes.Muted) continue;
            if (member.NotificationMode == InternalChatNotificationModes.Mentions && !isMention && !isReply) continue;
            var type = isMention ? UserNotificationTypes.ChatMention : isReply ? UserNotificationTypes.ChatReply : UserNotificationTypes.ChatMessage;
            var title = isMention ? $"Вас упомянули: {thread.Name}" : isReply ? $"Ответ в чате: {thread.Name}" : $"Новое сообщение: {thread.Name}";
            await _notifications.NotifyAsync(new NotificationEventEnvelope(
                member.UserId,
                type,
                $"chat:{message.Id}:{member.UserId}:{type}",
                title,
                MessagePreview(message.Text),
                "internalChat",
                thread.Id,
                thread.TaskJournalItemId,
                Metadata: new { threadId = thread.Id, messageId = message.Id },
                Channels: null,
                ActionUrl: $"/?ppTab=messenger&thread={thread.Id}"), ct);
        }
    }

    private static HashSet<int> TaskMemberIds(TaskJournalItem task)
    {
        var ids = task.Participants.Where(participant => participant.UserId.HasValue).Select(participant => participant.UserId!.Value).ToHashSet();
        if (task.AuthorUserId.HasValue) ids.Add(task.AuthorUserId.Value);
        if (task.AssigneeUserId.HasValue) ids.Add(task.AssigneeUserId.Value);
        return ids;
    }

    private static bool IsTaskChatAlreadyCreated(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_InternalChatThreads_TaskJournalItemId" } => true,
            SqliteException { SqliteExtendedErrorCode: 2067 } sqlite =>
                sqlite.Message.Contains("InternalChatThreads.TaskJournalItemId", StringComparison.Ordinal),
            _ => false
        };

    private static bool IsDirectChatAlreadyCreated(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_InternalChatThreads_DirectKey" } => true,
            SqliteException { SqliteExtendedErrorCode: 2067 } sqlite =>
                sqlite.Message.Contains("InternalChatThreads.DirectKey", StringComparison.Ordinal),
            _ => false
        };

    private static void SyncTaskMembers(InternalChatThread thread, IReadOnlySet<int> desiredIds, int? authorId, DateTime now)
    {
        foreach (var member in thread.Members)
        {
            member.IsActive = desiredIds.Contains(member.UserId) || member.Role == "guest" && member.IsActive;
            if (desiredIds.Contains(member.UserId)) member.Role = member.UserId == authorId ? "owner" : "member";
            member.UpdatedAt = now;
        }
        foreach (var userId in desiredIds.Where(id => thread.Members.All(member => member.UserId != id)))
            thread.Members.Add(new InternalChatMember { UserId = userId, Role = userId == authorId ? "owner" : "member", JoinedAt = now });
    }

    private async Task<(int Count, int? FirstMessageId)> GetUnreadSummaryAsync(int threadId, int userId, int? lastReadMessageId, CancellationToken ct)
    {
        var summary = await _db.InternalChatMessages.AsNoTracking()
            .Where(message => message.ThreadId == threadId
                && message.DeletedAt == null
                && message.AuthorUserId != userId
                && message.Id > (lastReadMessageId ?? 0))
            .GroupBy(message => message.ThreadId)
            .Select(group => new { Count = group.Count(), FirstMessageId = group.Min(message => message.Id) })
            .FirstOrDefaultAsync(ct);
        return summary is null ? (0, null) : (summary.Count, summary.FirstMessageId);
    }

    private static InternalChatThreadDto ToThreadDto(InternalChatThread thread, string role, string mode, string? overrideName = null, bool isArchived = false, int? lastReadMessageId = null, int unreadCount = 0, int? firstUnreadMessageId = null)
        => new(thread.Id, thread.Kind, overrideName ?? thread.Name, thread.TaskJournalItemId, role, mode, isArchived,
            thread.Members.Where(member => member.IsActive).Select(member => member.UserId).ToList(),
            thread.Members.Where(member => member.IsActive && member.Role == "guest").Select(member => member.UserId).ToList(),
            unreadCount, null, AsUtc(thread.LastMessageAt), AsUtc(thread.CreatedAt), lastReadMessageId, firstUnreadMessageId);

    private static InternalChatMessageDto MapMessage(InternalChatMessage message, IReadOnlyList<int>? readByUserIds = null)
        => new(
            message.Id,
            message.ThreadId,
            message.AuthorUserId,
            message.AuthorUser is null ? "Система" : DisplayName(message.AuthorUser),
            message.Kind,
            message.DeletedAt.HasValue ? string.Empty : message.Text,
            message.ReplyToMessageId,
            message.ReplyToMessage is null ? null : MessagePreview(message.ReplyToMessage.Text),
            message.ReplyToMessage?.AuthorUser is null ? null : DisplayName(message.ReplyToMessage.AuthorUser),
            message.CreatedTaskJournalItemId,
            message.IsPinned,
            message.NextNoReplyReminderAt.HasValue,
            AsUtc(message.EditedAt),
            AsUtc(message.DeletedAt),
            AsUtc(message.CreatedAt),
            message.Attachments.Select(file => new InternalChatAttachmentDto(file.Id, file.FileName, file.ContentType, file.Size)).ToList(),
            message.Reactions.GroupBy(reaction => reaction.Emoji).Select(group => new InternalChatReactionDto(group.Key, group.Count(), group.Select(reaction => reaction.UserId).ToList())).ToList(),
            message.Mentions.Select(mention => mention.UserId).ToList(),
            readByUserIds ?? []);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    private static string DisplayName(User user) => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();
    private static string BuildDirectKey(int first, int second) => first < second ? $"{first}:{second}" : $"{second}:{first}";
    private static string MessagePreview(string? text) => string.IsNullOrWhiteSpace(text) ? "Вложение" : text.Trim()[..Math.Min(text.Trim().Length, 180)];
    private static string? NormalizeRequired(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private static HashSet<int> ParseMentionIds(string text, IEnumerable<int>? explicitIds)
    {
        var ids = (explicitIds ?? []).Where(id => id > 0).ToHashSet();
        foreach (Match match in MentionPattern().Matches(text))
            if (int.TryParse(match.Groups[1].Value, out var id) && id > 0) ids.Add(id);
        return ids;
    }

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name[..Math.Min(name.Length, 260)];
    }

    private static string? ValidateFiles(IReadOnlyCollection<IFormFile> files)
    {
        foreach (var file in files)
        {
            if (file.Length <= 0) return $"Файл «{SafeFileName(file.FileName)}» пуст.";
            if (file.Length > MaximumFileSize) return $"Файл «{SafeFileName(file.FileName)}» превышает 20 МБ.";
            if (ForbiddenExtensions.Contains(Path.GetExtension(file.FileName))) return $"Тип файла «{SafeFileName(file.FileName)}» запрещён из соображений безопасности.";
        }
        return null;
    }

    private string AttachmentPath(int threadId, string storedFileName)
        => Path.Combine(_filesRoot, threadId.ToString(), Path.GetFileName(storedFileName));

    [GeneratedRegex(@"@\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();
}

public sealed record InternalChatThreadDto(int Id, string Kind, string Name, int? TaskJournalItemId, string Role, string NotificationMode, bool IsArchived, IReadOnlyList<int> MemberUserIds, IReadOnlyList<int> GuestUserIds, int UnreadCount, string? LastMessagePreview, DateTime? LastMessageAt, DateTime CreatedAt, int? LastReadMessageId = null, int? FirstUnreadMessageId = null);
public sealed record InternalChatMessagePageDto(IReadOnlyList<InternalChatMessageDto> Items, bool HasMore, int? NextBeforeId, bool HasNewer = false);
public sealed record InternalChatMessageDto(int Id, int ThreadId, int? AuthorUserId, string AuthorName, string Kind, string Text, int? ReplyToMessageId, string? ReplyPreview, string? ReplyAuthorName, int? CreatedTaskJournalItemId, bool IsPinned, bool NoReplyReminderEnabled, DateTime? EditedAt, DateTime? DeletedAt, DateTime CreatedAt, IReadOnlyList<InternalChatAttachmentDto> Attachments, IReadOnlyList<InternalChatReactionDto> Reactions, IReadOnlyList<int> MentionUserIds, IReadOnlyList<int> ReadByUserIds);
public sealed record InternalChatAttachmentDto(int Id, string FileName, string ContentType, long Size);
public sealed record InternalChatReactionDto(string Emoji, int Count, IReadOnlyList<int> UserIds);
public sealed record InternalChatPersonDto(int Id, string Name, string Email, string? PhotoUrl);

public sealed class CreateInternalChatChannelRequest { public string Name { get; set; } = string.Empty; public List<int> MemberUserIds { get; set; } = []; }
public sealed class UpdateInternalChatMembersRequest { public List<int> MemberUserIds { get; set; } = []; }
public sealed class UpdateInternalChatGuestsRequest { public List<int> GuestUserIds { get; set; } = []; }
public sealed class UpdateInternalChatPersonalStateRequest { public string State { get; set; } = string.Empty; }
public sealed class SendInternalChatMessageRequest { public string? Text { get; set; } public int? ReplyToMessageId { get; set; } public List<int> MentionUserIds { get; set; } = []; public List<IFormFile> Files { get; set; } = []; }
public sealed class EditInternalChatMessageRequest { public string? Text { get; set; } public List<int> MentionUserIds { get; set; } = []; }
public sealed class ToggleInternalChatReactionRequest { public string? Emoji { get; set; } }
public sealed class PinInternalChatMessageRequest { public bool Pinned { get; set; } }
public sealed class SetInternalChatNoReplyReminderRequest { public bool Enabled { get; set; } }
public sealed class MarkInternalChatReadRequest { public int? MessageId { get; set; } }
public sealed class UpdateInternalChatNotificationModeRequest { public string? Mode { get; set; } }
public sealed class CreateTaskFromInternalChatMessageRequest { public string Title { get; set; } = string.Empty; public string? Description { get; set; } public int? AssigneeUserId { get; set; } public DateTime DueAt { get; set; } }

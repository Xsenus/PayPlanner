using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Requests;

public sealed record SalesRequestResult<T>(T? Value, string? Error = null, int Status = 200, object? Details = null)
{
    public bool Ok => Error is null;
    public static SalesRequestResult<T> Fail(string error, int status = 400, object? details = null) => new(default, error, status, details);
}

public sealed record SalesRequestCreateInput(
    string? Source,
    int? ClientId,
    string? NewClientName,
    string? NewClientPhone,
    string? NewClientEmail,
    int? ClientCaseId,
    string? Type,
    string? Summary,
    decimal? Amount,
    string? Comment,
    int? ResponsibleUserId);

public sealed record SalesRequestUpdateInput(string? Summary, string? Type, decimal? Amount, string? Comment, int? ResponsibleUserId, int ExpectedVersion);

public sealed record SalesRequestTransitionInput(int ToStatusId, int ExpectedVersion, int? PaidPaymentId, bool ForceCloseTasks, string? Comment);

public sealed record OpenTaskInfo(int Id, string Title, int? AssigneeUserId, string Status);

public sealed record SalesRequestReport(
    int Total, int Succeeded, int Failed, int InProgress, decimal ConversionPercent, decimal WonAmount, decimal LostAmount,
    IReadOnlyList<SalesRequestSourceReport> BySource);

public sealed record SalesRequestSourceReport(string Source, int Total, int Succeeded, decimal ConversionPercent);

public interface ISalesRequestService
{
    Task EnsureDefaultsAsync(CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> CreateAsync(SalesRequestCreateInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> UpdateAsync(int id, SalesRequestUpdateInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> TransitionAsync(int id, SalesRequestTransitionInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<TaskJournalItem>> CreateManualTaskAsync(int id, int assigneeUserId, string? title, DateTime? dueAt, int userId, CancellationToken ct);
    Task<SalesRequestReport> BuildReportAsync(DateTime from, DateTime to, IReadOnlyCollection<int>? visibleResponsibleUserIds, CancellationToken ct);
}

/// <summary>
/// Воронка заявок. Правила из ТЗ PAY-31, реализованные здесь: автотип по делу клиента, правила «при входе в статус создать задачу»,
/// закрытие задач прежнего статуса при переходе, блокировка финального статуса при открытых задачах (принудительное закрытие —
/// отдельный осознанный шаг), успех только при фактическом платеже клиента в CRM, оптимистичная блокировка по версии.
/// Автоматическое создание дела, документы и SLA-триггеры сознательно не входят в этот этап.
/// </summary>
public sealed class SalesRequestService : ISalesRequestService
{
    private static readonly string[] ClosedTaskStatuses = ["done", "cancelled"];

    private readonly PaymentContext _db;

    public SalesRequestService(PaymentContext db) => _db = db;

    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (await _db.SalesRequestStatuses.AnyAsync(ct))
        {
            return;
        }

        // (ключ, название, финал, правила задач: название, срок в минутах)
        var defaults = new (string Key, string Title, string Final, (string Title, int Minutes)[] Rules)[]
        {
            ("new", "Новая", SalesRequestFinalKinds.None, [("Принять заявку, связаться с клиентом", 30)]),
            ("qualification", "Квалификация", SalesRequestFinalKinds.None, [("Квалифицировать заявку", 120)]),
            ("calculation", "Расчёт", SalesRequestFinalKinds.None, [("Рассчитать стоимость", 24 * 60)]),
            ("approval", "Согласование", SalesRequestFinalKinds.None, [("Согласовать стоимость с клиентом", 2 * 24 * 60)]),
            ("documents-prep", "Подготовка документов", SalesRequestFinalKinds.None, [("Подготовить счёт и договор", 60)]),
            ("documents-sent", "Документы отправлены", SalesRequestFinalKinds.None, [("Отправить документы клиенту", 24 * 60)]),
            ("awaiting-payment", "Ожидание оплаты", SalesRequestFinalKinds.None, [("Напомнить об оплате", 2 * 24 * 60), ("Финальный контакт", 5 * 24 * 60)]),
            ("paid", "Оплачено", SalesRequestFinalKinds.Success, [("Назначить юриста по новому делу", 240)]),
            ("abandoned", "Брошен", SalesRequestFinalKinds.Failure, [("Дожим", 3 * 24 * 60)]),
            ("declined", "Отказ", SalesRequestFinalKinds.Failure, []),
        };

        var order = 0;
        foreach (var item in defaults)
        {
            var status = new SalesRequestStatus { Key = item.Key, Title = item.Title, SortOrder = ++order, FinalKind = item.Final };
            _db.SalesRequestStatuses.Add(status);
            await _db.SaveChangesAsync(ct);
            foreach (var rule in item.Rules)
            {
                _db.SalesRequestStatusTaskRules.Add(new SalesRequestStatusTaskRule { StatusId = status.Id, Title = rule.Title, DueInMinutes = rule.Minutes });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<SalesRequestResult<SalesRequest>> CreateAsync(SalesRequestCreateInput input, int userId, CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        if (string.IsNullOrWhiteSpace(input.Source) || !SalesRequestSources.All.Contains(input.Source))
        {
            return SalesRequestResult<SalesRequest>.Fail("Выберите источник: Звонок, Макс, Авито, Сайт или Другое.");
        }

        var summary = input.Summary?.Trim();
        if (string.IsNullOrEmpty(summary))
        {
            return SalesRequestResult<SalesRequest>.Fail("Опишите суть запроса.");
        }

        var clientResult = await ResolveClientAsync(input, userId, ct);
        if (!clientResult.Ok)
        {
            return SalesRequestResult<SalesRequest>.Fail(clientResult.Error!, clientResult.Status, clientResult.Details);
        }

        var client = clientResult.Value!;
        var type = SalesRequestTypes.New;
        var linkedClosed = false;
        string? autoComment = null;
        if (input.ClientCaseId is int caseId)
        {
            var linked = await _db.ClientCases.AsNoTracking().Where(item => item.Id == caseId).Select(item => new { item.ClientId, item.Status }).FirstOrDefaultAsync(ct);
            if (linked is null || linked.ClientId != client.Id)
            {
                return SalesRequestResult<SalesRequest>.Fail("Дело не найдено у этого клиента.", 404);
            }

            linkedClosed = linked.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;
            // Тип по договору (фиксированный/по факту) пока не определяется автоматически: по делу ставим «По факту», уточняет квалификатор.
            type = SalesRequestTypes.PerCase;
        }
        else if (!await _db.ClientCases.AsNoTracking().AnyAsync(item => item.ClientId == client.Id, ct))
        {
            autoComment = "Клиент без истории дел";
        }

        if (!string.IsNullOrWhiteSpace(input.Type) && SalesRequestTypes.All.Contains(input.Type))
        {
            type = input.Type;
        }

        var first = await _db.SalesRequestStatuses.Where(item => item.IsActive).OrderBy(item => item.SortOrder).FirstAsync(ct);
        var now = DateTime.UtcNow;
        var request = new SalesRequest
        {
            CreatedAt = now,
            Source = input.Source,
            ClientId = client.Id,
            ClientName = client.Name,
            ClientPhone = client.Phone,
            ClientCaseId = input.ClientCaseId,
            LinkedCaseWasClosed = linkedClosed,
            Type = type,
            Summary = summary,
            StatusId = first.Id,
            StatusEnteredAt = now,
            ResponsibleUserId = input.ResponsibleUserId ?? userId,
            Amount = input.Amount is > 0 ? decimal.Round(input.Amount.Value, 2, MidpointRounding.AwayFromZero) : null,
            Comment = Trim(string.Join(' ', new[] { input.Comment, autoComment }.Where(text => !string.IsNullOrWhiteSpace(text))), 2000),
            CreatedByUserId = userId,
            UpdatedAt = now,
        };
        _db.SalesRequests.Add(request);
        await _db.SaveChangesAsync(ct);
        AddEvent(request.Id, SalesRequestEventKinds.Created, null, first.Id, userId, null);
        await CreateRuleTasksAsync(request, first.Id, userId, ct);
        await _db.SaveChangesAsync(ct);
        return new(request);
    }

    public async Task<SalesRequestResult<SalesRequest>> UpdateAsync(int id, SalesRequestUpdateInput input, int userId, CancellationToken ct)
    {
        var request = await _db.SalesRequests.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (request is null)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявка не найдена.", 404);
        }

        if (request.Version != input.ExpectedVersion)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявку уже изменили. Обновите карточку.", 409);
        }

        if (request.FinalKind != SalesRequestFinalKinds.None)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявка закрыта и не меняется.", 409);
        }

        if (input.Summary is not null)
        {
            var summary = input.Summary.Trim();
            if (summary.Length == 0)
            {
                return SalesRequestResult<SalesRequest>.Fail("Суть запроса не может быть пустой.");
            }

            request.Summary = summary;
        }

        if (input.Type is not null)
        {
            if (!SalesRequestTypes.All.Contains(input.Type))
            {
                return SalesRequestResult<SalesRequest>.Fail("Неизвестный тип заявки.");
            }

            request.Type = input.Type;
        }

        if (input.Amount.HasValue)
        {
            if (input.Amount.Value < 0)
            {
                return SalesRequestResult<SalesRequest>.Fail("Сумма не может быть отрицательной.");
            }

            request.Amount = decimal.Round(input.Amount.Value, 2, MidpointRounding.AwayFromZero);
        }

        if (input.Comment is not null)
        {
            request.Comment = Trim(input.Comment, 2000);
        }

        if (input.ResponsibleUserId.HasValue)
        {
            if (!await _db.Users.AnyAsync(item => item.Id == input.ResponsibleUserId.Value && item.IsActive, ct))
            {
                return SalesRequestResult<SalesRequest>.Fail("Ответственный не найден.", 404);
            }

            request.ResponsibleUserId = input.ResponsibleUserId.Value;
        }

        request.Version++;
        request.UpdatedAt = DateTime.UtcNow;
        AddEvent(id, SalesRequestEventKinds.FieldsChanged, null, null, userId, null);
        return await SaveAsync(request, ct);
    }

    public async Task<SalesRequestResult<SalesRequest>> TransitionAsync(int id, SalesRequestTransitionInput input, int userId, CancellationToken ct)
    {
        var request = await _db.SalesRequests.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (request is null)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявка не найдена.", 404);
        }

        if (request.Version != input.ExpectedVersion)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявку уже изменили. Обновите карточку.", 409);
        }

        if (request.FinalKind != SalesRequestFinalKinds.None)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявка в финальном статусе и дальше не двигается.", 409);
        }

        var target = await _db.SalesRequestStatuses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ToStatusId && item.IsActive, ct);
        if (target is null)
        {
            return SalesRequestResult<SalesRequest>.Fail("Статус не найден.", 404);
        }

        if (target.Id == request.StatusId)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявка уже в этом статусе.", 409);
        }

        var isFinal = target.FinalKind != SalesRequestFinalKinds.None;
        if (target.FinalKind == SalesRequestFinalKinds.Success)
        {
            // «Оплачено» — только по фактическому платежу клиента в CRM, а не по отметке сотрудника.
            if (input.PaidPaymentId is not int paymentId)
            {
                return SalesRequestResult<SalesRequest>.Fail("Для статуса «Оплачено» укажите платёж клиента в CRM.");
            }

            var payment = await _db.Payments.AsNoTracking().Where(item => item.Id == paymentId)
                .Select(item => new { item.ClientId, item.PaidAmount }).FirstOrDefaultAsync(ct);
            if (payment is null || payment.ClientId != request.ClientId)
            {
                return SalesRequestResult<SalesRequest>.Fail("Платёж не найден у клиента заявки.", 404);
            }

            if (payment.PaidAmount <= 0)
            {
                return SalesRequestResult<SalesRequest>.Fail("По этому платежу оплата ещё не поступила.", 409);
            }

            request.PaidPaymentId = paymentId;
        }

        var openTasks = await LoadOpenTasksAsync(request.Id, ct);
        var leavingTag = StatusTag(request.StatusId);
        var leavingTasks = openTasks.Where(task => HasTag(task, leavingTag)).ToList();
        var foreignOpen = openTasks.Except(leavingTasks).ToList();
        if (isFinal && !input.ForceCloseTasks && foreignOpen.Count > 0)
        {
            return SalesRequestResult<SalesRequest>.Fail(
                "По заявке есть незакрытые задачи. Закройте их или подтвердите принудительное закрытие.",
                409,
                new { openTasks = foreignOpen.Select(task => new OpenTaskInfo(task.Id, task.Title, task.AssigneeUserId, task.Status)).ToList() });
        }

        var now = DateTime.UtcNow;
        foreach (var task in leavingTasks)
        {
            task.Status = "done";
            task.Result = "Закрыта при переходе заявки в следующий статус";
            task.UpdatedAt = now;
        }

        if (isFinal && input.ForceCloseTasks && foreignOpen.Count > 0)
        {
            foreach (var task in foreignOpen)
            {
                task.Status = "cancelled";
                task.Result = "Закрыта принудительно: заявка закрыта";
                task.UpdatedAt = now;
            }

            AddEvent(request.Id, SalesRequestEventKinds.TasksForceClosed, request.StatusId, target.Id, userId, $"Закрыто принудительно задач: {foreignOpen.Count}");
        }

        var fromStatusId = request.StatusId;
        request.StatusId = target.Id;
        request.StatusEnteredAt = now;
        request.Version++;
        request.UpdatedAt = now;
        if (isFinal)
        {
            request.FinalKind = target.FinalKind;
            request.ClosedAt = now;
            request.ClosedByUserId = userId;
        }

        AddEvent(request.Id, SalesRequestEventKinds.StatusChanged, fromStatusId, target.Id, userId, Trim(input.Comment, 1000));
        await CreateRuleTasksAsync(request, target.Id, userId, ct);
        return await SaveAsync(request, ct);
    }

    public async Task<SalesRequestResult<TaskJournalItem>> CreateManualTaskAsync(int id, int assigneeUserId, string? title, DateTime? dueAt, int userId, CancellationToken ct)
    {
        var request = await _db.SalesRequests.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (request is null)
        {
            return SalesRequestResult<TaskJournalItem>.Fail("Заявка не найдена.", 404);
        }

        if (request.FinalKind != SalesRequestFinalKinds.None)
        {
            return SalesRequestResult<TaskJournalItem>.Fail("Заявка закрыта — новые задачи не ставятся.", 409);
        }

        var text = title?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return SalesRequestResult<TaskJournalItem>.Fail("Укажите текст задачи.");
        }

        var task = await BuildTaskAsync(request, assigneeUserId, text, dueAt ?? DateTime.UtcNow.AddDays(1), userId, null, ct);
        if (task is null)
        {
            return SalesRequestResult<TaskJournalItem>.Fail("Исполнитель не найден.", 404);
        }

        _db.TaskJournalItems.Add(task);
        AddEvent(id, SalesRequestEventKinds.TaskCreated, null, null, userId, text);
        await _db.SaveChangesAsync(ct);
        return new(task);
    }

    public async Task<SalesRequestReport> BuildReportAsync(DateTime from, DateTime to, IReadOnlyCollection<int>? visibleResponsibleUserIds, CancellationToken ct)
    {
        var end = to.Date.AddDays(1);
        var query = _db.SalesRequests.AsNoTracking().Where(item => item.CreatedAt >= from.Date && item.CreatedAt < end);
        if (visibleResponsibleUserIds is not null)
        {
            query = query.Where(item => visibleResponsibleUserIds.Contains(item.ResponsibleUserId) || visibleResponsibleUserIds.Contains(item.CreatedByUserId));
        }

        var rows = await query.Select(item => new { item.Source, item.FinalKind, item.Amount }).ToListAsync(ct);
        var succeeded = rows.Count(item => item.FinalKind == SalesRequestFinalKinds.Success);
        var failed = rows.Count(item => item.FinalKind == SalesRequestFinalKinds.Failure);
        return new SalesRequestReport(
            rows.Count,
            succeeded,
            failed,
            rows.Count - succeeded - failed,
            Percent(succeeded, rows.Count),
            rows.Where(item => item.FinalKind == SalesRequestFinalKinds.Success).Sum(item => item.Amount ?? 0m),
            rows.Where(item => item.FinalKind == SalesRequestFinalKinds.Failure).Sum(item => item.Amount ?? 0m),
            rows.GroupBy(item => item.Source).OrderBy(group => group.Key)
                .Select(group => new SalesRequestSourceReport(group.Key, group.Count(), group.Count(item => item.FinalKind == SalesRequestFinalKinds.Success), Percent(group.Count(item => item.FinalKind == SalesRequestFinalKinds.Success), group.Count())))
                .ToList());
    }

    private async Task<SalesRequestResult<Client>> ResolveClientAsync(SalesRequestCreateInput input, int userId, CancellationToken ct)
    {
        if (input.ClientId is int clientId)
        {
            var existing = await _db.Clients.FirstOrDefaultAsync(item => item.Id == clientId, ct);
            return existing is null ? SalesRequestResult<Client>.Fail("Клиент не найден.", 404) : new(existing);
        }

        var name = input.NewClientName?.Trim();
        var phone = input.NewClientPhone?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(phone))
        {
            return SalesRequestResult<Client>.Fail("Для нового клиента укажите ФИО и телефон.");
        }

        var digits = Digits(phone);
        if (digits.Length < 10)
        {
            return SalesRequestResult<Client>.Fail("Телефон указан неполностью.");
        }

        // Блокировка дублей: клиент с тем же телефоном уже есть — выбираем его, а не создаём второго.
        var candidates = await _db.Clients.AsNoTracking().Where(item => item.Phone != "").Select(item => new { item.Id, item.Name, item.Phone }).ToListAsync(ct);
        var duplicate = candidates.FirstOrDefault(item => Digits(item.Phone).EndsWith(digits[^10..], StringComparison.Ordinal));
        if (duplicate is not null)
        {
            return SalesRequestResult<Client>.Fail("Клиент с таким телефоном уже есть. Выберите его из списка.", 409, new { existingClientId = duplicate.Id, duplicate.Name });
        }

        var client = new Client { Name = name, Phone = phone, Email = input.NewClientEmail?.Trim() ?? string.Empty };
        _db.Clients.Add(client);
        await _db.SaveChangesAsync(ct);
        return new(client);
    }

    private async Task CreateRuleTasksAsync(SalesRequest request, int statusId, int userId, CancellationToken ct)
    {
        var rules = await _db.SalesRequestStatusTaskRules.AsNoTracking().Where(rule => rule.StatusId == statusId && rule.IsActive).OrderBy(rule => rule.Id).ToListAsync(ct);
        foreach (var rule in rules)
        {
            var due = DateTime.UtcNow.AddMinutes(rule.DueInMinutes);
            var task = await BuildTaskAsync(request, rule.AssigneeUserId ?? request.ResponsibleUserId, $"{rule.Title}, заявка №{request.Id}", due, userId, StatusTag(statusId), ct);
            if (task is null)
            {
                continue;
            }

            _db.TaskJournalItems.Add(task);
            AddEvent(request.Id, SalesRequestEventKinds.TaskCreated, null, statusId, userId, task.Title);
        }
    }

    private async Task<TaskJournalItem?> BuildTaskAsync(SalesRequest request, int assigneeUserId, string title, DateTime dueAt, int authorUserId, string? tag, CancellationToken ct)
    {
        var assignee = await _db.Users.AsNoTracking().Where(item => item.Id == assigneeUserId && item.IsActive).Select(item => new { item.Id, item.FullName }).FirstOrDefaultAsync(ct);
        if (assignee is null)
        {
            return null;
        }

        var author = await _db.Users.AsNoTracking().Where(item => item.Id == authorUserId).Select(item => item.FullName).FirstOrDefaultAsync(ct);
        return new TaskJournalItem
        {
            ObjectName = $"Заявка №{request.Id}",
            Title = title.Length > 500 ? title[..500] : title,
            SalesRequestId = request.Id,
            ClientId = request.ClientId,
            ClientCaseId = request.ClientCaseId,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            AuthorUserId = authorUserId == 0 ? null : authorUserId,
            AuthorName = author ?? string.Empty,
            DueAt = dueAt,
            TagsJson = tag is null ? "[]" : JsonSerializer.Serialize(new[] { tag }),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private async Task<List<TaskJournalItem>> LoadOpenTasksAsync(int requestId, CancellationToken ct)
        => await _db.TaskJournalItems.Where(task => task.SalesRequestId == requestId && !ClosedTaskStatuses.Contains(task.Status)).ToListAsync(ct);

    private async Task<SalesRequestResult<SalesRequest>> SaveAsync(SalesRequest request, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return new(request);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SalesRequestResult<SalesRequest>.Fail("Заявку одновременно изменил другой сотрудник. Обновите карточку.", 409);
        }
    }

    private void AddEvent(int requestId, string kind, int? fromStatusId, int? toStatusId, int? userId, string? comment)
        => _db.SalesRequestEvents.Add(new SalesRequestEvent
        {
            SalesRequestId = requestId,
            Kind = kind,
            FromStatusId = fromStatusId,
            ToStatusId = toStatusId,
            UserId = userId == 0 ? null : userId,
            Comment = comment,
            CreatedAt = DateTime.UtcNow,
        });

    private static string StatusTag(int statusId) => $"request-status:{statusId}";

    private static bool HasTag(TaskJournalItem task, string tag)
        => task.TagsJson.Contains($"\"{tag}\"", StringComparison.Ordinal);

    private static decimal Percent(int part, int total) => total == 0 ? 0m : decimal.Round(part * 100m / total, 1, MidpointRounding.AwayFromZero);

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}

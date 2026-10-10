using System.Text.Json;
using System.Security.Claims;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services;

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

public sealed record SalesRequestRuleInput(string? Title, int? AssigneeUserId, int DueInMinutes, decimal? AmountGreaterThan = null, string? Source = null, string? RequestType = null);

public sealed record OpenTaskInfo(int Id, string Title, int? AssigneeUserId, string Status);

public sealed record SalesRequestReport(
    int Total, int Succeeded, int Failed, int InProgress, decimal ConversionPercent, decimal WonAmount, decimal LostAmount,
    IReadOnlyList<SalesRequestSourceReport> BySource,
    IReadOnlyList<SalesRequestStageLoss> LossByStage);

public sealed record SalesRequestSourceReport(string Source, int Total, int Succeeded, decimal ConversionPercent);

public sealed record SalesRequestStageLoss(string Stage, int Count, decimal LostAmount);

public interface ISalesRequestService
{
    Task EnsureDefaultsAsync(CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> CreateAsync(SalesRequestCreateInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> UpdateAsync(int id, SalesRequestUpdateInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<SalesRequest>> TransitionAsync(int id, SalesRequestTransitionInput input, int userId, CancellationToken ct);
    Task<SalesRequestResult<TaskJournalItem>> CreateManualTaskAsync(int id, int assigneeUserId, string? title, DateTime? dueAt, int userId, CancellationToken ct);
    Task<SalesRequestResult<SalesRequestStatus>> SaveStatusAsync(int? id, string? title, string? finalKind, int? sortOrder, bool? isActive, CancellationToken ct, int? autoAdvanceToStatusId = null, bool clearAutoAdvance = false);
    Task<SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>> ReplaceRulesAsync(int statusId, IReadOnlyList<SalesRequestRuleInput> rules, CancellationToken ct);
    Task<int> AdvanceCompletedAsync(CancellationToken ct);
    Task<int> MarkSlaBreachesAsync(CancellationToken ct);
    Task<SalesRequestResult<SalesRequestStatus>> SetSlaAsync(int statusId, int? slaMinutes, CancellationToken ct);
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
    private readonly INotificationService? _notifications;
    private readonly ILogger<SalesRequestService>? _logger;
    private readonly List<(TaskJournalItem Task, int ActorUserId)> _pendingNotifications = [];

    public SalesRequestService(PaymentContext db, INotificationService? notifications = null, ILogger<SalesRequestService>? logger = null)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (await _db.SalesRequestStatuses.AnyAsync(ct))
        {
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(831031)", ct);
        if (await _db.SalesRequestStatuses.AnyAsync(ct)) return;

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

        // Цепочка из ТЗ: когда выполнены задачи подготовки документов, заявка сама уходит дальше.
        var byKey = await _db.SalesRequestStatuses.ToDictionaryAsync(item => item.Key, ct);
        byKey["documents-prep"].AutoAdvanceToStatusId = byKey["documents-sent"].Id;
        byKey["documents-sent"].AutoAdvanceToStatusId = byKey["awaiting-payment"].Id;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<SalesRequestResult<SalesRequest>> CreateAsync(SalesRequestCreateInput input, int userId, CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        if (string.IsNullOrWhiteSpace(input.Source) || !SalesRequestSources.All.Contains(input.Source))
        {
            return SalesRequestResult<SalesRequest>.Fail("Выберите источник: Звонок, Макс, Авито, Сайт или Другое.");
        }

        var summary = input.Summary?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > 4000)
        {
            return SalesRequestResult<SalesRequest>.Fail("Опишите суть запроса.");
        }

        if (input.Type is not null && !SalesRequestTypes.All.Contains(input.Type))
            return SalesRequestResult<SalesRequest>.Fail("Неизвестный тип заявки.");
        if (input.Amount is < 0 or > 1_000_000_000_000m)
            return SalesRequestResult<SalesRequest>.Fail("Сумма должна быть от 0 до 1 000 000 000 000.");
        if (!await _db.Users.AnyAsync(item => item.Id == (input.ResponsibleUserId ?? userId) && item.IsActive, ct))
            return SalesRequestResult<SalesRequest>.Fail("Ответственный не найден.", 404);
        if (!await CanViewRequestsAsync(input.ResponsibleUserId ?? userId, ct))
            return SalesRequestResult<SalesRequest>.Fail("У ответственного нет доступа к заявкам.", 400);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
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

        var first = await _db.SalesRequestStatuses.Where(item => item.IsActive && item.FinalKind == SalesRequestFinalKinds.None).OrderBy(item => item.SortOrder).ThenBy(item => item.Id).FirstAsync(ct);
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
        await transaction.CommitAsync(ct);
        await FlushNotificationsAsync(ct);
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
            if (summary.Length == 0 || summary.Length > 4000)
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
            if (input.Amount.Value < 0 || input.Amount.Value > 1_000_000_000_000m)
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

            if (!await CanViewRequestsAsync(input.ResponsibleUserId.Value, ct))
                return SalesRequestResult<SalesRequest>.Fail("У ответственного нет доступа к заявкам.", 400);

            request.ResponsibleUserId = input.ResponsibleUserId.Value;
        }

        request.Version++;
        request.UpdatedAt = DateTime.UtcNow;
        AddEvent(id, SalesRequestEventKinds.FieldsChanged, null, null, userId, null);
        return await SaveAsync(request, ct);
    }

    public async Task<SalesRequestResult<SalesRequest>> TransitionAsync(int id, SalesRequestTransitionInput input, int userId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (await _db.SalesRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), ct) != 1)
            return SalesRequestResult<SalesRequest>.Fail("Заявка не найдена.", 404);
        var tracked = _db.SalesRequests.Local.FirstOrDefault(item => item.Id == id);
        if (tracked is not null) await _db.Entry(tracked).ReloadAsync(ct);
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
            task.Status = "cancelled";
            task.Result = "Отменена в связи со сменой этапа заявки";
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
        // Дело создаётся только для обращения без привязки к делу и только один раз; ошибка сохранения откатывает и дело.
        if (target.FinalKind == SalesRequestFinalKinds.Success && request.ClientCaseId is null && request.ResultClientCaseId is null)
        {
            var title = request.Summary.Length > 200 ? request.Summary[..200] : request.Summary;
            var created = new ClientCase { ClientId = request.ClientId, Title = title, Status = ClientCaseStatus.Open, ResponsibleUserId = request.ResponsibleUserId };
            _db.ClientCases.Add(created);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return SalesRequestResult<SalesRequest>.Fail("Заявку одновременно изменил другой сотрудник. Обновите карточку.", 409);
            }

            request.ResultClientCaseId = created.Id;
            AddEvent(request.Id, SalesRequestEventKinds.CaseCreated, null, target.Id, userId, $"Создано дело №{created.Id}");
        }

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
        var saved = await SaveAsync(request, ct);
        if (saved.Ok)
        {
            await transaction.CommitAsync(ct);
            await FlushNotificationsAsync(ct);
        }
        else
        {
            _pendingNotifications.Clear();
        }

        return saved;
    }

    public async Task<SalesRequestResult<TaskJournalItem>> CreateManualTaskAsync(int id, int assigneeUserId, string? title, DateTime? dueAt, int userId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (await _db.SalesRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), ct) != 1)
            return SalesRequestResult<TaskJournalItem>.Fail("Заявка не найдена.", 404);
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
        await transaction.CommitAsync(ct);
        _pendingNotifications.Add((task, userId));
        await FlushNotificationsAsync(ct);
        return new(task);
    }

    public async Task<SalesRequestResult<SalesRequestStatus>> SaveStatusAsync(int? id, string? title, string? finalKind, int? sortOrder, bool? isActive, CancellationToken ct, int? autoAdvanceToStatusId = null, bool clearAutoAdvance = false)
    {
        await EnsureDefaultsAsync(ct);
        SalesRequestStatus status;
        if (id is int statusId)
        {
            var existing = await _db.SalesRequestStatuses.FirstOrDefaultAsync(item => item.Id == statusId, ct);
            if (existing is null)
            {
                return SalesRequestResult<SalesRequestStatus>.Fail("Статус не найден.", 404);
            }

            // Смысл финала уже вошёл в историю и отчёты, поэтому у существующего статуса он не меняется.
            if (finalKind is not null && finalKind != existing.FinalKind)
            {
                return SalesRequestResult<SalesRequestStatus>.Fail("Тип финала существующего статуса менять нельзя — создайте новый статус.", 409);
            }

            status = existing;
        }
        else
        {
            var kind = finalKind ?? SalesRequestFinalKinds.None;
            if (kind is not (SalesRequestFinalKinds.None or SalesRequestFinalKinds.Success or SalesRequestFinalKinds.Failure))
            {
                return SalesRequestResult<SalesRequestStatus>.Fail("Неизвестный тип финала.");
            }

            status = new SalesRequestStatus { Key = $"custom-{Guid.NewGuid():N}"[..20], FinalKind = kind, SortOrder = (await _db.SalesRequestStatuses.MaxAsync(item => (int?)item.SortOrder, ct) ?? 0) + 1 };
            _db.SalesRequestStatuses.Add(status);
        }

        if (title is not null)
        {
            var text = title.Trim();
            if (text.Length == 0 || text.Length > 100)
            {
                return SalesRequestResult<SalesRequestStatus>.Fail("Название статуса — от 1 до 100 символов.");
            }

            status.Title = text;
        }
        else if (id is null)
        {
            return SalesRequestResult<SalesRequestStatus>.Fail("Укажите название статуса.");
        }

        if (sortOrder.HasValue)
        {
            status.SortOrder = sortOrder.Value;
        }

        if (clearAutoAdvance)
        {
            status.AutoAdvanceToStatusId = null;
        }
        else if (autoAdvanceToStatusId.HasValue)
        {
            var target = await _db.SalesRequestStatuses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == autoAdvanceToStatusId.Value && item.IsActive, ct);
            if (target is null || target.Id == status.Id || status.FinalKind != SalesRequestFinalKinds.None)
            {
                return SalesRequestResult<SalesRequestStatus>.Fail("Автопереход возможен только из рабочего статуса в другой действующий статус.");
            }

            if (target.FinalKind == SalesRequestFinalKinds.Success)
                return SalesRequestResult<SalesRequestStatus>.Fail("Оплата требует указания фактического платежа и не выполняется автопереходом.");
            var transitions = await _db.SalesRequestStatuses.AsNoTracking().Where(item => item.IsActive)
                .ToDictionaryAsync(item => item.Id, item => item.AutoAdvanceToStatusId, ct);
            var seen = new HashSet<int> { status.Id };
            int? next = target.Id;
            while (next.HasValue)
            {
                if (!seen.Add(next.Value))
                    return SalesRequestResult<SalesRequestStatus>.Fail("Цепочка автопереходов не должна возвращаться в уже пройденный этап.");
                next = transitions.GetValueOrDefault(next.Value);
            }

            status.AutoAdvanceToStatusId = target.Id;
        }

        if (isActive.HasValue && isActive.Value != status.IsActive)
        {
            if (!isActive.Value)
            {
                if (await _db.SalesRequests.AnyAsync(item => item.StatusId == status.Id && item.FinalKind == SalesRequestFinalKinds.None, ct))
                {
                    return SalesRequestResult<SalesRequestStatus>.Fail("В этом статусе есть заявки в работе — сначала переведите их.", 409);
                }

                // В воронке всегда остаются начальный статус, успешный финал и провальный финал.
                var remaining = await _db.SalesRequestStatuses.Where(item => item.IsActive && item.Id != status.Id).Select(item => item.FinalKind).ToListAsync(ct);
                if (status.FinalKind != SalesRequestFinalKinds.None && !remaining.Contains(status.FinalKind))
                {
                    return SalesRequestResult<SalesRequestStatus>.Fail("Нельзя отключить последний финальный статус этого типа.", 409);
                }

                if (status.FinalKind == SalesRequestFinalKinds.None && !remaining.Contains(SalesRequestFinalKinds.None))
                {
                    return SalesRequestResult<SalesRequestStatus>.Fail("Нельзя отключить последний рабочий статус.", 409);
                }
            }

            status.IsActive = isActive.Value;
        }

        await _db.SaveChangesAsync(ct);
        return new(status);
    }

    public async Task<SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>> ReplaceRulesAsync(int statusId, IReadOnlyList<SalesRequestRuleInput> rules, CancellationToken ct)
    {
        if (!await _db.SalesRequestStatuses.AnyAsync(item => item.Id == statusId, ct))
        {
            return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Статус не найден.", 404);
        }

        if (rules.Count > 20)
        {
            return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Не больше 20 правил на статус.");
        }

        var assigneeIds = rules.Where(rule => rule.AssigneeUserId.HasValue).Select(rule => rule.AssigneeUserId!.Value).Distinct().ToList();
        var activeUsers = (await _db.Users.Where(user => assigneeIds.Contains(user.Id) && user.IsActive && user.IsApproved
                && user.Role != null && (user.Role.Name.ToLower() == "admin"
                    || user.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Requests && permission.CanView)))
            .Select(user => user.Id).ToListAsync(ct)).ToHashSet();
        var prepared = new List<SalesRequestStatusTaskRule>();
        foreach (var rule in rules)
        {
            var text = rule.Title?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 300)
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Текст задачи — от 1 до 300 символов.");
            }

            if (rule.DueInMinutes is < 1 or > 60 * 24 * 365)
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Срок задачи — от 1 минуты до года.");
            }

            if (rule.AssigneeUserId.HasValue && !activeUsers.Contains(rule.AssigneeUserId.Value))
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Исполнитель правила не найден.", 404);
            }

            if (rule.AmountGreaterThan is < 0)
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Порог суммы не может быть отрицательным.");
            }

            if (!string.IsNullOrEmpty(rule.Source) && !SalesRequestSources.All.Contains(rule.Source))
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Неизвестный источник в условии.");
            }

            if (!string.IsNullOrEmpty(rule.RequestType) && !SalesRequestTypes.All.Contains(rule.RequestType))
            {
                return SalesRequestResult<IReadOnlyList<SalesRequestStatusTaskRule>>.Fail("Неизвестный тип заявки в условии.");
            }

            prepared.Add(new SalesRequestStatusTaskRule
            {
                StatusId = statusId,
                Title = text,
                AssigneeUserId = rule.AssigneeUserId,
                DueInMinutes = rule.DueInMinutes,
                ConditionAmountGreaterThan = rule.AmountGreaterThan,
                ConditionSource = string.IsNullOrEmpty(rule.Source) ? null : rule.Source,
                ConditionType = string.IsNullOrEmpty(rule.RequestType) ? null : rule.RequestType,
            });
        }

        // Правила применяются только к будущим переходам; уже созданные задачи заявок не меняются.
        _db.SalesRequestStatusTaskRules.RemoveRange(_db.SalesRequestStatusTaskRules.Where(rule => rule.StatusId == statusId));
        _db.SalesRequestStatusTaskRules.AddRange(prepared);
        await _db.SaveChangesAsync(ct);
        return new(prepared);
    }

    private static bool RuleApplies(SalesRequestStatusTaskRule rule, SalesRequest request)
        => (rule.ConditionAmountGreaterThan is not decimal threshold || (request.Amount is decimal amount && amount > threshold))
            && (string.IsNullOrEmpty(rule.ConditionSource) || rule.ConditionSource == request.Source)
            && (string.IsNullOrEmpty(rule.ConditionType) || rule.ConditionType == request.Type);

    public async Task<SalesRequestResult<SalesRequestStatus>> SetSlaAsync(int statusId, int? slaMinutes, CancellationToken ct)
    {
        var status = await _db.SalesRequestStatuses.FirstOrDefaultAsync(item => item.Id == statusId, ct);
        if (status is null)
        {
            return SalesRequestResult<SalesRequestStatus>.Fail("Статус не найден.", 404);
        }

        if (slaMinutes is < 1 or > 60 * 24 * 365 || (slaMinutes.HasValue && status.FinalKind != SalesRequestFinalKinds.None))
        {
            return SalesRequestResult<SalesRequestStatus>.Fail("SLA задаётся для рабочего статуса, от 1 минуты до года.");
        }

        status.SlaMinutes = slaMinutes;
        await _db.SaveChangesAsync(ct);
        return new(status);
    }

    public async Task<int> MarkSlaBreachesAsync(CancellationToken ct)
    {
        var limits = await _db.SalesRequestStatuses.AsNoTracking().Where(item => item.IsActive && item.SlaMinutes != null)
            .ToDictionaryAsync(item => item.Id, item => item.SlaMinutes!.Value, ct);
        if (limits.Count == 0) return 0;
        var ids = limits.Keys.ToList();
        var now = DateTime.UtcNow;
        var lastId = 0;
        var marked = 0;
        while (true)
        {
            var batch = await _db.SalesRequests.AsNoTracking()
                .Where(item => item.Id > lastId && item.FinalKind == SalesRequestFinalKinds.None && ids.Contains(item.StatusId)
                    && !_db.SalesRequestEvents.Any(e => e.SalesRequestId == item.Id && e.Kind == SalesRequestEventKinds.SlaBreached
                        && e.ToStatusId == item.StatusId && e.CreatedAt >= item.StatusEnteredAt))
                .OrderBy(item => item.Id).Select(item => new { item.Id, item.StatusId, item.StatusEnteredAt }).Take(500).ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;
            var due = batch.Where(item => item.StatusEnteredAt.AddMinutes(limits[item.StatusId]) <= now).ToList();
            foreach (var item in due)
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                if (await _db.SalesRequests.Where(r => r.Id == item.Id && r.StatusId == item.StatusId
                    && r.StatusEnteredAt == item.StatusEnteredAt && r.FinalKind == SalesRequestFinalKinds.None)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Version, r => r.Version), ct) != 1) continue;
                if (await _db.SalesRequestEvents.AnyAsync(e => e.SalesRequestId == item.Id && e.Kind == SalesRequestEventKinds.SlaBreached
                    && e.ToStatusId == item.StatusId && e.CreatedAt >= item.StatusEnteredAt, ct)) continue;
                AddEvent(item.Id, SalesRequestEventKinds.SlaBreached, null, item.StatusId, null, $"Превышен срок этапа ({limits[item.StatusId]} мин)");
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                marked++;
                foreach (var e in _db.ChangeTracker.Entries<SalesRequestEvent>().Where(e => e.Entity.SalesRequestId == item.Id).ToList())
                    e.State = EntityState.Detached;
            }
        }
        return marked;
    }

    public async Task<int> AdvanceCompletedAsync(CancellationToken ct)
    {
        var advancing = await _db.SalesRequestStatuses.AsNoTracking()
            .Where(item => item.IsActive && item.AutoAdvanceToStatusId != null)
            .ToDictionaryAsync(item => item.Id, item => item.AutoAdvanceToStatusId!.Value, ct);
        if (advancing.Count == 0) return 0;
        var statusIds = advancing.Keys.ToList();
        var lastId = 0;
        var moved = 0;
        while (true)
        {
            var candidates = await _db.SalesRequests.AsNoTracking()
                .Where(item => item.Id > lastId && item.FinalKind == SalesRequestFinalKinds.None && statusIds.Contains(item.StatusId))
                .OrderBy(item => item.Id).Select(item => new { item.Id, item.StatusId, item.Version }).Take(200).ToListAsync(ct);
            if (candidates.Count == 0) break;
            lastId = candidates[^1].Id;
            var requestIds = candidates.Select(item => item.Id).ToList();
            var tasks = (await _db.TaskJournalItems.AsNoTracking().Where(task => task.SalesRequestId != null && requestIds.Contains(task.SalesRequestId.Value))
                .Select(task => new { task.SalesRequestId, task.TagsJson, task.Status }).ToListAsync(ct)).ToLookup(task => task.SalesRequestId!.Value);
            foreach (var candidate in candidates)
            {
                var tag = $"\"{StatusTag(candidate.StatusId)}\"";
                var stageTasks = tasks[candidate.Id].Where(task => task.TagsJson.Contains(tag, StringComparison.Ordinal)).ToList();
                if (stageTasks.Count == 0 || stageTasks.Any(task => !ClosedTaskStatuses.Contains(task.Status)) || !stageTasks.Any(task => task.Status == "done")) continue;
                var result = await TransitionAsync(candidate.Id, new SalesRequestTransitionInput(advancing[candidate.StatusId], candidate.Version, null, false,
                    "Автоматически: задачи этапа выполнены"), 0, ct);
                if (result.Ok) moved++;
                _db.ChangeTracker.Clear();
            }
        }
        return moved;
    }

    public async Task<SalesRequestReport> BuildReportAsync(DateTime from, DateTime to, IReadOnlyCollection<int>? visibleResponsibleUserIds, CancellationToken ct)
    {
        var end = to.Date.AddDays(1);
        var query = _db.SalesRequests.AsNoTracking().Where(item => item.CreatedAt >= from.Date && item.CreatedAt < end);
        if (visibleResponsibleUserIds is not null)
        {
            query = query.Where(item => visibleResponsibleUserIds.Contains(item.ResponsibleUserId) || visibleResponsibleUserIds.Contains(item.CreatedByUserId));
        }

        var rows = await query.Select(item => new { item.Id, item.Source, item.FinalKind, item.Amount }).ToListAsync(ct);
        var succeeded = rows.Count(item => item.FinalKind == SalesRequestFinalKinds.Success);
        var failed = rows.Count(item => item.FinalKind == SalesRequestFinalKinds.Failure);
        var failedIds = rows.Where(item => item.FinalKind == SalesRequestFinalKinds.Failure).Select(item => item.Id).ToList();
        var stageByRequest = failedIds.Count == 0
            ? new Dictionary<int, int?>()
            : (await _db.SalesRequestEvents.AsNoTracking()
                .Where(e => failedIds.Contains(e.SalesRequestId) && e.Kind == SalesRequestEventKinds.StatusChanged)
                .OrderBy(e => e.Id)
                .Select(e => new { e.SalesRequestId, e.FromStatusId })
                .ToListAsync(ct))
                .GroupBy(e => e.SalesRequestId)
                .ToDictionary(group => group.Key, group => group.Last().FromStatusId);
        var titles = await _db.SalesRequestStatuses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Title, ct);
        var loss = rows.Where(item => item.FinalKind == SalesRequestFinalKinds.Failure)
            .GroupBy(item => stageByRequest.GetValueOrDefault(item.Id) is int stageId && titles.TryGetValue(stageId, out var title) ? title : "Не определён")
            .OrderByDescending(group => group.Count())
            .Select(group => new SalesRequestStageLoss(group.Key, group.Count(), group.Sum(item => item.Amount ?? 0m)))
            .ToList();
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
                .ToList(),
            loss);
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

        // The caller owns a write transaction. SQLite serializes its writers; PostgreSQL needs a phone-scoped lock.
        var suffix = digits[^10..];
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(831032, hashtext({suffix}))", ct);
        var pattern = "%" + string.Join('%', suffix.ToCharArray()) + "%";
        var candidates = _db.Clients.AsNoTracking().Where(item => EF.Functions.Like(item.Phone, pattern)).Select(item => new { item.Id, item.Name, item.Phone });
        await foreach (var duplicate in candidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (Digits(duplicate.Phone).EndsWith(suffix, StringComparison.Ordinal))
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
        foreach (var rule in rules.Where(item => RuleApplies(item, request)))
        {
            var due = DateTime.UtcNow.AddMinutes(rule.DueInMinutes);
            var task = await BuildTaskAsync(request, rule.AssigneeUserId ?? request.ResponsibleUserId, $"{rule.Title}, заявка №{request.Id}", due, userId, StatusTag(statusId), ct);
            if (task is null)
            {
                continue;
            }

            _db.TaskJournalItems.Add(task);
            _pendingNotifications.Add((task, userId));
            AddEvent(request.Id, SalesRequestEventKinds.TaskCreated, null, statusId, userId, task.Title);
        }
    }

    private async Task<TaskJournalItem?> BuildTaskAsync(SalesRequest request, int assigneeUserId, string title, DateTime dueAt, int authorUserId, string? tag, CancellationToken ct)
    {
        if (!await _db.Users.WithSalesRequestAccess(request.Id, _db).AnyAsync(user => user.Id == assigneeUserId, ct)) return null;
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
            ClientCaseId = request.ResultClientCaseId ?? request.ClientCaseId,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            AuthorUserId = authorUserId == 0 ? null : authorUserId,
            AuthorName = author ?? string.Empty,
            DueAt = dueAt,
            TagsJson = tag is null ? "[]" : JsonSerializer.Serialize(new[] { tag }),
            ChannelsJson = JsonSerializer.Serialize(new[] { NotificationChannels.InApp }),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private async Task FlushNotificationsAsync(CancellationToken ct)
    {
        var pending = _pendingNotifications.ToList();
        _pendingNotifications.Clear();
        if (_notifications is null)
        {
            return;
        }

        foreach (var (task, actorUserId) in pending)
        {
            try
            {
                // Уведомление не должно ломать воронку: задача уже создана, сбой доставки только пишется в журнал.
                await _notifications.NotifyTaskAssignedAsync(task, "Заявки", actorUserId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Не удалось уведомить об автозадаче заявки {TaskId}.", task.Id);
            }
        }
    }

    private async Task<bool> CanViewRequestsAsync(int userId, CancellationToken ct)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))], "request-assignment"));
        var access = await _db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Requests, ct, requireApprovedUser: true);
        return access is not null && (access.Visibility.IsAdmin || access.Permission?.CanView == true);
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

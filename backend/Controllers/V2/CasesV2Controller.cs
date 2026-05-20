using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/cases")]
[Authorize]
public class CasesV2Controller : ControllerBase
{
    private const long MaxCaseDocumentBytes = 25 * 1024 * 1024;
    private const int MaxRegistryPageSize = 200;
    private const int MaxRegistryExportRows = 5000;
    private const string RelatedPartySourceClient = "client";
    private const string RelatedPartySourceExternalContact = "externalContact";
    private const string RelatedPartySourceLegalEntity = "legalEntity";
    private const string CaseConversationPrefix = "Внутреннее сообщение:";
    private const string CaseHistoryCommentPrefix = "Комментарий по делу:";
    private const string CaseHistoryAuthorMarker = "Автор:";
    private const string CaseDocumentTemplatesSettingKey = "cases.documentTemplates";
    private static readonly JsonSerializerOptions CaseDocumentTemplateJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions UserNotificationJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedCasePriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "normal",
        "high",
        "urgent",
    };
    private static readonly CaseDocumentTemplateDefinition[] CaseDocumentTemplateDefinitions =
    [
        new("case-summary", "Справка по делу", "Карточка дела", "Справка по делу"),
        new("claim", "Исковое заявление", "Иск", "Исковое заявление"),
        new("motion", "Ходатайство", "Ходатайство", "Ходатайство"),
        new("objection", "Возражение", "Возражение", "Возражение"),
        new("court-letter", "Письмо в суд", "Переписка", "Письмо в суд"),
    ];
    private static readonly string[] DefaultCaseDocumentTypes =
    [
        "Иск",
        "Ходатайство",
        "Жалоба",
        "Возражение",
        "Судебный акт",
        "Доверенность",
        "Переписка",
        "Доказательство",
        "Договорной/финансовый документ",
        "Внутренний служебный файл",
    ];

    private readonly PaymentContext _db;
    private readonly ICaseDocumentStorage _caseDocumentStorage;

    public CasesV2Controller(
        PaymentContext db,
        IWebHostEnvironment? environment = null,
        ICaseDocumentStorage? caseDocumentStorage = null)
    {
        _db = db;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment);
    }

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<ActionResult?> RequireCasesPermissionAsync(
        Func<RolePermission, bool> predicate,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var isAdmin = User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct);
        if (isAdmin)
        {
            return null;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Cases, ct);
        return permission is not null && predicate(permission) ? null : Forbid();
    }

    private async Task<ActionResult?> RequireDocumentTemplateEditPermissionAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var isAdmin = User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct);
        if (isAdmin)
        {
            return null;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Dictionaries, ct);
        return permission is not null && permission.CanEdit ? null : Forbid();
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxRegistryPageSize);

        var q = BuildRegistryQuery(
            visibility,
            clientId,
            legalEntityId,
            status,
            search,
            responsibleUserId,
            caseType,
            priority,
            from,
            to,
            minFee,
            maxFee,
            sortBy,
            sortDir,
            scope,
            activity,
            inactiveDays);

        var total = await q.CountAsync(ct);
        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportRegistryCsv(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await BuildRegistryQuery(
                visibility,
                clientId,
                legalEntityId,
                status,
                search,
                responsibleUserId,
                caseType,
                priority,
                from,
                to,
                minFee,
                maxFee,
                sortBy,
                sortDir,
                scope,
                activity,
                inactiveDays)
            .Take(MaxRegistryExportRows)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        var bytes = BuildRegistryCsv(items);
        var fileName = $"cases-registry-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    [HttpGet("print")]
    public async Task<IActionResult> PrintRegistry(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] string? priority,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] decimal? minFee,
        [FromQuery] decimal? maxFee,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? scope = null,
        [FromQuery] string? activity = null,
        [FromQuery] int? inactiveDays = null,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanExport, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await BuildRegistryQuery(
                visibility,
                clientId,
                legalEntityId,
                status,
                search,
                responsibleUserId,
                caseType,
                priority,
                from,
                to,
                minFee,
                maxFee,
                sortBy,
                sortDir,
                scope,
                activity,
                inactiveDays)
            .Take(MaxRegistryExportRows)
            .AsNoTracking()
            .ToListAsync(ct);
        await ApplyFinanceSummaryAsync(items, ct);
        await ApplyTaskSummaryAsync(items, ct);

        return Content(BuildRegistryPrintHtml(items), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private IQueryable<ClientCase> BuildRegistryQuery(
        DataVisibilityAccess visibility,
        int? clientId,
        int? legalEntityId,
        ClientCaseStatus? status,
        string? search,
        int? responsibleUserId,
        string? caseType,
        string? priority,
        DateTime? from,
        DateTime? to,
        decimal? minFee,
        decimal? maxFee,
        string? sortBy,
        string? sortDir,
        string? scope,
        string? activity,
        int? inactiveDays)
        => _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseRegistryScope(scope, visibility.UserId)
            .ApplyCaseFilters(clientId, legalEntityId, status, search, responsibleUserId, caseType, priority, from, to, minFee, maxFee)
            .ApplyCaseActivityFilter(activity, inactiveDays, _db.TaskJournalItems.IgnoreQueryFilters())
            .ApplyCaseSort(sortBy, sortDir);

    private static IQueryable<ClientCase> ApplyClientCaseAccessManagementVisibility(
        IQueryable<ClientCase> query,
        DataVisibilityAccess visibility)
    {
        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleUserIds = visibility.VisibleUserIds.ToArray();
        return query.Where(clientCase =>
            (clientCase.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.ResponsibleUserId.Value))
            || (clientCase.Client != null
                && clientCase.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.Client.ResponsibleUserId.Value))
            || (clientCase.LegalEntity != null
                && clientCase.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.LegalEntity.ResponsibleUserId.Value)));
    }

    private async Task<IReadOnlyList<int>> ResolveCaseAccessManagerUserIdsAsync(
        ClientCase clientCase,
        int requesterUserId,
        CancellationToken ct)
    {
        var targetUserIds = new HashSet<int>();
        AddIfPositive(targetUserIds, clientCase.ResponsibleUserId);
        AddIfPositive(targetUserIds, clientCase.Client?.ResponsibleUserId);
        AddIfPositive(targetUserIds, clientCase.LegalEntity?.ResponsibleUserId);

        var targetUsers = await _db.Users
            .AsNoTracking()
            .Where(item => targetUserIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.DepartmentId,
                item.Department,
            })
            .ToListAsync(ct);

        var candidates = await _db.Users
            .AsNoTracking()
            .Where(item => item.IsActive && item.IsApproved)
            .Select(item => new
            {
                item.Id,
                item.DepartmentId,
                item.Department,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
                Permission = item.Role == null
                    ? null
                    : item.Role.Permissions
                        .Where(permission => permission.Section == RolePermissionCatalog.Cases)
                        .Select(permission => new
                        {
                            permission.CanView,
                            permission.CanEdit,
                            permission.ViewScope,
                        })
                        .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return candidates
            .Where(candidate => candidate.Id != requesterUserId)
            .Where(candidate =>
            {
                var isAdmin = string.Equals(candidate.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
                if (!isAdmin && candidate.Permission is not { CanView: true, CanEdit: true })
                {
                    return false;
                }

                var scope = isAdmin
                    ? DataVisibilityScope.All
                    : DataVisibilityScope.Normalize(candidate.Permission?.ViewScope, DataVisibilityScope.Own);

                if (string.Equals(scope, DataVisibilityScope.All, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(scope, DataVisibilityScope.Own, StringComparison.OrdinalIgnoreCase))
                {
                    return targetUserIds.Contains(candidate.Id);
                }

                if (!string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var candidateDepartment = candidate.Department?.Trim();
                return targetUsers.Any(target =>
                    (candidate.DepartmentId.HasValue
                        && target.DepartmentId.HasValue
                        && candidate.DepartmentId.Value == target.DepartmentId.Value)
                    || (!string.IsNullOrWhiteSpace(candidateDepartment)
                        && !string.IsNullOrWhiteSpace(target.Department)
                        && string.Equals(candidateDepartment, target.Department.Trim(), StringComparison.Ordinal)));
            })
            .Select(candidate => candidate.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    private async Task AddCaseUserNotificationAsync(
        int recipientUserId,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        ClientCase clientCase,
        object? metadata,
        CancellationToken ct)
    {
        if (recipientUserId <= 0 || string.IsNullOrWhiteSpace(eventKey))
        {
            return;
        }

        if (!await IsCaseUserNotificationTypeEnabledAsync(recipientUserId, type, ct))
        {
            return;
        }

        var alreadyExists = await _db.UserNotifications
            .AsNoTracking()
            .AnyAsync(item => item.RecipientUserId == recipientUserId && item.EventKey == eventKey, ct);
        if (alreadyExists)
        {
            return;
        }

        var recipientExists = await _db.Users
            .AsNoTracking()
            .AnyAsync(item => item.Id == recipientUserId && item.IsActive, ct);
        if (!recipientExists)
        {
            return;
        }

        _db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = recipientUserId,
            Type = type,
            EventKey = eventKey,
            Title = Truncate(title.Trim(), 300),
            BodyPreview = Truncate(body.Trim(), 1000),
            Tone = string.IsNullOrWhiteSpace(tone) ? "blue" : tone.Trim(),
            ObjectType = "case",
            ObjectId = clientCase.Id,
            MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, UserNotificationJsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task<bool> IsCaseUserNotificationTypeEnabledAsync(int recipientUserId, string type, CancellationToken ct)
    {
        var setting = await _db.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.UserId == recipientUserId
                && item.Key == UserNotificationPreferenceSettings.SettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);

        return UserNotificationPreferenceSettings.IsTypeEnabled(setting, type);
    }

    private static void AddIfPositive(ISet<int> values, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            values.Add(value.Value);
        }
    }

    private static string BuildCaseNotificationTitle(ClientCase clientCase)
    {
        if (!string.IsNullOrWhiteSpace(clientCase.CaseNumber))
        {
            return $"№{clientCase.CaseNumber} · {clientCase.Title}";
        }

        return string.IsNullOrWhiteSpace(clientCase.Title)
            ? $"Дело #{clientCase.Id}"
            : clientCase.Title;
    }

    private static string BuildCaseOwnerName(ClientCase clientCase)
        => clientCase.Client?.Name
            ?? clientCase.LegalEntity?.ShortName
            ?? clientCase.LegalEntity?.FullName
            ?? "Контрагент не указан";

    private static string BuildCaseAccessRequestNotificationBody(
        ClientCase clientCase,
        string actorName,
        string? reason)
    {
        var builder = new StringBuilder()
            .Append("Пользователь ")
            .Append(actorName)
            .Append(" запросил доступ к скрытому делу.")
            .AppendLine()
            .Append("Контрагент: ")
            .Append(BuildCaseOwnerName(clientCase))
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase));

        if (!string.IsNullOrWhiteSpace(reason))
        {
            builder
                .AppendLine()
                .Append("Причина: ")
                .Append(reason);
        }

        return builder.ToString();
    }

    private static string BuildCaseAccessResolutionNotificationBody(
        ClientCase clientCase,
        bool granted,
        string actorName,
        string? comment)
    {
        var builder = new StringBuilder()
            .Append(actorName)
            .Append(granted ? " разрешил доступ к делу." : " отклонил запрос доступа к делу.")
            .AppendLine()
            .Append("Контрагент: ")
            .Append(BuildCaseOwnerName(clientCase))
            .AppendLine()
            .Append("Дело: ")
            .Append(BuildCaseNotificationTitle(clientCase));

        if (!string.IsNullOrWhiteSpace(comment))
        {
            builder
                .AppendLine()
                .Append("Комментарий: ")
                .Append(comment);
        }

        return builder.ToString();
    }

    private static byte[] BuildRegistryCsv(IReadOnlyCollection<ClientCase> items)
    {
        var header = new[]
        {
            "№ дела",
            "Название",
            "Клиент/юр. лицо",
            "Ответственный",
            "Статус",
            "Тип",
            "Этап",
            "Создано",
            "Начало",
            "Срок",
            "Открытые задачи",
            "Просроченные задачи",
            "Выполненные задачи",
            "Прогресс",
            "Последняя активность",
            "Гонорар",
            "Счета",
            "Оплачено",
            "Долг",
            "Последний просмотр",
            "Активность подтвердил",
            "Приоритет",
            "Суд",
            "№ в суде",
            "Заседание",
        };

        var lines = new List<string>
        {
            string.Join(';', header.Select(CsvCell)),
        };

        foreach (var item in items)
        {
            lines.Add(string.Join(';', new[]
            {
                item.CaseNumber,
                item.Title,
                GetOwnerName(item),
                GetResponsibleName(item),
                GetCaseStatusLabel(item.Status),
                item.CaseType,
                item.Stage,
                FormatRuDate(item.CreatedAt),
                FormatRuDate(item.StartDate),
                FormatRuDate(item.DueDate),
                item.OpenTaskCount.ToString(CultureInfo.InvariantCulture),
                item.OverdueTaskCount.ToString(CultureInfo.InvariantCulture),
                item.CompletedTaskCount.ToString(CultureInfo.InvariantCulture),
                $"{item.TaskProgressPercent}%",
                FormatRuDateTime(GetLatestCaseActivityAt(item)),
                FormatMoney(item.FeeAmount),
                FormatMoney(item.IncomeTotal),
                FormatMoney(item.IncomePaid),
                FormatMoney(item.IncomeOutstanding),
                FormatRuDateTime(item.LastViewedAt),
                item.ActivityConfirmedByUserName,
                GetPriorityLabel(item.Priority),
                item.CourtName,
                item.CourtCaseNumber,
                FormatRuDateTime(item.NextHearingAt),
            }.Select(CsvCell)));
        }

        var csv = string.Join('\n', lines);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string BuildRegistryPrintHtml(IReadOnlyCollection<ClientCase> items)
    {
        var generatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var totalFee = items.Sum(item => item.FeeAmount);
        var totalIncome = items.Sum(item => item.IncomeTotal);
        var totalPaid = items.Sum(item => item.IncomePaid);
        var totalDebt = items.Sum(item => item.IncomeOutstanding);
        var rows = string.Join('\n', items.Select(item => $"""
            <tr>
              <td>{Html(item.CaseNumber)}</td>
              <td>
                <strong>{Html(item.Title)}</strong>
                <div class="muted">{Html(GetOwnerName(item))}</div>
              </td>
              <td>{Html(GetResponsibleName(item))}</td>
              <td>{Html(GetCaseStatusLabel(item.Status))}</td>
              <td>{Html(item.CaseType)}</td>
              <td>{Html(item.Stage)}</td>
              <td>{Html(FormatRuDate(item.DueDate))}</td>
              <td class="num">{Html(FormatMoney(item.FeeAmount))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeTotal))}</td>
              <td class="num">{Html(FormatMoney(item.IncomePaid))}</td>
              <td class="num">{Html(FormatMoney(item.IncomeOutstanding))}</td>
              <td>{item.OpenTaskCount} / {item.OverdueTaskCount}</td>
              <td>{Html(FormatRuDateTime(GetLatestCaseActivityAt(item)))}</td>
            </tr>
            """));

        return $$"""
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <title>Реестр дел</title>
              <style>
                body { font-family: Arial, sans-serif; color: #0f172a; margin: 24px; }
                h1 { margin: 0 0 6px; font-size: 24px; }
                .meta { color: #64748b; margin-bottom: 18px; font-size: 12px; }
                .summary { display: grid; grid-template-columns: repeat(5, minmax(120px, 1fr)); gap: 8px; margin-bottom: 18px; }
                .summary div { border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px; }
                .summary span { display: block; color: #64748b; font-size: 11px; text-transform: uppercase; }
                .summary strong { font-size: 15px; }
                table { border-collapse: collapse; width: 100%; font-size: 11px; }
                th, td { border: 1px solid #cbd5e1; padding: 6px 7px; vertical-align: top; }
                th { background: #f1f5f9; text-align: left; }
                .muted { color: #64748b; margin-top: 3px; }
                .num { white-space: nowrap; text-align: right; }
                @media print {
                  body { margin: 10mm; }
                  .summary { grid-template-columns: repeat(5, 1fr); }
                }
              </style>
            </head>
            <body>
              <h1>Реестр дел</h1>
              <div class="meta">Сформировано: {{Html(generatedAt)}}. В печатную форму попали первые {{MaxRegistryExportRows}} строк текущей выборки.</div>
              <section class="summary">
                <div><span>Дел</span><strong>{{items.Count}}</strong></div>
                <div><span>Гонорар</span><strong>{{Html(FormatMoney(totalFee))}}</strong></div>
                <div><span>Счета</span><strong>{{Html(FormatMoney(totalIncome))}}</strong></div>
                <div><span>Оплачено</span><strong>{{Html(FormatMoney(totalPaid))}}</strong></div>
                <div><span>Долг</span><strong>{{Html(FormatMoney(totalDebt))}}</strong></div>
              </section>
              <table>
                <thead>
                  <tr>
                    <th>№ дела</th>
                    <th>Название / клиент</th>
                    <th>Ответственный</th>
                    <th>Статус</th>
                    <th>Тип</th>
                    <th>Этап</th>
                    <th>Срок</th>
                    <th>Гонорар</th>
                    <th>Счета</th>
                    <th>Оплачено</th>
                    <th>Долг</th>
                    <th>Задачи</th>
                    <th>Активность</th>
                  </tr>
                </thead>
                <tbody>
                  {{rows}}
                </tbody>
              </table>
            </body>
            </html>
            """;
    }

    private static string CsvCell(string? value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string GetOwnerName(ClientCase clientCase)
        => FirstNonEmpty(clientCase.Client?.Name, clientCase.LegalEntity?.ShortName, clientCase.LegalEntity?.FullName, "—");

    private static string GetResponsibleName(ClientCase clientCase)
        => FirstNonEmpty(clientCase.ResponsibleUser?.FullName, "Ответственный владельца");

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatRuDate(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string FormatRuDateTime(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
            : string.Empty;

    private static string FormatMoney(decimal value)
        => value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";

    private static DateTime? GetLatestCaseActivityAt(ClientCase clientCase)
    {
        var values = new[]
        {
            clientCase.LastTaskAt,
            clientCase.ActivityConfirmedAt,
            clientCase.LastViewedAt,
        }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        return values.Length == 0 ? null : values.Max();
    }

    private static string GetCaseStatusLabel(ClientCaseStatus status)
        => status switch
        {
            ClientCaseStatus.Open => "В работе",
            ClientCaseStatus.OnHold => "Приостановлено",
            ClientCaseStatus.Closed => "Закрыто",
            ClientCaseStatus.Lost => "Проиграно",
            _ => status.ToString(),
        };

    private static string GetPriorityLabel(string? priority)
        => priority?.Trim().ToLowerInvariant() switch
        {
            "low" => "Низкий",
            "normal" => "Обычный",
            "high" => "Высокий",
            "urgent" => "Критический",
            _ => priority ?? string.Empty,
        };

    [HttpGet("document-templates")]
    [HttpGet("/api/document-templates")]
    public async Task<ActionResult<IReadOnlyList<CaseDocumentTemplateDefinitionDto>>> GetDocumentTemplates(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct))
            .Select(MapDocumentTemplateDefinition)
            .ToList();

        return Ok(templates);
    }

    [HttpPost("document-templates")]
    [HttpPost("/api/document-templates")]
    public async Task<ActionResult<CaseDocumentTemplateDefinitionDto>> CreateDocumentTemplate(
        [FromBody] UpsertCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var validation = BuildCaseDocumentTemplateDefinition(request, existing: null);
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct)).ToList();
        if (templates.Any(item => string.Equals(item.Key, validation.Template.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { message = "Шаблон с таким кодом уже существует." });
        }

        templates.Add(validation.Template);
        await SaveCaseDocumentTemplateLibraryAsync(templates, ct);

        return Created($"/api/document-templates/{validation.Template.Key}", MapDocumentTemplateDefinition(validation.Template));
    }

    [HttpPatch("document-templates/{templateKey}")]
    [HttpPatch("/api/document-templates/{templateKey}")]
    public async Task<ActionResult<CaseDocumentTemplateDefinitionDto>> UpdateDocumentTemplate(
        string templateKey,
        [FromBody] UpsertCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireDocumentTemplateEditPermissionAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var normalizedKey = NormalizeDocumentTemplateCode(templateKey);
        if (normalizedKey is null)
        {
            return BadRequest(new { message = "Некорректный код шаблона." });
        }

        var templates = (await GetCaseDocumentTemplateLibraryAsync(ct)).ToList();
        var index = templates.FindIndex(item => string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return NotFound();
        }

        var validation = BuildCaseDocumentTemplateDefinition(request, templates[index] with { Key = normalizedKey });
        if (validation.Error is not null)
        {
            return BadRequest(new { message = validation.Error });
        }

        templates[index] = validation.Template;
        await SaveCaseDocumentTemplateLibraryAsync(templates, ct);

        return Ok(MapDocumentTemplateDefinition(validation.Template));
    }

    [HttpPost("document-templates/{templateKey}/render")]
    [HttpPost("/api/document-templates/{templateKey}/render")]
    public async Task<ActionResult<RenderedCaseDocumentTemplateDto>> RenderDocumentTemplate(
        string templateKey,
        [FromBody] RenderCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (request is null || request.ClientCaseId <= 0)
        {
            return BadRequest(new { message = "Укажите дело для автозаполнения шаблона." });
        }

        var normalizedKey = NormalizeDocumentTemplateCode(templateKey);
        if (normalizedKey is null)
        {
            return BadRequest(new { message = "Некорректный код шаблона." });
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var templates = await GetCaseDocumentTemplateLibraryAsync(ct);
        var template = templates.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(template.Key))
        {
            return NotFound();
        }

        var documentType = NormalizeOptional(request.DocumentType) ?? template.DocumentType;
        var caseNumber = !string.IsNullOrWhiteSpace(clientCase.CaseNumber)
            ? clientCase.CaseNumber.Trim()
            : clientCase.Id.ToString();

        return Ok(new RenderedCaseDocumentTemplateDto
        {
            TemplateKey = template.Key,
            TemplateLabel = template.Title,
            DocumentType = documentType,
            FileName = $"{template.Title} - дело {caseNumber}.html",
            Html = BuildCaseDocumentTemplateHtml(clientCase, template, documentType),
        });
    }

    [HttpGet("document-types")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetDocumentTypes(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var storedTypes = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Select(item => item.DocumentType)
            .Where(value => value != "")
            .Distinct()
            .ToListAsync(ct);

        var comparer = StringComparer.OrdinalIgnoreCase;
        var templateTypes = (await GetCaseDocumentTemplateLibraryAsync(ct))
            .Where(static item => item.IsActive)
            .Select(item => item.DocumentType);
        var result = DefaultCaseDocumentTypes
            .Concat(templateTypes)
            .Concat(storedTypes)
            .Select(NormalizeOptional)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(comparer)
            .OrderBy(value => value, StringComparer.Create(new CultureInfo("ru-RU"), ignoreCase: true))
            .ToList();

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var entity = await _db.ClientCases
            .WithCaseIncludes(includePayments: true)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null)
        {
            await ApplyFinanceSummaryAsync([entity], ct);
            await ApplyTaskSummaryAsync([entity], ct);
        }

        if (entity is null)
        {
            var isHidden = await _db.ClientCases
                .AsNoTracking()
                .AnyAsync(item => item.Id == id && item.IsHiddenFromExecutor, ct);
            if (isHidden)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Дело скрыто от исполнителя. Отправьте запрос доступа ответственному руководителю.",
                });
            }
        }

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost("{id:int}/access-request")]
    public async Task<ActionResult<ClientCaseAccessRequestDto>> RequestAccess(
        int id,
        [FromBody] RequestCaseAccessRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Forbid();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var actorName = User.GetDisplayName();
        var reason = NormalizeOptional(request?.Reason);
        reason = reason is null ? string.Empty : Truncate(NormalizeHistoryText(reason), 1000);
        var now = DateTime.UtcNow;

        clientCase.AccessRequestedAt = now;
        clientCase.AccessRequestedByUserId = currentUserId.Value;
        clientCase.AccessRequestedByUserName = Truncate(actorName, 256);
        clientCase.AccessRequestReason = reason;

        var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Причина: {reason}.";
        AppendCaseEvent(clientCase, $"Запрошен доступ к делу. Автор: {actorName}.{reasonSuffix}");
        _db.SetAuditReason($"Запрошен доступ к делу: {actorName}");

        var managerUserIds = await ResolveCaseAccessManagerUserIdsAsync(clientCase, currentUserId.Value, ct);
        foreach (var managerUserId in managerUserIds)
        {
            await AddCaseUserNotificationAsync(
                managerUserId,
                UserNotificationTypes.CaseAccessRequested,
                $"case:{clientCase.Id}:access-request:{now:yyyyMMddHHmmssfff}:{currentUserId.Value}",
                $"Запрошен доступ к делу: {BuildCaseNotificationTitle(clientCase)}",
                BuildCaseAccessRequestNotificationBody(clientCase, actorName, reason),
                "violet",
                clientCase,
                new
                {
                    requestedByUserId = currentUserId.Value,
                    requestedByUserName = actorName,
                    reason,
                    requestedAt = now,
                },
                ct);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseAccessRequestDto
        {
            ClientCaseId = clientCase.Id,
            RequestedAt = now,
            RequestedByUserId = currentUserId.Value,
            RequestedByUserName = actorName,
            Reason = reason,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        });
    }

    [HttpGet("access-requests")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseAccessRequestListItemDto>>> GetAccessRequests(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var items = await ApplyClientCaseAccessManagementVisibility(_db.ClientCases.AsQueryable(), visibility)
            .WithCaseIncludes()
            .Where(item => item.AccessRequestedAt.HasValue)
            .OrderByDescending(item => item.AccessRequestedAt)
            .Take(100)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(items.Select(MapClientCaseAccessRequest).ToList());
    }

    [HttpPost("{id:int}/access-request/resolve")]
    public async Task<ActionResult<ClientCaseAccessRequestResolutionDto>> ResolveAccessRequest(
        int id,
        [FromBody] ResolveCaseAccessRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await ApplyClientCaseAccessManagementVisibility(_db.ClientCases.AsQueryable(), visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        if (!clientCase.AccessRequestedAt.HasValue)
        {
            return BadRequest(new { message = "По этому делу нет активного запроса доступа." });
        }

        var requestedByUserId = clientCase.AccessRequestedByUserId;
        var requestedByUserName = clientCase.AccessRequestedByUserName;
        var requestReason = clientCase.AccessRequestReason;
        var grant = request?.Grant == true;
        var actorName = User.GetDisplayName();
        var actorUserId = User.GetUserId();
        var resolvedAt = DateTime.UtcNow;
        var comment = NormalizeOptional(request?.Comment);
        comment = comment is null ? string.Empty : Truncate(NormalizeHistoryText(comment), 1000);
        var commentSuffix = string.IsNullOrWhiteSpace(comment) ? string.Empty : $" Комментарий: {comment}.";

        if (grant)
        {
            ApplyHiddenFromExecutorMetadata(clientCase, isHidden: false, User.GetUserId(), actorName);
            AppendCaseEvent(clientCase, $"Запрос доступа разрешен. Решение: {actorName}.{commentSuffix}");
            _db.SetAuditReason($"Запрос доступа к делу разрешен: {actorName}");
        }
        else
        {
            clientCase.AccessRequestedAt = null;
            clientCase.AccessRequestedByUserId = null;
            clientCase.AccessRequestedByUserName = string.Empty;
            clientCase.AccessRequestReason = string.Empty;
            AppendCaseEvent(clientCase, $"Запрос доступа отклонен. Решение: {actorName}.{commentSuffix}");
            _db.SetAuditReason($"Запрос доступа к делу отклонен: {actorName}");
        }

        if (requestedByUserId.HasValue && (!actorUserId.HasValue || requestedByUserId.Value != actorUserId.Value))
        {
            await AddCaseUserNotificationAsync(
                requestedByUserId.Value,
                UserNotificationTypes.CaseAccessResolved,
                $"case:{clientCase.Id}:access-request:resolved:{(grant ? "granted" : "denied")}:{resolvedAt:yyyyMMddHHmmssfff}:{requestedByUserId.Value}",
                grant
                    ? $"Доступ к делу разрешен: {BuildCaseNotificationTitle(clientCase)}"
                    : $"Доступ к делу отклонен: {BuildCaseNotificationTitle(clientCase)}",
                BuildCaseAccessResolutionNotificationBody(clientCase, grant, actorName, comment),
                grant ? "emerald" : "orange",
                clientCase,
                new
                {
                    requestedByUserId,
                    requestedByUserName,
                    requestReason,
                    resolvedByUserName = actorName,
                    resolvedAt,
                    granted = grant,
                    comment,
                },
                ct);
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseAccessRequestResolutionDto
        {
            ClientCaseId = clientCase.Id,
            Granted = grant,
            ResolvedAt = resolvedAt,
            ResolvedByUserName = actorName,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        });
    }

    [HttpGet("{id:int}/timeline")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseTimelineEntryDto>>> GetTimeline(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var timeline = SplitCaseHistory(clientCase.EventHistory)
            .Select((line, index) => MapTimelineEntry(clientCase.Id, line, index))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

        return Ok(timeline);
    }

    [HttpGet("{id:int}/conversation")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> GetConversation(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return Ok(MapCaseConversation(clientCase.Id, clientCase.EventHistory));
    }

    [HttpGet("{id:int}/summary")]
    public async Task<ActionResult<ClientCaseSummaryDto>> GetSummary(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes(includePayments: true)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        await ApplyFinanceSummaryAsync([clientCase], ct);
        await ApplyTaskSummaryAsync([clientCase], ct);

        return Ok(MapCaseSummary(clientCase));
    }

    [HttpGet("{id:int}/finance")]
    public async Task<ActionResult<ClientCaseFinanceDetailDto>> GetFinance(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var payments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.InvoiceClientCases)
            .Include(payment => payment.Contract)
            .Include(payment => payment.PaymentSource)
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.ClientCaseId.HasValue && payment.ClientCaseId.Value == id) ||
                payment.InvoiceClientCases.Any(link => link.ClientCaseId == id))
            .ToListAsync(ct);

        var visiblePayments = InvoiceManagedPaymentRules.FilterVisiblePayments(payments)
            .OrderByDescending(ResolveFinancePaymentSortDate)
            .ThenByDescending(payment => payment.Id)
            .ToList();

        var paymentIds = visiblePayments
            .Select(payment => payment.Id)
            .Distinct()
            .ToArray();
        var invoicePaymentIds = visiblePayments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .Select(payment => payment.Id)
            .Distinct()
            .ToArray();
        var linkedInvoicePaymentIds = paymentIds
            .Concat(invoicePaymentIds)
            .Distinct()
            .ToArray();

        var linkedContracts = await LoadFinanceContractsAsync(visiblePayments, linkedInvoicePaymentIds, paymentIds, ct);

        return Ok(new ClientCaseFinanceDetailDto
        {
            CaseId = clientCase.Id,
            CaseTitle = clientCase.Title,
            OwnerName = ResolveCaseOwnerName(clientCase),
            Totals = BuildFinanceTotals(clientCase, visiblePayments),
            Calendar = BuildFinanceCalendar(visiblePayments),
            Invoices = BuildFinanceInvoices(visiblePayments, linkedContracts),
            Payments = visiblePayments.Select(MapFinancePayment).ToList(),
            Contracts = linkedContracts.Values
                .OrderByDescending(contract => contract.Date)
                .ThenBy(contract => contract.Number)
                .Select(MapFinanceContract)
                .ToList(),
        });
    }

    [HttpGet("{id:int}/documents")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseDocumentDto>>> GetDocuments(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var documents = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id)
            .OrderBy(item => item.DocumentType)
            .ThenBy(item => item.OriginalFileName)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);

        return Ok(MapDocuments(documents));
    }

    [HttpGet("/api/case-documents")]
    public async Task<IActionResult> GetDocumentRegistry(
        [FromQuery] int? clientCaseId,
        [FromQuery] string? search,
        [FromQuery] string? documentType,
        [FromQuery] string? status,
        [FromQuery] int? sourceTaskId,
        [FromQuery] bool latestOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxRegistryPageSize);

        var visibleCaseIds = _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .Select(item => item.Id);
        var query = _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => visibleCaseIds.Contains(item.ClientCaseId));

        if (clientCaseId.HasValue)
        {
            query = query.Where(item => item.ClientCaseId == clientCaseId.Value);
        }

        var normalizedSearch = NormalizeOptional(search);
        if (normalizedSearch is not null)
        {
            query = query.Where(item =>
                item.OriginalFileName.Contains(normalizedSearch) ||
                item.DocumentType.Contains(normalizedSearch) ||
                item.Status.Contains(normalizedSearch) ||
                item.DocumentKey.Contains(normalizedSearch));
        }

        var normalizedType = NormalizeOptional(documentType);
        if (normalizedType is not null)
        {
            query = query.Where(item => item.DocumentType == normalizedType);
        }

        var normalizedStatus = NormalizeOptional(status);
        if (normalizedStatus is not null)
        {
            query = query.Where(item => item.Status == normalizedStatus);
        }

        if (sourceTaskId.HasValue)
        {
            var prefix = $"task-{sourceTaskId.Value}-";
            query = query.Where(item => EF.Functions.Like(item.DocumentKey, prefix + "%"));
        }

        if (latestOnly)
        {
            query = query.Where(item => !_db.ClientCaseDocuments.Any(other =>
                other.ClientCaseId == item.ClientCaseId &&
                other.DocumentKey == item.DocumentKey &&
                other.Version > item.Version));
        }

        var total = await query.CountAsync(ct);
        var documents = await query
            .OrderByDescending(item => item.UploadedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var items = await MapDocumentsWithLatestAsync(documents, ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("/api/case-documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> GetDocumentById(int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var canViewCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .AnyAsync(item => item.Id == document.ClientCaseId, ct);
        if (!canViewCase)
        {
            return NotFound();
        }

        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == document.ClientCaseId && item.DocumentKey == document.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? document.Version;

        return Ok(MapDocument(document, document.Version == latestVersion));
    }

    [HttpPost("{id:int}/documents")]
    [RequestSizeLimit(MaxCaseDocumentBytes)]
    public async Task<ActionResult<ClientCaseDocumentDto>> UploadDocument(
        int id,
        [FromForm] IFormFile? file,
        [FromForm] string? documentType,
        [FromForm] string? status,
        [FromForm] string? documentKey,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return await UploadDocumentVersionCore(
            clientCase,
            file,
            documentType,
            status,
            documentKey,
            requireExistingDocument: false,
            ct);
    }

    [HttpPost("/api/case-documents/{documentId:int}/versions")]
    [RequestSizeLimit(MaxCaseDocumentBytes)]
    public async Task<ActionResult<ClientCaseDocumentDto>> UploadDocumentVersionById(
        int documentId,
        [FromForm] IFormFile? file,
        [FromForm] string? documentType,
        [FromForm] string? status,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var baseDocument = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (baseDocument is null)
        {
            return NotFound();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == baseDocument.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        return await UploadDocumentVersionCore(
            clientCase,
            file,
            NormalizeOptional(documentType) ?? baseDocument.DocumentType,
            NormalizeOptional(status) ?? baseDocument.Status,
            baseDocument.DocumentKey,
            requireExistingDocument: true,
            ct);
    }

    [HttpPost("{id:int}/documents/generate-template")]
    public async Task<ActionResult<ClientCaseDocumentDto>> GenerateDocumentTemplate(
        int id,
        [FromBody] GenerateCaseDocumentTemplateRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var template = BuildCaseDocumentTemplate(clientCase, request);
        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == id && item.DocumentKey == template.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? 0;
        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(template.FileName);
        var storedFileName = BuildStoredFileName(template.DocumentKey, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(id, storedFileName);

        var content = Encoding.UTF8.GetBytes(template.Html);
        try
        {
            await _caseDocumentStorage.SaveAsync(relativePath, content, ct);

            var document = new ClientCaseDocument
            {
                ClientCaseId = id,
                DocumentKey = template.DocumentKey,
                Version = version,
                DocumentType = template.DocumentType,
                Status = template.Status,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = "text/html; charset=utf-8",
                SizeBytes = content.LongLength,
                UploadedByUserId = User.GetUserId(),
                UploadedByName = User.GetDisplayName(),
                UploadedAt = DateTime.UtcNow,
            };

            _db.ClientCaseDocuments.Add(document);
            AppendCaseEvent(
                clientCase,
                $"Сформирован шаблон «{document.OriginalFileName}»: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Автор: {User.GetDisplayName()}.");
            await _db.SaveChangesAsync(ct);

            return Created($"/api/v2/cases/{id}/documents/{document.Id}", MapDocument(document, true));
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    [HttpGet("{id:int}/documents/{documentId:int}/download")]
    public async Task<IActionResult> DownloadDocument(int id, int documentId, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        if (!_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        var fullPath = _caseDocumentStorage.ResolvePath(document.StoragePath);
        return PhysicalFile(fullPath, document.ContentType, document.OriginalFileName);
    }

    [HttpGet("/api/case-documents/{documentId:int}/download")]
    public async Task<IActionResult> DownloadDocumentById(int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var canViewCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .AnyAsync(item => item.Id == document.ClientCaseId, ct);
        if (!canViewCase || !_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        var fullPath = _caseDocumentStorage.ResolvePath(document.StoragePath);
        return PhysicalFile(fullPath, document.ContentType, document.OriginalFileName);
    }

    [HttpPatch("/api/case-documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> UpdateDocumentById(
        int documentId,
        [FromBody] UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == document.ClientCaseId, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var dto = await UpdateDocumentCore(clientCase, document, request, ct);
        return Ok(dto);
    }

    [HttpPatch("{id:int}/documents/{documentId:int}")]
    public async Task<ActionResult<ClientCaseDocumentDto>> UpdateDocument(
        int id,
        int documentId,
        [FromBody] UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        var dto = await UpdateDocumentCore(clientCase, document, request, ct);
        return Ok(dto);
    }

    private async Task<ActionResult<ClientCaseDocumentDto>> UploadDocumentVersionCore(
        ClientCase clientCase,
        IFormFile? file,
        string? documentType,
        string? status,
        string? documentKey,
        bool requireExistingDocument,
        CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Выберите файл для загрузки." });
        }

        if (file.Length > MaxCaseDocumentBytes)
        {
            return BadRequest(new { message = "Файл не должен превышать 25 МБ." });
        }

        var normalizedDocumentKey = NormalizeDocumentKey(documentKey);
        if (documentKey is not null && normalizedDocumentKey is null)
        {
            return BadRequest(new { message = "Некорректный ключ документа." });
        }

        var isNewDocument = normalizedDocumentKey is null;
        if (isNewDocument)
        {
            if (requireExistingDocument)
            {
                return BadRequest(new { message = "Документ для новой версии не найден." });
            }

            normalizedDocumentKey = Guid.NewGuid().ToString("N");
        }

        var latestVersion = isNewDocument
            ? 0
            : await _db.ClientCaseDocuments
                .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == normalizedDocumentKey)
                .MaxAsync(item => (int?)item.Version, ct) ?? 0;

        if (!isNewDocument && latestVersion == 0)
        {
            return BadRequest(new { message = "Документ для новой версии не найден." });
        }

        var version = latestVersion + 1;
        var safeFileName = SanitizeFileName(file.FileName);
        var storedFileName = BuildStoredFileName(normalizedDocumentKey!, version, safeFileName);
        var relativePath = _caseDocumentStorage.BuildRelativePath(clientCase.Id, storedFileName);

        try
        {
            await using var stream = file.OpenReadStream();
            await _caseDocumentStorage.SaveAsync(relativePath, stream, ct);

            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = normalizedDocumentKey!,
                Version = version,
                DocumentType = NormalizeRequired(documentType, "Документ"),
                Status = NormalizeRequired(status, "Подготовлен"),
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                StoragePath = relativePath,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
                UploadedByUserId = User.GetUserId(),
                UploadedByName = User.GetDisplayName(),
                UploadedAt = DateTime.UtcNow,
            };

            _db.ClientCaseDocuments.Add(document);
            var eventPrefix = isNewDocument
                ? $"Документ «{document.OriginalFileName}» загружен"
                : $"Документ «{document.OriginalFileName}»: загружена новая версия";
            AppendCaseEvent(
                clientCase,
                $"{eventPrefix}: тип «{document.DocumentType}», статус «{document.Status}», версия {document.Version}. Добавил: {User.GetDisplayName()}.");
            await _db.SaveChangesAsync(ct);

            return Created($"/api/case-documents/{document.Id}", MapDocument(document, true));
        }
        catch
        {
            _caseDocumentStorage.DeleteIfExists(relativePath);
            throw;
        }
    }

    private async Task<ClientCaseDocumentDto> UpdateDocumentCore(
        ClientCase clientCase,
        ClientCaseDocument document,
        UpdateCaseDocumentRequest? request,
        CancellationToken ct)
    {
        var changes = new List<string>();
        var nextType = NormalizeOptional(request?.DocumentType);
        var nextStatus = NormalizeOptional(request?.Status);

        if (nextType is not null && !string.Equals(document.DocumentType, nextType, StringComparison.Ordinal))
        {
            changes.Add($"тип «{document.DocumentType}» → «{nextType}»");
            document.DocumentType = nextType;
        }

        if (nextStatus is not null && !string.Equals(document.Status, nextStatus, StringComparison.Ordinal))
        {
            changes.Add($"статус «{document.Status}» → «{nextStatus}»");
            document.Status = nextStatus;
        }

        if (changes.Count > 0)
        {
            AppendCaseEvent(
                clientCase,
                $"Документ «{document.OriginalFileName}»: {string.Join(", ", changes)}. Изменил: {User.GetDisplayName()}.");
            await _db.SaveChangesAsync(ct);
        }

        var latestVersion = await _db.ClientCaseDocuments
            .Where(item => item.ClientCaseId == clientCase.Id && item.DocumentKey == document.DocumentKey)
            .MaxAsync(item => (int?)item.Version, ct) ?? document.Version;

        return MapDocument(document, document.Version == latestVersion);
    }

    [HttpDelete("{id:int}/documents/{documentId:int}")]
    public async Task<IActionResult> DeleteDocument(int id, int documentId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var document = await _db.ClientCaseDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId && item.ClientCaseId == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        var storagePath = document.StoragePath;
        _db.ClientCaseDocuments.Remove(document);
        AppendCaseEvent(
            clientCase,
            $"Документ «{document.OriginalFileName}» удален. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);
        _caseDocumentStorage.DeleteIfExists(storagePath);

        return NoContent();
    }

    [HttpPost("{id:int}/activity")]
    public async Task<ActionResult<ClientCaseActivityDto>> MarkActivity(
        int id,
        [FromBody] MarkClientCaseActivityRequest? request,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var userId = User.GetUserId();
        var displayName = User.GetDisplayName();
        var responsibleUserId = clientCase.ResponsibleUserId ?? clientCase.Client?.ResponsibleUserId ?? clientCase.LegalEntity?.ResponsibleUserId;
        var resetsInactivityTimer = !responsibleUserId.HasValue || responsibleUserId == userId;
        if (resetsInactivityTimer)
        {
            clientCase.LastViewedAt = now;
            clientCase.LastViewedByUserId = userId;
            clientCase.LastViewedByUserName = displayName;
        }

        if (request?.Confirmed == true)
        {
            clientCase.ActivityConfirmedAt = now;
            clientCase.ActivityConfirmedByUserId = userId;
            clientCase.ActivityConfirmedByUserName = displayName;
            AppendCaseEvent(clientCase, $"Активность по делу подтверждена. Подтвердил: {displayName}.");
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseActivityDto
        {
            LastViewedAt = clientCase.LastViewedAt,
            LastViewedByUserId = clientCase.LastViewedByUserId,
            LastViewedByUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/history")]
    public async Task<ActionResult<ClientCaseActivityDto>> AddHistoryEntry(
        int id,
        [FromBody] AddClientCaseHistoryEntryRequest request,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var message = NormalizeOptional(request.Text);
        if (message is null)
        {
            return BadRequest(new { message = "Введите текст комментария по делу." });
        }

        if (message.Length > 2000)
        {
            return BadRequest(new { message = "Комментарий по делу не должен превышать 2000 символов." });
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var displayName = User.GetDisplayName();
        AppendCaseEvent(clientCase, $"Комментарий по делу: {message}. Автор: {displayName}.");

        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseActivityDto
        {
            LastViewedAt = clientCase.LastViewedAt,
            LastViewedByUserId = clientCase.LastViewedByUserId,
            LastViewedByUserName = clientCase.LastViewedByUserName,
            ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
            ActivityConfirmedByUserId = clientCase.ActivityConfirmedByUserId,
            ActivityConfirmedByUserName = clientCase.ActivityConfirmedByUserName,
            InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
            EventHistory = clientCase.EventHistory,
        });
    }

    [HttpPost("{id:int}/conversation")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseConversationEntryDto>>> AddConversationEntry(
        int id,
        [FromBody] AddClientCaseConversationEntryRequest request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var message = NormalizeOptional(request.Text);
        if (message is null)
        {
            return BadRequest(new { message = "Введите текст сообщения по делу." });
        }

        message = NormalizeHistoryText(message);
        if (message.Length > 2000)
        {
            return BadRequest(new { message = "Сообщение по делу не должно превышать 2000 символов." });
        }

        var clientCase = await _db.ClientCases
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var displayName = User.GetDisplayName();
        AppendCaseEvent(clientCase, $"{CaseConversationPrefix} {message}. {CaseHistoryAuthorMarker} {displayName}.");

        await _db.SaveChangesAsync(ct);

        return Ok(MapCaseConversation(clientCase.Id, clientCase.EventHistory));
    }

    [HttpGet("{id:int}/related-parties")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseRelatedPartyDto>>> GetRelatedParties(
        int id,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var query = _db.ClientCaseRelatedParties
            .AsNoTracking()
            .Where(item => item.ClientCaseId == id);
        if (!includeArchived)
        {
            query = query.Where(item => !item.IsArchived);
        }

        var items = await query
            .OrderBy(item => item.IsArchived)
            .ThenBy(item => item.SourceType)
            .ThenBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        return Ok(items.Select(MapRelatedParty).ToList());
    }

    [HttpPost("{id:int}/related-parties")]
    public async Task<ActionResult<ClientCaseRelatedPartyDto>> AddRelatedParty(
        int id,
        [FromBody] UpsertClientCaseRelatedPartyRequest request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var sourceType = NormalizeRelatedPartySourceType(request.SourceType);
        if (sourceType is null || request.SourceId <= 0)
        {
            return BadRequest(new { message = "Выберите связанное лицо или организацию из справочника." });
        }

        var role = NormalizeRequired(request.Role, "Участник");
        var notes = NormalizeOptional(request.Notes) ?? string.Empty;
        ClientCaseRelatedParty? relatedParty = null;
        LegalEntity? autoLegalEntity = null;

        if (sourceType == RelatedPartySourceClient)
        {
            var client = await _db.Clients
                .AsNoTracking()
                .Include(item => item.LegalEntity)
                .ApplyClientVisibility(visibility)
                .FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (client is null)
            {
                return BadRequest(new { message = "Физическое лицо не найдено или недоступно." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceClient &&
                    !item.IsArchived &&
                    item.ClientId == client.Id, ct))
            {
                return Conflict(new { message = "Это физическое лицо уже связано с делом." });
            }

            var archivedParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceClient &&
                    item.IsArchived &&
                    item.ClientId == client.Id, ct);
            if (archivedParty is not null)
            {
                archivedParty.DisplayName = client.Name;
                archivedParty.Role = role;
                archivedParty.OrganizationName = client.LegalEntity?.ShortName ?? client.Company ?? string.Empty;
                archivedParty.Phone = client.Phone;
                archivedParty.Email = client.Email;
                archivedParty.Notes = notes;
                RestoreRelatedParty(archivedParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedParty.DisplayName} ({archivedParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedParty.DisplayName} ({archivedParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedParty));
            }
            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceClient,
                ClientId = client.Id,
                DisplayName = client.Name,
                Role = role,
                OrganizationName = client.LegalEntity?.ShortName ?? client.Company ?? string.Empty,
                Phone = client.Phone,
                Email = client.Email,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
            autoLegalEntity = client.LegalEntity;
        }
        else if (sourceType == RelatedPartySourceExternalContact)
        {
            var visibleUserIds = visibility.VisibleUserIds.ToArray();
            var contactQuery = _db.ExternalContacts
                .AsNoTracking()
                .Include(item => item.LegalEntity)
                .AsQueryable();
            if (!visibility.SeesAll)
            {
                contactQuery = contactQuery.Where(item =>
                    item.ResponsibleUserId.HasValue && visibleUserIds.Contains(item.ResponsibleUserId.Value));
            }

            var contact = await contactQuery.FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (contact is null)
            {
                return BadRequest(new { message = "Контакт не найден или недоступен." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceExternalContact &&
                    !item.IsArchived &&
                    item.ExternalContactId == contact.Id, ct))
            {
                return Conflict(new { message = "Этот контакт уже связан с делом." });
            }

            var archivedContactParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceExternalContact &&
                    item.IsArchived &&
                    item.ExternalContactId == contact.Id, ct);
            if (archivedContactParty is not null)
            {
                archivedContactParty.DisplayName = contact.FullName;
                archivedContactParty.Role = role;
                archivedContactParty.OrganizationName = contact.LegalEntity?.ShortName ?? string.Empty;
                archivedContactParty.Phone = contact.Phone ?? string.Empty;
                archivedContactParty.Email = contact.Email ?? string.Empty;
                archivedContactParty.Notes = notes;
                RestoreRelatedParty(archivedContactParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedContactParty.DisplayName} ({archivedContactParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedContactParty.DisplayName} ({archivedContactParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedContactParty));
            }

            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceExternalContact,
                ExternalContactId = contact.Id,
                DisplayName = contact.FullName,
                Role = role,
                OrganizationName = contact.LegalEntity?.ShortName ?? string.Empty,
                Phone = contact.Phone ?? string.Empty,
                Email = contact.Email ?? string.Empty,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
            autoLegalEntity = contact.LegalEntity;
        }
        else if (sourceType == RelatedPartySourceLegalEntity)
        {
            var legalEntity = await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .FirstOrDefaultAsync(item => item.Id == request.SourceId, ct);
            if (legalEntity is null)
            {
                return BadRequest(new { message = "Организация не найдена или недоступна." });
            }

            if (await _db.ClientCaseRelatedParties.AnyAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceLegalEntity &&
                    !item.IsArchived &&
                    item.LegalEntityId == legalEntity.Id, ct))
            {
                return Conflict(new { message = "Эта организация уже связана с делом." });
            }

            var archivedLegalEntityParty = await _db.ClientCaseRelatedParties
                .FirstOrDefaultAsync(item =>
                    item.ClientCaseId == id &&
                    item.SourceType == RelatedPartySourceLegalEntity &&
                    item.IsArchived &&
                    item.LegalEntityId == legalEntity.Id, ct);
            if (archivedLegalEntityParty is not null)
            {
                archivedLegalEntityParty.DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}";
                archivedLegalEntityParty.Role = role;
                archivedLegalEntityParty.OrganizationName = legalEntity.FullName ?? string.Empty;
                archivedLegalEntityParty.Phone = legalEntity.Phone ?? string.Empty;
                archivedLegalEntityParty.Email = legalEntity.Email ?? string.Empty;
                archivedLegalEntityParty.Notes = notes;
                RestoreRelatedParty(archivedLegalEntityParty);
                _db.SetAuditReason($"Связанное лицо восстановлено в деле #{id}: {archivedLegalEntityParty.DisplayName} ({archivedLegalEntityParty.Role}).");
                AppendCaseEvent(clientCase, $"Связь с делом восстановлена: {archivedLegalEntityParty.DisplayName} ({archivedLegalEntityParty.Role}). Восстановил: {User.GetDisplayName()}.");
                await _db.SaveChangesAsync(ct);
                return Ok(MapRelatedParty(archivedLegalEntityParty));
            }

            relatedParty = new ClientCaseRelatedParty
            {
                ClientCaseId = id,
                SourceType = RelatedPartySourceLegalEntity,
                LegalEntityId = legalEntity.Id,
                DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}",
                Role = role,
                OrganizationName = legalEntity.FullName ?? string.Empty,
                Phone = legalEntity.Phone ?? string.Empty,
                Email = legalEntity.Email ?? string.Empty,
                Notes = notes,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            };
        }

        if (relatedParty is null)
        {
            return BadRequest(new { message = "Не удалось определить тип связи." });
        }

        _db.ClientCaseRelatedParties.Add(relatedParty);
        _db.SetAuditReason($"Связанное лицо добавлено к делу #{id}: {relatedParty.DisplayName} ({relatedParty.Role}).");
        AppendCaseEvent(clientCase, $"Связь с делом добавлена: {relatedParty.DisplayName} ({relatedParty.Role}). Добавил: {User.GetDisplayName()}.");
        var autoRelatedParty = await BuildAutoLegalEntityRelatedPartyAsync(
            id,
            clientCase.LegalEntityId,
            autoLegalEntity,
            visibility,
            User.GetUserId(),
            User.GetDisplayName(),
            ct);
        if (autoRelatedParty is not null)
        {
            _db.ClientCaseRelatedParties.Add(autoRelatedParty);
            AppendCaseEvent(
                clientCase,
                $"Связь с организацией участника добавлена автоматически: {autoRelatedParty.DisplayName}. Добавил: {User.GetDisplayName()}.");
        }

        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/related-parties/{relatedParty.Id}", MapRelatedParty(relatedParty));
    }

    [HttpDelete("{id:int}/related-parties/{partyId:int}")]
    public async Task<IActionResult> DeleteRelatedParty(int id, int partyId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var relatedParty = await _db.ClientCaseRelatedParties
            .FirstOrDefaultAsync(item => item.Id == partyId && item.ClientCaseId == id, ct);
        if (relatedParty is null)
        {
            return NotFound();
        }

        if (relatedParty.IsArchived)
        {
            return NoContent();
        }

        relatedParty.IsArchived = true;
        relatedParty.ArchivedAt = DateTime.UtcNow;
        relatedParty.ArchivedByUserId = User.GetUserId();
        relatedParty.ArchivedByUserName = User.GetDisplayName();
        relatedParty.ArchiveReason = "Archived from case card";
        _db.SetAuditReason($"Связанное лицо архивировано в деле #{id}: {relatedParty.DisplayName}.");
        AppendCaseEvent(clientCase, $"Связь с делом удалена: {relatedParty.DisplayName}. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpGet("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> GetWorkOrder(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var exists = await _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);

        return Ok(workOrder is null
            ? ClientCaseWorkOrderDto.Empty(id)
            : MapWorkOrder(workOrder));
    }

    [HttpGet("{id:int}/work-order/print")]
    public async Task<IActionResult> PrintWorkOrder(int id, CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ResponsibleUser)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .AsNoTracking()
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (workOrder is null)
        {
            return NotFound(new { message = "По делу еще нет заказ-наряда." });
        }

        return Content(BuildWorkOrderPrintHtml(clientCase, workOrder), "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpPost("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> CreateWorkOrder(
        int id,
        [FromBody] UpsertClientCaseWorkOrderRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var existing = await _db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (existing is not null)
        {
            return Conflict(new { message = "По делу уже есть предварительный расчет." });
        }

        var now = DateTime.UtcNow;
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCaseId = id,
            Title = NormalizeRequired(request?.Title, "Предварительный расчет"),
            Status = NormalizeWorkOrderStatus(request?.Status),
            Currency = NormalizeCurrency(request?.Currency),
            Notes = Truncate(NormalizeOptional(request?.Notes) ?? string.Empty, 1000),
            CreatedAt = now,
            CreatedByUserId = User.GetUserId(),
            CreatedByUserName = User.GetDisplayName(),
        };

        _db.ClientCaseWorkOrders.Add(workOrder);
        _db.SetAuditReason($"Предварительный расчет создан в деле #{id}: {workOrder.Title}.");
        AppendCaseEvent(clientCase, $"Создан предварительный расчет «{workOrder.Title}». Автор: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v2/cases/{id}/work-order", MapWorkOrder(workOrder));
    }

    [HttpPatch("{id:int}/work-order")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> UpdateWorkOrder(
        int id,
        [FromBody] UpsertClientCaseWorkOrderRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var changes = new List<string>();
        var nextTitle = NormalizeOptional(request?.Title);
        if (nextTitle is not null && !string.Equals(workOrder.Title, nextTitle, StringComparison.Ordinal))
        {
            workOrder.Title = nextTitle;
            changes.Add($"название: {nextTitle}");
        }

        var nextStatus = NormalizeOptional(request?.Status);
        if (nextStatus is not null)
        {
            var normalizedStatus = NormalizeWorkOrderStatus(nextStatus);
            if (IsWorkOrderApprovalManagedStatus(normalizedStatus))
            {
                return BadRequest(new { message = "Статусы согласования заказ-наряда меняются только через действия согласования и создания счета." });
            }

            if (!string.Equals(workOrder.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
            {
                workOrder.Status = normalizedStatus;
                changes.Add($"статус: {FormatWorkOrderStatus(normalizedStatus)}");
            }
        }

        var nextCurrency = NormalizeOptional(request?.Currency);
        if (nextCurrency is not null)
        {
            var normalizedCurrency = NormalizeCurrency(nextCurrency);
            if (!string.Equals(workOrder.Currency, normalizedCurrency, StringComparison.Ordinal))
            {
                workOrder.Currency = normalizedCurrency;
                changes.Add($"валюта: {normalizedCurrency}");
            }
        }

        if (request?.Notes is not null)
        {
            workOrder.Notes = Truncate(NormalizeOptional(request.Notes) ?? string.Empty, 1000);
            changes.Add("примечание обновлено");
        }

        if (changes.Count == 0)
        {
            return Ok(MapWorkOrder(workOrder));
        }

        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Предварительный расчет обновлен в деле #{id}: {string.Join("; ", changes)}.");
        AppendCaseEvent(clientCase, $"Предварительный расчет обновлен: {string.Join("; ", changes)}. Изменил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpDelete("{id:int}/work-order")]
    public async Task<IActionResult> DeleteWorkOrder(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        _db.ClientCaseWorkOrders.Remove(workOrder);
        _db.SetAuditReason($"Предварительный расчет удален из дела #{id}: {workOrder.Title}.");
        AppendCaseEvent(clientCase, $"Предварительный расчет «{workOrder.Title}» удален. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:int}/work-order/items")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> AddWorkOrderItem(
        int id,
        [FromBody] UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var (item, itemError) = await BuildWorkOrderItemAsync(workOrder, request, ct);
        if (itemError is not null)
        {
            return BadRequest(new { message = itemError });
        }

        item ??= new ClientCaseWorkOrderItem();
        if (item.ServiceName.Length == 0)
        {
            return BadRequest(new { message = "Укажите услугу или этап работ." });
        }

        workOrder.Items.Add(item);
        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"В предварительный расчет дела #{id} добавлена услуга: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"В предварительный расчет добавлена услуга «{item.ServiceName}» на сумму {item.TotalAmount:0.##} {workOrder.Currency}. Добавил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPatch("{id:int}/work-order/items/{itemId:int}")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> UpdateWorkOrderItem(
        int id,
        int itemId,
        [FromBody] UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var item = workOrder.Items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            return NotFound();
        }

        var itemError = await ApplyWorkOrderItemAsync(item, request, ct);
        if (itemError is not null)
        {
            return BadRequest(new { message = itemError });
        }

        if (item.ServiceName.Length == 0)
        {
            return BadRequest(new { message = "Укажите услугу или этап работ." });
        }

        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Позиция предварительного расчета обновлена в деле #{id}: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"Позиция предварительного расчета «{item.ServiceName}» обновлена. Изменил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpDelete("{id:int}/work-order/items/{itemId:int}")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> DeleteWorkOrderItem(
        int id,
        int itemId,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (IsWorkOrderLockedForEditing(workOrder))
        {
            return BadRequest(new { message = BuildWorkOrderLockedMessage(workOrder.Status) });
        }

        var item = workOrder.Items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            return NotFound();
        }

        _db.ClientCaseWorkOrderItems.Remove(item);
        workOrder.Items.Remove(item);
        RecalculateWorkOrder(workOrder);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Позиция предварительного расчета удалена из дела #{id}: {item.ServiceName}.");
        AppendCaseEvent(clientCase, $"Позиция предварительного расчета «{item.ServiceName}» удалена. Удалил: {User.GetDisplayName()}.");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/create-invoice")]
    public async Task<ActionResult<ClientCaseWorkOrderInvoiceDto>> CreateInvoiceFromWorkOrder(
        int id,
        [FromBody] CreateClientCaseWorkOrderInvoiceRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        RecalculateWorkOrder(workOrder);
        if (workOrder.TotalAmount <= 0m || workOrder.Items.Count == 0)
        {
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед созданием счета." });
        }
        if (!string.Equals(NormalizeWorkOrderStatus(workOrder.Status), "approved", StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Перед созданием счета согласуйте заказ-наряд." });
        }

        var invoiceDate = request?.Date?.Date ?? DateTime.UtcNow.Date;
        var dueDate = request?.DueDate?.Date ?? invoiceDate;
        var number = await BuildWorkOrderInvoiceNumberAsync(id, workOrder.Id, invoiceDate, request?.Number, ct);
        var now = DateTime.UtcNow;
        var invoice = new Invoice
        {
            Number = number,
            Date = invoiceDate,
            CreatedAt = now,
        };
        var payment = new Payment
        {
            Account = number,
            AccountDate = invoiceDate,
            Invoice = invoice,
            Date = dueDate,
            PlannedDate = dueDate,
            Amount = workOrder.TotalAmount,
            PaidAmount = 0m,
            Status = PaymentStatus.Pending,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Description = NormalizeOptional(request?.Description)
                ?? $"Счет по заказ-наряду «{workOrder.Title}»",
            ClientId = clientCase.ClientId,
            LegalEntityId = clientCase.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ClientCaseReference = NormalizeOptional(clientCase.CaseNumber) ?? clientCase.Title,
            CreatedAt = now,
        };

        payment.InvoiceClientCases.Add(new InvoiceClientCase
        {
            ClientCaseId = clientCase.Id,
        });

        var responsibleIds = workOrder.Items
            .Where(item => item.ExecutorUserId.HasValue)
            .Select(item => item.ExecutorUserId!.Value)
            .Distinct()
            .ToList();
        foreach (var responsibleId in responsibleIds)
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        var sortOrder = 1;
        foreach (var item in workOrder.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            payment.InvoiceLineItems.Add(new InvoiceLineItem
            {
                Name = item.ServiceName,
                Quantity = item.Quantity,
                Unit = "шт",
                Price = item.UnitPrice,
                SortOrder = sortOrder++,
                CreatedAt = now,
            });
        }

        PaymentDomainService.Normalize(payment);
        workOrder.Status = "invoice_created";
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"По предварительному расчету дела #{id} создан счет №{number}.");
        AppendCaseEvent(clientCase, $"По предварительному расчету «{workOrder.Title}» создан счет №{number} на сумму {workOrder.TotalAmount:0.##} {workOrder.Currency}. Создал: {User.GetDisplayName()}.");

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        return Created($"/invoices/{payment.Id}", new ClientCaseWorkOrderInvoiceDto
        {
            PaymentId = payment.Id,
            InvoiceEntityId = payment.InvoiceId ?? invoice.Id,
            Number = number,
            Date = invoiceDate,
            DueDate = dueDate,
            Amount = payment.Amount,
            WorkOrder = MapWorkOrder(workOrder),
        });
    }

    [HttpPost("{id:int}/work-order/submit-approval")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> SubmitWorkOrderApproval(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        RecalculateWorkOrder(workOrder);
        if (workOrder.TotalAmount <= 0m || workOrder.Items.Count == 0)
        {
            return BadRequest(new { message = "Добавьте услуги с ненулевой стоимостью перед отправкой заказ-наряда на согласование." });
        }

        var now = DateTime.UtcNow;
        workOrder.Status = "pending_approval";
        workOrder.ApprovalRequestedAt = now;
        workOrder.ApprovalRequestedByUserId = User.GetUserId();
        workOrder.ApprovalRequestedByUserName = User.GetDisplayName();
        workOrder.ApprovalDecisionAt = null;
        workOrder.ApprovalDecisionByUserId = null;
        workOrder.ApprovalDecisionByUserName = string.Empty;
        workOrder.ApprovalComment = Truncate(NormalizeOptional(request?.Comment) ?? string.Empty, 1000);
        StampWorkOrderUpdate(workOrder);
        _db.SetAuditReason($"Заказ-наряд дела #{id} отправлен на согласование.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» отправлен на согласование. Инициатор: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/approve")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> ApproveWorkOrder(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        if (!string.Equals(NormalizeWorkOrderStatus(workOrder.Status), "pending_approval", StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Согласовать можно только заказ-наряд в статусе «На согласовании»." });
        }

        ApplyWorkOrderApprovalDecision(workOrder, "approved", request?.Comment);
        _db.SetAuditReason($"Заказ-наряд дела #{id} согласован.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» согласован. Согласующий: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpPost("{id:int}/work-order/request-revision")]
    public async Task<ActionResult<ClientCaseWorkOrderDto>> RequestWorkOrderRevision(
        int id,
        [FromBody] ClientCaseWorkOrderApprovalRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var loadResult = await LoadEditableWorkOrderAsync(id, ct);
        if (loadResult.Result is not null)
        {
            return loadResult.Result;
        }

        var (clientCase, workOrder) = loadResult.Value;
        var currentStatus = NormalizeWorkOrderStatus(workOrder.Status);
        if (currentStatus is not "pending_approval" and not "approved")
        {
            return BadRequest(new { message = "Вернуть на доработку можно только заказ-наряд в статусе «На согласовании» или «Согласован»." });
        }

        ApplyWorkOrderApprovalDecision(workOrder, "revision_required", request?.Comment);
        _db.SetAuditReason($"Заказ-наряд дела #{id} возвращен на доработку.");
        AppendCaseEvent(clientCase, $"Заказ-наряд «{workOrder.Title}» возвращен на доработку. Согласующий: {User.GetDisplayName()}.{FormatOptionalComment(workOrder.ApprovalComment)}");
        await _db.SaveChangesAsync(ct);

        return Ok(MapWorkOrder(workOrder));
    }

    [HttpGet("{id:int}/court-sync/logs")]
    public async Task<ActionResult<IReadOnlyList<ClientCaseCourtSyncLogDto>>> GetCourtSyncLogs(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var logs = SplitCaseHistory(clientCase.EventHistory)
            .Where(IsCourtSyncHistoryLine)
            .Select((line, index) => MapCourtSyncLog(clientCase.Id, line, index))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

        return Ok(logs);
    }

    [HttpPost("{id:int}/court-sync/run")]
    public async Task<ActionResult<ClientCaseCourtSyncRunDto>> RunCourtSync(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var actorName = User.GetDisplayName();
        var checkedAt = DateTime.UtcNow;
        var hasCourtBinding =
            !string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber)
            || !string.IsNullOrWhiteSpace(clientCase.CourtUrl);
        var status = hasCourtBinding ? "manual-check-recorded" : "binding-required";
        var message = hasCourtBinding
            ? $"Ручная проверка судебного источника зафиксирована. Внешняя синхронизация пока не подключена; проверьте данные суда вручную. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}."
            : "Ручная проверка судебного источника не выполнена: в деле не указан номер дела в суде или ссылка на первоисточник.";

        AppendCaseEvent(clientCase, $"{message} Инициатор: {actorName}.");
        await _db.SaveChangesAsync(ct);

        return Ok(new ClientCaseCourtSyncRunDto
        {
            ClientCaseId = clientCase.Id,
            Status = status,
            HasCourtBinding = hasCourtBinding,
            Message = message,
            CheckedAt = checkedAt,
            CheckedByUserId = User.GetUserId(),
            CheckedByUserName = actorName,
            CourtName = clientCase.CourtName,
            CourtCaseNumber = clientCase.CourtCaseNumber,
            CourtUrl = clientCase.CourtUrl,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        ApplyCaseDetails(model, model);
        ApplyHiddenFromExecutorMetadata(model, model.IsHiddenFromExecutor, User.GetUserId(), User.GetDisplayName());
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, model.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Created($"/api/v2/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;

        var shouldCloseLinkedTasks = !IsFinishedCaseStatus(e.Status) && IsFinishedCaseStatus(model.Status);
        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        var previousHiddenFromExecutor = e.IsHiddenFromExecutor;
        ApplyCaseDetails(e, model);
        if (previousHiddenFromExecutor != e.IsHiddenFromExecutor)
        {
            ApplyHiddenFromExecutorMetadata(e, e.IsHiddenFromExecutor, User.GetUserId(), User.GetDisplayName());
        }

        if (shouldCloseLinkedTasks)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
        foreach (var p in payments) p.ClientCaseId = null;
        await _db.SaveChangesAsync(ct);

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("bulk-update")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkUpdateCasesRequest request, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var ids = (request.Ids ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .Take(500)
            .ToArray();
        if (ids.Length == 0)
        {
            return BadRequest("Выберите дела для группового изменения.");
        }

        var nextStage = NormalizeOptional(request.Stage);
        var nextPriority = NormalizeOptional(request.Priority);
        if (request.Status is null
            && !request.ResponsibleUserId.HasValue
            && nextStage is null
            && nextPriority is null
            && !request.IsHiddenFromExecutor.HasValue
            && !request.InactivityRemindersEnabled.HasValue)
        {
            return BadRequest("Укажите новый статус, ответственного, стадию, приоритет, режим доступа или автоконтроль.");
        }

        User? nextResponsible = null;
        if (request.ResponsibleUserId.HasValue)
        {
            nextResponsible = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == request.ResponsibleUserId.Value && user.IsActive, ct);
        }

        if (request.ResponsibleUserId.HasValue && nextResponsible is null)
        {
            return BadRequest($"Unknown responsibleUserId {request.ResponsibleUserId.Value}");
        }

        if (nextStage is not null && nextStage.Length > 160)
        {
            return BadRequest("Стадия дела слишком длинная.");
        }

        if (nextPriority is not null && !AllowedCasePriorities.Contains(nextPriority))
        {
            return BadRequest("Некорректный приоритет дела.");
        }

        var cases = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .Include(item => item.ResponsibleUser)
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(ct);
        if (cases.Count == 0)
        {
            return NotFound();
        }

        var casesClosedByUpdate = new List<int>();
        var changedCasesCount = 0;
        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        foreach (var item in cases)
        {
            var changes = new List<string>();
            if (request.Status is not null)
            {
                if (!IsFinishedCaseStatus(item.Status) && IsFinishedCaseStatus(request.Status.Value))
                {
                    casesClosedByUpdate.Add(item.Id);
                }

                if (item.Status != request.Status.Value)
                {
                    changes.Add($"статус «{FormatCaseStatus(item.Status)}» → «{FormatCaseStatus(request.Status.Value)}»");
                    item.Status = request.Status.Value;
                }
            }

            if (request.ResponsibleUserId.HasValue)
            {
                if (item.ResponsibleUserId != request.ResponsibleUserId.Value)
                {
                    changes.Add($"ответственный «{FormatCaseResponsible(item.ResponsibleUser)}» → «{FormatCaseResponsible(nextResponsible)}»");
                    item.ResponsibleUserId = request.ResponsibleUserId.Value;
                }
            }

            if (nextStage is not null && !string.Equals(item.Stage, nextStage, StringComparison.Ordinal))
            {
                changes.Add($"стадия «{FormatCaseTextValue(item.Stage)}» → «{nextStage}»");
                item.Stage = nextStage;
            }

            if (nextPriority is not null && !string.Equals(item.Priority, nextPriority, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"приоритет «{FormatCasePriority(item.Priority)}» → «{FormatCasePriority(nextPriority)}»");
                item.Priority = nextPriority;
            }

            if (request.IsHiddenFromExecutor.HasValue && item.IsHiddenFromExecutor != request.IsHiddenFromExecutor.Value)
            {
                var nextHidden = request.IsHiddenFromExecutor.Value;
                changes.Add(nextHidden ? "доступ: скрыто от исполнителя" : "доступ: открыто исполнителю");
                item.IsHiddenFromExecutor = nextHidden;
                if (nextHidden)
                {
                    ApplyHiddenFromExecutorMetadata(item, true, actorUserId, actorName);
                }
                else
                {
                    ApplyHiddenFromExecutorMetadata(item, false, actorUserId, actorName);
                }
            }

            if (request.InactivityRemindersEnabled.HasValue && item.InactivityRemindersEnabled != request.InactivityRemindersEnabled.Value)
            {
                var nextEnabled = request.InactivityRemindersEnabled.Value;
                changes.Add(nextEnabled ? "автоконтроль: включен" : "автоконтроль: выключен");
                item.InactivityRemindersEnabled = nextEnabled;
            }

            if (changes.Count > 0)
            {
                changedCasesCount++;
                AppendCaseEvent(item, $"Массовое изменение: {string.Join("; ", changes)}. Изменил: {actorName}.");
            }
        }

        if (changedCasesCount > 0)
        {
            _db.SetAuditReason(BuildBulkUpdateAuditReason(request, nextResponsible, nextStage, nextPriority, changedCasesCount));
        }

        if (casesClosedByUpdate.Count > 0)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCasesAsync(_db, casesClosedByUpdate, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { updated = cases.Count, requested = ids.Length });
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(
        int? clientId,
        int? legalEntityId,
        DataVisibilityAccess visibility,
        CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(visibility)
                .AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }

    private static void ApplyCaseDetails(ClientCase target, ClientCase source)
    {
        var previousCourtDecisionAt = target.CourtDecisionAt;
        var previousAppealDeadlineAt = target.AppealDeadlineAt;
        var previousCassationDeadlineAt = target.CassationDeadlineAt;

        target.CaseNumber = source.CaseNumber?.Trim() ?? string.Empty;
        target.CaseType = source.CaseType?.Trim() ?? string.Empty;
        target.Stage = source.Stage?.Trim() ?? string.Empty;
        target.ResponsibleUserId = source.ResponsibleUserId;
        target.StartDate = source.StartDate;
        target.DueDate = source.DueDate;
        target.FeeAmount = source.FeeAmount;
        target.Priority = string.IsNullOrWhiteSpace(source.Priority) ? "normal" : source.Priority.Trim();
        target.CourtName = source.CourtName?.Trim() ?? string.Empty;
        target.CourtCaseNumber = source.CourtCaseNumber?.Trim() ?? string.Empty;
        target.CourtUrl = source.CourtUrl?.Trim() ?? string.Empty;
        target.NextHearingAt = source.NextHearingAt;
        target.Claimant = source.Claimant?.Trim() ?? string.Empty;
        target.Defendant = source.Defendant?.Trim() ?? string.Empty;
        target.ThirdParties = source.ThirdParties?.Trim() ?? string.Empty;
        target.Representatives = source.Representatives?.Trim() ?? string.Empty;
        target.JudgeName = source.JudgeName?.Trim() ?? string.Empty;
        target.Courtroom = source.Courtroom?.Trim() ?? string.Empty;
        target.CourtSecretaryContacts = source.CourtSecretaryContacts?.Trim() ?? string.Empty;
        target.ClaimFiledAt = source.ClaimFiledAt;
        target.CourtDecisionAt = source.CourtDecisionAt;
        target.AppealDeadlineAt = ResolveProceduralDeadline(
            source.AppealDeadlineAt,
            target.CourtDecisionAt,
            previousCourtDecisionAt,
            previousAppealDeadlineAt,
            monthsToAdd: 1);
        target.CassationDeadlineAt = ResolveProceduralDeadline(
            source.CassationDeadlineAt,
            target.CourtDecisionAt,
            previousCourtDecisionAt,
            previousCassationDeadlineAt,
            monthsToAdd: 3);
        target.MotionDeadlineAt = source.MotionDeadlineAt;
        target.ObjectionDeadlineAt = source.ObjectionDeadlineAt;
        target.DocumentsSummary = source.DocumentsSummary?.Trim() ?? string.Empty;
        target.EventHistory = source.EventHistory?.Trim() ?? string.Empty;
        target.Strategy = source.Strategy?.Trim() ?? string.Empty;
        target.InactivityRemindersEnabled = source.InactivityRemindersEnabled;
        target.IsHiddenFromExecutor = source.IsHiddenFromExecutor;
    }

    private static void ApplyHiddenFromExecutorMetadata(
        ClientCase clientCase,
        bool isHidden,
        int? actorUserId,
        string actorName)
    {
        clientCase.IsHiddenFromExecutor = isHidden;
        if (isHidden)
        {
            clientCase.HiddenFromExecutorAt = DateTime.UtcNow;
            clientCase.HiddenFromExecutorByUserId = actorUserId;
            clientCase.HiddenFromExecutorByUserName = Truncate(actorName, 256);
            return;
        }

        clientCase.HiddenFromExecutorAt = null;
        clientCase.HiddenFromExecutorByUserId = null;
        clientCase.HiddenFromExecutorByUserName = string.Empty;
        clientCase.AccessRequestedAt = null;
        clientCase.AccessRequestedByUserId = null;
        clientCase.AccessRequestedByUserName = string.Empty;
        clientCase.AccessRequestReason = string.Empty;
    }

    private static DateTime? ResolveProceduralDeadline(
        DateTime? requestedDeadline,
        DateTime? courtDecisionAt,
        DateTime? previousCourtDecisionAt,
        DateTime? previousDeadline,
        int monthsToAdd)
    {
        if (requestedDeadline.HasValue)
        {
            return requestedDeadline.Value;
        }

        if (!courtDecisionAt.HasValue)
        {
            return null;
        }

        if (previousDeadline.HasValue && SameDate(previousCourtDecisionAt, courtDecisionAt))
        {
            return null;
        }

        if (previousCourtDecisionAt.HasValue && previousDeadline.HasValue)
        {
            var previousCalculated = CalculateProceduralDeadline(previousCourtDecisionAt.Value, monthsToAdd);
            if (!SameDate(previousDeadline, previousCalculated))
            {
                return null;
            }
        }

        return CalculateProceduralDeadline(courtDecisionAt.Value, monthsToAdd);
    }

    private static DateTime CalculateProceduralDeadline(DateTime decisionAt, int monthsToAdd)
    {
        var calculated = decisionAt.Date.AddMonths(monthsToAdd);
        while (IsRussianNonWorkingDay(calculated))
        {
            calculated = calculated.AddDays(1);
        }

        return DateTime.SpecifyKind(calculated, decisionAt.Kind);
    }

    private static bool IsRussianNonWorkingDay(DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsFixedRussianHoliday(date);

    private static bool IsFixedRussianHoliday(DateTime date)
        => (date.Month == 1 && date.Day is >= 1 and <= 8)
           || (date.Month == 2 && date.Day == 23)
           || (date.Month == 3 && date.Day == 8)
           || (date.Month == 5 && date.Day is 1 or 9)
           || (date.Month == 6 && date.Day == 12)
           || (date.Month == 11 && date.Day == 4);

    private static bool SameDate(DateTime? left, DateTime? right)
        => left.HasValue == right.HasValue && (!left.HasValue || left.Value.Date == right!.Value.Date);

    private async Task ApplyFinanceSummaryAsync(IReadOnlyCollection<ClientCase> cases, CancellationToken ct)
    {
        var caseIds = cases
            .Select(item => item.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (caseIds.Length == 0)
        {
            return;
        }

        var caseById = cases.ToDictionary(item => item.Id);
        var caseIdSet = caseIds.ToHashSet();
        var payments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.ClientCaseId.HasValue && caseIds.Contains(payment.ClientCaseId.Value)) ||
                payment.InvoiceClientCases.Any(link => caseIds.Contains(link.ClientCaseId)))
            .ToListAsync(ct);

        foreach (var payment in InvoiceManagedPaymentRules.FilterVisiblePayments(payments))
        {
            var linkedCaseIds = payment.InvoiceClientCases
                .Select(link => link.ClientCaseId)
                .Concat(payment.ClientCaseId.HasValue ? new[] { payment.ClientCaseId.Value } : Array.Empty<int>())
                .Where(caseIdSet.Contains)
                .Distinct();
            var outstanding = payment.Amount > payment.PaidAmount
                ? Math.Round(payment.Amount - payment.PaidAmount, 2)
                : 0m;

            foreach (var caseId in linkedCaseIds)
            {
                if (!caseById.TryGetValue(caseId, out var clientCase))
                {
                    continue;
                }

                if (payment.Type == PaymentType.Income)
                {
                    clientCase.IncomeTotal += payment.Amount;
                    clientCase.IncomePaid += payment.PaidAmount;
                    clientCase.IncomeOutstanding += outstanding;
                    clientCase.IncomeCount += 1;
                }
                else
                {
                    clientCase.ExpenseTotal += payment.Amount;
                    clientCase.ExpensePaid += payment.PaidAmount;
                    clientCase.ExpenseOutstanding += outstanding;
                    clientCase.ExpenseCount += 1;
                }
            }
        }
    }

    private async Task ApplyTaskSummaryAsync(IReadOnlyCollection<ClientCase> cases, CancellationToken ct)
    {
        var caseIds = cases
            .Select(item => item.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (caseIds.Length == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var summaryByCaseId = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(task => task.ClientCaseId.HasValue && caseIds.Contains(task.ClientCaseId.Value))
            .GroupBy(task => task.ClientCaseId!.Value)
            .Select(group => new
            {
                CaseId = group.Key,
                Total = group.Count(),
                Open = group.Count(task => task.Status != "done" && task.Status != "cancelled"),
                Completed = group.Count(task => task.Status == "done" || task.Status == "cancelled"),
                Overdue = group.Count(task => task.Status != "done" && task.Status != "cancelled" && task.DueAt < now),
                LastTaskAt = group.Max(task => task.UpdatedAt ?? task.CreatedAt),
            })
            .ToDictionaryAsync(item => item.CaseId, ct);

        foreach (var clientCase in cases)
        {
            if (!summaryByCaseId.TryGetValue(clientCase.Id, out var summary))
            {
                continue;
            }

            clientCase.OpenTaskCount = summary.Open;
            clientCase.CompletedTaskCount = summary.Completed;
            clientCase.OverdueTaskCount = summary.Overdue;
            clientCase.LastTaskAt = summary.LastTaskAt;
            clientCase.TaskProgressPercent = summary.Total == 0
                ? 0
                : (int)Math.Round(summary.Completed * 100m / summary.Total, MidpointRounding.AwayFromZero);
        }
    }

    private static CaseDocumentTemplate BuildCaseDocumentTemplate(ClientCase clientCase, GenerateCaseDocumentTemplateRequest? request)
    {
        var templateKey = NormalizeCaseDocumentTemplateKey(request?.TemplateKey, request?.DocumentType);
        var definition = GetCaseDocumentTemplateDefinition(templateKey);
        var documentType = NormalizeOptional(request?.DocumentType) ?? definition.DocumentType;
        var status = NormalizeRequired(request?.Status, "Подготовлен");
        var caseNumber = !string.IsNullOrWhiteSpace(clientCase.CaseNumber)
            ? clientCase.CaseNumber.Trim()
            : clientCase.Id.ToString();
        var fileName = $"{definition.Title} - дело {caseNumber}.html";

        return new CaseDocumentTemplate(
            $"template-{definition.Key}",
            documentType,
            status,
            fileName,
            BuildCaseDocumentTemplateHtml(clientCase, definition, documentType));
    }

    private async Task<IReadOnlyList<CaseDocumentTemplateDefinition>> GetCaseDocumentTemplateLibraryAsync(CancellationToken ct)
    {
        var rawJson = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == CaseDocumentTemplatesSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

        var storage = DeserializeCaseDocumentTemplateLibrary(rawJson);
        var templates = storage?.Templates
            .Select(static item => new CaseDocumentTemplateDefinition(
                item.Key,
                item.Label,
                item.DocumentType,
                item.Heading,
                item.Description,
                item.Content,
                item.Version <= 0 ? 1 : item.Version,
                item.IsActive,
                item.UpdatedAt))
            .ToList();

        return NormalizeCaseDocumentTemplateLibrary(
            templates is { Count: > 0 }
                ? templates
                : CaseDocumentTemplateDefinitions.ToList());
    }

    private async Task SaveCaseDocumentTemplateLibraryAsync(
        IReadOnlyCollection<CaseDocumentTemplateDefinition> templates,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var storage = new CaseDocumentTemplateLibraryStorage
        {
            Templates = NormalizeCaseDocumentTemplateLibrary(templates)
                .Select(template => new CaseDocumentTemplateStorage
                {
                    Key = template.Key,
                    Label = template.Title,
                    DocumentType = template.DocumentType,
                    Heading = template.Heading,
                    Description = template.Description,
                    Content = template.Content,
                    Version = template.Version <= 0 ? 1 : template.Version,
                    IsActive = template.IsActive,
                    UpdatedAt = template.UpdatedAt ?? now,
                })
                .ToList(),
        };
        var value = JsonSerializer.Serialize(storage, CaseDocumentTemplateJsonOptions);
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == CaseDocumentTemplatesSettingKey, ct);
        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = CaseDocumentTemplatesSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static CaseDocumentTemplateLibraryStorage? DeserializeCaseDocumentTemplateLibrary(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaseDocumentTemplateLibraryStorage>(rawJson, CaseDocumentTemplateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<CaseDocumentTemplateDefinition> NormalizeCaseDocumentTemplateLibrary(
        IEnumerable<CaseDocumentTemplateDefinition> templates)
    {
        var result = new List<CaseDocumentTemplateDefinition>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            var key = NormalizeDocumentTemplateCode(template.Key);
            if (key is null || !usedKeys.Add(key))
            {
                continue;
            }

            var title = NormalizeRequired(template.Title, template.Heading);
            title = NormalizeRequired(title, key);
            var documentType = NormalizeRequired(template.DocumentType, "Документ");
            result.Add(template with
            {
                Key = key,
                Title = title,
                DocumentType = documentType,
                Heading = NormalizeRequired(template.Heading, title),
                Version = template.Version <= 0 ? 1 : template.Version,
                IsActive = template.IsActive,
            });
        }

        return result.Count > 0
            ? result
            : CaseDocumentTemplateDefinitions.ToList();
    }

    private static (CaseDocumentTemplateDefinition Template, string? Error) BuildCaseDocumentTemplateDefinition(
        UpsertCaseDocumentTemplateRequest? request,
        CaseDocumentTemplateDefinition? existing)
    {
        var key = existing?.Key ?? NormalizeDocumentTemplateCode(request?.Key);
        if (key is null)
        {
            return (default, "Укажите код шаблона латиницей: буквы, цифры, дефис или подчеркивание.");
        }

        var label = NormalizeOptional(request?.Label)
            ?? NormalizeOptional(request?.Name)
            ?? existing?.Title;
        if (string.IsNullOrWhiteSpace(label))
        {
            return (default, "Укажите название шаблона.");
        }

        var documentType = NormalizeOptional(request?.DocumentType) ?? existing?.DocumentType ?? "Документ";
        var heading = NormalizeOptional(request?.Heading) ?? existing?.Heading ?? label;
        var now = DateTime.UtcNow;

        return (new CaseDocumentTemplateDefinition(
            key,
            label,
            documentType,
            heading,
            request?.Description ?? existing?.Description,
            request?.Content ?? request?.Html ?? existing?.Content,
            existing.HasValue ? existing.Value.Version + 1 : 1,
            request?.IsActive ?? existing?.IsActive ?? true,
            now), null);
    }

    private static CaseDocumentTemplateDefinitionDto MapDocumentTemplateDefinition(CaseDocumentTemplateDefinition template)
        => new()
        {
            Key = template.Key,
            Label = template.Title,
            DocumentType = template.DocumentType,
            Heading = template.Heading,
            Description = template.Description,
            Content = template.Content,
            Version = template.Version <= 0 ? 1 : template.Version,
            IsActive = template.IsActive,
            UpdatedAt = template.UpdatedAt,
        };

    private static CaseDocumentTemplateDefinition GetCaseDocumentTemplateDefinition(string key)
    {
        var definition = CaseDocumentTemplateDefinitions.FirstOrDefault(item => item.Key == key);
        return string.IsNullOrWhiteSpace(definition.Key) ? CaseDocumentTemplateDefinitions[0] : definition;
    }

    private static string NormalizeCaseDocumentTemplateKey(string? templateKey, string? documentType)
    {
        var normalized = templateKey?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized switch
            {
                "claim" or "isk" => "claim",
                "motion" => "motion",
                "objection" => "objection",
                "court-letter" or "letter" => "court-letter",
                _ => "case-summary",
            };
        }

        var type = documentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type.Contains("иск", StringComparison.OrdinalIgnoreCase)) return "claim";
        if (type.Contains("ходатай", StringComparison.OrdinalIgnoreCase)) return "motion";
        if (type.Contains("возраж", StringComparison.OrdinalIgnoreCase)) return "objection";
        if (type.Contains("переписк", StringComparison.OrdinalIgnoreCase) || type.Contains("письм", StringComparison.OrdinalIgnoreCase)) return "court-letter";
        return "case-summary";
    }

    private static string? NormalizeDocumentTemplateCode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
    }

    private static string BuildCaseDocumentTemplateHtml(
        ClientCase clientCase,
        CaseDocumentTemplateDefinition definition,
        string documentType)
    {
        var ownerName = ResolveCaseOwnerName(clientCase);
        var responsibleName = clientCase.ResponsibleUser?.FullName ?? string.Empty;
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(definition.Content))
        {
            return BuildCustomCaseDocumentTemplateHtml(clientCase, definition, ownerName, responsibleName, documentType, now);
        }

        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>
""");
        html.Append(E(definition.Title));
        html.Append("""
</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 32px; color: #111827; line-height: 1.45; }
    .document-template { max-width: 780px; margin: 0 auto; }
    h1 { font-size: 22px; text-align: center; margin: 28px 0; text-transform: uppercase; }
    h2 { font-size: 15px; margin: 24px 0 8px; }
    .meta { width: 100%; border-collapse: collapse; margin: 16px 0 24px; }
    .meta td { border: 1px solid #d1d5db; padding: 8px 10px; vertical-align: top; }
    .meta td:first-child { width: 34%; font-weight: 700; background: #f9fafb; }
    .recipient { text-align: right; margin-bottom: 28px; }
    .body p { margin: 0 0 12px; }
    .signature { margin-top: 42px; display: flex; justify-content: space-between; gap: 24px; }
    .muted { color: #6b7280; font-size: 12px; }
  </style>
</head>
<body>
  <div class="document-template">
""");

        if (!string.IsNullOrWhiteSpace(clientCase.CourtName))
        {
            html.AppendLine("""    <div class="recipient">""");
            html.Append("      <div>В ");
            html.Append(E(clientCase.CourtName));
            html.AppendLine("</div>");
            if (!string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber))
            {
                html.Append("      <div>по делу № ");
                html.Append(E(clientCase.CourtCaseNumber));
                html.AppendLine("</div>");
            }
            html.AppendLine("    </div>");
        }

        html.Append("    <h1>");
        html.Append(E(definition.Heading));
        html.AppendLine("</h1>");
        html.AppendLine("    <table class=\"meta\">");
        AppendTemplateRow(html, "Карточка дела", clientCase.Title);
        AppendTemplateRow(html, "Номер дела", clientCase.CaseNumber);
        AppendTemplateRow(html, "Клиент / доверитель", ownerName);
        AppendTemplateRow(html, "Тип дела", clientCase.CaseType);
        AppendTemplateRow(html, "Стадия", clientCase.Stage);
        AppendTemplateRow(html, "Ответственный", responsibleName);
        AppendTemplateRow(html, "Истец / заявитель", clientCase.Claimant);
        AppendTemplateRow(html, "Ответчик / должник", clientCase.Defendant);
        AppendTemplateRow(html, "Третьи лица", clientCase.ThirdParties);
        AppendTemplateRow(html, "Представители", clientCase.Representatives);
        AppendTemplateRow(html, "Судья", clientCase.JudgeName);
        AppendTemplateRow(html, "Ближайшее заседание", FormatTemplateDateTime(clientCase.NextHearingAt));
        AppendTemplateRow(html, "Дата подачи заявления", FormatTemplateDate(clientCase.ClaimFiledAt));
        AppendTemplateRow(html, "Дата решения суда", FormatTemplateDate(clientCase.CourtDecisionAt));
        AppendTemplateRow(html, "Срок апелляции", FormatTemplateDate(clientCase.AppealDeadlineAt));
        AppendTemplateRow(html, "Срок кассации", FormatTemplateDate(clientCase.CassationDeadlineAt));
        html.AppendLine("    </table>");

        html.AppendLine("    <div class=\"body\">");
        html.AppendLine("      <h2>Текст документа</h2>");
        foreach (var paragraph in BuildTemplateParagraphs(definition.Key, clientCase, ownerName, documentType))
        {
            html.Append("      <p>");
            html.Append(E(paragraph));
            html.AppendLine("</p>");
        }
        html.AppendLine("    </div>");
        html.AppendLine("    <div class=\"signature\">");
        html.Append("      <div>Дата: ");
        html.Append(E(now.ToString("dd.MM.yyyy")));
        html.AppendLine("</div>");
        html.AppendLine("      <div>Подпись: ____________________</div>");
        html.AppendLine("    </div>");
        html.Append("    <p class=\"muted\">Сформировано автоматически из карточки дела CRM ");
        html.Append(E(now.ToString("dd.MM.yyyy HH:mm")));
        html.AppendLine(".</p>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string BuildCustomCaseDocumentTemplateHtml(
        ClientCase clientCase,
        CaseDocumentTemplateDefinition definition,
        string ownerName,
        string responsibleName,
        string documentType,
        DateTime now)
    {
        var renderedContent = ReplaceCaseDocumentTemplateTokens(
            definition.Content ?? string.Empty,
            clientCase,
            ownerName,
            responsibleName,
            documentType,
            now);
        if (renderedContent.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return renderedContent;
        }

        var contentHtml = renderedContent.Contains('<')
            ? renderedContent
            : string.Join("<br>", renderedContent
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(E));

        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>
""");
        html.Append(E(definition.Title));
        html.Append("""
</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 32px; color: #111827; line-height: 1.45; }
    .document-template { max-width: 780px; margin: 0 auto; }
    h1 { font-size: 22px; text-align: center; margin: 28px 0; text-transform: uppercase; }
    .body { margin-top: 24px; }
    .muted { color: #6b7280; font-size: 12px; margin-top: 36px; }
  </style>
</head>
<body>
  <div class="document-template">
    <h1>
""");
        html.Append(E(definition.Heading));
        html.AppendLine("</h1>");
        html.AppendLine("    <div class=\"body\">");
        html.Append(contentHtml);
        html.AppendLine();
        html.Append("    </div><p class=\"muted\">Сформировано автоматически из карточки дела CRM ");
        html.Append(E(now.ToString("dd.MM.yyyy HH:mm")));
        html.AppendLine(".</p>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string ReplaceCaseDocumentTemplateTokens(
        string content,
        ClientCase clientCase,
        string ownerName,
        string responsibleName,
        string documentType,
        DateTime now)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{{case.id}}"] = clientCase.Id.ToString(CultureInfo.InvariantCulture),
            ["{{case.title}}"] = clientCase.Title,
            ["{{case.number}}"] = clientCase.CaseNumber ?? string.Empty,
            ["{{case.type}}"] = clientCase.CaseType ?? string.Empty,
            ["{{case.stage}}"] = clientCase.Stage ?? string.Empty,
            ["{{case.priority}}"] = clientCase.Priority ?? string.Empty,
            ["{{client.name}}"] = ownerName,
            ["{{responsible.name}}"] = responsibleName,
            ["{{court.name}}"] = clientCase.CourtName ?? string.Empty,
            ["{{court.caseNumber}}"] = clientCase.CourtCaseNumber ?? string.Empty,
            ["{{claimant}}"] = clientCase.Claimant ?? string.Empty,
            ["{{defendant}}"] = clientCase.Defendant ?? string.Empty,
            ["{{thirdParties}}"] = clientCase.ThirdParties ?? string.Empty,
            ["{{representatives}}"] = clientCase.Representatives ?? string.Empty,
            ["{{judge.name}}"] = clientCase.JudgeName ?? string.Empty,
            ["{{nextHearingAt}}"] = FormatTemplateDateTime(clientCase.NextHearingAt),
            ["{{claimFiledAt}}"] = FormatTemplateDate(clientCase.ClaimFiledAt),
            ["{{courtDecisionAt}}"] = FormatTemplateDate(clientCase.CourtDecisionAt),
            ["{{appealDeadlineAt}}"] = FormatTemplateDate(clientCase.AppealDeadlineAt),
            ["{{cassationDeadlineAt}}"] = FormatTemplateDate(clientCase.CassationDeadlineAt),
            ["{{document.type}}"] = documentType,
            ["{{today}}"] = now.ToString("dd.MM.yyyy"),
            ["{{now}}"] = now.ToString("dd.MM.yyyy HH:mm"),
        };

        var result = content;
        foreach (var pair in values)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IReadOnlyList<string> BuildTemplateParagraphs(
        string templateKey,
        ClientCase clientCase,
        string ownerName,
        string documentType)
        => templateKey switch
        {
            "claim" =>
            [
                $"В интересах доверителя {ValueOrDash(ownerName)} подготовлено исковое заявление по делу «{ValueOrDash(clientCase.Title)}».",
                "Прошу суд принять заявление к производству, рассмотреть приложенные материалы и вынести судебный акт с учетом фактических обстоятельств дела.",
                "Основания, требования и перечень приложений необходимо уточнить перед отправкой документа."
            ],
            "motion" =>
            [
                $"По делу «{ValueOrDash(clientCase.Title)}» заявляется ходатайство.",
                "Прошу суд рассмотреть настоящее ходатайство и приобщить его к материалам дела.",
                "Конкретную просьбу и правовое основание необходимо заполнить перед направлением документа."
            ],
            "objection" =>
            [
                $"По делу «{ValueOrDash(clientCase.Title)}» подготовлены возражения.",
                "С доводами противоположной стороны не согласны по основаниям, которые подлежат уточнению в тексте документа.",
                "Прошу учесть настоящие возражения при рассмотрении дела."
            ],
            "court-letter" =>
            [
                $"Направляем информацию по делу «{ValueOrDash(clientCase.Title)}».",
                "Просим принять документ к сведению и при необходимости приобщить к материалам дела.",
                "Реквизиты адресата, исходящий номер и приложения необходимо уточнить перед отправкой."
            ],
            _ =>
            [
                $"Справка сформирована по карточке дела «{ValueOrDash(clientCase.Title)}».",
                $"Клиент / доверитель: {ValueOrDash(ownerName)}.",
                $"Тип документа: {ValueOrDash(documentType)}. Данные предназначены для подготовки рабочего документа и последующей ручной проверки."
            ],
        };

    private static void AppendTemplateRow(StringBuilder html, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        html.Append("      <tr><td>");
        html.Append(E(label));
        html.Append("</td><td>");
        html.Append(E(value));
        html.AppendLine("</td></tr>");
    }

    private static string ResolveCaseOwnerName(ClientCase clientCase)
    {
        if (clientCase.Client is not null && !string.IsNullOrWhiteSpace(clientCase.Client.Name))
        {
            return clientCase.Client.Name;
        }

        if (clientCase.LegalEntity is not null)
        {
            if (!string.IsNullOrWhiteSpace(clientCase.LegalEntity.ShortName))
            {
                return clientCase.LegalEntity.ShortName;
            }

            if (!string.IsNullOrWhiteSpace(clientCase.LegalEntity.FullName))
            {
                return clientCase.LegalEntity.FullName;
            }
        }

        return string.Empty;
    }

    private static string FormatTemplateDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy") : string.Empty;

    private static string FormatTemplateDateTime(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy HH:mm") : string.Empty;

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string E(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static IReadOnlyList<ClientCaseDocumentDto> MapDocuments(IReadOnlyCollection<ClientCaseDocument> documents)
    {
        var latestVersions = documents
            .GroupBy(item => item.DocumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Version), StringComparer.OrdinalIgnoreCase);

        return documents
            .Select(item => MapDocument(item, latestVersions.TryGetValue(item.DocumentKey, out var latest) && item.Version == latest))
            .ToList();
    }

    private async Task<IReadOnlyList<ClientCaseDocumentDto>> MapDocumentsWithLatestAsync(
        IReadOnlyCollection<ClientCaseDocument> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return Array.Empty<ClientCaseDocumentDto>();
        }

        var caseIds = documents.Select(item => item.ClientCaseId).Distinct().ToArray();
        var documentKeys = documents.Select(item => item.DocumentKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var latestVersions = await _db.ClientCaseDocuments
            .AsNoTracking()
            .Where(item => caseIds.Contains(item.ClientCaseId) && documentKeys.Contains(item.DocumentKey))
            .GroupBy(item => new { item.ClientCaseId, item.DocumentKey })
            .Select(group => new
            {
                group.Key.ClientCaseId,
                group.Key.DocumentKey,
                Version = group.Max(item => item.Version),
            })
            .ToListAsync(ct);
        var latestByKey = latestVersions.ToDictionary(
            item => $"{item.ClientCaseId}:{item.DocumentKey}",
            item => item.Version,
            StringComparer.OrdinalIgnoreCase);

        return documents
            .Select(item => MapDocument(
                item,
                latestByKey.TryGetValue($"{item.ClientCaseId}:{item.DocumentKey}", out var latest) && item.Version == latest))
            .ToList();
    }

    private static ClientCaseDocumentDto MapDocument(ClientCaseDocument document, bool isLatestVersion)
    {
        var template = ResolveDocumentTemplate(document.DocumentKey);

        return new ClientCaseDocumentDto
        {
            Id = document.Id,
            ClientCaseId = document.ClientCaseId,
            DocumentKey = document.DocumentKey,
            TemplateKey = template?.Key,
            TemplateLabel = template?.Title,
            SourceTaskId = ResolveDocumentSourceTaskId(document.DocumentKey),
            Version = document.Version,
            DocumentType = document.DocumentType,
            Status = document.Status,
            OriginalFileName = document.OriginalFileName,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            UploadedByUserId = document.UploadedByUserId,
            UploadedByName = document.UploadedByName,
            UploadedAt = document.UploadedAt,
            IsLatestVersion = isLatestVersion,
        };
    }

    private static CaseDocumentTemplateDefinition? ResolveDocumentTemplate(string documentKey)
    {
        const string templatePrefix = "template-";
        if (!documentKey.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var templateKey = documentKey[templatePrefix.Length..];
        var definition = CaseDocumentTemplateDefinitions.FirstOrDefault(item =>
            string.Equals(item.Key, templateKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(definition.Key) ? null : definition;
    }

    private static int? ResolveDocumentSourceTaskId(string documentKey)
    {
        const string taskPrefix = "task-";
        if (!documentKey.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = documentKey[taskPrefix.Length..];
        var separatorIndex = rest.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return null;
        }

        return int.TryParse(rest[..separatorIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskId)
            ? taskId
            : null;
    }

    private static ClientCaseSummaryDto MapCaseSummary(ClientCase clientCase)
    {
        var paidAmount = clientCase.IncomePaid;
        var debtAmount = clientCase.FeeAmount > 0
            ? Math.Max(clientCase.FeeAmount - paidAmount, 0m)
            : clientCase.IncomeOutstanding;

        return new ClientCaseSummaryDto
        {
            Id = clientCase.Id,
            Title = clientCase.Title,
            CaseNumber = clientCase.CaseNumber,
            Status = clientCase.Status.ToString(),
            StatusLabel = FormatCaseStatus(clientCase.Status),
            Priority = clientCase.Priority,
            PriorityLabel = FormatCasePriority(clientCase.Priority),
            Stage = clientCase.Stage,
            OwnerName = clientCase.Client?.Name
                ?? clientCase.LegalEntity?.ShortName
                ?? clientCase.LegalEntity?.FullName
                ?? string.Empty,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = FormatCaseResponsible(clientCase.ResponsibleUser),
            StartDate = clientCase.StartDate,
            DueDate = clientCase.DueDate,
            NextHearingAt = clientCase.NextHearingAt,
            OpenTaskCount = clientCase.OpenTaskCount,
            OverdueTaskCount = clientCase.OverdueTaskCount,
            CompletedTaskCount = clientCase.CompletedTaskCount,
            TaskProgressPercent = clientCase.TaskProgressPercent,
            FeeAmount = clientCase.FeeAmount,
            PaidAmount = paidAmount,
            DebtAmount = debtAmount,
            LastTaskAt = clientCase.LastTaskAt,
            LastViewedAt = clientCase.LastViewedAt,
        };
    }

    private static ClientCaseAccessRequestListItemDto MapClientCaseAccessRequest(ClientCase clientCase)
        => new()
        {
            ClientCaseId = clientCase.Id,
            CaseNumber = clientCase.CaseNumber,
            Title = clientCase.Title,
            OwnerName = clientCase.Client?.Name
                ?? clientCase.LegalEntity?.ShortName
                ?? clientCase.LegalEntity?.FullName
                ?? string.Empty,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = FormatCaseResponsible(clientCase.ResponsibleUser),
            RequestedAt = clientCase.AccessRequestedAt!.Value,
            RequestedByUserId = clientCase.AccessRequestedByUserId,
            RequestedByUserName = clientCase.AccessRequestedByUserName,
            Reason = clientCase.AccessRequestReason,
            IsHiddenFromExecutor = clientCase.IsHiddenFromExecutor,
        };

    private async Task<Dictionary<int, Contract>> LoadFinanceContractsAsync(
        IReadOnlyCollection<Payment> visiblePayments,
        IReadOnlyCollection<int> invoicePaymentIds,
        IReadOnlyCollection<int> paymentIds,
        CancellationToken ct)
    {
        var contractsById = visiblePayments
            .Where(payment => payment.Contract is not null)
            .Select(payment => payment.Contract!)
            .GroupBy(contract => contract.Id)
            .ToDictionary(group => group.Key, group => group.First());

        if (invoicePaymentIds.Count > 0)
        {
            var invoiceContracts = await _db.ContractInvoices
                .AsNoTracking()
                .Include(link => link.Contract)
                .Where(link => invoicePaymentIds.Contains(link.InvoiceId))
                .Select(link => link.Contract)
                .ToListAsync(ct);

            foreach (var contract in invoiceContracts)
            {
                contractsById.TryAdd(contract.Id, contract);
            }
        }

        if (paymentIds.Count > 0)
        {
            var paymentContracts = await _db.ContractPayments
                .AsNoTracking()
                .Include(link => link.Contract)
                .Where(link => paymentIds.Contains(link.PaymentId))
                .Select(link => link.Contract)
                .ToListAsync(ct);

            foreach (var contract in paymentContracts)
            {
                contractsById.TryAdd(contract.Id, contract);
            }
        }

        return contractsById;
    }

    private static ClientCaseFinanceTotalsDto BuildFinanceTotals(ClientCase clientCase, IReadOnlyCollection<Payment> payments)
    {
        var totals = new ClientCaseFinanceTotalsDto
        {
            FeeAmount = clientCase.FeeAmount,
        };

        foreach (var payment in payments)
        {
            var outstanding = ResolveFinancePaymentOutstanding(payment);
            if (payment.Type == PaymentType.Income)
            {
                totals.IncomeTotal += payment.Amount;
                totals.IncomePaid += payment.PaidAmount;
                totals.IncomeOutstanding += outstanding;
                totals.IncomeCount += 1;
            }
            else
            {
                totals.ExpenseTotal += payment.Amount;
                totals.ExpensePaid += payment.PaidAmount;
                totals.ExpenseOutstanding += outstanding;
                totals.ExpenseCount += 1;
            }
        }

        totals.PlannedMargin = totals.IncomeTotal - totals.ExpenseTotal;
        totals.PaidMargin = totals.IncomePaid - totals.ExpensePaid;
        totals.OutstandingBalance = clientCase.FeeAmount > 0
            ? Math.Max(clientCase.FeeAmount - totals.IncomePaid, 0m)
            : totals.IncomeOutstanding;
        return totals;
    }

    private static ClientCaseFinanceCalendarDto BuildFinanceCalendar(IReadOnlyCollection<Payment> payments)
    {
        var today = DateTime.UtcNow.Date;
        var calendar = new ClientCaseFinanceCalendarDto();

        foreach (var payment in payments)
        {
            var outstanding = ResolveFinancePaymentOutstanding(payment);
            calendar.PaymentCount += 1;
            calendar.PlanTotal += payment.Amount;
            calendar.PaidTotal += payment.PaidAmount;
            calendar.OutstandingTotal += outstanding;

            if (payment.Status == PaymentStatus.Cancelled)
            {
                calendar.CancelledCount += 1;
                continue;
            }

            if (outstanding <= 0m || payment.Status == PaymentStatus.Completed)
            {
                calendar.PaidCount += 1;
                continue;
            }

            var plannedDate = ResolveFinancePaymentCalendarDate(payment).Date;
            if (payment.Status == PaymentStatus.Overdue || plannedDate < today)
            {
                calendar.OverdueCount += 1;
                calendar.OverdueTotal += outstanding;
                continue;
            }

            calendar.PlannedCount += 1;
            calendar.NextPaymentDate = calendar.NextPaymentDate is null || plannedDate < calendar.NextPaymentDate.Value.Date
                ? plannedDate
                : calendar.NextPaymentDate;
        }

        return calendar;
    }

    private static IReadOnlyList<ClientCaseFinanceInvoiceDto> BuildFinanceInvoices(
        IReadOnlyCollection<Payment> payments,
        IReadOnlyDictionary<int, Contract> contractsById)
    {
        return payments
            .Where(IsFinanceInvoiceCandidate)
            .GroupBy(BuildFinanceInvoiceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => MapFinanceInvoice(group.ToList(), contractsById))
            .OrderByDescending(invoice => invoice.Date ?? DateTime.MinValue)
            .ThenBy(invoice => invoice.Number)
            .ToList();
    }

    private static bool IsFinanceInvoiceCandidate(Payment payment)
        => payment.DocumentKind == DocumentKind.Invoice
           || !string.IsNullOrWhiteSpace(payment.Account)
           || !string.IsNullOrWhiteSpace(payment.LegacyAccountNumber);

    private static string BuildFinanceInvoiceKey(Payment payment)
    {
        var number = NormalizeFinanceInvoiceNumber(payment.LegacyAccountNumber ?? payment.Account);
        var date = payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date;
        if (!string.IsNullOrWhiteSpace(number))
        {
            return $"{payment.Type}:{number}:{date:yyyy-MM-dd}";
        }

        return $"payment:{payment.Id}";
    }

    private static ClientCaseFinanceInvoiceDto MapFinanceInvoice(
        IReadOnlyCollection<Payment> group,
        IReadOnlyDictionary<int, Contract> contractsById)
    {
        var representative = group
            .OrderByDescending(payment => payment.DocumentKind == DocumentKind.Invoice)
            .ThenBy(payment => payment.Id)
            .First();
        var contract = representative.ContractId.HasValue && contractsById.TryGetValue(representative.ContractId.Value, out var directContract)
            ? directContract
            : null;

        var amount = group.Sum(payment => payment.Amount);
        var paidAmount = group.Sum(payment => payment.PaidAmount);
        var outstandingAmount = group.Sum(ResolveFinancePaymentOutstanding);

        return new ClientCaseFinanceInvoiceDto
        {
            Id = representative.Id,
            Number = ResolveFinanceInvoiceNumber(representative),
            Date = representative.LegacyAccountDate ?? representative.AccountDate ?? representative.Date,
            Amount = amount,
            PaidAmount = paidAmount,
            OutstandingAmount = outstandingAmount,
            Status = ResolveFinanceGroupStatus(group),
            WorkflowStatus = representative.InvoiceWorkflowStatus,
            ContractId = contract?.Id ?? representative.ContractId,
            ContractNumber = contract?.Number ?? representative.Contract?.Number,
        };
    }

    private static string ResolveFinanceInvoiceNumber(Payment payment)
    {
        var number = payment.LegacyAccountNumber ?? payment.Account;
        return !string.IsNullOrWhiteSpace(number)
            ? number.Trim()
            : $"#{payment.Id}";
    }

    private static string ResolveFinanceGroupStatus(IReadOnlyCollection<Payment> payments)
    {
        if (payments.Any(payment => payment.Status == PaymentStatus.Overdue))
        {
            return PaymentStatus.Overdue.ToString();
        }

        if (payments.All(payment => payment.Status == PaymentStatus.Completed))
        {
            return PaymentStatus.Completed.ToString();
        }

        if (payments.Any(payment => payment.Status == PaymentStatus.Processing))
        {
            return PaymentStatus.Processing.ToString();
        }

        return payments.First().Status.ToString();
    }

    private static ClientCaseFinancePaymentDto MapFinancePayment(Payment payment)
        => new()
        {
            Id = payment.Id,
            DocumentKind = payment.DocumentKind.ToString(),
            Type = payment.Type.ToString(),
            Amount = payment.Amount,
            PaidAmount = payment.PaidAmount,
            OutstandingAmount = ResolveFinancePaymentOutstanding(payment),
            Status = payment.Status.ToString(),
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            PaidDate = payment.PaidDate,
            InvoiceId = payment.DocumentKind == DocumentKind.Invoice ? payment.Id : null,
            InvoiceNumber = IsFinanceInvoiceCandidate(payment) ? ResolveFinanceInvoiceNumber(payment) : null,
            InvoiceDate = IsFinanceInvoiceCandidate(payment) ? payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date : null,
            ContractId = payment.ContractId,
            ContractNumber = payment.Contract?.Number,
            PaymentSourceName = payment.PaymentSource?.Name,
            PaymentOrderNumber = payment.PaymentOrderNumber,
            PaymentDocumentDate = payment.PaymentDocumentDate,
            Description = payment.Description,
        };

    private static ClientCaseFinanceContractDto MapFinanceContract(Contract contract)
        => new()
        {
            Id = contract.Id,
            Number = contract.Number,
            Title = contract.Title,
            Date = contract.Date,
            Amount = contract.Amount,
            Status = contract.Status,
        };

    private static decimal ResolveFinancePaymentOutstanding(Payment payment)
        => payment.Amount > payment.PaidAmount
            ? Math.Round(payment.Amount - payment.PaidAmount, 2)
            : 0m;

    private static DateTime ResolveFinancePaymentSortDate(Payment payment)
        => payment.PaidDate ?? payment.LastPaymentDate ?? payment.Date;

    private static DateTime ResolveFinancePaymentCalendarDate(Payment payment)
        => payment.PlannedDate != default
            ? payment.PlannedDate
            : payment.Date;

    private static string? NormalizeFinanceInvoiceNumber(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private async Task<ActionResult<(ClientCase ClientCase, ClientCaseWorkOrder WorkOrder)>> LoadEditableWorkOrderAsync(
        int id,
        CancellationToken ct)
    {
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var workOrder = await _db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id, ct);
        if (workOrder is null)
        {
            return NotFound();
        }

        return (clientCase, workOrder);
    }

    private async Task<(ClientCaseWorkOrderItem? Item, string? Error)> BuildWorkOrderItemAsync(
        ClientCaseWorkOrder workOrder,
        UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        var nextSortOrder = workOrder.Items.Count == 0 ? 1 : workOrder.Items.Max(item => item.SortOrder) + 1;
        var item = new ClientCaseWorkOrderItem
        {
            ClientCaseWorkOrderId = workOrder.Id,
            SortOrder = request?.SortOrder > 0 ? request.SortOrder.Value : nextSortOrder,
        };
        var error = await ApplyWorkOrderItemAsync(item, request, ct);
        return (item, error);
    }

    private async Task<string?> ApplyWorkOrderItemAsync(
        ClientCaseWorkOrderItem item,
        UpsertClientCaseWorkOrderItemRequest? request,
        CancellationToken ct)
    {
        item.ServiceName = Truncate(NormalizeRequired(request?.ServiceName, item.ServiceName), 200);
        item.Description = Truncate(NormalizeOptional(request?.Description) ?? item.Description, 1000);
        item.DueDate = request?.DueDate ?? item.DueDate;
        item.Quantity = NormalizePositiveMoney(request?.Quantity, item.Quantity <= 0 ? 1 : item.Quantity);
        item.UnitPrice = NormalizeNonNegativeMoney(request?.UnitPrice, item.UnitPrice);
        item.TotalAmount = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
        if (request?.SortOrder is > 0)
        {
            item.SortOrder = request.SortOrder.Value;
        }

        if (request?.ExecutorUserId is not null)
        {
            if (request.ExecutorUserId <= 0)
            {
                item.ExecutorUserId = null;
                item.ExecutorName = string.Empty;
            }
            else
            {
                var executor = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(user => user.Id == request.ExecutorUserId.Value && user.IsActive, ct);
                if (executor is null)
                {
                    return "Исполнитель услуги не найден или неактивен.";
                }

                item.ExecutorUserId = executor.Id;
                item.ExecutorName = executor.FullName;
            }
        }
        else if (NormalizeOptional(request?.ExecutorName) is { } executorName)
        {
            item.ExecutorName = Truncate(executorName, 256);
        }

        return null;
    }

    private static void RecalculateWorkOrder(ClientCaseWorkOrder workOrder)
    {
        foreach (var item in workOrder.Items)
        {
            item.TotalAmount = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
        }

        workOrder.TotalAmount = decimal.Round(workOrder.Items.Sum(item => item.TotalAmount), 2, MidpointRounding.AwayFromZero);
    }

    private void StampWorkOrderUpdate(ClientCaseWorkOrder workOrder)
    {
        workOrder.UpdatedAt = DateTime.UtcNow;
        workOrder.UpdatedByUserId = User.GetUserId();
        workOrder.UpdatedByUserName = User.GetDisplayName();
    }

    private void ApplyWorkOrderApprovalDecision(ClientCaseWorkOrder workOrder, string status, string? comment)
    {
        workOrder.Status = status;
        workOrder.ApprovalDecisionAt = DateTime.UtcNow;
        workOrder.ApprovalDecisionByUserId = User.GetUserId();
        workOrder.ApprovalDecisionByUserName = User.GetDisplayName();
        workOrder.ApprovalComment = Truncate(NormalizeOptional(comment) ?? string.Empty, 1000);
        StampWorkOrderUpdate(workOrder);
    }

    private static string FormatOptionalComment(string? comment)
    {
        var normalized = NormalizeOptional(comment);
        return normalized is null ? string.Empty : $" Комментарий: {normalized}.";
    }

    private static ClientCaseWorkOrderDto MapWorkOrder(ClientCaseWorkOrder workOrder)
    {
        RecalculateWorkOrder(workOrder);
        return new ClientCaseWorkOrderDto
        {
            Id = workOrder.Id,
            ClientCaseId = workOrder.ClientCaseId,
            Title = workOrder.Title,
            Status = workOrder.Status,
            StatusLabel = FormatWorkOrderStatus(workOrder.Status),
            Currency = workOrder.Currency,
            Notes = workOrder.Notes,
            TotalAmount = workOrder.TotalAmount,
            CreatedAt = workOrder.CreatedAt,
            CreatedByUserId = workOrder.CreatedByUserId,
            CreatedByUserName = workOrder.CreatedByUserName,
            UpdatedAt = workOrder.UpdatedAt,
            UpdatedByUserId = workOrder.UpdatedByUserId,
            UpdatedByUserName = workOrder.UpdatedByUserName,
            ApprovalRequestedAt = workOrder.ApprovalRequestedAt,
            ApprovalRequestedByUserId = workOrder.ApprovalRequestedByUserId,
            ApprovalRequestedByUserName = workOrder.ApprovalRequestedByUserName,
            ApprovalDecisionAt = workOrder.ApprovalDecisionAt,
            ApprovalDecisionByUserId = workOrder.ApprovalDecisionByUserId,
            ApprovalDecisionByUserName = workOrder.ApprovalDecisionByUserName,
            ApprovalComment = workOrder.ApprovalComment,
            Items = workOrder.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(MapWorkOrderItem)
                .ToList(),
        };
    }

    private static ClientCaseWorkOrderItemDto MapWorkOrderItem(ClientCaseWorkOrderItem item)
        => new()
        {
            Id = item.Id,
            ClientCaseWorkOrderId = item.ClientCaseWorkOrderId,
            ServiceName = item.ServiceName,
            Description = item.Description,
            ExecutorUserId = item.ExecutorUserId,
            ExecutorName = item.ExecutorName,
            DueDate = item.DueDate,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            TotalAmount = item.TotalAmount,
            SortOrder = item.SortOrder,
        };

    private static string BuildWorkOrderPrintHtml(ClientCase clientCase, ClientCaseWorkOrder workOrder)
    {
        RecalculateWorkOrder(workOrder);
        var generatedAt = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var rows = string.Join(Environment.NewLine, workOrder.Items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select((item, index) => $"""
              <tr>
                <td>{index + 1}</td>
                <td>
                  <strong>{Html(item.ServiceName)}</strong>
                  <div class="muted">{Html(item.Description)}</div>
                </td>
                <td>{Html(item.ExecutorName)}</td>
                <td>{Html(FormatRuDate(item.DueDate))}</td>
                <td class="num">{Html(item.Quantity.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")))}</td>
                <td class="num">{Html(FormatMoney(item.UnitPrice))}</td>
                <td class="num">{Html(FormatMoney(item.TotalAmount))}</td>
              </tr>
            """));
        if (string.IsNullOrWhiteSpace(rows))
        {
            rows = """<tr><td colspan="7" class="empty">Услуги пока не добавлены.</td></tr>""";
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Заказ-наряд {{Html(workOrder.Title)}}</title>
  <style>
    :root { color-scheme: light; font-family: Inter, Arial, sans-serif; color: #0f172a; }
    body { margin: 0; padding: 32px; background: #f8fafc; }
    main { max-width: 1100px; margin: 0 auto; background: #fff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 28px; }
    h1 { margin: 0; font-size: 28px; line-height: 1.2; }
    h2 { margin: 28px 0 10px; font-size: 16px; text-transform: uppercase; letter-spacing: .08em; color: #64748b; }
    .meta { margin-top: 8px; color: #64748b; font-size: 13px; }
    .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin-top: 20px; }
    .stat { border: 1px solid #e2e8f0; border-radius: 10px; padding: 12px; background: #f8fafc; }
    .stat span { display: block; color: #64748b; font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }
    .stat strong { display: block; margin-top: 6px; font-size: 18px; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; font-size: 13px; }
    th, td { border-bottom: 1px solid #e2e8f0; padding: 10px 8px; text-align: left; vertical-align: top; }
    th { color: #475569; font-size: 11px; text-transform: uppercase; letter-spacing: .08em; background: #f8fafc; }
    .num { text-align: right; white-space: nowrap; }
    .muted { margin-top: 3px; color: #64748b; font-size: 12px; }
    .empty { text-align: center; color: #64748b; padding: 24px; }
    .total { margin-top: 18px; display: flex; justify-content: flex-end; font-size: 20px; font-weight: 700; }
    @media print { body { padding: 0; background: #fff; } main { border: 0; border-radius: 0; } }
  </style>
</head>
<body>
  <main>
    <h1>Заказ-наряд / предварительный расчет</h1>
    <div class="meta">Сформировано: {{Html(generatedAt)}}</div>
    <div class="grid">
      <div class="stat"><span>Дело</span><strong>{{Html(clientCase.Title)}}</strong><div class="muted">{{Html(clientCase.CaseNumber)}}</div></div>
      <div class="stat"><span>Клиент</span><strong>{{Html(GetOwnerName(clientCase))}}</strong></div>
      <div class="stat"><span>Ответственный</span><strong>{{Html(GetResponsibleName(clientCase))}}</strong></div>
      <div class="stat"><span>Статус</span><strong>{{Html(FormatWorkOrderStatus(workOrder.Status))}}</strong></div>
      <div class="stat"><span>Валюта</span><strong>{{Html(workOrder.Currency)}}</strong></div>
      <div class="stat"><span>Итого</span><strong>{{Html(FormatMoney(workOrder.TotalAmount))}}</strong></div>
    </div>
    <h2>{{Html(workOrder.Title)}}</h2>
    <div class="meta">{{Html(workOrder.Notes)}}</div>
    <table>
      <thead>
        <tr>
          <th>в"–</th>
          <th>Услуга</th>
          <th>Исполнитель</th>
          <th>Срок</th>
          <th class="num">Кол-во</th>
          <th class="num">Цена</th>
          <th class="num">Сумма</th>
        </tr>
      </thead>
      <tbody>
{{rows}}
      </tbody>
    </table>
    <div class="total">Итого: {{Html(FormatMoney(workOrder.TotalAmount))}}</div>
  </main>
</body>
</html>
""";
    }

    private static string NormalizeWorkOrderStatus(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "approved" or "согласован" or "согласовано" => "approved",
            "pending_approval" or "pending-approval" or "on_approval" or "on-approval" or "на согласовании" => "pending_approval",
            "revision_required" or "revision-required" or "returned" or "returned_for_revision" or "на доработке" or "доработка" => "revision_required",
            "invoice_created" or "invoice-created" or "счет создан" or "счёт создан" => "invoice_created",
            "cancelled" or "canceled" or "отменен" or "отменён" => "cancelled",
            _ => "draft",
        };
    }

    private static string FormatWorkOrderStatus(string? value)
        => NormalizeWorkOrderStatus(value) switch
        {
            "approved" => "Согласован",
            "pending_approval" => "На согласовании",
            "revision_required" => "На доработке",
            "invoice_created" => "Счет создан",
            "cancelled" => "Отменен",
            _ => "Черновик",
        };

    private static bool IsWorkOrderLockedForEditing(ClientCaseWorkOrder workOrder)
        => IsWorkOrderApprovalManagedStatus(workOrder.Status);

    private static bool IsWorkOrderApprovalManagedStatus(string? status)
        => NormalizeWorkOrderStatus(status) is "pending_approval" or "approved" or "invoice_created";

    private static string BuildWorkOrderLockedMessage(string? status)
        => NormalizeWorkOrderStatus(status) switch
        {
            "pending_approval" => "Заказ-наряд на согласовании: редактирование доступно после возврата на доработку.",
            "approved" => "Согласованный заказ-наряд нельзя редактировать. Создайте счет или верните расчет на доработку.",
            "invoice_created" => "По заказ-наряду уже создан счет: редактирование заблокировано.",
            _ => "Заказ-наряд недоступен для редактирования в текущем статусе.",
        };

    private async Task<string> BuildWorkOrderInvoiceNumberAsync(
        int clientCaseId,
        int workOrderId,
        DateTime invoiceDate,
        string? requestedNumber,
        CancellationToken ct)
    {
        var baseNumber = NormalizeOptional(requestedNumber) ?? $"WO-{clientCaseId}-{workOrderId}";
        var candidate = baseNumber.Length > 120 ? baseNumber[..120] : baseNumber;
        var suffix = 1;
        while (await _db.Invoices.AsNoTracking().AnyAsync(
            invoice => invoice.Number == candidate && invoice.Date.Date == invoiceDate.Date,
            ct))
        {
            suffix++;
            var suffixText = $"-{suffix}";
            var maxBaseLength = Math.Max(1, 120 - suffixText.Length);
            var truncatedBase = baseNumber.Length > maxBaseLength ? baseNumber[..maxBaseLength] : baseNumber;
            candidate = $"{truncatedBase}{suffixText}";
        }

        return candidate;
    }

    private static string NormalizeCurrency(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToUpperInvariant();
        return normalized is { Length: 3 } ? normalized : "RUB";
    }

    private static decimal NormalizePositiveMoney(decimal? value, decimal fallback)
    {
        var current = value ?? fallback;
        return current <= 0 ? 1 : decimal.Round(current, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal NormalizeNonNegativeMoney(decimal? value, decimal fallback)
    {
        var current = value ?? fallback;
        return current < 0 ? 0 : decimal.Round(current, 2, MidpointRounding.AwayFromZero);
    }

    private async Task<ClientCaseRelatedParty?> BuildAutoLegalEntityRelatedPartyAsync(
        int clientCaseId,
        int? ownerLegalEntityId,
        LegalEntity? legalEntity,
        DataVisibilityAccess visibility,
        int? actorUserId,
        string actorName,
        CancellationToken ct)
    {
        if (legalEntity is null
            || legalEntity.Id <= 0
            || (ownerLegalEntityId.HasValue && ownerLegalEntityId.Value == legalEntity.Id)
            || !CanSeeLegalEntity(legalEntity, visibility))
        {
            return null;
        }

        var alreadyLinked = await _db.ClientCaseRelatedParties.AnyAsync(item =>
            item.ClientCaseId == clientCaseId &&
            item.SourceType == RelatedPartySourceLegalEntity &&
            !item.IsArchived &&
            item.LegalEntityId == legalEntity.Id, ct);
        if (alreadyLinked)
        {
            return null;
        }

        return new ClientCaseRelatedParty
        {
            ClientCaseId = clientCaseId,
            SourceType = RelatedPartySourceLegalEntity,
            LegalEntityId = legalEntity.Id,
            DisplayName = !string.IsNullOrWhiteSpace(legalEntity.ShortName) ? legalEntity.ShortName : legalEntity.FullName ?? $"Юрлицо #{legalEntity.Id}",
            Role = "Организация участника",
            OrganizationName = legalEntity.FullName ?? string.Empty,
            Phone = legalEntity.Phone ?? string.Empty,
            Email = legalEntity.Email ?? string.Empty,
            Notes = !string.IsNullOrWhiteSpace(legalEntity.Director) ? $"Руководитель: {legalEntity.Director.Trim()}" : string.Empty,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorName,
        };
    }

    private static bool CanSeeLegalEntity(LegalEntity legalEntity, DataVisibilityAccess visibility)
        => visibility.SeesAll
            || (legalEntity.ResponsibleUserId.HasValue && visibility.VisibleUserIds.Contains(legalEntity.ResponsibleUserId.Value));

    private static void RestoreRelatedParty(ClientCaseRelatedParty party)
    {
        party.IsArchived = false;
        party.ArchivedAt = null;
        party.ArchivedByUserId = null;
        party.ArchivedByUserName = string.Empty;
        party.ArchiveReason = string.Empty;
    }

    private static ClientCaseRelatedPartyDto MapRelatedParty(ClientCaseRelatedParty party)
        => new()
        {
            Id = party.Id,
            ClientCaseId = party.ClientCaseId,
            SourceType = party.SourceType,
            SourceId = ResolveRelatedPartySourceId(party),
            ClientId = party.ClientId,
            ExternalContactId = party.ExternalContactId,
            LegalEntityId = party.LegalEntityId,
            DisplayName = party.DisplayName,
            Role = party.Role,
            OrganizationName = party.OrganizationName,
            Phone = party.Phone,
            Email = party.Email,
            Notes = party.Notes,
            CreatedAt = party.CreatedAt,
            CreatedByUserId = party.CreatedByUserId,
            CreatedByUserName = party.CreatedByUserName,
            IsArchived = party.IsArchived,
            ArchivedAt = party.ArchivedAt,
            ArchivedByUserId = party.ArchivedByUserId,
            ArchivedByUserName = party.ArchivedByUserName,
            ArchiveReason = party.ArchiveReason,
        };

    private static int? ResolveRelatedPartySourceId(ClientCaseRelatedParty party)
        => party.SourceType switch
        {
            RelatedPartySourceClient => party.ClientId,
            RelatedPartySourceExternalContact => party.ExternalContactId,
            RelatedPartySourceLegalEntity => party.LegalEntityId,
            _ => party.ClientId ?? party.ExternalContactId ?? party.LegalEntityId,
        };

    private static string? NormalizeRelatedPartySourceType(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.ToLowerInvariant() switch
        {
            "client" or "person" or "physical" => RelatedPartySourceClient,
            "external" or "externalcontact" or "contact" => RelatedPartySourceExternalContact,
            "legalentity" or "legal" or "organization" or "company" => RelatedPartySourceLegalEntity,
            _ => null,
        };
    }

    private static string NormalizeRequired(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeDocumentKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > 64)
        {
            return null;
        }

        return normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static string NormalizeHistoryText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static void AppendCaseEvent(ClientCase clientCase, string eventText)
    {
        var line = $"{DateTime.UtcNow:dd.MM.yyyy HH:mm}: {eventText}";
        var existing = clientCase.EventHistory?.Trim();
        var next = string.IsNullOrWhiteSpace(existing)
            ? line
            : $"{existing}{Environment.NewLine}{line}";

        clientCase.EventHistory = next.Length <= 4000
            ? next
            : next[^4000..].TrimStart();
    }

    private static IReadOnlyList<string> SplitCaseHistory(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();

    private static bool IsCourtSyncHistoryLine(string line)
        => line.Contains("проверка судебного источника", StringComparison.OrdinalIgnoreCase)
            || line.Contains("судебного источника", StringComparison.OrdinalIgnoreCase);

    private static ClientCaseTimelineEntryDto MapTimelineEntry(int clientCaseId, string line, int index)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, out var parsed)
            ? parsed
            : (DateTime?)null;
        var text = line.Length > 18 && line[16] == ':' && line[17] == ' '
            ? line[18..].Trim()
            : line.Trim();

        return new ClientCaseTimelineEntryDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            OccurredAt = occurredAt,
            Text = text,
        };
    }

    private static IReadOnlyList<ClientCaseConversationEntryDto> MapCaseConversation(int clientCaseId, string? eventHistory)
        => SplitCaseHistory(eventHistory)
            .Select((line, index) => MapConversationEntry(clientCaseId, line, index))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.Id)
            .ToList();

    private static ClientCaseConversationEntryDto? MapConversationEntry(int clientCaseId, string line, int index)
    {
        var timelineEntry = MapTimelineEntry(clientCaseId, line, index);
        var text = timelineEntry.Text.Trim();
        var kind = "message";
        if (text.StartsWith(CaseConversationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[CaseConversationPrefix.Length..].Trim();
        }
        else if (text.StartsWith(CaseHistoryCommentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[CaseHistoryCommentPrefix.Length..].Trim();
            kind = "comment";
        }
        else
        {
            return null;
        }

        var authorName = ExtractCaseHistoryMarker(text, CaseHistoryAuthorMarker);
        var authorIndex = text.LastIndexOf(CaseHistoryAuthorMarker, StringComparison.OrdinalIgnoreCase);
        if (authorIndex >= 0)
        {
            text = text[..authorIndex].Trim();
        }

        text = text.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new ClientCaseConversationEntryDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            OccurredAt = timelineEntry.OccurredAt,
            Text = text,
            AuthorName = authorName ?? string.Empty,
            Kind = kind,
        };
    }

    private static ClientCaseCourtSyncLogDto MapCourtSyncLog(int clientCaseId, string line, int index)
    {
        var occurredAt = TryParseCaseHistoryTimestamp(line, out var parsed)
            ? parsed
            : (DateTime?)null;
        var message = line.Length > 18 && line[16] == ':' && line[17] == ' '
            ? line[18..].Trim()
            : line.Trim();

        var actorName = ExtractCaseHistoryMarker(message, "Инициатор:");
        var status = message.Contains("не выполнена", StringComparison.OrdinalIgnoreCase)
            ? "binding-required"
            : message.Contains("зафиксирована", StringComparison.OrdinalIgnoreCase)
                ? "manual-check-recorded"
                : "history";

        return new ClientCaseCourtSyncLogDto
        {
            Id = index + 1,
            ClientCaseId = clientCaseId,
            Status = status,
            Message = message,
            OccurredAt = occurredAt,
            ActorName = actorName,
        };
    }

    private static bool TryParseCaseHistoryTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        return line.Length >= 16
            && DateTime.TryParseExact(
                line[..16],
                "dd.MM.yyyy HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
    }

    private static string? ExtractCaseHistoryMarker(string message, string marker)
    {
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        var value = message[start..].Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string SanitizeFileName(string? value)
    {
        var fileName = Path.GetFileName(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName.Length <= 260 ? fileName : fileName[^260..];
    }

    private static string BuildStoredFileName(string documentKey, int version, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (extension.Length > 24)
        {
            extension = string.Empty;
        }

        return $"{documentKey}-v{version}-{Guid.NewGuid():N}{extension}";
    }

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;

    private static string FormatCaseStatus(ClientCaseStatus status) => status switch
    {
        ClientCaseStatus.Open => "В работе",
        ClientCaseStatus.OnHold => "Приостановлено",
        ClientCaseStatus.Closed => "Закрыто",
        ClientCaseStatus.Lost => "Проиграно",
        _ => status.ToString(),
    };

    private static string FormatCaseResponsible(User? user)
        => string.IsNullOrWhiteSpace(user?.FullName)
            ? string.IsNullOrWhiteSpace(user?.Email) ? "Ответственный владельца" : user.Email
            : user.FullName;

    private static string FormatCaseTextValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "не указано" : value.Trim();

    private static string FormatCasePriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "Низкий",
        "normal" => "Обычный",
        "high" => "Высокий",
        "urgent" => "Критический",
        _ => FormatCaseTextValue(value),
    };

    private static string BuildBulkUpdateAuditReason(
        BulkUpdateCasesRequest request,
        User? nextResponsible,
        string? nextStage,
        string? nextPriority,
        int changedCasesCount)
    {
        var targets = new List<string>();

        if (request.Status is not null)
        {
            targets.Add($"статус: {FormatCaseStatus(request.Status.Value)}");
        }

        if (request.ResponsibleUserId.HasValue)
        {
            targets.Add($"ответственный: {FormatCaseResponsible(nextResponsible)}");
        }

        if (request.IsHiddenFromExecutor.HasValue)
        {
            targets.Add(request.IsHiddenFromExecutor.Value
                ? "доступ: скрыто от исполнителя"
                : "доступ: открыто исполнителю");
        }

        if (request.InactivityRemindersEnabled.HasValue)
        {
            targets.Add(request.InactivityRemindersEnabled.Value
                ? "автоконтроль: включен"
                : "автоконтроль: выключен");
        }

        if (nextStage is not null)
        {
            targets.Add($"стадия: {nextStage}");
        }

        if (nextPriority is not null)
        {
            targets.Add($"приоритет: {FormatCasePriority(nextPriority)}");
        }

        var summary = targets.Count == 0 ? "поля не указаны" : string.Join("; ", targets);
        return $"Массовое изменение дел ({changedCasesCount}): {summary}";
    }

    public sealed class BulkUpdateCasesRequest
    {
        public int[]? Ids { get; set; }
        public ClientCaseStatus? Status { get; set; }
        public int? ResponsibleUserId { get; set; }
        public string? Stage { get; set; }
        public string? Priority { get; set; }
        public bool? IsHiddenFromExecutor { get; set; }
        public bool? InactivityRemindersEnabled { get; set; }
    }

    public sealed class RequestCaseAccessRequest
    {
        public string? Reason { get; set; }
    }

    public sealed class ResolveCaseAccessRequest
    {
        public bool Grant { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class ClientCaseAccessRequestDto
    {
        public int ClientCaseId { get; set; }
        public DateTime RequestedAt { get; set; }
        public int RequestedByUserId { get; set; }
        public string RequestedByUserName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class ClientCaseAccessRequestListItemDto
    {
        public int ClientCaseId { get; set; }
        public string CaseNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public int? ResponsibleUserId { get; set; }
        public string ResponsibleName { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
        public int? RequestedByUserId { get; set; }
        public string RequestedByUserName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class ClientCaseAccessRequestResolutionDto
    {
        public int ClientCaseId { get; set; }
        public bool Granted { get; set; }
        public DateTime ResolvedAt { get; set; }
        public string ResolvedByUserName { get; set; } = string.Empty;
        public bool IsHiddenFromExecutor { get; set; }
    }

    public sealed class MarkClientCaseActivityRequest
    {
        public bool Confirmed { get; set; }
    }

    public sealed class AddClientCaseHistoryEntryRequest
    {
        public string? Text { get; set; }
    }

    public sealed class AddClientCaseConversationEntryRequest
    {
        public string? Text { get; set; }
    }

    public sealed class UpsertClientCaseRelatedPartyRequest
    {
        public string? SourceType { get; set; }
        public int SourceId { get; set; }
        public string? Role { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class UpsertClientCaseWorkOrderRequest
    {
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Currency { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class UpsertClientCaseWorkOrderItemRequest
    {
        public string? ServiceName { get; set; }
        public string? Description { get; set; }
        public int? ExecutorUserId { get; set; }
        public string? ExecutorName { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public int? SortOrder { get; set; }
    }

    public sealed class CreateClientCaseWorkOrderInvoiceRequest
    {
        public string? Number { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? DueDate { get; set; }
        public string? Description { get; set; }
    }

    public sealed class ClientCaseWorkOrderApprovalRequest
    {
        public string? Comment { get; set; }
    }

    public sealed class ClientCaseWorkOrderInvoiceDto
    {
        public int PaymentId { get; set; }
        public int? InvoiceEntityId { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime DueDate { get; set; }
        public decimal Amount { get; set; }
        public ClientCaseWorkOrderDto WorkOrder { get; set; } = new();
    }

    public sealed class ClientCaseWorkOrderDto
    {
        public int? Id { get; set; }
        public int ClientCaseId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string Currency { get; set; } = "RUB";
        public string Notes { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string UpdatedByUserName { get; set; } = string.Empty;
        public DateTime? ApprovalRequestedAt { get; set; }
        public int? ApprovalRequestedByUserId { get; set; }
        public string ApprovalRequestedByUserName { get; set; } = string.Empty;
        public DateTime? ApprovalDecisionAt { get; set; }
        public int? ApprovalDecisionByUserId { get; set; }
        public string ApprovalDecisionByUserName { get; set; } = string.Empty;
        public string ApprovalComment { get; set; } = string.Empty;
        public IReadOnlyList<ClientCaseWorkOrderItemDto> Items { get; set; } = [];

        public static ClientCaseWorkOrderDto Empty(int clientCaseId) => new()
        {
            ClientCaseId = clientCaseId,
            Title = "Предварительный расчет",
            Status = "draft",
            StatusLabel = "Черновик",
            Currency = "RUB",
            Items = [],
        };
    }

    public sealed class ClientCaseWorkOrderItemDto
    {
        public int Id { get; set; }
        public int ClientCaseWorkOrderId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ExecutorUserId { get; set; }
        public string ExecutorName { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class ClientCaseActivityDto
    {
        public DateTime? LastViewedAt { get; set; }
        public int? LastViewedByUserId { get; set; }
        public string LastViewedByUserName { get; set; } = string.Empty;
        public DateTime? ActivityConfirmedAt { get; set; }
        public int? ActivityConfirmedByUserId { get; set; }
        public string ActivityConfirmedByUserName { get; set; } = string.Empty;
        public bool InactivityRemindersEnabled { get; set; }
        public string EventHistory { get; set; } = string.Empty;
    }

    public sealed class ClientCaseTimelineEntryDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public DateTime? OccurredAt { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public sealed class ClientCaseConversationEntryDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public DateTime? OccurredAt { get; set; }
        public string Text { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class ClientCaseSummaryDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CaseNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string PriorityLabel { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public int? ResponsibleUserId { get; set; }
        public string ResponsibleName { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? NextHearingAt { get; set; }
        public int OpenTaskCount { get; set; }
        public int OverdueTaskCount { get; set; }
        public int CompletedTaskCount { get; set; }
        public int TaskProgressPercent { get; set; }
        public decimal? FeeAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal DebtAmount { get; set; }
        public DateTime? LastTaskAt { get; set; }
        public DateTime? LastViewedAt { get; set; }
    }

    public sealed class ClientCaseFinanceDetailDto
    {
        public int CaseId { get; set; }
        public string CaseTitle { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public ClientCaseFinanceTotalsDto Totals { get; set; } = new();
        public ClientCaseFinanceCalendarDto Calendar { get; set; } = new();
        public IReadOnlyList<ClientCaseFinanceInvoiceDto> Invoices { get; set; } = [];
        public IReadOnlyList<ClientCaseFinancePaymentDto> Payments { get; set; } = [];
        public IReadOnlyList<ClientCaseFinanceContractDto> Contracts { get; set; } = [];
    }

    public sealed class ClientCaseFinanceTotalsDto
    {
        public decimal FeeAmount { get; set; }
        public decimal IncomeTotal { get; set; }
        public decimal IncomePaid { get; set; }
        public decimal IncomeOutstanding { get; set; }
        public int IncomeCount { get; set; }
        public decimal ExpenseTotal { get; set; }
        public decimal ExpensePaid { get; set; }
        public decimal ExpenseOutstanding { get; set; }
        public int ExpenseCount { get; set; }
        public decimal PlannedMargin { get; set; }
        public decimal PaidMargin { get; set; }
        public decimal OutstandingBalance { get; set; }
    }

    public sealed class ClientCaseFinanceCalendarDto
    {
        public int PaymentCount { get; set; }
        public decimal PlanTotal { get; set; }
        public decimal PaidTotal { get; set; }
        public decimal OutstandingTotal { get; set; }
        public decimal OverdueTotal { get; set; }
        public int PaidCount { get; set; }
        public int PlannedCount { get; set; }
        public int OverdueCount { get; set; }
        public int CancelledCount { get; set; }
        public DateTime? NextPaymentDate { get; set; }
    }

    public sealed class ClientCaseFinanceInvoiceDto
    {
        public int Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string WorkflowStatus { get; set; } = string.Empty;
        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }
    }

    public sealed class ClientCaseFinancePaymentDto
    {
        public int Id { get; set; }
        public string DocumentKind { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime PlannedDate { get; set; }
        public DateTime? PaidDate { get; set; }
        public int? InvoiceId { get; set; }
        public string? InvoiceNumber { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }
        public string? PaymentSourceName { get; set; }
        public string? PaymentOrderNumber { get; set; }
        public DateTime? PaymentDocumentDate { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public sealed class ClientCaseFinanceContractDto
    {
        public int Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string? Title { get; set; }
        public DateTime Date { get; set; }
        public decimal? Amount { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public sealed class ClientCaseCourtSyncRunDto
    {
        public int ClientCaseId { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool HasCourtBinding { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
        public int? CheckedByUserId { get; set; }
        public string CheckedByUserName { get; set; } = string.Empty;
        public string CourtName { get; set; } = string.Empty;
        public string CourtCaseNumber { get; set; } = string.Empty;
        public string CourtUrl { get; set; } = string.Empty;
    }

    public sealed class ClientCaseCourtSyncLogDto
    {
        public int Id { get; set; }
        public int ClientCaseId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime? OccurredAt { get; set; }
        public string? ActorName { get; set; }
    }

    public sealed class UpdateCaseDocumentRequest
    {
        public string? DocumentType { get; set; }
        public string? Status { get; set; }
    }

    public sealed class GenerateCaseDocumentTemplateRequest
    {
        public string? TemplateKey { get; set; }
        public string? DocumentType { get; set; }
        public string? Status { get; set; }
    }

    public sealed class CaseDocumentTemplateDefinitionDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Content { get; set; }
        public int Version { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedAt { get; set; }
    }

    public sealed class UpsertCaseDocumentTemplateRequest
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Name { get; set; }
        public string? DocumentType { get; set; }
        public string? Heading { get; set; }
        public string? Description { get; set; }
        public string? Content { get; set; }
        public string? Html { get; set; }
        public bool? IsActive { get; set; }
    }

    public sealed class RenderCaseDocumentTemplateRequest
    {
        public int ClientCaseId { get; set; }
        public string? DocumentType { get; set; }
    }

    public sealed class RenderedCaseDocumentTemplateDto
    {
        public string TemplateKey { get; set; } = string.Empty;
        public string TemplateLabel { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
    }

    private sealed class CaseDocumentTemplateLibraryStorage
    {
        public List<CaseDocumentTemplateStorage> Templates { get; set; } = new();
    }

    private sealed class CaseDocumentTemplateStorage
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Content { get; set; }
        public int Version { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedAt { get; set; }
    }

    private readonly record struct CaseDocumentTemplate(
        string DocumentKey,
        string DocumentType,
        string Status,
        string FileName,
        string Html);

    private readonly record struct CaseDocumentTemplateDefinition(
        string Key,
        string Title,
        string DocumentType,
        string Heading,
        string? Description = null,
        string? Content = null,
        int Version = 1,
        bool IsActive = true,
        DateTime? UpdatedAt = null);
}

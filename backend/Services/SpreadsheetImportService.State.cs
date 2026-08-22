using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task<ImportState> LoadStateAsync(PaymentContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.FirstName,
                user.LastName,
                user.MiddleName,
            })
            .ToListAsync(cancellationToken);

        var clients = await db.Clients
            .AsNoTracking()
            .Select(client => new
            {
                client.Id,
                client.Name,
                client.Company,
                client.Email,
                client.Phone,
                client.Address,
                client.Notes,
                client.LegalEntityId,
                LegalEntityShortName = client.LegalEntity != null ? client.LegalEntity.ShortName : null,
                LegalEntityFullName = client.LegalEntity != null ? client.LegalEntity.FullName : null,
                LegalEntityInn = client.LegalEntity != null ? client.LegalEntity.Inn : null,
                ResponsibleName = client.ResponsibleUser != null ? client.ResponsibleUser.FullName : null,
            })
            .ToListAsync(cancellationToken);

        var legalEntities = await db.LegalEntities
            .AsNoTracking()
            .Select(entity => new
            {
                entity.Id,
                entity.ShortName,
                entity.FullName,
                entity.Inn,
                entity.Kpp,
                entity.Ogrn,
                entity.Address,
                entity.Phone,
                entity.Email,
                entity.Director,
                entity.OrganizationForm,
                entity.BankAccountsJson,
                ResponsibleName = entity.ResponsibleUser != null ? entity.ResponsibleUser.FullName : null,
            })
            .ToListAsync(cancellationToken);

        var invoices = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .Select(payment => new
            {
                payment.Id,
                Number = payment.Invoice != null
                    ? payment.Invoice.Number
                    : payment.Account ?? payment.LegacyAccountNumber,
                Date = payment.Invoice != null
                    ? (DateTime?)payment.Invoice.Date
                    : payment.AccountDate ?? payment.LegacyAccountDate ?? payment.Date,
                payment.Amount,
                payment.Type,
                payment.Notes,
                DueDate = (DateTime?)payment.Date,
                CounterpartyName = payment.Client != null ? payment.Client.Name : payment.CounterpartyName,
                payment.Status,
                payment.PaidDate,
                ActReference = payment.Acts
                    .OrderBy(act => act.Date)
                    .ThenBy(act => act.Id)
                    .Select(act => act.Number)
                    .FirstOrDefault(),
                ResponsibleName = payment.Responsibles
                    .OrderBy(item => item.UserId)
                    .Select(item => item.User != null ? item.User.FullName : null)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var acts = await db.Acts
            .AsNoTracking()
            .Select(act => new
            {
                act.Id,
                act.Number,
                act.Date,
                act.Amount,
                CounterpartyName = act.Client != null ? act.Client.Name : null,
                act.CounterpartyInn,
                act.Status,
                Type = act.Invoice != null ? act.Invoice.Type : PaymentType.Income,
                act.InvoiceNumber,
                act.Comment,
                ResponsibleName = act.Responsible != null ? act.Responsible.FullName : null,
            })
            .ToListAsync(cancellationToken);

        var invoiceStateItems = invoices.Select(invoice => new InvoiceStateItem
        {
            PaymentId = invoice.Id,
            ExternalId = ExtractOneCExternalId(invoice.Notes),
            Number = NormalizeText(invoice.Number),
            Date = invoice.Date?.Date,
            Amount = invoice.Amount,
            DueDate = invoice.DueDate?.Date,
            CounterpartyName = NormalizeText(invoice.CounterpartyName),
            Status = invoice.Status,
            Type = invoice.Type,
            PaidDate = invoice.PaidDate?.Date,
            ActReference = NormalizeText(invoice.ActReference),
            ResponsibleName = NormalizeText(invoice.ResponsibleName),
            ImportedInCurrentBatch = false,
        }).ToList();

        var actStateItems = acts.Select(act => new ActStateItem
        {
            ActId = act.Id,
            ExternalId = ExtractOneCExternalId(act.Comment),
            Number = NormalizeText(act.Number),
            Date = ActDomainService.NormalizeLocalDate(act.Date),
            Amount = act.Amount,
            CounterpartyName = NormalizeText(act.CounterpartyName),
            CounterpartyInn = NormalizeInn(act.CounterpartyInn),
            Status = act.Status,
            Type = act.Type,
            InvoiceNumber = NormalizeText(act.InvoiceNumber),
            ResponsibleName = NormalizeText(act.ResponsibleName),
            ImportedInCurrentBatch = false,
        }).ToList();

        return new ImportState
        {
            Users = users.Select(user => new UserStateItem
            {
                Id = user.Id,
                FullName = NormalizeText(user.FullName) ?? user.Email ?? $"User #{user.Id}",
                MatchKeys = BuildUserMatchKeys(
                    user.FullName,
                    user.Email,
                    user.LastName,
                    user.FirstName,
                    user.MiddleName),
            }).ToList(),
            Clients = clients.Select(client => new ClientStateItem
            {
                Id = client.Id,
                Name = client.Name,
                NormalizedName = NormalizeNameKey(client.Name),
                Company = NormalizeText(client.Company),
                NormalizedCompany = NormalizeNameKey(client.Company),
                NormalizedInn = NormalizeInn(client.LegalEntityInn) ?? ExtractClientInn(client.Notes),
                NormalizedPhone = NormalizePhone(client.Phone),
                NormalizedEmail = NormalizeEmail(client.Email),
                Phone = NormalizeText(client.Phone),
                Email = NormalizeEmail(client.Email),
                Address = NormalizeText(client.Address),
                ResponsibleName = NormalizeText(client.ResponsibleName),
                LegalEntityId = client.LegalEntityId,
                LegalEntityShortName = NormalizeText(client.LegalEntityShortName),
                LegalEntityFullName = NormalizeText(client.LegalEntityFullName),
                MatchKeys = BuildCounterpartyMatchKeys(
                    client.Name,
                    client.Company,
                    client.LegalEntityShortName,
                    client.LegalEntityFullName),
            }).ToList(),
            LegalEntities = legalEntities.Select(entity => new LegalEntityStateItem
            {
                Id = entity.Id,
                ShortName = entity.ShortName,
                FullName = NormalizeText(entity.FullName),
                Inn = NormalizeInn(entity.Inn),
                Kpp = NormalizeText(entity.Kpp),
                Ogrn = NormalizeText(entity.Ogrn),
                Address = NormalizeText(entity.Address),
                Phone = NormalizeText(entity.Phone),
                Email = NormalizeEmail(entity.Email),
                Director = NormalizeText(entity.Director),
                ResponsibleName = NormalizeText(entity.ResponsibleName),
                OrganizationForm = NormalizeText(entity.OrganizationForm),
                BankAccountsJson = NormalizeText(entity.BankAccountsJson),
                NormalizedShortName = NormalizeNameKey(entity.ShortName),
                NormalizedFullName = NormalizeNameKey(entity.FullName),
                NormalizedInn = NormalizeInn(entity.Inn),
                NormalizedPhone = NormalizePhone(entity.Phone),
                NormalizedEmail = NormalizeEmail(entity.Email),
                MatchKeys = BuildCounterpartyMatchKeys(
                    entity.ShortName,
                    entity.FullName),
            }).ToList(),
            Invoices = BuildInvoiceStateIndex(invoiceStateItems),
            Acts = BuildActStateIndex(actStateItems),
        };
    }

    private static Dictionary<string, List<InvoiceStateItem>> BuildInvoiceStateIndex(IEnumerable<InvoiceStateItem> items)
    {
        var index = new Dictionary<string, List<InvoiceStateItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var key in BuildInvoiceMatchKeys(item.ExternalId, item.Number, item.Date, item.Type))
            {
                AddInvoiceStateIndexItem(index, key, item);
            }
        }

        return index;
    }

    private static Dictionary<string, List<ActStateItem>> BuildActStateIndex(IEnumerable<ActStateItem> items)
    {
        var index = new Dictionary<string, List<ActStateItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var key in BuildActMatchKeys(item.ExternalId, item.Number, item.Date, item.Type))
            {
                AddActStateIndexItem(index, key, item);
            }
        }

        return index;
    }

    private static void AddInvoiceStateIndexItem(
        IDictionary<string, List<InvoiceStateItem>> index,
        string key,
        InvoiceStateItem item)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<InvoiceStateItem>();
            index[key] = list;
        }

        list.Add(new InvoiceStateItem
        {
            PaymentId = item.PaymentId,
            Key = key,
            ExternalId = item.ExternalId,
            Number = item.Number,
            Date = item.Date,
            Amount = item.Amount,
            DueDate = item.DueDate,
            CounterpartyName = item.CounterpartyName,
            Status = item.Status,
            Type = item.Type,
            PaidDate = item.PaidDate,
            ActReference = item.ActReference,
            ResponsibleName = item.ResponsibleName,
            ImportedInCurrentBatch = item.ImportedInCurrentBatch,
        });
    }

    private static void AddActStateIndexItem(
        IDictionary<string, List<ActStateItem>> index,
        string key,
        ActStateItem item)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<ActStateItem>();
            index[key] = list;
        }

        list.Add(new ActStateItem
        {
            ActId = item.ActId,
            Key = key,
            ExternalId = item.ExternalId,
            Number = item.Number,
            Date = item.Date,
            Amount = item.Amount,
            CounterpartyName = item.CounterpartyName,
            CounterpartyInn = item.CounterpartyInn,
            Status = item.Status,
            Type = item.Type,
            InvoiceNumber = item.InvoiceNumber,
            ResponsibleName = item.ResponsibleName,
            ImportedInCurrentBatch = item.ImportedInCurrentBatch,
        });
    }

    private static string BuildBatchTitle(IReadOnlyCollection<ParsedFile> files)
    {
        var kinds = files
            .Select(file => GetSectionLabel(file.DetectedKind))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (kinds.Count == 0)
        {
            return $"Импорт данных {DateTime.UtcNow:dd.MM.yyyy HH:mm}";
        }

        return $"Импорт: {string.Join(", ", kinds)}";
    }

    private static SpreadsheetImportBatchSummaryDto MapBatchSummary(SpreadsheetImportBatch batch)
    {
        return new SpreadsheetImportBatchSummaryDto
        {
            Id = batch.Id,
            Title = batch.Title,
            Status = batch.Status,
            TotalRows = batch.TotalRows,
            CreatedRows = batch.CreatedRows,
            UpdatedRows = batch.UpdatedRows,
            SkippedRows = batch.SkippedRows,
            ErrorRows = batch.ErrorRows,
            RollbackRows = batch.RollbackRows,
            CreatedAt = batch.CreatedAt,
            AppliedAt = batch.AppliedAt,
            RolledBackAt = batch.RolledBackAt,
            CreatedByUserId = batch.CreatedByUserId,
            CreatedByUserName = batch.CreatedByUserName,
            Files = batch.Files
                .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.SheetName, StringComparer.OrdinalIgnoreCase)
                .Select(MapBatchFile)
                .ToList(),
        };
    }

    private static SpreadsheetImportBatchDetailDto MapBatchDetail(
        SpreadsheetImportBatch batch,
        IReadOnlyCollection<SpreadsheetImportBatchOperation> operations,
        int operationsTotal,
        int operationsPage,
        int operationsPageSize)
    {
        return new SpreadsheetImportBatchDetailDto
        {
            Id = batch.Id,
            Title = batch.Title,
            Status = batch.Status,
            TotalRows = batch.TotalRows,
            CreatedRows = batch.CreatedRows,
            UpdatedRows = batch.UpdatedRows,
            SkippedRows = batch.SkippedRows,
            ErrorRows = batch.ErrorRows,
            RollbackRows = batch.RollbackRows,
            CreatedAt = batch.CreatedAt,
            AppliedAt = batch.AppliedAt,
            RolledBackAt = batch.RolledBackAt,
            CreatedByUserId = batch.CreatedByUserId,
            CreatedByUserName = batch.CreatedByUserName,
            Files = batch.Files
                .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.SheetName, StringComparer.OrdinalIgnoreCase)
                .Select(MapBatchFile)
                .ToList(),
            OperationsTotal = operationsTotal,
            OperationsPage = operationsPage,
            OperationsPageSize = operationsPageSize,
            Operations = operations
                .Select(operation => new SpreadsheetImportBatchOperationDto
                {
                    Id = operation.Id,
                    SectionKey = operation.SectionKey,
                    SourceFileName = operation.SourceFileName,
                    SourceFileHash = operation.SourceFileHash,
                    SourceRowId = operation.SourceRowId,
                    SourceRowNumber = operation.SourceRowNumber,
                    SourceKey = operation.SourceKey,
                    SourceTitle = operation.SourceTitle,
                    Action = operation.Action,
                    EntityType = operation.EntityType,
                    EntityId = operation.EntityId,
                    Message = operation.Message,
                    MetadataJson = operation.MetadataJson,
                    RollbackAction = operation.RollbackAction,
                    RollbackMessage = operation.RollbackMessage,
                    CreatedAt = operation.CreatedAt,
                    RolledBackAt = operation.RolledBackAt,
                })
                .ToList(),
        };
    }

    private static SpreadsheetImportBatchFileDto MapBatchFile(SpreadsheetImportBatchFile file)
        => new()
        {
            FileName = file.FileName,
            FileHash = file.FileHash,
            DetectedKind = file.DetectedKind,
            SheetName = file.SheetName,
            RowCount = file.RowCount,
            CreatedAt = file.CreatedAt,
        };

    private int? GetCurrentUserId()
    {
        var value = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var userId) ? userId : null;
    }

    private async Task<DateTime> GetCurrentUserLocalDateAsync(PaymentContext db, CancellationToken cancellationToken)
    {
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, GetCurrentUserId(), cancellationToken);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    private string? GetCurrentUserName()
        => NormalizeText(_httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Name));

    private static List<string> BuildUserMatchKeys(params string?[] values)
        => values
            .SelectMany(value =>
            {
                var keys = new List<string>();
                var normalizedText = NormalizeText(value);
                var normalizedName = NormalizeNameKey(value);
                var normalizedEmail = NormalizeEmail(value);

                if (!string.IsNullOrWhiteSpace(normalizedText))
                {
                    keys.Add(normalizedText.ToLowerInvariant());
                }

                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    keys.Add(normalizedName);
                }

                if (!string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    keys.Add(normalizedEmail);
                }

                return keys;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static int? FindUserId(IEnumerable<UserStateItem> users, string? value)
    {
        var normalizedText = NormalizeText(value)?.ToLowerInvariant();
        var normalizedName = NormalizeNameKey(value);
        var normalizedEmail = NormalizeEmail(value);

        if (string.IsNullOrWhiteSpace(normalizedText)
            && string.IsNullOrWhiteSpace(normalizedName)
            && string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return null;
        }

        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(normalizedEmail) && user.MatchKeys.Contains(normalizedEmail, StringComparer.Ordinal))
            {
                return user.Id;
            }

            if (!string.IsNullOrWhiteSpace(normalizedName) && user.MatchKeys.Contains(normalizedName, StringComparer.Ordinal))
            {
                return user.Id;
            }

            if (!string.IsNullOrWhiteSpace(normalizedText) && user.MatchKeys.Contains(normalizedText, StringComparer.Ordinal))
            {
                return user.Id;
            }
        }

        return null;
    }

    private static ClientStateItem? FindClient(
        ImportState state,
        string? name,
        string? inn,
        string? phone = null,
        string? email = null)
    {
        var normalizedInn = NormalizeInn(inn);
        if (!string.IsNullOrWhiteSpace(normalizedInn))
        {
            var byInn = state.Clients.FirstOrDefault(client => client.NormalizedInn == normalizedInn);
            if (byInn is not null)
            {
                return byInn;
            }
        }

        var normalizedName = NormalizeNameKey(name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            var byName = state.Clients.FirstOrDefault(client =>
                client.NormalizedName == normalizedName
                || client.NormalizedCompany == normalizedName
                || client.MatchKeys.Contains(normalizedName, StringComparer.Ordinal));
            if (byName is not null)
            {
                return byName;
            }
        }

        var normalizedPhone = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            var byPhone = state.Clients.FirstOrDefault(client => client.NormalizedPhone == normalizedPhone);
            if (byPhone is not null)
            {
                return byPhone;
            }
        }

        var normalizedEmail = NormalizeEmail(email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var byEmail = state.Clients.FirstOrDefault(client => client.NormalizedEmail == normalizedEmail);
            if (byEmail is not null)
            {
                return byEmail;
            }
        }

        return null;
    }

    private static int? FindClientId(
        ImportState state,
        string? name,
        string? inn,
        string? phone = null,
        string? email = null)
        => FindClient(state, name, inn, phone, email)?.Id;

    private static LegalEntityStateItem? FindLegalEntity(
        ImportState state,
        string? name,
        string? inn,
        string? phone = null,
        string? email = null,
        int? preferredLegalEntityId = null)
    {
        if (preferredLegalEntityId.HasValue)
        {
            var preferred = state.LegalEntities.FirstOrDefault(entity => entity.Id == preferredLegalEntityId.Value);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var normalizedInn = NormalizeInn(inn);
        if (!string.IsNullOrWhiteSpace(normalizedInn))
        {
            var byInn = state.LegalEntities.FirstOrDefault(entity => entity.NormalizedInn == normalizedInn);
            if (byInn is not null)
            {
                return byInn;
            }
        }

        var normalizedName = NormalizeNameKey(name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            var byName = state.LegalEntities.FirstOrDefault(entity =>
                entity.NormalizedShortName == normalizedName
                || entity.NormalizedFullName == normalizedName
                || entity.MatchKeys.Contains(normalizedName, StringComparer.Ordinal));
            if (byName is not null)
            {
                return byName;
            }
        }

        var normalizedPhone = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            var byPhone = state.LegalEntities.FirstOrDefault(entity => entity.NormalizedPhone == normalizedPhone);
            if (byPhone is not null)
            {
                return byPhone;
            }
        }

        var normalizedEmail = NormalizeEmail(email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var byEmail = state.LegalEntities.FirstOrDefault(entity => entity.NormalizedEmail == normalizedEmail);
            if (byEmail is not null)
            {
                return byEmail;
            }
        }

        return null;
    }

    private static string GetSectionLabel(string? key)
    {
        return key switch
        {
            "counterparties" => "Контрагенты",
            "invoices" => "Счета",
            "acts" => "Акты",
            OneCExchangeKind => "КУБ/1С XML",
            _ => "Неизвестный файл",
        };
    }

    private static string BuildInvoiceTitle(string? number, DateTime? date)
        => $"Счет №{NormalizeText(number) ?? "без номера"}{(date.HasValue ? $" от {date.Value:dd.MM.yyyy}" : string.Empty)}";

    private static string BuildActTitle(string? number, DateTime? date)
        => $"Акт №{NormalizeText(number) ?? "без номера"}{(date.HasValue ? $" от {date.Value:dd.MM.yyyy}" : string.Empty)}";

    private static string FormatDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatAmount(decimal? value)
        => value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;
}

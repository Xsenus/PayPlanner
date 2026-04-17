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
                entity.Phone,
                entity.Email,
                entity.Director,
                entity.OrganizationForm,
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
                act.InvoiceNumber,
                ResponsibleName = act.Responsible != null ? act.Responsible.FullName : null,
            })
            .ToListAsync(cancellationToken);

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
                Phone = NormalizeText(entity.Phone),
                Email = NormalizeEmail(entity.Email),
                Director = NormalizeText(entity.Director),
                ResponsibleName = NormalizeText(entity.ResponsibleName),
                OrganizationForm = NormalizeText(entity.OrganizationForm),
                NormalizedShortName = NormalizeNameKey(entity.ShortName),
                NormalizedFullName = NormalizeNameKey(entity.FullName),
                NormalizedInn = NormalizeInn(entity.Inn),
                NormalizedPhone = NormalizePhone(entity.Phone),
                NormalizedEmail = NormalizeEmail(entity.Email),
                MatchKeys = BuildCounterpartyMatchKeys(
                    entity.ShortName,
                    entity.FullName),
            }).ToList(),
            Invoices = invoices
                .Where(invoice => !string.IsNullOrWhiteSpace(BuildInvoiceKey(invoice.Number, invoice.Date)))
                .GroupBy(invoice => BuildInvoiceKey(invoice.Number, invoice.Date), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(invoice => new InvoiceStateItem
                    {
                        PaymentId = invoice.Id,
                        Key = group.Key,
                        Number = NormalizeText(invoice.Number),
                        Date = invoice.Date?.Date,
                        Amount = invoice.Amount,
                        DueDate = invoice.DueDate?.Date,
                        CounterpartyName = NormalizeText(invoice.CounterpartyName),
                        Status = invoice.Status,
                        PaidDate = invoice.PaidDate?.Date,
                        ActReference = NormalizeText(invoice.ActReference),
                        ResponsibleName = NormalizeText(invoice.ResponsibleName),
                    }).ToList(),
                    StringComparer.Ordinal),
            Acts = acts
                .Where(act => !string.IsNullOrWhiteSpace(BuildActKey(act.Number, act.Date)))
                .GroupBy(act => BuildActKey(act.Number, act.Date), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(act => new ActStateItem
                    {
                        ActId = act.Id,
                        Key = group.Key,
                        Number = NormalizeText(act.Number),
                        Date = act.Date.Date,
                        Amount = act.Amount,
                        CounterpartyName = NormalizeText(act.CounterpartyName),
                        CounterpartyInn = NormalizeInn(act.CounterpartyInn),
                        Status = act.Status,
                        InvoiceNumber = NormalizeText(act.InvoiceNumber),
                        ResponsibleName = NormalizeText(act.ResponsibleName),
                    }).ToList(),
                    StringComparer.Ordinal),
        };
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

    private static SpreadsheetImportBatchDetailDto MapBatchDetail(SpreadsheetImportBatch batch)
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
            Operations = batch.Operations
                .OrderBy(operation => operation.CreatedAt)
                .ThenBy(operation => operation.Id)
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

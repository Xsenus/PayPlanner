using System.Globalization;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private static string BuildLegalEntityKey(LegalEntity item)
    {
        if (!string.IsNullOrWhiteSpace(item.Inn) || !string.IsNullOrWhiteSpace(item.Kpp))
        {
            return Key(item.Inn, item.Kpp, item.Ogrn);
        }

        if (!string.IsNullOrWhiteSpace(item.Ogrn))
        {
            return Key(item.Ogrn);
        }

        return Key(item.ShortName);
    }

    private static string BuildClientKey(Client item)
    {
        if (!string.IsNullOrWhiteSpace(item.Email))
        {
            return Key(NormalizeEmail(item.Email));
        }

        return Key(item.Name, item.Phone, item.Company);
    }

    private static string BuildContractKey(Contract item) => Key(item.Number, item.Date);

    private static string BuildInvoiceKey(Invoice item) => Key(item.Number, item.Date);

    private static string BuildPaymentKey(Payment item, ImportMaps maps) => Key(
        MapId(maps.Clients, item.ClientId),
        MapId(maps.ClientCases, item.ClientCaseId),
        MapId(maps.Contracts, item.ContractId),
        MapId(maps.Invoices, item.InvoiceId),
        item.DocumentKind,
        item.Type,
        item.Date,
        item.PlannedDate,
        item.Amount,
        item.Description,
        item.Account,
        item.AccountDate,
        item.LegacyAccountNumber,
        item.LegacyAccountDate);

    private static string BuildTargetPaymentKey(Payment item) => Key(
        item.ClientId,
        item.ClientCaseId,
        item.ContractId,
        item.InvoiceId,
        item.DocumentKind,
        item.Type,
        item.Date,
        item.PlannedDate,
        item.Amount,
        item.Description,
        item.Account,
        item.AccountDate,
        item.LegacyAccountNumber,
        item.LegacyAccountDate);

    private static string BuildActKey(Act item, ImportMaps maps) => Key(item.Number, MapId(maps.Clients, item.ClientId));

    private static string BuildTargetActKey(Act item) => Key(item.Number, item.ClientId);

    private static string BuildAddressKey(AddressRecord item, ImportMaps maps)
    {
        var ownerType = item.ClientId.HasValue ? "client" : "legal";
        var ownerId = item.ClientId.HasValue
            ? MapId(maps.Clients, item.ClientId)
            : MapId(maps.LegalEntities, item.LegalEntityId);

        return ownerId.HasValue
            ? Key(ownerType, ownerId.Value, item.Type, item.Value, item.IsPrimary)
            : string.Empty;
    }

    private static string BuildTargetAddressKey(AddressRecord item)
    {
        var ownerType = item.ClientId.HasValue ? "client" : "legal";
        var ownerId = item.ClientId ?? item.LegalEntityId;
        return ownerId.HasValue ? Key(ownerType, ownerId.Value, item.Type, item.Value, item.IsPrimary) : string.Empty;
    }

    private static string BuildMoneyOperationKey(MoneyOperation item) => Key(
        item.Source,
        item.Direction,
        item.AccountingType,
        item.Date,
        item.Amount,
        item.PaymentOrderNumber,
        item.CounterpartyName,
        item.Purpose);

    private static int? MapId(Dictionary<int, int> map, int? sourceId)
    {
        if (!sourceId.HasValue)
        {
            return null;
        }

        return map.TryGetValue(sourceId.Value, out var targetId) ? targetId : null;
    }

    private static int MapRequired(Dictionary<int, int> map, int sourceId)
    {
        if (map.TryGetValue(sourceId, out var targetId))
        {
            return targetId;
        }

        throw new InvalidOperationException($"Не удалось сопоставить ссылку на запись с ID {sourceId}.");
    }

    private static string BuildDefaultMessage(DatabaseImportTableResultDto table)
        => $"Импорт завершён: добавлено {table.Inserted}, обновлено {table.Updated}, пропущено {table.Skipped}, конфликтов {table.Conflicts}, ошибок {table.Errors}.";

    private static string Key(params object?[] parts)
        => string.Join("::", parts.Select(FormatKeyPart));

    private static string FormatKeyPart(object? value) => value switch
    {
        null => string.Empty,
        string text => NormalizeText(text),
        DateTime dateTime => NormalizeDateTime(dateTime).ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        decimal amount => amount.ToString("0.############################", CultureInfo.InvariantCulture),
        float amount => amount.ToString("0.############################", CultureInfo.InvariantCulture),
        double amount => amount.ToString("0.############################", CultureInfo.InvariantCulture),
        Enum enumValue => Convert.ToInt64(enumValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
    };

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string NormalizeEmail(string? email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();
}

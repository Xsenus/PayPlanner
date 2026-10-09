using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Banking;

/// <summary>
/// Сопоставление кодов статуса счёта Точки со статусами CRM (ТЗ PAY-36, п. 4.1):
/// Enabled — открыт, Disabled — открыт с ограничениями, Deleted — закрыт, ProForma — предварительный (не активен),
/// Pending — открывается. Неизвестный код не считается ни открытым, ни закрытым.
/// </summary>
public static class TochkaAccountStatus
{
    public static string Map(string? bankStatus) => bankStatus?.Trim() switch
    {
        "Enabled" => BankAccountStatuses.Open,
        "Disabled" => BankAccountStatuses.Limited,
        "Deleted" => BankAccountStatuses.Closed,
        "ProForma" => BankAccountStatuses.Preliminary,
        "Pending" => BankAccountStatuses.Pending,
        _ => BankAccountStatuses.Unavailable,
    };

    /// <summary>
    /// Разбор accountId Точки вида «номер_счёта/БИК» (до 40 знаков): номер до «/», БИК после.
    /// Возвращает <c>false</c>, если формат не соответствует ожидаемому.
    /// </summary>
    public static bool TryParseAccountId(string? accountId, out string accountNumber, out string bik)
    {
        accountNumber = string.Empty;
        bik = string.Empty;
        var parts = accountId?.Trim().Split('/');
        if (parts is not { Length: 2 } || parts[0].Length == 0 || parts[1].Length == 0 || accountId!.Trim().Length > 40)
        {
            return false;
        }

        accountNumber = parts[0];
        bik = parts[1];
        return true;
    }
}

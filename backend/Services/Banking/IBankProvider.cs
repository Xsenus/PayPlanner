namespace PayPlanner.Api.Services.Banking;

/// <summary>Открытые данные доступа к банку для одного вызова адаптера.</summary>
public sealed record BankCredentials(string AccessToken, string? RefreshToken);

public sealed record BankAccountInfo(
    string ExternalId,
    string AccountNumber,
    string? Bik,
    string Currency,
    string Status,
    string? OwnerInn);

public sealed record BankOperationInfo(
    string ExternalId,
    DateTime Date,
    decimal Amount,
    bool IsIncome,
    bool IsBooked,
    string? CounterpartyInn,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? Purpose);

public sealed record BankStatementPage(IReadOnlyList<BankOperationInfo> Items, string? NextCursor);

/// <summary>Банк отклонил токен или согласие отозвано: подключению нужна повторная авторизация.</summary>
public sealed class BankAuthorizationException : Exception
{
    public BankAuthorizationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Адаптер конкретного банка. Содержит только обращение к API банка; хранение, повторы, защита от дублей
/// и права находятся в <see cref="BankSyncService"/>.
/// </summary>
public interface IBankProvider
{
    string Key { get; }

    Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(BankCredentials credentials, CancellationToken ct);

    /// <summary>Одна страница выписки за период; <paramref name="cursor"/> — значение <c>NextCursor</c> прошлой страницы.</summary>
    Task<BankStatementPage> GetStatementPageAsync(
        BankCredentials credentials,
        string externalAccountId,
        DateTime from,
        DateTime to,
        string? cursor,
        CancellationToken ct);
}

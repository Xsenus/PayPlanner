namespace PayPlanner.Api.Services.LegalIntegrations;

/// <summary>
/// Describes an approved external court source whose network adapter is intentionally unavailable
/// until its access contract and anonymized fixtures have been verified.
/// </summary>
public abstract class ApprovedDisabledCourtSyncProvider : ICourtSyncProvider, ICourtSyncProviderAvailability
{
    protected ApprovedDisabledCourtSyncProvider(
        string key,
        string label,
        string sourceType,
        IReadOnlyList<string> supportedHosts,
        string message)
    {
        Key = key;
        Label = label;
        SourceType = sourceType;
        SupportedHosts = supportedHosts;
        Message = message;
    }

    public string Key { get; }
    public string Label { get; }
    public string SourceType { get; }
    public int SchemaVersion => 1;
    public CourtSyncProviderCapability Capabilities => CourtSyncProviderCapability.None;
    public IReadOnlyList<string> SupportedHosts { get; }
    public bool IsAvailable => false;
    public string Status => "approved-disabled";
    public string Message { get; }

    public bool CanHandle(string? sourceUrl) => false;
}

public sealed class KadArbitrCourtSyncProvider : ApprovedDisabledCourtSyncProvider
{
    public KadArbitrCourtSyncProvider()
        : base(
            "kadArbitr",
            "КАД Арбитр",
            "kadArbitr",
            ["kad.arbitr.ru"],
            "Источник утвержден, но сетевой адаптер выключен до проверки допустимого публичного контракта и тестовых дел. Обход captcha и proxy запрещен.")
    {
    }
}

public sealed class MosGorSudCourtSyncProvider : ApprovedDisabledCourtSyncProvider
{
    public MosGorSudCourtSyncProvider()
        : base(
            "mosGorSud",
            "Мосгорсуд",
            "mosGorSud",
            ["mos-gorsud.ru"],
            "Источник утвержден, но сетевой адаптер выключен до проверки публичных карточек на тестовых делах. Разрешены только опубликованные акты без ограниченного доступа.")
    {
    }
}

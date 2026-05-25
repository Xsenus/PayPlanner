using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed record CourtSyncProviderDescriptor(
    string Key,
    string Label,
    string SourceType,
    string Status,
    bool IsAvailable,
    bool FeatureFlagEnabled,
    bool SupportsManualRun,
    bool SupportsBackgroundSync,
    IReadOnlyList<string> SupportedHosts,
    string Message);

public interface ICourtSyncProvider
{
    string Key { get; }
    string Label { get; }
    string SourceType { get; }
    IReadOnlyList<string> SupportedHosts { get; }
    bool CanHandle(string? sourceUrl);
    Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null);
}

public interface ICourtSyncProviderRegistry
{
    IReadOnlyList<CourtSyncProviderDescriptor> GetProviders();
    ICourtSyncProvider? Resolve(string? sourceUrl);
}

public sealed class SudrfCourtSyncProvider : ICourtSyncProvider
{
    private readonly ISudrfCourtSyncService _service;

    public SudrfCourtSyncProvider(ISudrfCourtSyncService service)
    {
        _service = service;
    }

    public string Key => "sudrf";
    public string Label => "ГАС Правосудие / sudrf.ru";
    public string SourceType => "sudrf";
    public IReadOnlyList<string> SupportedHosts { get; } = ["sudrf.ru"];

    public bool CanHandle(string? sourceUrl)
        => _service.CanHandle(sourceUrl);

    public Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
        => _service.FetchCaseAsync(sourceUrl, ct, courtCaseNumber);
}

public sealed class CourtSyncProviderRegistry : ICourtSyncProviderRegistry
{
    private readonly IReadOnlyList<ICourtSyncProvider> _providers;
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _featureFlags;

    public CourtSyncProviderRegistry(
        IEnumerable<ICourtSyncProvider> providers,
        IOptionsSnapshot<AppFeatureFlagsOptions> featureFlags)
    {
        _providers = providers.ToList();
        _featureFlags = featureFlags;
    }

    public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders()
    {
        var legal = _featureFlags.Value.Legal;
        var activeProviders = _providers
            .Select(provider => BuildActiveDescriptor(provider, legal.ExternalCourtSync))
            .ToList();

        activeProviders.AddRange(GetPlannedDescriptors(legal.ExternalCourtSync));
        return activeProviders;
    }

    public ICourtSyncProvider? Resolve(string? sourceUrl)
        => _providers.FirstOrDefault(provider => provider.CanHandle(sourceUrl));

    public static IReadOnlyList<CourtSyncProviderDescriptor> GetDefaultDescriptors(bool externalCourtSyncEnabled)
    {
        var descriptors = new List<CourtSyncProviderDescriptor>
        {
            BuildSudrfDescriptor(externalCourtSyncEnabled),
        };
        descriptors.AddRange(GetPlannedDescriptors(externalCourtSyncEnabled));
        return descriptors;
    }

    private static CourtSyncProviderDescriptor BuildActiveDescriptor(ICourtSyncProvider provider, bool externalCourtSyncEnabled)
        => provider.Key == "sudrf"
            ? BuildSudrfDescriptor(externalCourtSyncEnabled)
            : new CourtSyncProviderDescriptor(
                provider.Key,
                provider.Label,
                provider.SourceType,
                externalCourtSyncEnabled ? "active" : "manual-only",
                true,
                externalCourtSyncEnabled,
                true,
                externalCourtSyncEnabled,
                provider.SupportedHosts,
                externalCourtSyncEnabled
                    ? "Источник доступен для ручной и фоновой судебной синхронизации."
                    : "Источник доступен для ручной проверки; фоновый режим включается через серверный feature flag.");

    private static CourtSyncProviderDescriptor BuildSudrfDescriptor(bool externalCourtSyncEnabled)
        => new(
            "sudrf",
            "ГАС Правосудие / sudrf.ru",
            "sudrf",
            externalCourtSyncEnabled ? "active" : "manual-only",
            true,
            externalCourtSyncEnabled,
            true,
            externalCourtSyncEnabled,
            ["sudrf.ru"],
            externalCourtSyncEnabled
                ? "Поддерживается ручное обновление карточки дела и фоновая синхронизация открытых дел."
                : "Поддерживается ручная проверка карточки дела; фоновая синхронизация начнет работать после включения серверного feature flag.");

    private static IReadOnlyList<CourtSyncProviderDescriptor> GetPlannedDescriptors(bool externalCourtSyncEnabled)
        =>
        [
            new(
                "kadArbitr",
                "КАД Арбитр",
                "kadArbitr",
                "planned",
                false,
                externalCourtSyncEnabled,
                false,
                false,
                ["kad.arbitr.ru"],
                "Источник добавлен в дорожную карту, но автоматический разбор карточек пока не включен."),
            new(
                "mosGorSud",
                "Мосгорсуд",
                "mosGorSud",
                "planned",
                false,
                externalCourtSyncEnabled,
                false,
                false,
                ["mos-gorsud.ru"],
                "Источник добавлен в дорожную карту, но автоматический разбор карточек пока не включен."),
            new(
                "regionalCourts",
                "Региональные и мировые суды",
                "regionalCourt",
                "planned",
                false,
                externalCourtSyncEnabled,
                false,
                false,
                [],
                "Источник будет подключаться после уточнения обязательных судебных порталов и допустимых способов получения данных."),
        ];
}

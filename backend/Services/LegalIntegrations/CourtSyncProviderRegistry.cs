using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed record CourtSyncProviderDescriptor(
    string Key,
    string Label,
    string SourceType,
    int SchemaVersion,
    IReadOnlyList<string> Capabilities,
    string Status,
    bool IsAvailable,
    bool FeatureFlagEnabled,
    bool SupportsManualRun,
    bool SupportsBackgroundSync,
    IReadOnlyList<string> SupportedHosts,
    string Message);

[Flags]
public enum CourtSyncProviderCapability
{
    None = 0,
    SearchCases = 1 << 0,
    BindCase = 1 << 1,
    ReadCase = 1 << 2,
    DownloadDocuments = 1 << 3,
    DetectChanges = 1 << 4,
    ManualSync = 1 << 5,
    BackgroundSync = 1 << 6,
}

public sealed record CourtSyncCaseSearchRequest(
    string? SourceUrl = null,
    string? CourtCaseNumber = null,
    string? JudicialUid = null,
    string? CourtName = null,
    string? PartyName = null,
    string? Instance = null);

public sealed record CourtSyncCaseSearchResult(
    string ProviderKey,
    string SourceType,
    string SourceUrl,
    string CourtName,
    string CaseNumber,
    string JudicialUid,
    string JudgeName,
    string Instance,
    string Title,
    string Summary,
    bool CanFetch);

public sealed record CourtSyncCaseBinding(
    string SourceUrl,
    string? CourtCaseNumber = null,
    string? JudicialUid = null,
    string? Instance = null);

public sealed record CourtSyncDocumentReference(
    string ExternalId,
    string SourceUrl,
    string Title,
    string FileName,
    string ContentType);

public sealed record CourtSyncChangeCheckRequest(
    CourtSyncCaseBinding Binding,
    string? Checkpoint = null);

public sealed record CourtSyncChangeCheckResult(
    bool HasChanges,
    string? NextCheckpoint = null);

public interface ICourtSyncProvider
{
    string Key { get; }
    string Label { get; }
    string SourceType { get; }
    int SchemaVersion { get; }
    CourtSyncProviderCapability Capabilities { get; }
    IReadOnlyList<string> SupportedHosts { get; }
    bool CanHandle(string? sourceUrl);
}

public interface ICourtSyncProviderAvailability
{
    bool IsAvailable { get; }
    string Status { get; }
    string Message { get; }
}

public interface ICourtSyncCaseSearchProvider : ICourtSyncProvider
{
    Task<IReadOnlyList<CourtSyncCaseSearchResult>> SearchCasesAsync(CourtSyncCaseSearchRequest request, CancellationToken ct);
}

public interface ICourtSyncCaseReader : ICourtSyncProvider
{
    Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct);

    Task<SudrfCourtCaseData> ReadCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
        => ReadCaseAsync(new CourtSyncCaseBinding(sourceUrl, courtCaseNumber), ct);
}

public interface ICourtSyncDocumentProvider : ICourtSyncProvider
{
    Task<IReadOnlyList<CourtSyncDocumentReference>> GetDocumentsAsync(CourtSyncCaseBinding binding, CancellationToken ct);
    Task<Stream> OpenDocumentAsync(CourtSyncDocumentReference document, CancellationToken ct);
}

public interface ICourtSyncChangeDetector : ICourtSyncProvider
{
    Task<CourtSyncChangeCheckResult> CheckForChangesAsync(CourtSyncChangeCheckRequest request, CancellationToken ct);
}

public interface ICourtSyncProviderRegistry
{
    IReadOnlyList<CourtSyncProviderDescriptor> GetProviders();
    ICourtSyncProvider? Resolve(string? sourceUrl);
}

public sealed class SudrfCourtSyncProvider : ICourtSyncCaseSearchProvider, ICourtSyncCaseReader
{
    private readonly ISudrfCourtSyncService _service;

    public SudrfCourtSyncProvider(ISudrfCourtSyncService service)
    {
        _service = service;
    }

    public string Key => "sudrf";
    public string Label => "ГАС Правосудие / sudrf.ru";
    public string SourceType => "sudrf";
    public int SchemaVersion => 1;
    public CourtSyncProviderCapability Capabilities =>
        CourtSyncProviderCapability.SearchCases
        | CourtSyncProviderCapability.BindCase
        | CourtSyncProviderCapability.ReadCase
        | CourtSyncProviderCapability.ManualSync
        | CourtSyncProviderCapability.BackgroundSync;
    public IReadOnlyList<string> SupportedHosts { get; } = ["sudrf.ru"];

    public bool CanHandle(string? sourceUrl)
        => _service.CanHandle(sourceUrl);

    public async Task<IReadOnlyList<CourtSyncCaseSearchResult>> SearchCasesAsync(CourtSyncCaseSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceUrl) || !CanHandle(request.SourceUrl))
        {
            return [];
        }

        var courtData = await ReadCaseAsync(
            new CourtSyncCaseBinding(
                request.SourceUrl,
                FirstNonEmpty(request.JudicialUid, request.CourtCaseNumber),
                request.JudicialUid,
                request.Instance),
            ct);

        var requestedInstance = request.Instance?.Trim().ToLowerInvariant();
        if (requestedInstance is ("first" or "appeal") && courtData.Instance != requestedInstance)
        {
            throw new SudrfCourtSyncException("По указанной ссылке найдена карточка другой инстанции. Укажите сайт суда выбранной инстанции.");
        }

        if (!string.IsNullOrWhiteSpace(request.JudicialUid)
            && !string.Equals(courtData.JudicialUid, request.JudicialUid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SudrfCourtSyncException("Судебная карточка не подтверждает указанный УИД. Проверьте ссылку и идентификатор дела.");
        }

        return [MapSearchResult(courtData)];
    }

    public Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct)
        => _service.FetchCaseAsync(binding.SourceUrl, ct, FirstNonEmpty(binding.CourtCaseNumber, binding.JudicialUid));

    private CourtSyncCaseSearchResult MapSearchResult(SudrfCourtCaseData courtData)
    {
        var caseNumber = FirstNonEmpty(courtData.CaseNumber, courtData.JudicialUid);
        var title = FirstNonEmpty(courtData.CourtName, caseNumber, courtData.SourceUrl) ?? courtData.SourceUrl;
        var summaryParts = new[]
            {
                string.IsNullOrWhiteSpace(courtData.CourtName) ? null : courtData.CourtName,
                string.IsNullOrWhiteSpace(caseNumber) ? null : $"№ {caseNumber}",
                string.IsNullOrWhiteSpace(courtData.JudgeName) ? null : $"Судья: {courtData.JudgeName}",
            }
            .Where(static item => !string.IsNullOrWhiteSpace(item));

        return new CourtSyncCaseSearchResult(
            Key,
            SourceType,
            courtData.SourceUrl,
            courtData.CourtName,
            courtData.CaseNumber,
            courtData.JudicialUid,
            courtData.JudgeName,
            courtData.Instance,
            title,
            string.Join("; ", summaryParts),
            !string.IsNullOrWhiteSpace(courtData.SourceUrl));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed class CourtSyncProviderRegistry : ICourtSyncProviderRegistry
{
    private static readonly (CourtSyncProviderCapability Capability, string Key)[] CapabilityKeys =
    [
        (CourtSyncProviderCapability.SearchCases, "case-search"),
        (CourtSyncProviderCapability.BindCase, "case-binding"),
        (CourtSyncProviderCapability.ReadCase, "case-reading"),
        (CourtSyncProviderCapability.DownloadDocuments, "document-download"),
        (CourtSyncProviderCapability.DetectChanges, "change-detection"),
        (CourtSyncProviderCapability.ManualSync, "manual-sync"),
        (CourtSyncProviderCapability.BackgroundSync, "background-sync"),
    ];

    private readonly IReadOnlyList<ICourtSyncProvider> _providers;
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _featureFlags;
    private readonly Lazy<IReadOnlyList<CourtSyncProviderDescriptor>> _providerDescriptors;

    public CourtSyncProviderRegistry(
        IEnumerable<ICourtSyncProvider> providers,
        IOptionsSnapshot<AppFeatureFlagsOptions> featureFlags)
    {
        _providers = providers.ToList();
        _featureFlags = featureFlags;
        _providerDescriptors = new Lazy<IReadOnlyList<CourtSyncProviderDescriptor>>(
            BuildProviderDescriptors,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders()
        => _providerDescriptors.Value;

    private IReadOnlyList<CourtSyncProviderDescriptor> BuildProviderDescriptors()
    {
        var legal = _featureFlags.Value.Legal;
        var activeProviders = _providers
            .Select(provider => BuildActiveDescriptor(provider, legal))
            .ToList();

        var activeKeys = activeProviders
            .Select(static provider => provider.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        activeProviders.AddRange(
            GetPlannedDescriptors(legal.ExternalCourtSync)
                .Where(provider => !activeKeys.Contains(provider.Key)));
        return activeProviders.AsReadOnly();
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

    private static CourtSyncProviderDescriptor BuildActiveDescriptor(
        ICourtSyncProvider provider,
        LegalFeatureFlagsOptions legal)
    {
        if (provider.Key == "sudrf")
        {
            return BuildSudrfDescriptor(legal.ExternalCourtSync);
        }

        var capabilities = GetEffectiveCapabilities(provider);
        var featureFlagEnabled = IsProviderFeatureEnabled(provider.Key, legal);
        var supportsManualRun = (capabilities & CourtSyncProviderCapability.ManualSync) != 0;
        var supportsBackgroundSync =
            featureFlagEnabled
            && (capabilities & CourtSyncProviderCapability.BackgroundSync) != 0;
        var availability = provider as ICourtSyncProviderAvailability;
        var isAvailable = availability?.IsAvailable ?? true;
        var status = availability?.Status
            ?? (featureFlagEnabled ? "active" : "manual-only");
        var message = availability?.Message
            ?? (supportsBackgroundSync
                ? "Источник доступен для ручной и фоновой судебной синхронизации."
                : supportsManualRun
                    ? "Источник доступен для ручной проверки; фоновая синхронизация для него недоступна."
                    : "Источник подключён, но не поддерживает ручное или фоновое чтение карточки.");

        return new CourtSyncProviderDescriptor(
            provider.Key,
            provider.Label,
            provider.SourceType,
            provider.SchemaVersion,
            GetCapabilityKeys(capabilities),
            status,
            isAvailable,
            featureFlagEnabled,
            supportsManualRun,
            supportsBackgroundSync,
            provider.SupportedHosts,
            message);
    }

    private static bool IsProviderFeatureEnabled(string providerKey, LegalFeatureFlagsOptions legal)
        => providerKey switch
        {
            "kadArbitr" => legal.ExternalCourtSync && legal.KadArbitrCourtSync,
            "mosGorSud" => legal.ExternalCourtSync && legal.MosGorSudCourtSync,
            _ => legal.ExternalCourtSync,
        };

    private static CourtSyncProviderDescriptor BuildSudrfDescriptor(bool externalCourtSyncEnabled)
        => new(
            "sudrf",
            "ГАС Правосудие / sudrf.ru",
            "sudrf",
            1,
            GetCapabilityKeys(
                CourtSyncProviderCapability.SearchCases
                | CourtSyncProviderCapability.BindCase
                | CourtSyncProviderCapability.ReadCase
                | CourtSyncProviderCapability.ManualSync
                | CourtSyncProviderCapability.BackgroundSync),
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
                1,
                [],
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
                1,
                [],
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
                1,
                [],
                "planned",
                false,
                externalCourtSyncEnabled,
                false,
                false,
                [],
                "Источник будет подключаться после уточнения обязательных судебных порталов и допустимых способов получения данных."),
        ];

    private static IReadOnlyList<string> GetCapabilityKeys(CourtSyncProviderCapability capabilities)
        => CapabilityKeys
            .Where(item => (capabilities & item.Capability) != 0)
            .Select(static item => item.Key)
            .ToArray();

    private static CourtSyncProviderCapability GetEffectiveCapabilities(ICourtSyncProvider provider)
    {
        var capabilities = provider.Capabilities;
        if (provider is not ICourtSyncCaseSearchProvider)
        {
            capabilities &= ~CourtSyncProviderCapability.SearchCases;
        }

        if (provider is not ICourtSyncCaseReader)
        {
            capabilities &=
                ~CourtSyncProviderCapability.ReadCase
                & ~CourtSyncProviderCapability.ManualSync
                & ~CourtSyncProviderCapability.BackgroundSync;
        }

        if (provider is not ICourtSyncDocumentProvider)
        {
            capabilities &= ~CourtSyncProviderCapability.DownloadDocuments;
        }

        if (provider is not ICourtSyncChangeDetector)
        {
            capabilities &= ~CourtSyncProviderCapability.DetectChanges;
        }

        return capabilities;
    }
}

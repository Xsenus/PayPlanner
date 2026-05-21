using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.LegalIntegrations;

public interface ILegalIntegrationDiagnosticsService
{
    LegalIntegrationDiagnosticsDto GetDiagnostics();
}

public sealed class LegalIntegrationDiagnosticsService : ILegalIntegrationDiagnosticsService
{
    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _options;

    public LegalIntegrationDiagnosticsService(IOptionsSnapshot<AppFeatureFlagsOptions> options)
    {
        _options = options;
    }

    public LegalIntegrationDiagnosticsDto GetDiagnostics()
        => BuildDiagnostics(_options.Value);

    public static LegalIntegrationDiagnosticsDto BuildDiagnostics(AppFeatureFlagsOptions options)
    {
        var legal = options.Legal;
        var items = new List<LegalIntegrationDiagnosticItemDto>
        {
            BuildItem(
                "externalCourtSync",
                "Внешняя синхронизация судов",
                "FeatureFlags:Legal:ExternalCourtSync",
                legal.ExternalCourtSync,
                "Интеграция с внешними судебными источниками выключена до отдельного решения."),
            BuildItem(
                "externalCalendarSync",
                "Google/Яндекс календарь",
                "FeatureFlags:Legal:ExternalCalendarSync",
                legal.ExternalCalendarSync,
                "Интеграция с внешними календарями выключена до отдельного решения."),
        };

        return new LegalIntegrationDiagnosticsDto
        {
            GeneratedAt = DateTime.UtcNow,
            HasEnabledExternalLegalFlags = items.Any(static item => item.Enabled),
            Items = items,
        };
    }

    private static LegalIntegrationDiagnosticItemDto BuildItem(
        string key,
        string label,
        string featureFlagPath,
        bool enabled,
        string disabledMessage)
        => new()
        {
            Key = key,
            Label = label,
            FeatureFlagPath = featureFlagPath,
            Enabled = enabled,
            Status = enabled ? "requiresApproval" : "disabled",
            SafeToRun = !enabled,
            Message = enabled
                ? "Feature flag включен. Перед запуском нужен отдельный dry-run, проверка ролей и явное release-решение."
                : disabledMessage,
        };
}

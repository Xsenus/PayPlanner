using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientCaseStageClassifier
{
    private static readonly string[] PretrialAliases =
    [
        "внесуд",
        "досуд",
        "претенз",
        "переговор"
    ];

    public static bool IsPretrial(ClientCase? clientCase)
        => clientCase is not null && IsPretrial(clientCase.Stage, clientCase.CaseType);

    public static bool IsPretrial(string? stage, string? caseType)
    {
        var haystack = string.Join(' ', new[] { stage, caseType }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()));

        return PretrialAliases.Any(alias => haystack.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }
}

using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncLoggingSafetyTests
{
    [Fact]
    public void CourtSyncSources_DoNotLogFullSourceUrlsOrExceptionPayloads()
    {
        var sudrfSource = ReadSourceFile("backend", "Services", "LegalIntegrations", "SudrfCourtSyncService.cs");
        var schedulerSource = ReadSourceFile("backend", "Services", "LegalIntegrations", "ClientCaseCourtSyncSchedulerService.cs");

        Assert.DoesNotContain("{SourceUrl}", sudrfSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger.LogWarning(ex", sudrfSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger.LogError(ex", schedulerSource, StringComparison.Ordinal);
        Assert.Contains("sourceUri.IdnHost", sudrfSource, StringComparison.Ordinal);
        Assert.Contains("exception.GetType().Name", sudrfSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLogging_DisablesInformationalHttpClientLogs()
    {
        var settingsPath = ResolveRepositoryFile("backend", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var level = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty("System.Net.Http.HttpClient")
            .GetString();

        Assert.Equal("Warning", level);
    }

    private static string ReadSourceFile(params string[] pathParts)
        => File.ReadAllText(ResolveRepositoryFile(pathParts));

    private static string ResolveRepositoryFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Не найден файл репозитория: {Path.Combine(pathParts)}");
    }
}

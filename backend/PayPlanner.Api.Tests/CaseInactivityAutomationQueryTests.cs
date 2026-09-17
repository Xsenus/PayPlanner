using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseInactivityAutomationQueryTests
{
    [Fact]
    public void AggregatePreviewQuery_UsesBoundedPreAggregatedSql()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_query_translation;Username=payplanner")
            .Options;
        using var db = new PostgreSqlPaymentContext(options);
        var now = DateTime.UtcNow;

        var classifiedCases = CaseInactivityAutomationQuery.Build(
            db,
            [],
            hasAvailableDefaultAssignee: true,
            inactivityCutoff: now.AddDays(-14),
            escalationCutoff: now.AddDays(-30));
        var sql = classifiedCases
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TrackedCaseCount = group.Count(),
                ExcludedByStatusCount = group.Count(item => item.IsExcludedStatus),
                PausedByVacationCount = group.Count(item => item.IsPausedByVacation),
                DisabledCount = group.Count(item => item.IsDisabled),
                ActiveTaskCount = group.Count(item => item.IsBlockedByActiveTask),
                UnassignedCount = group.Count(item => item.IsUnassigned),
                RecentlyActiveCount = group.Count(item => item.IsRecentlyActive),
                CandidateCount = group.Count(item => item.IsCandidate),
                EscalationCandidateCount = group.Count(item => item.IsCriticalCandidate),
            })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", sql, StringComparison.Ordinal);
        Assert.True(
            sql.Length < 100_000,
            $"SQL предварительного расчёта разросся до {sql.Length} символов.");
        Assert.True(
            CountOccurrences(sql, "SELECT ") < 100,
            $"SQL предварительного расчёта содержит {CountOccurrences(sql, "SELECT ")} вложенных SELECT.");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }
}

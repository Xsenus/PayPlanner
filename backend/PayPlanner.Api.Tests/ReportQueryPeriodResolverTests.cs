using PayPlanner.Api.Services.Reports;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ReportQueryPeriodResolverTests
{
    [Fact]
    public void ResolveReportPeriod_DefaultsToTodayMonth()
    {
        var today = new DateTime(2026, 6, 10, 15, 30, 0, DateTimeKind.Utc);

        var (start, end) = ReportQueryPeriodResolver.ResolveReportPeriod(null, null, today);

        Assert.Equal(new DateTime(2026, 6, 1), start);
        Assert.Equal(new DateTime(2026, 6, 30), end);
    }

    [Fact]
    public void ResolveReportPeriod_StripsTimeAndOrdersRange()
    {
        var today = new DateTime(2026, 6, 10);
        var from = new DateTime(2026, 6, 20, 23, 59, 59, DateTimeKind.Local);
        var to = new DateTime(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);

        var (start, end) = ReportQueryPeriodResolver.ResolveReportPeriod(from, to, today);

        Assert.Equal(new DateTime(2026, 6, 5), start);
        Assert.Equal(new DateTime(2026, 6, 20), end);
    }

    [Fact]
    public void NormalizeReportDate_ReturnsDateWithUnspecifiedKind()
    {
        var value = new DateTime(2026, 6, 10, 18, 45, 0, DateTimeKind.Utc);

        var normalized = ReportQueryPeriodResolver.NormalizeReportDate(value);

        Assert.Equal(new DateTime(2026, 6, 10), normalized);
        Assert.Equal(DateTimeKind.Unspecified, normalized.Kind);
    }

    [Fact]
    public void NormalizeReportInstant_TreatsUnspecifiedAsUtcInstant()
    {
        var value = new DateTime(2026, 6, 10, 12, 30, 0, DateTimeKind.Unspecified);

        var normalized = ReportQueryPeriodResolver.NormalizeReportInstant(value);

        Assert.Equal(new DateTime(2026, 6, 10, 12, 30, 0, DateTimeKind.Utc), normalized);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Fact]
    public void ResolveReportLocalDate_ConvertsUtcInstantToUserLocalDate()
    {
        var plusThree = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");
        var value = new DateTime(2026, 4, 30, 21, 30, 0, DateTimeKind.Utc);

        var localDate = ReportQueryPeriodResolver.ResolveReportLocalDate(value, plusThree);

        Assert.Equal(new DateTime(2026, 5, 1), localDate);
        Assert.Equal(DateTimeKind.Unspecified, localDate.Kind);
    }
}

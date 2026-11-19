using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services.Invoices;

public sealed class ActAnnualNumberExhaustedException : InvalidOperationException
{
    public ActAnnualNumberExhaustedException() : base("Годовая последовательность номеров актов исчерпана.") { }
}

public sealed class ActAnnualNumberingService(PaymentContext db)
{
    public async Task<string> NextAsync(DateTime date, CancellationToken ct)
    {
        await LockAsync(ct);
        await EnsureFloorAsync(date.Year, 0, ct);
        if (await db.ActAnnualNumberCounters.Where(counter => counter.Year == date.Year && counter.LastNumber < long.MaxValue)
            .ExecuteUpdateAsync(setters => setters.SetProperty(counter => counter.LastNumber, counter => counter.LastNumber + 1), ct) != 1)
            throw new ActAnnualNumberExhaustedException();
        var number = await db.ActAnnualNumberCounters.AsNoTracking().Where(counter => counter.Year == date.Year)
            .Select(counter => counter.LastNumber).SingleAsync(ct);
        return number.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<string> PreviewNextAsync(DateTime date, CancellationToken ct)
    {
        var floor = await db.ActAnnualNumberCounters.AsNoTracking().Where(counter => counter.Year == date.Year)
            .Select(counter => (long?)counter.LastNumber).SingleOrDefaultAsync(ct)
            ?? await HistoricalFloorAsync(date.Year, ct);
        if (floor == long.MaxValue) throw new ActAnnualNumberExhaustedException();
        return (floor + 1).ToString(CultureInfo.InvariantCulture);
    }

    public async Task RaiseFloorAsync(DateTime date, string? number, CancellationToken ct)
    {
        var text = number?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(character => character is < '0' or > '9')) return;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0) return;
        await LockAsync(ct);
        await EnsureFloorAsync(date.Year, ordinal, ct);
    }

    public async Task RefreshYearAsync(int year, CancellationToken ct)
    {
        await LockAsync(ct);
        await EnsureFloorAsync(year, await HistoricalFloorAsync(year, ct), ct);
    }

    public Task LockAsync(CancellationToken ct)
        => new GeneratedFinancialClaims(db).LockNumberScopeAsync(GeneratedFinancialDocumentKind.Act, ct);

    private async Task EnsureFloorAsync(int year, long minimum, CancellationToken ct)
    {
        var existing = await db.ActAnnualNumberCounters.AsNoTracking().Where(counter => counter.Year == year)
            .Select(counter => (long?)counter.LastNumber).SingleOrDefaultAsync(ct);
        if (existing.HasValue && existing.Value >= minimum) return;
        var floor = Math.Max(minimum, existing ?? await HistoricalFloorAsync(year, ct));
        // Keep allocator metadata outside the change tracker. Capture callbacks
        // must remain clean and imports may already have unrelated tracked rows.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActAnnualNumberCounters" ("Year", "LastNumber") VALUES ({year}, {floor})
            ON CONFLICT ("Year") DO UPDATE SET "LastNumber" =
                CASE WHEN "ActAnnualNumberCounters"."LastNumber" < excluded."LastNumber"
                    THEN excluded."LastNumber" ELSE "ActAnnualNumberCounters"."LastNumber" END
            """, ct);
    }

    private async Task<long> HistoricalFloorAsync(int year, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())
            throw new NotSupportedException("Годовая нумерация актов поддерживает SQLite и PostgreSQL.");
        var start = new DateTime(year, 1, 1);
        var end = year == 9999 ? DateTime.MaxValue : start.AddYears(1);
        var startKey = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endKey = year == 9999 ? "9999-12-32" : end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var upperComparison = year == 9999 ? "<=" : "<";
        // Match the existing invoice generator's representable-ordinal rule.
        // Longer historical identifiers remain intact and cannot occupy this series.
        var ordinal = "ltrim(trim(\"Number\"), '0')";
        var reachable = $"(length({ordinal}) BETWEEN 1 AND 18 OR (length({ordinal}) = 19 AND {ordinal} <= '9223372036854775807'))";
        var digits = db.Database.IsNpgsql() ? $"{ordinal} ~ '^[0-9]+$'" : $"{ordinal} <> '' AND {ordinal} NOT GLOB '*[^0-9]*'";
        var integerType = db.Database.IsNpgsql() ? "bigint" : "INTEGER";
        var candidate = $"CASE WHEN {digits} AND {reachable} THEN CAST({ordinal} AS {integerType}) ELSE 0 END";
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
        command.CommandText = $"""
            SELECT COALESCE(MAX({candidate}), 0) FROM (
                SELECT "Number" FROM "Acts" WHERE "Date" >= @start AND "Date" {upperComparison} @end
                UNION ALL
                SELECT "Number" FROM "GeneratedFinancialNumberClaims"
                    WHERE "Kind" = 'act' AND "DateKey" >= @startKey AND "DateKey" < @endKey
            ) AS history
            """;
        AddParameter(command, "start", DateTime.SpecifyKind(start, DateTimeKind.Unspecified));
        AddParameter(command, "end", DateTime.SpecifyKind(end, DateTimeKind.Unspecified));
        AddParameter(command, "startKey", startKey);
        AddParameter(command, "endKey", endKey);
        var close = command.Connection!.State != System.Data.ConnectionState.Open;
        try
        {
            if (close) await db.Database.OpenConnectionAsync(ct);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { if (close) await db.Database.CloseConnectionAsync(); }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

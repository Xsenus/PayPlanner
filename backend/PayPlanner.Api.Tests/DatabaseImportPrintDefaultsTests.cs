using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class DatabaseImportServiceTests
{
    [Fact]
    public async Task ImportSqliteAsync_PrintDefaultsKeepTargetCompanyOwnershipAndOrdinarySettingsImport()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"payplanner-print-defaults-{Guid.NewGuid():N}.sqlite");
        await using var targetConnection = await CreateOpenConnectionAsync();
        var existingKey = CompanyDocumentImagePrintDefaultsService.Key(7);
        var sourceOnlyKey = CompanyDocumentImagePrintDefaultsService.Key(8);
        var targetValue = CompanyDocumentImagePrintDefaultsService.Serialize(CompanyDocumentImagePrintDefaultsTests.Value(5));
        try
        {
            await using (var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString()))
            {
                await sourceConnection.OpenAsync();
                await using var source = CreateContext(sourceConnection);
                await source.Database.EnsureCreatedAsync();
                source.SystemSettings.AddRange(
                    new SystemSetting { Key = existingKey, Value = "foreign profile with same local ID" },
                    new SystemSetting { Key = "  " + sourceOnlyKey.ToUpperInvariant() + "  ", Value = "foreign source-only profile" },
                    new SystemSetting { Key = "ui.synthetic.print-defaults-import", Value = "ordinary source value" });
                await source.SaveChangesAsync();
            }
            await using (var target = CreateContext(targetConnection))
            {
                await target.Database.EnsureCreatedAsync();
                target.SystemSettings.Add(new SystemSetting { Key = existingKey, Value = targetValue });
                await target.SaveChangesAsync();
            }

            var result = await CreateService(targetConnection).ImportSqliteAsync(sourcePath);
            Assert.True(result.Success);
            Assert.DoesNotContain(result.Tables, table => table.Errors > 0);
            await using var verify = CreateContext(targetConnection);
            var rows = await verify.SystemSettings.AsNoTracking().ToListAsync();
            Assert.Equal(targetValue, Assert.Single(rows, row => row.Key == existingKey).Value);
            Assert.DoesNotContain(rows, row => row.Key.Trim().Equals(sourceOnlyKey, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("ordinary source value", Assert.Single(rows, row => row.Key == "ui.synthetic.print-defaults-import").Value);
            Assert.Equal(2, rows.Count);
            await using var originalConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await originalConnection.OpenAsync();
            await using var original = CreateContext(originalConnection);
            Assert.Equal(3, await original.SystemSettings.CountAsync());
            Assert.Equal("foreign profile with same local ID", (await original.SystemSettings.SingleAsync(row => row.Key == existingKey)).Value);
        }
        finally
        {
            TryDeleteFile(sourcePath);
        }
    }
}

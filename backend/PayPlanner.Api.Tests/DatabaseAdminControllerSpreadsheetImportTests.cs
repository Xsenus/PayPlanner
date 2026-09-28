using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DatabaseAdminControllerSpreadsheetImportTests
{
    [Theory]
    [InlineData("/api/database/status")]
    [InlineData("/api/database/backups/catalog")]
    public async Task DatabaseAdminEndpoints_ForbidUserWithoutAdminRoleInDatabase(string endpoint)
    {
        await using var connection = await CreateOpenConnectionAsync();
        var backupDirectory = CreateTempDirectory();

        try
        {
            using var server = await CreateServerAsync(connection, backupDirectory, userRoleName: "user");
            using var client = server.CreateClient();

            using var response = await client.GetAsync(endpoint);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    [Fact]
    public async Task BackupCatalog_PagesEqualTimestampsAndPreservesCopyStatesAsUtc()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var backupDirectory = CreateTempDirectory();
        try
        {
            using var server = await CreateServerAsync(connection, backupDirectory);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                var createdAt = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
                foreach (var id in new[] { "001", "002", "003" })
                {
                    var set = new DatabaseBackupSet
                    {
                        Id = id,
                        BackupRelativePath = $"catalog/{id}.zip",
                        CreatedAt = createdAt,
                        Status = DatabaseBackupSetStatuses.NoLocalCopy,
                    };
                    set.Copies.Add(new DatabaseBackupCopy
                    {
                        ProviderKey = "local",
                        Status = DatabaseBackupCopyStatuses.Deleted,
                    });
                    set.Copies.Add(new DatabaseBackupCopy
                    {
                        ProviderKey = "s3",
                        ConnectionId = "main",
                        Status = DatabaseBackupCopyStatuses.Verified,
                        LastVerifiedAt = createdAt,
                        AttemptCount = 2,
                    });
                    db.DatabaseBackupSets.Add(set);
                }
                await db.SaveChangesAsync();
            }
            using var client = server.CreateClient();
            var first = await client.GetFromJsonAsync<DatabaseBackupCatalogPageDto>("/api/database/backups/catalog?pageSize=1");
            Assert.Equal("003", Assert.Single(first!.Items).Id);
            Assert.Equal(DateTimeKind.Utc, first.Items[0].CreatedAt.Kind);
            Assert.Equal("003", first.NextCursor);
            Assert.Contains(first.Items[0].Copies, copy => copy.ProviderKey == "local" && copy.Status == "deleted");
            Assert.Contains(first.Items[0].Copies, copy => copy.ProviderKey == "s3" && copy.AttemptCount == 2
                && copy.LastVerifiedAt!.Value.Kind == DateTimeKind.Utc);
            var second = await client.GetFromJsonAsync<DatabaseBackupCatalogPageDto>("/api/database/backups/catalog?pageSize=1&after=003");
            Assert.Equal("002", Assert.Single(second!.Items).Id);
            var last = await client.GetFromJsonAsync<DatabaseBackupCatalogPageDto>("/api/database/backups/catalog?pageSize=1&after=002");
            Assert.Equal("001", Assert.Single(last!.Items).Id);
            Assert.Null(last.NextCursor);
        }
        finally { TryDeleteDirectory(backupDirectory); }
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("after=missing")]
    public async Task BackupCatalog_RejectsInvalidPaging(string query)
    {
        await using var connection = await CreateOpenConnectionAsync();
        var backupDirectory = CreateTempDirectory();
        try
        {
            using var server = await CreateServerAsync(connection, backupDirectory);
            using var client = server.CreateClient();
            using var response = await client.GetAsync($"/api/database/backups/catalog?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally { TryDeleteDirectory(backupDirectory); }
    }

    [Fact]
    public async Task ApplySpreadsheetImport_WithJsonRequestPayload_Succeeds()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var backupDirectory = CreateTempDirectory();

        try
        {
            using var server = await CreateServerAsync(connection, backupDirectory);
            var workbook = CreateCounterpartiesWorkbook();
            var preview = await PreviewAsync(server, workbook);
            var previewId = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows).PreviewId;

            using var client = server.CreateClient();
            using var payload = CreateMultipartWithFile(workbook);
            payload.Add(new StringContent($$"""
                {
                  "SelectedPreviewIds": ["{{previewId}}"]
                }
                """, Encoding.UTF8), "requestJson");

            using var response = await client.PostAsync("/api/database/import/spreadsheets/apply", payload);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var batch = await response.Content.ReadFromJsonAsync<SpreadsheetImportBatchDetailDto>();
            Assert.NotNull(batch);
            Assert.Equal(1, batch!.TotalRows);
            Assert.True(batch.CreatedRows + batch.UpdatedRows + batch.SkippedRows + batch.ErrorRows >= 1);
        }
        finally
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    [Fact]
    public async Task ApplySpreadsheetImport_WithMoreThanDefaultFormValueCount_Succeeds()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var backupDirectory = CreateTempDirectory();

        try
        {
            using var server = await CreateServerAsync(connection, backupDirectory);
            var workbook = CreateCounterpartiesWorkbook();
            var preview = await PreviewAsync(server, workbook);
            var previewId = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows).PreviewId;

            using var client = server.CreateClient();
            using var payload = CreateMultipartWithFile(workbook);

            for (var index = 0; index < 1500; index++)
            {
                payload.Add(new StringContent(previewId, Encoding.UTF8), "SelectedPreviewIds");
            }

            using var response = await client.PostAsync("/api/database/import/spreadsheets/apply", payload);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var batch = await response.Content.ReadFromJsonAsync<SpreadsheetImportBatchDetailDto>();
            Assert.NotNull(batch);
            Assert.Equal(1, batch!.TotalRows);
            Assert.True(batch.CreatedRows + batch.UpdatedRows + batch.SkippedRows + batch.ErrorRows >= 1);
        }
        finally
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    private static async Task<SpreadsheetImportPreviewDto> PreviewAsync(TestServer server, byte[] workbook)
    {
        using var client = server.CreateClient();
        using var payload = CreateMultipartWithFile(workbook);
        using var response = await client.PostAsync("/api/database/import/spreadsheets/preview", payload);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SpreadsheetImportPreviewDto>()
            ?? throw new InvalidOperationException("Preview response was empty.");
    }

    private static MultipartFormDataContent CreateMultipartWithFile(byte[] workbook)
    {
        var payload = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(workbook);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        payload.Add(fileContent, "files", "Контрагенты.xlsx");
        return payload;
    }

    private static async Task<TestServer> CreateServerAsync(
        SqliteConnection connection,
        string backupDirectory,
        string userRoleName = "admin")
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabaseBackup:Directory"] = backupDirectory,
                });
            })
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddHttpContextAccessor();
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
                });
                services.AddDbContextFactory<PaymentContext>(options => options.UseSqlite(connection));
                services.AddScoped<PaymentContext>(sp =>
                    sp.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContext());
                services.AddSingleton(new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, connection.ConnectionString));
                services.AddScoped<IDatabaseImportService, DatabaseImportService>();
                services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
                services.AddSingleton<DatabaseBackupService>();
                services.AddControllers()
                    .AddApplicationPart(typeof(DatabaseAdminController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });

        var server = new TestServer(builder);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await db.Database.EnsureCreatedAsync();
        var role = new Role
        {
            Name = userRoleName,
            Description = userRoleName,
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        db.Users.Add(new User
        {
            Id = TestAuthHandler.UserId,
            Email = $"database-admin-{Guid.NewGuid():N}@example.test",
            FullName = "Spreadsheet Import Test User",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return server;
    }

    private static byte[] CreateCounterpartiesWorkbook()
        => CreateWorkbook(("Контрагенты", new[]
        {
            new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
            new[] { "ООО Ромашка", "1234567890", "Иван", "+7 (999) 000-00-00", "test@example.com", "Основной", "5", "Юр. услуги", "" },
        }));

    private static async Task<SqliteConnection> CreateOpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-import-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] CreateWorkbook(params (string Name, string[][] Rows)[] sheets)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", BuildContentTypesXml(sheets.Length));
            AddEntry(archive, "_rels/.rels", RootRelationshipsXml);
            AddEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheets));
            AddEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml(sheets.Length));

            for (var index = 0; index < sheets.Length; index++)
            {
                AddEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheetXml(sheets[index].Rows));
            }
        }

        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildContentTypesXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        builder.AppendLine("""  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        builder.AppendLine("""  <Default Extension="xml" ContentType="application/xml"/>""");
        builder.AppendLine("""  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");

        for (var index = 0; index < sheetCount; index++)
        {
            builder.AppendLine($"""  <Override PartName="/xl/worksheets/sheet{index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        builder.AppendLine("</Types>");
        return builder.ToString();
    }

    private static string BuildWorkbookXml((string Name, string[][] Rows)[] sheets)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        builder.AppendLine("  <sheets>");

        for (var index = 0; index < sheets.Length; index++)
        {
            builder.AppendLine($"""    <sheet name="{EscapeXml(sheets[index].Name)}" sheetId="{index + 1}" r:id="rId{index + 1}"/>""");
        }

        builder.AppendLine("  </sheets>");
        builder.AppendLine("</workbook>");
        return builder.ToString();
    }

    private static string BuildWorkbookRelationshipsXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");

        for (var index = 0; index < sheetCount; index++)
        {
            builder.AppendLine($"""  <Relationship Id="rId{index + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index + 1}.xml"/>""");
        }

        builder.AppendLine("</Relationships>");
        return builder.ToString();
    }

    private static string BuildWorksheetXml(string[][] rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("  <sheetData>");

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            builder.AppendLine($"""    <row r="{rowIndex + 1}">""");

            for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
            {
                var cellReference = $"{GetColumnName(columnIndex)}{rowIndex + 1}";
                var value = rows[rowIndex][columnIndex] ?? string.Empty;
                builder.AppendLine($"""      <c r="{cellReference}" t="inlineStr"><is><t>{EscapeXml(value)}</t></is></c>""");
            }

            builder.AppendLine("    </row>");
        }

        builder.AppendLine("  </sheetData>");
        builder.AppendLine("</worksheet>");
        return builder.ToString();
    }

    private static string GetColumnName(int columnIndex)
    {
        var dividend = columnIndex + 1;
        var columnName = string.Empty;

        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }

    private static string EscapeXml(string value)
        => SecurityElement.Escape(value) ?? string.Empty;

    private const string RootRelationshipsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n"
        + "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\n"
        + "</Relationships>\n";

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const int UserId = 999;

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, "Spreadsheet Import Test User"),
                new Claim(ClaimTypes.Role, "admin"),
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

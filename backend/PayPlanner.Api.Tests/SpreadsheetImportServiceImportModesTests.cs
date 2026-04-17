using System.IO.Compression;
using System.Security;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SpreadsheetImportServiceImportModesTests
{
    [Fact]
    public async Task PreviewAsync_ForEntrepreneurCounterparty_DefaultsToLegalEntityMode()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var preview = await service.PreviewAsync(new[]
        {
            CreateCounterpartiesFile(
                new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
                new[] { "ИП Загородняя Ирина Александровна", "633003961003", "Загородняя Ирина Александровна", "+7 937 200 37 04", "iviazav@mail.ru", "Основной", "5", "Оплата от покупателя", "" }),
        });

        var counterpartySection = Assert.Single(preview.Sections, section => section.Key == "counterparties");
        var row = Assert.Single(counterpartySection.Rows);

        Assert.Equal("LegalEntityWithClient", row.CounterpartyImportMode);
        Assert.Contains("ClientOnly", row.AvailableCounterpartyImportModes);
        Assert.Contains("LegalEntityWithClient", row.AvailableCounterpartyImportModes);
    }

    [Fact]
    public async Task ApplyAsync_ForEntrepreneurCounterparty_CreatesLinkedLegalEntityAndClient_AndRollbackRemovesBoth()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateCounterpartiesFile(
                new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
                new[] { "ИП Загородняя Ирина Александровна", "633003961003", "Загородняя Ирина Александровна", "+7 937 200 37 04", "iviazav@mail.ru", "Основной", "5", "Оплата от покупателя", "" }),
        };

        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows);

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = new List<string> { row.PreviewId },
        });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);

        await using (var db = CreateContext(connection))
        {
            var client = await db.Clients.SingleAsync();
            var legalEntity = await db.LegalEntities.SingleAsync();

            Assert.Equal(legalEntity.Id, client.LegalEntityId);
            Assert.Equal("ИП Загородняя Ирина Александровна", legalEntity.ShortName);
            Assert.Equal("ИП Загородняя Ирина Александровна", client.Name);
        }

        var rollback = await service.RollbackAsync(batch.Id);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(1, rollback.RollbackRows);

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(0, await db.Clients.CountAsync());
            Assert.Equal(0, await db.LegalEntities.CountAsync());
        }
    }

    [Fact]
    public async Task ApplyAsync_ForEntrepreneurCounterparty_ClientOnlyMode_CreatesOnlyClient()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateCounterpartiesFile(
                new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
                new[] { "ИП Загородняя Ирина Александровна", "633003961003", "Загородняя Ирина Александровна", "+7 937 200 37 04", "iviazav@mail.ru", "Основной", "5", "Оплата от покупателя", "" }),
        };

        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows);

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = new List<string> { row.PreviewId },
            CounterpartyOptions = new List<SpreadsheetImportCounterpartyOptionRequest>
            {
                new()
                {
                    PreviewId = row.PreviewId,
                    ImportMode = "ClientOnly",
                },
            },
        });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);

        await using var db = CreateContext(connection);
        var client = await db.Clients.SingleAsync();
        Assert.Equal(0, await db.LegalEntities.CountAsync());
        Assert.Null(client.LegalEntityId);
        Assert.Equal("Загородняя Ирина Александровна", client.Name);
    }

    [Fact]
    public async Task ApplyAsync_ForInvoiceImport_CreatesPaymentWithInvoiceLinkAndStatus()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Дата оплаты", "Номенклатурная часть" },
                new[] { "01.06.2026", "INV-115", "1500", "ООО Ромашка", "Оплачен", "05.06.2026", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
        };

        var preview = await service.PreviewAsync(files);
        var selectedPreviewIds = preview.Sections
            .SelectMany(section => section.Rows)
            .Where(row => row.IsSelectable)
            .Select(row => row.PreviewId)
            .ToList();

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = selectedPreviewIds,
        });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);

        await using var db = CreateContext(connection);
        var payment = await db.Payments
            .Include(item => item.Invoice)
            .SingleAsync();

        Assert.Equal(DocumentKind.Invoice, payment.DocumentKind);
        Assert.NotNull(payment.Invoice);
        Assert.Equal(payment.Invoice!.Id, payment.InvoiceId);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(1500m, payment.Amount);
        Assert.Equal(1500m, payment.PaidAmount);
        Assert.True(payment.IsPaid);
    }

    private static SpreadsheetImportUploadFile CreateInvoiceFile(params string[][] rows)
        => CreateUploadFile("Счета.xlsx", CreateWorkbook(("Счета", rows)));

    private static SpreadsheetImportUploadFile CreateCounterpartiesFile(params string[][] rows)
        => CreateUploadFile("Контрагенты.xlsx", CreateWorkbook(("Контрагенты", rows)));

    private static SpreadsheetImportUploadFile CreateUploadFile(string fileName, byte[] content)
        => new(fileName, content);

    private static async Task<SpreadsheetImportService> CreateServiceAsync(SqliteConnection connection)
    {
        var httpContextAccessor = CreateHttpContextAccessor();
        var factory = new TestPaymentContextFactory(CreateOptions(connection), httpContextAccessor);

        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        return new SpreadsheetImportService(factory, httpContextAccessor, NullLogger<SpreadsheetImportService>.Instance);
    }

    private static PaymentContext CreateContext(SqliteConnection connection)
        => new(CreateOptions(connection), CreateHttpContextAccessor());

    private static DbContextOptions<PaymentContext> CreateOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

    private static async Task<SqliteConnection> CreateOpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static IHttpContextAccessor CreateHttpContextAccessor()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "Import Tester"),
        }, "Test");

        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            },
        };
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
                var cellRef = $"{GetColumnName(columnIndex)}{rowIndex + 1}";
                var value = rows[rowIndex][columnIndex] ?? string.Empty;
                builder.AppendLine($"""      <c r="{cellRef}" t="inlineStr"><is><t>{EscapeXml(value)}</t></is></c>""");
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

    private sealed class TestPaymentContextFactory : IDbContextFactory<PaymentContext>
    {
        private readonly DbContextOptions<PaymentContext> _options;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public TestPaymentContextFactory(DbContextOptions<PaymentContext> options, IHttpContextAccessor httpContextAccessor)
        {
            _options = options;
            _httpContextAccessor = httpContextAccessor;
        }

        public PaymentContext CreateDbContext()
            => new(_options, _httpContextAccessor);

        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

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

public class SpreadsheetImportServiceTests
{
    [Fact]
    public async Task PreviewAsync_DetectsRussianSpreadsheetSections()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var preview = await service.PreviewAsync(new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Дата оплаты", "Акт", "Номенклатурная часть" },
                new[] { "01.06.2026", "INV-001", "1500", "ООО Ромашка", "Ожидает", "", "ACT-001", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
            CreateActFile(
                new[] { "Дата акта", "№ акта", "Сумма", "Контрагент", "ИНН контрагента", "Статус", "Счёт №", "Номенклатурная часть", "Дата платежа" },
                new[] { "05.06.2026", "ACT-001", "1500", "ООО Ромашка", "1234567890", "Подписан", "INV-001", "Юридические услуги 1 шт x 1500 р. = 1500 р.", "" }),
            CreateCounterpartiesFile(
                new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
                new[] { "ООО Ромашка", "1234567890", "Иван", "+7 (999) 000-00-00", "test@example.com", "Основной", "5", "Юр. услуги", "" }),
        });

        Assert.Equal(3, preview.Sections.Count);

        var invoiceSection = Assert.Single(preview.Sections, section => section.Key == "invoices");
        var actSection = Assert.Single(preview.Sections, section => section.Key == "acts");
        var counterpartySection = Assert.Single(preview.Sections, section => section.Key == "counterparties");

        Assert.Equal(1, invoiceSection.SelectableRows);
        Assert.Equal(1, actSection.SelectableRows);
        Assert.Equal(1, counterpartySection.SelectableRows);

        var counterpartyRow = Assert.Single(counterpartySection.Rows);
        Assert.True(counterpartyRow.IsSelectable);
        Assert.True(counterpartyRow.IsDerived);
        Assert.NotEmpty(counterpartyRow.DependencyPreviewIds);
        Assert.NotEmpty(counterpartyRow.DependencyTitles);
        Assert.Contains(counterpartyRow.Issues, issue => issue.Contains("Нужен для документов", StringComparison.Ordinal));
        Assert.Contains(preview.Messages, message => message.Contains("Связанные контрагенты", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_WhenInvoiceSelectedWithoutRequiredCounterparty_ReturnsErrorAndDoesNotCreateHiddenCounterparty()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Номенклатурная часть" },
                new[] { "01.06.2026", "INV-001", "1500", "ООО Ромашка", "Ожидает", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
        };

        var preview = await service.PreviewAsync(files);
        var invoiceRow = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "invoices").Rows);
        var counterpartyRow = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows);

        Assert.True(counterpartyRow.IsSelectable);
        Assert.True(counterpartyRow.IsDerived);
        Assert.Contains(invoiceRow.PreviewId, counterpartyRow.DependencyPreviewIds);

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = new List<string> { invoiceRow.PreviewId },
        });

        Assert.Equal(SpreadsheetImportBatchStatus.AppliedWithErrors, batch.Status);
        Assert.Equal(1, batch.TotalRows);
        Assert.Equal(1, batch.ErrorRows);
        Assert.DoesNotContain(batch.Operations, operation => operation.SectionKey == "counterparties");

        var invoiceOperation = Assert.Single(batch.Operations, operation => operation.SectionKey == "invoices");
        Assert.Equal(SpreadsheetImportOperationAction.Error, invoiceOperation.Action);
        Assert.Contains("выбран в разделе импорта контрагентов", invoiceOperation.Message ?? string.Empty, StringComparison.Ordinal);

        await using var db = CreateContext(connection);
        Assert.Equal(0, await db.Clients.CountAsync());
        Assert.Equal(0, await db.Payments.CountAsync());
        Assert.Equal(0, await db.Acts.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_CreatesDerivedCounterpartyInvoiceAndAct_AndRollbackRemovesThem()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Номенклатурная часть" },
                new[] { "01.06.2026", "INV-001", "1500", "ООО Ромашка", "Ожидает", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
            CreateActFile(
                new[] { "Дата акта", "№ акта", "Сумма", "Контрагент", "ИНН контрагента", "Статус", "Счёт №", "Номенклатурная часть" },
                new[] { "05.06.2026", "ACT-001", "1500", "ООО Ромашка", "1234567890", "Подписан", "INV-001", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
        };

        var preview = await service.PreviewAsync(files);
        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = preview.Sections
                .SelectMany(section => section.Rows)
                .Where(row => row.IsSelectable)
                .Select(row => row.PreviewId)
                .ToList(),
        });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);
        Assert.Equal(3, batch.CreatedRows);
        Assert.Equal(3, batch.TotalRows);

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(1, await db.Clients.CountAsync());
            Assert.Equal(1, await db.Payments.CountAsync());
            Assert.Equal(1, await db.Acts.CountAsync());
        }

        var rollback = await service.RollbackAsync(batch.Id);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(3, rollback.RollbackRows);

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(0, await db.Clients.CountAsync());
            Assert.Equal(0, await db.Payments.CountAsync());
            Assert.Equal(0, await db.Acts.CountAsync());
        }
    }

    [Fact]
    public async Task RollbackAsync_WhenImportedInvoiceChanged_DoesNotCrashAndMarksConflicts()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var files = new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Номенклатурная часть" },
                new[] { "01.06.2026", "INV-001", "1500", "ООО Ромашка", "Ожидает", "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
        };

        var preview = await service.PreviewAsync(files);
        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = preview.Sections
                .SelectMany(section => section.Rows)
                .Where(row => row.IsSelectable)
                .Select(row => row.PreviewId)
                .ToList(),
        });

        await using (var db = CreateContext(connection))
        {
            var payment = await db.Payments.SingleAsync();
            payment.Description = "Изменено вручную после импорта";
            await db.SaveChangesAsync();
        }

        var rollback = await service.RollbackAsync(batch.Id);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);

        var invoiceOperation = Assert.Single(rollback.Operations, operation => operation.SectionKey == "invoices");
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, invoiceOperation.RollbackAction);

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(1, await db.Payments.CountAsync());
            Assert.Equal(1, await db.Clients.CountAsync());
        }
    }

    private static SpreadsheetImportUploadFile CreateInvoiceFile(params string[][] rows)
        => CreateUploadFile("Счета.xlsx", CreateWorkbook(("Счета", rows)));

    private static SpreadsheetImportUploadFile CreateActFile(params string[][] rows)
        => CreateUploadFile("Акты.xlsx", CreateWorkbook(("Акты", rows)));

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

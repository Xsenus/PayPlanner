using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SpreadsheetImportServiceOneCXmlTests
{
    [Fact]
    public async Task ApplyAsync_ParsesOneCZipXml_IncludingExpenseInvoicesActsAndLegalRequisites()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var files = new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", BuildOneCXml())),
        };

        var preview = await service.PreviewAsync(files);

        Assert.Contains(preview.Files, file => file.DetectedKind == "onec-xml");
        Assert.Contains(preview.Messages, message =>
            message.Contains("Банки: 1", StringComparison.Ordinal)
            && message.Contains("БанковскиеСчета: 1", StringComparison.Ordinal)
            && message.Contains("ДоговорыКонтрагентов: 1", StringComparison.Ordinal));
        var invoiceSection = Assert.Single(preview.Sections, section => section.Key == "invoices");
        Assert.True(
            invoiceSection.TotalRows == 2,
            string.Join(" | ", preview.Messages.Concat(invoiceSection.Rows.Select(row => $"{row.Title}:{row.Message}"))));
        Assert.Equal(1, Assert.Single(preview.Sections, section => section.Key == "acts").TotalRows);
        Assert.True(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows
            .All(row => row.CounterpartyImportMode == "LegalEntityWithClient"));

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = preview.Sections
                .SelectMany(section => section.Rows)
                .Where(row => row.IsSelectable)
                .Select(row => row.PreviewId)
                .ToList(),
        });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);

        await using var db = CreateContext(connection);
        var payments = await db.Payments
            .Include(payment => payment.InvoiceLineItems)
            .ToListAsync();
        var income = Assert.Single(payments, payment => payment.Account == "INV-45");
        var expense = Assert.Single(payments, payment => payment.Account == "SUP-43");

        Assert.Equal(PaymentType.Income, income.Type);
        Assert.Equal(PaymentStatus.Processing, income.Status);
        Assert.Equal(0m, income.PaidAmount);
        Assert.Single(income.InvoiceLineItems);
        Assert.Contains("UID 1С: INV-UID-45", income.Notes);

        Assert.Equal(PaymentType.Expense, expense.Type);
        Assert.Null(expense.ExpenseClientId);
        Assert.Single(expense.InvoiceLineItems);

        var act = await db.Acts.Include(item => item.LineItems).SingleAsync();
        Assert.Equal("ACT-34", act.Number);
        Assert.Equal(income.Id, act.InvoiceId);
        Assert.Equal("INV-45", act.InvoiceNumber);
        Assert.Single(act.LineItems);

        var legalEntity = await db.LegalEntities.SingleAsync(entity => entity.Inn == "1234567890");
        Assert.Equal("770101001", legalEntity.Kpp);
        Assert.Equal("1027700000000", legalEntity.Ogrn);
        Assert.Equal("ООО", legalEntity.OrganizationForm);
        Assert.Contains("40702810000000000001", legalEntity.BankAccountsJson ?? string.Empty);
    }

    [Fact]
    public async Task ApplyAsync_ReimportsOneCXmlByExternalIds_WhenDocumentNumbersChange()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", BuildOneCXml())),
        });

        int originalPaymentId;
        int originalActId;
        await using (var db = CreateContext(connection))
        {
            originalPaymentId = await db.Payments
                .Where(payment => payment.Account == "INV-45")
                .Select(payment => payment.Id)
                .SingleAsync();
            originalActId = await db.Acts
                .Where(act => act.Number == "ACT-34")
                .Select(act => act.Id)
                .SingleAsync();
        }

        var updatedXml = BuildOneCXml()
            .Replace("INV-45", "INV-45-UPDATED", StringComparison.Ordinal)
            .Replace("ACT-34", "ACT-34-UPDATED", StringComparison.Ordinal)
            .Replace("2022-01-17T00:00:00", "2022-01-18T00:00:00", StringComparison.Ordinal);

        var preview = await service.PreviewAsync(new[]
        {
            new SpreadsheetImportUploadFile("1c_export_updated.zip", CreateZip("1c_export.xml", updatedXml)),
        });

        Assert.Contains(
            Assert.Single(preview.Sections, section => section.Key == "invoices").Rows,
            row => row.Action == SpreadsheetImportOperationAction.Updated && row.Title.Contains("INV-45-UPDATED", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Single(preview.Sections, section => section.Key == "acts").Rows,
            row => row.Action == SpreadsheetImportOperationAction.Updated && row.Title.Contains("ACT-34-UPDATED", StringComparison.Ordinal));

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export_updated.zip", CreateZip("1c_export.xml", updatedXml)),
        });

        await using (var db = CreateContext(connection))
        {
            var payments = await db.Payments.OrderBy(payment => payment.Id).ToListAsync();
            var acts = await db.Acts.OrderBy(act => act.Id).ToListAsync();

            Assert.Equal(2, payments.Count);
            Assert.Single(acts);

            var updatedPayment = Assert.Single(payments, payment => payment.Account == "INV-45-UPDATED");
            Assert.Equal(originalPaymentId, updatedPayment.Id);

            var updatedAct = Assert.Single(acts);
            Assert.Equal(originalActId, updatedAct.Id);
            Assert.Equal("ACT-34-UPDATED", updatedAct.Number);
            Assert.Equal(originalPaymentId, updatedAct.InvoiceId);
        }
    }

    [Fact]
    public async Task RollbackAsync_RemovesCreatedOneCXmlEntitiesAndLineItems()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var files = new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", BuildOneCXml())),
        };

        var batch = await ApplyAllSelectableRowsAsync(service, files);

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(2, await db.Clients.CountAsync());
            Assert.Equal(2, await db.LegalEntities.CountAsync());
            Assert.Equal(2, await db.Payments.CountAsync());
            Assert.Equal(2, await db.Invoices.CountAsync());
            Assert.Equal(2, await db.InvoiceLineItems.CountAsync());
            Assert.Equal(1, await db.Acts.CountAsync());
            Assert.Equal(1, await db.Set<ActLineItem>().CountAsync());
        }

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(batch.CreatedRows, rollback.RollbackRows);
        Assert.All(
            rollback.Operations.Where(operation => operation.Action == SpreadsheetImportOperationAction.Created),
            operation => Assert.Equal(SpreadsheetImportRollbackAction.Deleted, operation.RollbackAction));

        await using (var db = CreateContext(connection))
        {
            Assert.Equal(0, await db.Clients.CountAsync());
            Assert.Equal(0, await db.LegalEntities.CountAsync());
            Assert.Equal(0, await db.Payments.CountAsync());
            Assert.Equal(0, await db.Invoices.CountAsync());
            Assert.Equal(0, await db.InvoiceLineItems.CountAsync());
            Assert.Equal(0, await db.Acts.CountAsync());
            Assert.Equal(0, await db.Set<ActLineItem>().CountAsync());
        }
    }

    [Fact]
    public async Task PreviewAsync_AllowsDistinctOneCInvoices_WithRepeatedExternalId()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = BuildOneCXml().Replace(
            "</ФайлОбмена>",
            """
              <Объект Нпп="12" Тип="ДокументСсылка.СчетНаОплатуПокупателю" ИмяПравила="СчетНаОплатуПокупателю">
                <Ссылка Нпп="12"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>INV-UID-45</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>2022-01-18T00:00:00</Значение></Свойство></Ссылка>
                <Свойство Имя="ДатаОплатитьДо" Тип="Дата"><Значение>2022-02-18T00:00:00</Значение></Свойство>
                <Свойство Имя="Статус" Тип="Строка"><Значение>Ожидает оплаты</Значение></Свойство>
                <Свойство Имя="Номер" Тип="Строка"><Значение>INV-46</Значение></Свойство>
                <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
                <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>12000.00</Значение></Свойство>
                <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>12000.00</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>12000.00</Значение></Свойство></Запись></ТабличнаяЧасть>
              </Объект>
            </ФайлОбмена>
            """,
            StringComparison.Ordinal);

        var preview = await service.PreviewAsync(new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", xml)),
        });

        var invoiceSection = Assert.Single(preview.Sections, section => section.Key == "invoices");
        Assert.Equal(3, invoiceSection.TotalRows);
        Assert.Equal(3, invoiceSection.SelectableRows);
        Assert.DoesNotContain(invoiceSection.Rows, row => row.Action == SpreadsheetImportOperationAction.Skipped);
    }

    [Fact]
    public async Task ApplyAsync_DoesNotUseRepeatedOneCExternalIdAsFallback_WhenAmbiguous()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var initialXml = AppendOneCObjects(
            BuildOneCXml(),
            BuildRepeatedOneCInvoiceObject("INV-46", "2022-01-18T00:00:00", "12000.00"));

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", initialXml)),
        });

        var newInvoiceXml = BuildOneCSingleInvoiceXml("INV-47", "2022-01-19T00:00:00", "13000.00");
        var preview = await service.PreviewAsync(new[]
        {
            new SpreadsheetImportUploadFile("1c_export_new.zip", CreateZip("1c_export.xml", newInvoiceXml)),
        });

        var invoiceRow = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "invoices").Rows);
        Assert.Equal(SpreadsheetImportOperationAction.Created, invoiceRow.Action);

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export_new.zip", CreateZip("1c_export.xml", newInvoiceXml)),
        });

        await using var db = CreateContext(connection);
        var incomeAccounts = await db.Payments
            .Where(payment => payment.Type == PaymentType.Income)
            .OrderBy(payment => payment.Account)
            .Select(payment => payment.Account)
            .ToListAsync();

        Assert.Equal(new[] { "INV-45", "INV-46", "INV-47" }, incomeAccounts);
    }

    [Fact]
    public async Task ApplyAsync_IgnoresNonSelectableRows_WhenRequestContainsSkippedPreviewIds()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = AppendOneCObjects(
            BuildOneCSingleInvoiceXml("INV-DUP", "2022-01-20T00:00:00", "100.00"),
            BuildRepeatedOneCInvoiceObject("INV-DUP", "2022-01-20T00:00:00", "999.00"));
        var files = new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", xml)),
        };

        var preview = await service.PreviewAsync(files);
        var invoiceRows = Assert.Single(preview.Sections, section => section.Key == "invoices").Rows;
        Assert.Equal(2, invoiceRows.Count);
        Assert.Equal(1, invoiceRows.Count(row => row.IsSelectable));
        Assert.Equal(1, invoiceRows.Count(row => row.Action == SpreadsheetImportOperationAction.Skipped));

        var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = preview.Sections
                .SelectMany(section => section.Rows)
                .Select(row => row.PreviewId)
                .ToList(),
        });

        Assert.DoesNotContain(batch.Operations, operation =>
            operation.SectionKey == "invoices" && operation.SourceRowNumber == 12);

        await using var db = CreateContext(connection);
        var payment = await db.Payments.SingleAsync(payment => payment.Account == "INV-DUP");
        Assert.Equal(100m, payment.Amount);
    }

    [Fact]
    public async Task ApplyAsync_UsesOneCRowAmountForLinePrice_WhenKubExportsTotalAsPrice()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = BuildOneCSingleInvoiceXml("INV-QTY", "2022-01-21T00:00:00", "165000.00")
            .Replace(
                "<Свойство Имя=\"Количество\" Тип=\"Число\"><Значение>1</Значение></Свойство>",
                "<Свойство Имя=\"Количество\" Тип=\"Число\"><Значение>2</Значение></Свойство>",
                StringComparison.Ordinal);

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export_qty.zip", CreateZip("1c_export.xml", xml)),
        });

        await using var db = CreateContext(connection);
        var payment = await db.Payments
            .Include(item => item.InvoiceLineItems)
            .SingleAsync(payment => payment.Account == "INV-QTY");

        Assert.Equal(165_000m, payment.Amount);
        var lineItem = Assert.Single(payment.InvoiceLineItems);
        Assert.Equal(2m, lineItem.Quantity);
        Assert.Equal(82_500m, lineItem.Price);
        Assert.Equal(165_000m, lineItem.Quantity * lineItem.Price);
    }

    [Fact]
    public async Task ApplyAsync_LinksActByInvoiceNumber_WhenInvoiceExternalIdIsRepeated()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = AppendOneCObjects(
            BuildOneCSingleInvoiceXml("INV-45", "2022-01-17T00:00:00", "10000.00"),
            BuildRepeatedOneCInvoiceObject("INV-46", "2022-01-18T00:00:00", "12000.00")
            + BuildOneCActObject("ACT-46", "2022-01-19T00:00:00", "12000.00", "INV-46", "2022-01-18T00:00:00", "INV-UID-45"));

        await ApplyAllSelectableRowsAsync(service, new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", xml)),
        });

        await using var db = CreateContext(connection);
        var linkedInvoice = await db.Payments.SingleAsync(payment => payment.Account == "INV-46");
        var act = await db.Acts.SingleAsync();

        Assert.Equal(linkedInvoice.Id, act.InvoiceId);
        Assert.Equal("INV-46", act.InvoiceNumber);
    }

    [Fact]
    public async Task ApplyAsync_ReimportsSameOneCXmlWithoutCreatingDuplicates()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = AppendOneCObjects(
            BuildOneCSingleInvoiceXml("INV-45", "2022-01-17T00:00:00", "10000.00"),
            BuildRepeatedOneCInvoiceObject("INV-46", "2022-01-18T00:00:00", "12000.00")
            + BuildOneCActObject("ACT-46", "2022-01-19T00:00:00", "12000.00", "INV-46", "2022-01-18T00:00:00", "INV-UID-45"));
        var files = new[]
        {
            new SpreadsheetImportUploadFile("1c_export.zip", CreateZip("1c_export.xml", xml)),
        };

        await ApplyAllSelectableRowsAsync(service, files);
        var secondBatch = await ApplyAllSelectableRowsAsync(service, files);

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, secondBatch.Status);
        await using var db = CreateContext(connection);
        var changedOperationDetails = await db.SpreadsheetImportBatchOperations
            .AsNoTracking()
            .Where(operation => operation.BatchId == secondBatch.Id
                && (operation.Action == SpreadsheetImportOperationAction.Created
                    || operation.Action == SpreadsheetImportOperationAction.Updated))
            .OrderBy(operation => operation.Id)
            .ToListAsync();
        var changedOperations = secondBatch.Operations
            .Where(operation => operation.Action is SpreadsheetImportOperationAction.Created or SpreadsheetImportOperationAction.Updated)
            .ToList();
        Assert.True(
            changedOperations.Count == 0,
            string.Join(
                "\n",
                changedOperationDetails.Select(operation =>
                    $"{operation.SectionKey}:{operation.SourceTitle} {operation.Action}\nBefore: {operation.BeforeJson}\nAfter: {operation.AfterJson}")));
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Equal(1, await db.Acts.CountAsync());
        Assert.Equal(1, await db.Clients.CountAsync());
    }

    private static byte[] CreateZip(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        return stream.ToArray();
    }

    private static string AppendOneCObjects(string xml, string objects)
        => xml.Replace("</ФайлОбмена>", $"{objects}\n           </ФайлОбмена>", StringComparison.Ordinal);

    private static string BuildRepeatedOneCInvoiceObject(string number, string date, string amount)
        => """
             <Объект Нпп="12" Тип="ДокументСсылка.СчетНаОплатуПокупателю" ИмяПравила="СчетНаОплатуПокупателю">
               <Ссылка Нпп="12"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>INV-UID-45</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>__DATE__</Значение></Свойство></Ссылка>
               <Свойство Имя="ДатаОплатитьДо" Тип="Дата"><Значение>2022-02-18T00:00:00</Значение></Свойство>
               <Свойство Имя="Статус" Тип="Строка"><Значение>Ожидает оплаты</Значение></Свойство>
               <Свойство Имя="Номер" Тип="Строка"><Значение>__NUMBER__</Значение></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство></Запись></ТабличнаяЧасть>
             </Объект>
           """
            .Replace("__NUMBER__", number, StringComparison.Ordinal)
            .Replace("__DATE__", date, StringComparison.Ordinal)
            .Replace("__AMOUNT__", amount, StringComparison.Ordinal);

    private static string BuildOneCActObject(
        string number,
        string date,
        string amount,
        string invoiceNumber,
        string invoiceDate,
        string invoiceExternalId)
        => """
             <Объект Нпп="20" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
               <Ссылка Нпп="20"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ACT-UID-46</Значение></Свойство></Ссылка>
               <Свойство Имя="Номер" Тип="Строка"><Значение>__NUMBER__</Значение></Свойство>
               <Свойство Имя="Дата" Тип="Дата"><Значение>__DATE__</Значение></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство></Запись></ТабличнаяЧасть>
               <Свойство Имя="УидСчета" Тип="Строка"><Значение>__INVOICE_UID__</Значение></Свойство>
               <Свойство Имя="Счет" Тип="ДокументСсылка.СчетНаОплатуПокупателю"><Ссылка><Свойство Имя="Номер" Тип="Строка"><Значение>__INVOICE_NUMBER__</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>__INVOICE_DATE__</Значение></Свойство></Ссылка></Свойство>
             </Объект>
           """
            .Replace("__NUMBER__", number, StringComparison.Ordinal)
            .Replace("__DATE__", date, StringComparison.Ordinal)
            .Replace("__AMOUNT__", amount, StringComparison.Ordinal)
            .Replace("__INVOICE_NUMBER__", invoiceNumber, StringComparison.Ordinal)
            .Replace("__INVOICE_DATE__", invoiceDate, StringComparison.Ordinal)
            .Replace("__INVOICE_UID__", invoiceExternalId, StringComparison.Ordinal);

    private static string BuildOneCSingleInvoiceXml(string number, string date, string amount)
        => """
           <?xml version="1.0" encoding="UTF-8"?>
           <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0" ДатаВыгрузки="2026-04-23T13:08:57" НачалоПериодаВыгрузки="2022-01-01T00:00:00" ОкончаниеПериодаВыгрузки="2022-12-31T00:00:00">
             <Объект Нпп="1" Тип="СправочникСсылка.Контрагенты" ИмяПравила="Контрагенты">
               <Ссылка Нпп="1">
                 <Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство>
                 <Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство>
                 <Свойство Имя="КПП" Тип="Строка"><Значение>770101001</Значение></Свойство>
               </Ссылка>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство>
               <Свойство Имя="ОГРН" Тип="Строка"><Значение>1027700000000</Значение></Свойство>
               <Свойство Имя="ЮридическийАдрес" Тип="Строка"><Значение>г. Москва, ул. Тестовая, д. 1</Значение></Свойство>
               <Свойство Имя="ФормаСобственностиСокращенно" Тип="Строка"><Значение>ООО</Значение></Свойство>
             </Объект>
             <Объект Нпп="2" Тип="ДокументСсылка.СчетНаОплатуПокупателю" ИмяПравила="СчетНаОплатуПокупателю">
               <Ссылка Нпп="2"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>INV-UID-45</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>__DATE__</Значение></Свойство></Ссылка>
               <Свойство Имя="ДатаОплатитьДо" Тип="Дата"><Значение>2022-02-19T00:00:00</Значение></Свойство>
               <Свойство Имя="Статус" Тип="Строка"><Значение>Ожидает оплаты</Значение></Свойство>
               <Свойство Имя="Номер" Тип="Строка"><Значение>__NUMBER__</Значение></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>__AMOUNT__</Значение></Свойство></Запись></ТабличнаяЧасть>
             </Объект>
           </ФайлОбмена>
           """
            .Replace("__NUMBER__", number, StringComparison.Ordinal)
            .Replace("__DATE__", date, StringComparison.Ordinal)
            .Replace("__AMOUNT__", amount, StringComparison.Ordinal);

    private static string BuildOneCXml()
        => """
           <?xml version="1.0" encoding="UTF-8"?>
           <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0" ДатаВыгрузки="2026-04-23T13:08:57" НачалоПериодаВыгрузки="2022-01-01T00:00:00" ОкончаниеПериодаВыгрузки="2022-12-31T00:00:00">
             <Объект Нпп="1" Тип="СправочникСсылка.Контрагенты" ИмяПравила="Контрагенты">
               <Ссылка Нпп="1">
                 <Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство>
                 <Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство>
                 <Свойство Имя="КПП" Тип="Строка"><Значение>770101001</Значение></Свойство>
               </Ссылка>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство>
               <Свойство Имя="ОГРН" Тип="Строка"><Значение>1027700000000</Значение></Свойство>
               <Свойство Имя="ЮридическийАдрес" Тип="Строка"><Значение>г. Москва, ул. Тестовая, д. 1</Значение></Свойство>
               <Свойство Имя="ФормаСобственностиСокращенно" Тип="Строка"><Значение>ООО</Значение></Свойство>
               <Свойство Имя="ФормаСобственностиПолностью" Тип="Строка"><Значение>Общество с ограниченной ответственностью</Значение></Свойство>
             </Объект>
             <Объект Нпп="2" Тип="СправочникСсылка.Контрагенты" ИмяПравила="Контрагенты">
               <Ссылка Нпп="2">
                 <Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-SUPPLIER</Значение></Свойство>
                 <Свойство Имя="ИНН" Тип="Строка"><Значение>2222222222</Значение></Свойство>
                 <Свойство Имя="КПП" Тип="Строка"><Значение>770201001</Значение></Свойство>
               </Ссылка>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Чистая вода</Значение></Свойство>
               <Свойство Имя="ФормаСобственностиСокращенно" Тип="Строка"><Значение>ООО</Значение></Свойство>
             </Объект>
             <Объект Нпп="3" Тип="СправочникСсылка.Банки" ИмяПравила="Банки">
               <Ссылка Нпп="3"><Свойство Имя="Код" Тип="Строка"><Значение>044525411</Значение></Свойство></Ссылка>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>Тестовый банк</Значение></Свойство>
               <Свойство Имя="КоррСчет" Тип="Строка"><Значение>30101810000000000411</Значение></Свойство>
             </Объект>
             <Объект Нпп="4" Тип="СправочникСсылка.БанковскиеСчета" ИмяПравила="БанковскиеСчета">
               <Ссылка Нпп="4">
                 <Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ACC-BUYER</Значение></Свойство>
                 <Свойство Имя="НомерСчета" Тип="Строка"><Значение>40702810000000000001</Значение></Свойство>
                 <Свойство Имя="Владелец" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               </Ссылка>
               <Свойство Имя="Банк" Тип="СправочникСсылка.Банки"><Ссылка><Свойство Имя="Код" Тип="Строка"><Значение>044525411</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>Основной счет</Значение></Свойство>
             </Объект>
             <Объект Нпп="5" Тип="СправочникСсылка.ДоговорыКонтрагентов" ИмяПравила="ДоговорыКонтрагентов">
               <Ссылка Нпп="5"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CONTRACT-1</Значение></Свойство></Ссылка>
               <Свойство Имя="Наименование" Тип="Строка"><Значение>Договор 1 от 17.01.2022</Значение></Свойство>
             </Объект>
             <Объект Нпп="10" Тип="ДокументСсылка.СчетНаОплатуПокупателю" ИмяПравила="СчетНаОплатуПокупателю">
               <Ссылка Нпп="10"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>INV-UID-45</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>2022-01-17T00:00:00</Значение></Свойство></Ссылка>
               <Свойство Имя="ДатаОплатитьДо" Тип="Дата"><Значение>2022-02-17T00:00:00</Значение></Свойство>
               <Свойство Имя="Статус" Тип="Строка"><Значение>Оплачен частично</Значение></Свойство>
               <Свойство Имя="Номер" Тип="Строка"><Значение>INV-45</Значение></Свойство>
               <Свойство Имя="Комментарий" Тип="Строка"><Значение>Комментарий счета</Значение></Свойство>
               <Свойство Имя="ДоговорКонтрагента" Тип="СправочникСсылка.ДоговорыКонтрагентов"><Ссылка><Свойство Имя="Наименование" Тип="Строка"><Значение>Договор 1 от 17.01.2022</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>10000.00</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>10000.00</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>10000.00</Значение></Свойство></Запись></ТабличнаяЧасть>
             </Объект>
             <Объект Нпп="11" Тип="ДокументСсылка.СчетНаОплатуПоставщика" ИмяПравила="СчетНаОплатуПоставщика">
               <Ссылка Нпп="11"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>SUP-UID-43</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>2022-02-03T00:00:00</Значение></Свойство></Ссылка>
               <Свойство Имя="Номер" Тип="Строка"><Значение>SUP-43</Значение></Свойство>
               <Свойство Имя="ДатаВходящегоДокумента" Тип="Дата"><Значение>2022-02-03T00:00:00</Значение></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-SUPPLIER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Чистая вода</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>2222222222</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>1500.00</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Вода</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>1500.00</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>1500.00</Значение></Свойство></Запись></ТабличнаяЧасть>
             </Объект>
             <Объект Нпп="20" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
               <Ссылка Нпп="20"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ACT-UID-34</Значение></Свойство></Ссылка>
               <Свойство Имя="Номер" Тип="Строка"><Значение>ACT-34</Значение></Свойство>
               <Свойство Имя="Дата" Тип="Дата"><Значение>2022-02-17T00:00:00</Значение></Свойство>
               <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BUYER</Значение></Свойство><Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Ромашка</Значение></Свойство><Свойство Имя="ИНН" Тип="Строка"><Значение>1234567890</Значение></Свойство></Ссылка></Свойство>
               <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>10000.00</Значение></Свойство>
               <ТабличнаяЧасть Имя="Услуги"><Запись><Свойство Имя="Количество" Тип="Число"><Значение>1</Значение></Свойство><Свойство Имя="Содержание" Тип="Строка"><Значение>Юридические услуги</Значение></Свойство><Свойство Имя="Сумма" Тип="Число"><Значение>10000.00</Значение></Свойство><Свойство Имя="Цена" Тип="Число"><Значение>10000.00</Значение></Свойство></Запись></ТабличнаяЧасть>
               <Свойство Имя="УидСчета" Тип="Строка"><Значение>INV-UID-45</Значение></Свойство>
               <Свойство Имя="Счет" Тип="ДокументСсылка.СчетНаОплатуПокупателю"><Ссылка><Свойство Имя="Номер" Тип="Строка"><Значение>INV-45</Значение></Свойство><Свойство Имя="Дата" Тип="Дата"><Значение>2022-01-17T00:00:00</Значение></Свойство></Ссылка></Свойство>
             </Объект>
           </ФайлОбмена>
           """;

    private static async Task<SpreadsheetImportBatchDetailDto> ApplyAllSelectableRowsAsync(
        SpreadsheetImportService service,
        IReadOnlyCollection<SpreadsheetImportUploadFile> files)
    {
        var preview = await service.PreviewAsync(files);
        return await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            AllowUpdates = true,
            AllowClearingExistingValues = true,
            SelectedPreviewIds = preview.Sections
                .SelectMany(section => section.Rows)
                .Where(row => row.IsSelectable)
                .Select(row => row.PreviewId)
                .ToList(),
        });
    }

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

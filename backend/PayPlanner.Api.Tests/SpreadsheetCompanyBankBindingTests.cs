using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Cmd = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BindingGuard_OneCMayAppendAnUnrelatedAccountWithoutReplacingConfirmation(bool postgres)
        => WithBindingDatabaseAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db); var company = await db.LegalEntities.SingleAsync();
            db.Clients.Add(new Client { Name = company.ShortName, Company = company.ShortName, LegalEntityId = f.Company }); await db.SaveChangesAsync();
            var confirmed = await new CompanyBankAccountBindingService(db).BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Cmd.Principal(f.Actor) } };
            var service = new SpreadsheetImportService(new CompanyBankAccountBindingWriterTests.WriterFactory(db, accessor), accessor, NullLogger<SpreadsheetImportService>.Instance);
            var name = SecurityElement.Escape(company.ShortName);
            var xml = $$"""
                <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0">
                  <Объект Нпп="1" Тип="СправочникСсылка.Контрагенты" ИмяПравила="Контрагенты">
                    <Ссылка Нпп="1"><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BINDING</Значение></Свойство></Ссылка>
                    <Свойство Имя="Наименование" Тип="Строка"><Значение>{{name}}</Значение></Свойство>
                  </Объект>
                  <Объект Нпп="2" Тип="СправочникСсылка.БанковскиеСчета" ИмяПравила="БанковскиеСчета">
                    <Ссылка Нпп="2">
                      <Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ACC-BINDING</Значение></Свойство>
                      <Свойство Имя="НомерСчета" Тип="Строка"><Значение>40702810200000000002</Значение></Свойство>
                      <Свойство Имя="Владелец" Тип="СправочникСсылка.Контрагенты"><Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>CP-BINDING</Значение></Свойство></Ссылка></Свойство>
                    </Ссылка>
                    <Свойство Имя="Банк" Тип="СправочникСсылка.Банки"><Ссылка><Свойство Имя="Код" Тип="Строка"><Значение>044525225</Значение></Свойство></Ссылка></Свойство>
                  </Объект>
                </ФайлОбмена>
                """;
            var files = new[] { new SpreadsheetImportUploadFile("binding.xml", Encoding.UTF8.GetBytes(xml)) };
            var preview = await service.PreviewAsync(files);
            var row = Assert.Single(Assert.Single(preview.Sections, x => x.Key == "counterparties").Rows);
            var result = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest { SelectedPreviewIds = [row.PreviewId], CounterpartyOptions = [new() { PreviewId = row.PreviewId, ImportMode = "LegalEntityWithClient" }], AllowUpdates = true });
            Assert.Equal(SpreadsheetImportBatchStatus.Applied, result.Status);
            db.ChangeTracker.Clear();
            var json = (await db.LegalEntities.AsNoTracking().SingleAsync()).BankAccountsJson!;
            Assert.Contains("40702810200000000002", json); Assert.Contains("local-id", json); Assert.Contains(Cmd.Number, json);
            var binding = await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync();
            Assert.Equal(confirmed.BindingId, binding.Id); Assert.Equal(f.Bank, binding.BankConnectionAccountId); Assert.Null(binding.RevokedAtUtc);
            Assert.Equal(123.45m, (await db.MoneyOperations.AsNoTracking().SingleAsync()).Amount);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task BindingGuard_RollbackChecksHistoricalProfileAndRemainsAtomic(bool postgres, bool allowed)
        => WithBindingDatabaseAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db); var company = await db.LegalEntities.SingleAsync();
            var client = new Client { Name = "Current client", Company = company.ShortName, LegalEntityId = company.Id };
            db.Clients.Add(client); await db.SaveChangesAsync();
            var after = CaptureSnapshotJson("CaptureCounterpartySnapshot", client, company);
            var before = JsonNode.Parse(after)!;
            before["client"]!["name"] = "Before import";
            before["legalEntity"]!["phone"] = "Before phone";
            before["legalEntity"]!["bankAccountsJson"] = allowed ? company.BankAccountsJson!.Replace("Profile account", "Before label") : Cmd.Profile("historical-other-id");
            var batch = CreateBatch(); batch.TotalRows = 1; batch.UpdatedRows = 1;
            batch.Operations.Add(CreateOperation("counterparties", SpreadsheetImportOperationAction.Updated, "Client", client.Id,
                before.ToJsonString(), after, JsonSerializer.Serialize(new { legalEntityId = f.Company, legalEntityCreated = false, importMode = "LegalEntityWithClient" })));
            db.SpreadsheetImportBatches.Add(batch); await db.SaveChangesAsync();
            var confirmed = await new CompanyBankAccountBindingService(db).BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Cmd.Principal(f.Actor) } };
            var service = new SpreadsheetImportService(new CompanyBankAccountBindingWriterTests.WriterFactory(db, accessor), accessor, NullLogger<SpreadsheetImportService>.Instance);
            var result = await service.RollbackAsync(batch.Id);
            Assert.Equal(allowed ? SpreadsheetImportBatchStatus.RolledBack : SpreadsheetImportBatchStatus.RolledBackWithConflicts, result.Status);
            Assert.Equal(allowed ? SpreadsheetImportRollbackAction.Restored : SpreadsheetImportRollbackAction.Error, Assert.Single(result.Operations).RollbackAction);
            db.ChangeTracker.Clear();
            Assert.Equal(allowed ? "Before import" : "Current client", (await db.Clients.AsNoTracking().SingleAsync()).Name);
            var savedCompany = await db.LegalEntities.AsNoTracking().SingleAsync();
            Assert.Equal(allowed ? "Before phone" : null, savedCompany.Phone); Assert.Contains("local-id", savedCompany.BankAccountsJson!);
            var binding = await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync();
            Assert.Equal(confirmed.BindingId, binding.Id); Assert.Null(binding.RevokedAtUtc);
            Assert.Equal(123.45m, (await db.MoneyOperations.AsNoTracking().SingleAsync()).Amount);
        });

    private async Task WithBindingDatabaseAsync(bool postgres, Func<PaymentContext, Task> action)
    {
        if (postgres) { await using var database = await bankModelTemplate.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await action(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await action(db); }
    }
}

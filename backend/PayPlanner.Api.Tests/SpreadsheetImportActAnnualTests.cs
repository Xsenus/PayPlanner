using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    private const string AnnualOversizedImportNumber = "9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ReimportByUniqueSourceIdPreservesTheOldYearFloorAndRollbackRetainsBothFloors(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, postgres, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        var client = new Client { Name = "ООО Годовой акт", IsActive = true };
        var historical = new Act
        {
            Number = "42",
            Date = new DateTime(2032, 6, 1),
            Amount = 100m,
            Client = client,
            Comment = "UID 1С: ANNUAL-OLD",
        };
        fixture.Db.Acts.Add(historical);
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Db.ActAnnualNumberCounters.ToArrayAsync());
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0">
              <Объект Нпп="1" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
                <Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ANNUAL-OLD</Значение></Свойство></Ссылка>
                <Свойство Имя="Номер" Тип="Строка"><Значение>7</Значение></Свойство>
                <Свойство Имя="Дата" Тип="Дата"><Значение>2033-06-01T00:00:00</Значение></Свойство>
                <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка>
                  <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Годовой акт</Значение></Свойство>
                </Ссылка></Свойство>
                <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>100</Значение></Свойство>
              </Объект>
            </ФайлОбмена>
            """;
        var files = new[] { new SpreadsheetImportUploadFile("annual-reimport.xml", Encoding.UTF8.GetBytes(xml)) };
        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "acts").Rows);
        Assert.Equal(SpreadsheetImportOperationAction.Updated, row.Action);
        var selection = SelectOwnershipRows(preview);
        selection.AllowUpdates = true;
        selection.AllowClearingExistingValues = true;

        var batch = await service.ApplyAsync(files, selection);

        Assert.Equal(SpreadsheetImportOperationAction.Updated, Assert.Single(batch.Operations, operation => operation.SectionKey == "acts").Action);
        await using var verify = factory.CreateDbContext();
        var moved = await verify.Acts.AsNoTracking().SingleAsync();
        Assert.Equal(historical.Id, moved.Id);
        Assert.Equal("7", moved.Number);
        Assert.Equal(new DateTime(2033, 6, 1), moved.Date);
        Assert.Equal(42, (await verify.ActAnnualNumberCounters.SingleAsync(counter => counter.Year == 2032)).LastNumber);
        Assert.Equal(7, (await verify.ActAnnualNumberCounters.SingleAsync(counter => counter.Year == 2033)).LastNumber);
        var numbering = new ActAnnualNumberingService(verify);
        Assert.Equal("43", await numbering.PreviewNextAsync(new DateTime(2032, 1, 1), default));
        Assert.Equal("8", await numbering.PreviewNextAsync(new DateTime(2033, 1, 1), default));

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportRollbackAction.Restored, Assert.Single(rollback.Operations, operation => operation.SectionKey == "acts").RollbackAction);
        verify.ChangeTracker.Clear();
        var restored = await verify.Acts.AsNoTracking().SingleAsync();
        Assert.Equal(historical.Id, restored.Id);
        Assert.Equal("42", restored.Number);
        Assert.Equal(new DateTime(2032, 6, 1), restored.Date);
        Assert.Equal("43", await numbering.PreviewNextAsync(new DateTime(2032, 1, 1), default));
        Assert.Equal("8", await numbering.PreviewNextAsync(new DateTime(2033, 1, 1), default));
    }

    [Theory]
    [InlineData(false, "42", 42L, "43", "44")]
    [InlineData(true, "42", 42L, "43", "44")]
    [InlineData(false, AnnualOversizedImportNumber, 1L, "2", "3")]
    [InlineData(true, AnnualOversizedImportNumber, 1L, "2", "3")]
    public async Task AnnualAct_ImportKeepsDuplicateNumbersAndRollbackDoesNotLowerTheIssuedFloor(bool postgres, string importedNumber, long floor, string nextNumber, string afterRollback)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, postgres, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        var date = new DateTime(2050, 6, 1);
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            Assert.Equal("1", await new ActAnnualNumberingService(fixture.Db).NextAsync(date, default));
            await transaction.CommitAsync();
        }
        var files = new[]
        {
            CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус"],
                ["01.06.2050", importedNumber, "100", "Первый синтетический владелец", "Подписан"],
                ["01.06.2050", importedNumber, "200", "Второй синтетический владелец", "Подписан"])
        };
        var batch = await service.ApplyAsync(files, SelectOwnershipRows(await service.PreviewAsync(files)));
        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);
        await using var verify = factory.CreateDbContext();
        var imported = await verify.Acts.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, imported.Length);
        Assert.All(imported, act => Assert.Equal(importedNumber, act.Number));
        Assert.NotEqual(imported[0].ClientId, imported[1].ClientId);
        Assert.Equal(floor, (await verify.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        await using (var transaction = await verify.Database.BeginTransactionAsync())
        {
            Assert.Equal(nextNumber, await new ActAnnualNumberingService(verify).NextAsync(date, default));
            await transaction.CommitAsync();
        }

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(2, rollback.Operations.Count(item => item.SectionKey == "acts" && item.RollbackAction == SpreadsheetImportRollbackAction.Deleted));
        verify.ChangeTracker.Clear();
        Assert.Empty(await verify.Acts.ToArrayAsync());
        Assert.Equal(floor + 1, (await verify.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        await using (var transaction = await verify.Database.BeginTransactionAsync())
        {
            Assert.Equal(afterRollback, await new ActAnnualNumberingService(verify).NextAsync(date, default));
            await transaction.CommitAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnnualAct_LegacyImportRollbackPreservesOutgoingYearFloorWithMissingOrLowerCounter(bool postgres, bool lowerCounter)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, postgres, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        var client = new Client { Name = "ООО Прежний импорт", IsActive = true };
        var historical = new Act
        {
            Number = "1",
            Date = new DateTime(2026, 6, 1),
            Amount = 100m,
            Client = client,
            Comment = "UID 1С: ANNUAL-LEGACY",
        };
        fixture.Db.Acts.AddRange(historical, new Act
        {
            Number = "1",
            Date = new DateTime(2027, 6, 1),
            Amount = 200m,
            Client = client,
        });
        await fixture.Db.SaveChangesAsync();
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0">
              <Объект Нпп="1" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
                <Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ANNUAL-LEGACY</Значение></Свойство></Ссылка>
                <Свойство Имя="Номер" Тип="Строка"><Значение>2</Значение></Свойство>
                <Свойство Имя="Дата" Тип="Дата"><Значение>2027-06-01T00:00:00</Значение></Свойство>
                <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка>
                  <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Прежний импорт</Значение></Свойство>
                </Ссылка></Свойство>
                <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>100</Значение></Свойство>
              </Объект>
            </ФайлОбмена>
            """;
        var files = new[] { new SpreadsheetImportUploadFile("annual-legacy.xml", Encoding.UTF8.GetBytes(xml)) };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        selection.AllowUpdates = true;
        selection.AllowClearingExistingValues = true;
        var batch = await service.ApplyAsync(files, selection);
        Assert.Equal(2, batch.Operations.Count);
        Assert.Equal(SpreadsheetImportOperationAction.Updated,
            Assert.Single(batch.Operations, item => item.EntityType == nameof(Client)).Action);
        Assert.Equal(SpreadsheetImportOperationAction.Updated, Assert.Single(batch.Operations, item => item.EntityType == nameof(Act)).Action);
        await using var verify = factory.CreateDbContext();
        Assert.NotNull(verify.Model.FindEntityType(typeof(Act))!.FindProperty("IsDeleted"));
        var moved = await verify.Acts.AsNoTracking().SingleAsync(act => act.Id == historical.Id);
        Assert.Equal("2", moved.Number);
        Assert.Equal(new DateTime(2027, 6, 1), moved.Date);
        // Keep real import snapshots while modelling a batch saved before annual counters existed.
        await verify.ActAnnualNumberCounters.ExecuteDeleteAsync();
        if (lowerCounter)
        {
            verify.ActAnnualNumberCounters.Add(new ActAnnualNumberCounter { Year = 2027, LastNumber = 1 });
            await verify.SaveChangesAsync();
        }

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, Assert.Single(rollback.Operations, item => item.EntityType == nameof(Act)).RollbackAction);
        verify.ChangeTracker.Clear();
        var restored = await verify.Acts.AsNoTracking().SingleAsync(act => act.Id == historical.Id);
        Assert.Equal("1", restored.Number);
        Assert.Equal(new DateTime(2026, 6, 1), restored.Date);
        Assert.Equal(2, await verify.Acts.CountAsync());
        Assert.Equal(1, (await verify.ActAnnualNumberCounters.SingleAsync(counter => counter.Year == 2026)).LastNumber);
        Assert.Equal(2, (await verify.ActAnnualNumberCounters.SingleAsync(counter => counter.Year == 2027)).LastNumber);
        await using var transaction = await verify.Database.BeginTransactionAsync();
        var numbering = new ActAnnualNumberingService(verify);
        Assert.Equal("2", await numbering.NextAsync(new DateTime(2026, 1, 1), default));
        Assert.Equal("3", await numbering.NextAsync(new DateTime(2027, 1, 1), default));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnnualAct_LegacyImportHardDeleteRollbackPreservesOutgoingFloorWithMissingOrLowerCounter(bool postgres, bool lowerCounter)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new PhysicalActRollbackContextFactory(fixture.Db, postgres, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        var client = new Client { Name = "ООО Физическое удаление", IsActive = true };
        fixture.Db.Acts.Add(new Act
        {
            Number = "1",
            Date = new DateTime(2028, 6, 1),
            Amount = 100m,
            Client = client,
        });
        await fixture.Db.SaveChangesAsync();
        var files = new[]
        {
            CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус"],
                ["01.06.2028", "2", "200", client.Name, "Подписан"]),
        };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        selection.AllowUpdates = true;
        var batch = await service.ApplyAsync(files, selection);
        Assert.Equal(2, batch.Operations.Count);
        Assert.Equal(SpreadsheetImportOperationAction.Updated,
            Assert.Single(batch.Operations, item => item.EntityType == nameof(Client)).Action);
        var operation = Assert.Single(batch.Operations, item => item.EntityType == nameof(Act));
        Assert.Equal(SpreadsheetImportOperationAction.Created, operation.Action);
        await using var verify = factory.CreateDbContext();
        Assert.Null(verify.Model.FindEntityType(typeof(Act))!.FindProperty("IsDeleted"));
        await verify.ActAnnualNumberCounters.ExecuteDeleteAsync();
        if (lowerCounter)
        {
            verify.ActAnnualNumberCounters.Add(new ActAnnualNumberCounter { Year = 2028, LastNumber = 1 });
            await verify.SaveChangesAsync();
        }

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted, Assert.Single(rollback.Operations, item => item.EntityType == nameof(Act)).RollbackAction);
        verify.ChangeTracker.Clear();
        Assert.False(await verify.Acts.IgnoreQueryFilters().AnyAsync(act => act.Id == operation.EntityId));
        Assert.Equal("1", (await verify.Acts.IgnoreQueryFilters().SingleAsync()).Number);
        Assert.Equal(2, (await verify.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        await using var transaction = await verify.Database.BeginTransactionAsync();
        Assert.Equal("3", await new ActAnnualNumberingService(verify).NextAsync(new DateTime(2028, 1, 1), default));
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task AnnualAct_LegacyImportRollbackWithBusyActContinuesAndCanRetry_PostgreSql()
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(true, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, true, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        var client = new Client { Name = "ООО Занятый акт", IsActive = true };
        fixture.Db.Acts.Add(new Act
        {
            Number = "1",
            Date = new DateTime(2038, 6, 1),
            Amount = 100m,
            Client = client,
            Comment = "UID 1С: ANNUAL-BUSY-INDEPENDENT",
        });
        await fixture.Db.SaveChangesAsync();
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ФайлОбмена xmlns="http://kub-24.ru/upload" ВерсияФормата="2.0">
              <Объект Нпп="1" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
                <Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ANNUAL-BUSY-INDEPENDENT</Значение></Свойство></Ссылка>
                <Свойство Имя="Номер" Тип="Строка"><Значение>4</Значение></Свойство>
                <Свойство Имя="Дата" Тип="Дата"><Значение>2038-06-01T00:00:00</Значение></Свойство>
                <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка>
                  <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Занятый акт</Значение></Свойство>
                </Ссылка></Свойство>
                <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>400</Значение></Свойство>
              </Объект>
              <Объект Нпп="2" Тип="ДокументСсылка.РеализацияТоваровУслуг" ИмяПравила="РеализацияТоваровУслуг">
                <Ссылка><Свойство Имя="{УникальныйИдентификатор}" Тип="Строка"><Значение>ANNUAL-BUSY-CREATED</Значение></Свойство></Ссылка>
                <Свойство Имя="Номер" Тип="Строка"><Значение>2</Значение></Свойство>
                <Свойство Имя="Дата" Тип="Дата"><Значение>2039-06-01T00:00:00</Значение></Свойство>
                <Свойство Имя="Контрагент" Тип="СправочникСсылка.Контрагенты"><Ссылка>
                  <Свойство Имя="Наименование" Тип="Строка"><Значение>ООО Занятый акт</Значение></Свойство>
                </Ссылка></Свойство>
                <Свойство Имя="СуммаДокумента" Тип="Число"><Значение>200</Значение></Свойство>
              </Объект>
            </ФайлОбмена>
            """;
        var files = new[] { new SpreadsheetImportUploadFile("annual-busy.xml", Encoding.UTF8.GetBytes(xml)) };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        selection.AllowUpdates = true;
        selection.AllowClearingExistingValues = true;
        var batch = await service.ApplyAsync(files, selection);
        Assert.Equal(3, batch.Operations.Count);
        Assert.Equal(SpreadsheetImportOperationAction.Updated,
            Assert.Single(batch.Operations, item => item.EntityType == nameof(Client)).Action);
        await using var verify = factory.CreateDbContext();
        var locked = await verify.Acts.AsNoTracking().Include(act => act.LineItems).SingleAsync(act => act.Date.Year == 2039);
        var independent = await verify.Acts.AsNoTracking().Include(act => act.LineItems).SingleAsync(act => act.Date.Year == 2038);
        Assert.NotEmpty(independent.LineItems);
        var lockedOperation = Assert.Single(batch.Operations, item => item.EntityType == nameof(Act) && item.EntityId == locked.Id);
        var independentOperation = Assert.Single(batch.Operations, item => item.EntityType == nameof(Act) && item.EntityId == independent.Id);
        Assert.Equal(SpreadsheetImportOperationAction.Created, lockedOperation.Action);
        Assert.Equal(SpreadsheetImportOperationAction.Updated, independentOperation.Action);
        Assert.True(lockedOperation.Id > independentOperation.Id, "The locked row must be followed by an independent rollback operation.");
        await verify.ActAnnualNumberCounters.ExecuteDeleteAsync();
        verify.ActAnnualNumberCounters.Add(new ActAnnualNumberCounter { Year = 2039, LastNumber = 1 });
        await verify.SaveChangesAsync();
        DateTime? restoredAt = null;
        string? restoredMessage = null;
        await using (var blocker = factory.CreateDbContext())
        await using (var hold = await blocker.Database.BeginTransactionAsync())
        {
            await blocker.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = {locked.Id} FOR UPDATE").ToArrayAsync();

            var rollback = await service.RollbackAsync(batch.Id);

            Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
            Assert.Equal(2, rollback.RollbackRows);
            Assert.Equal(1, rollback.Operations.Count(item => item.EntityType == nameof(Act)
                && item.RollbackAction == SpreadsheetImportRollbackAction.Restored));
            Assert.Equal(SpreadsheetImportRollbackAction.Error, Assert.Single(rollback.Operations, item => item.Id == lockedOperation.Id).RollbackAction);
            Assert.Equal(SpreadsheetImportRollbackAction.Restored, Assert.Single(rollback.Operations, item => item.Id == independentOperation.Id).RollbackAction);
            verify.ChangeTracker.Clear();
            var unchanged = await verify.Acts.AsNoTracking().Include(act => act.LineItems).SingleAsync(act => act.Id == locked.Id);
            Assert.Equal(locked.Number, unchanged.Number);
            Assert.Equal(locked.Date, unchanged.Date);
            Assert.Equal(locked.Amount, unchanged.Amount);
            Assert.Equal(locked.LineItems.Select(item => item.Id).OrderBy(id => id), unchanged.LineItems.Select(item => item.Id).OrderBy(id => id));
            Assert.Equal(SpreadsheetImportRollbackAction.Error,
                (await verify.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.Id == lockedOperation.Id)).RollbackAction);
            Assert.Equal(1, (await verify.ActAnnualNumberCounters.AsNoTracking().SingleAsync(counter => counter.Year == 2039)).LastNumber);
            Assert.Equal(4, (await verify.ActAnnualNumberCounters.AsNoTracking().SingleAsync(counter => counter.Year == 2038)).LastNumber);
            var completed = await verify.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.Id == independentOperation.Id);
            restoredAt = completed.RolledBackAt;
            restoredMessage = completed.RollbackMessage;
            await hold.RollbackAsync();
        }

        var retried = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, retried.Status);
        Assert.Equal(3, retried.RollbackRows);
        Assert.Equal(2, retried.Operations.Count(item => item.EntityType == nameof(Act)
            && item.RollbackAction is SpreadsheetImportRollbackAction.Deleted or SpreadsheetImportRollbackAction.Restored));
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted, Assert.Single(retried.Operations, item => item.Id == lockedOperation.Id).RollbackAction);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, Assert.Single(retried.Operations, item => item.Id == independentOperation.Id).RollbackAction);
        verify.ChangeTracker.Clear();
        Assert.False(await verify.Acts.AnyAsync(act => act.Id == locked.Id));
        var restored = await verify.Acts.AsNoTracking().SingleAsync(act => act.Id == independent.Id);
        Assert.Equal("1", restored.Number);
        Assert.Equal(new DateTime(2038, 6, 1), restored.Date);
        Assert.Equal(100m, restored.Amount);
        var retained = await verify.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.Id == independentOperation.Id);
        Assert.Equal(restoredAt, retained.RolledBackAt);
        Assert.Equal(restoredMessage, retained.RollbackMessage);
        Assert.Equal(2, (await verify.ActAnnualNumberCounters.AsNoTracking().SingleAsync(counter => counter.Year == 2039)).LastNumber);
        Assert.Equal(4, (await verify.ActAnnualNumberCounters.AsNoTracking().SingleAsync(counter => counter.Year == 2038)).LastNumber);
        await using var transaction = await verify.Database.BeginTransactionAsync();
        Assert.Equal("3", await new ActAnnualNumberingService(verify).NextAsync(new DateTime(2039, 1, 1), default));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ImportRollbackLostCommitAcknowledgementPreservesDurableResultAndFloor(bool postgres)
    {
        await using var fixture = await SpreadsheetRollbackSessions.CreateAsync(postgres, bankModelTemplate);
        var fault = new CounterpartyRollbackCommitFailure();
        var service = fixture.Service(fault);
        fixture.Observer.Clients.Add(new Client { Name = "ООО Подтверждение отката", IsActive = true });
        await fixture.Observer.SaveChangesAsync();
        var files = new[]
        {
            CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус"],
                ["01.06.2040", "2", "200", "ООО Подтверждение отката", "Подписан"]),
        };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        selection.AllowUpdates = true;
        var batch = await service.ApplyAsync(files, selection);
        Assert.Equal(2, batch.Operations.Count);
        Assert.Equal(SpreadsheetImportOperationAction.Updated,
            Assert.Single(batch.Operations, item => item.EntityType == nameof(Client)).Action);
        Assert.Equal(SpreadsheetImportOperationAction.Created, Assert.Single(batch.Operations, item => item.EntityType == nameof(Act)).Action);
        await fixture.Observer.ActAnnualNumberCounters.ExecuteDeleteAsync();
        fault.Armed = true;

        var rollback = await service.RollbackAsync(batch.Id);

        Assert.Equal(1, fault.Fired);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        Assert.Equal(2, rollback.RollbackRows);
        Assert.Equal(1, rollback.Operations.Count(item => item.EntityType == nameof(Act)
            && item.RollbackAction == SpreadsheetImportRollbackAction.Deleted));
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted, Assert.Single(rollback.Operations, item => item.EntityType == nameof(Act)).RollbackAction);
        fixture.Observer.ChangeTracker.Clear();
        Assert.Empty(await fixture.Observer.Acts.ToArrayAsync());
        Assert.Equal(2, (await fixture.Observer.ActAnnualNumberCounters.AsNoTracking().SingleAsync()).LastNumber);
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted,
            (await fixture.Observer.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.BatchId == batch.Id && item.EntityType == nameof(Act))).RollbackAction);
        await using var transaction = await fixture.Observer.Database.BeginTransactionAsync();
        Assert.Equal("3", await new ActAnnualNumberingService(fixture.Observer).NextAsync(new DateTime(2040, 1, 1), default));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_CompletedImportRollbackWithLineItemsRemainsCompletedOnRetry(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, postgres, accessor);
        var service = new SpreadsheetImportService(factory, accessor, NullLogger<SpreadsheetImportService>.Instance);
        fixture.Db.Clients.Add(new Client { Name = "ООО Повторный откат", IsActive = true });
        await fixture.Db.SaveChangesAsync();
        var files = new[]
        {
            CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус"],
                ["01.06.2041", "2", "200", "ООО Повторный откат", "Подписан"]),
        };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        selection.AllowUpdates = true;
        var batch = await service.ApplyAsync(files, selection);
        Assert.Equal(2, batch.Operations.Count);
        Assert.Equal(SpreadsheetImportOperationAction.Updated,
            Assert.Single(batch.Operations, item => item.EntityType == nameof(Client)).Action);
        var operation = Assert.Single(batch.Operations, item => item.EntityType == nameof(Act));
        await using var verify = factory.CreateDbContext();
        Assert.NotEmpty(await verify.Set<ActLineItem>().Where(item => item.ActId == operation.EntityId).ToArrayAsync());

        var first = await service.RollbackAsync(batch.Id);
        var completed = await verify.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
        var repeated = await service.RollbackAsync(batch.Id);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, first.Status);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, repeated.Status);
        Assert.Equal(2, repeated.RollbackRows);
        Assert.Equal(1, repeated.Operations.Count(item => item.EntityType == nameof(Act)
            && item.RollbackAction == SpreadsheetImportRollbackAction.Deleted));
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted, Assert.Single(repeated.Operations, item => item.EntityType == nameof(Act)).RollbackAction);
        var unchanged = await verify.SpreadsheetImportBatchOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
        Assert.Equal(completed.RollbackAction, unchanged.RollbackAction);
        Assert.Equal(completed.RollbackMessage, unchanged.RollbackMessage);
        Assert.Equal(completed.RolledBackAt, unchanged.RolledBackAt);
        Assert.Empty(await verify.Set<ActLineItem>().Where(item => item.ActId == operation.EntityId).ToArrayAsync());
        Assert.False(await verify.Acts.AnyAsync(item => item.Id == operation.EntityId));
        Assert.Equal(2, (await verify.ActAnnualNumberCounters.AsNoTracking().SingleAsync()).LastNumber);
        await using var transaction = await verify.Database.BeginTransactionAsync();
        Assert.Equal("3", await new ActAnnualNumberingService(verify).NextAsync(new DateTime(2041, 1, 1), default));
        await transaction.CommitAsync();
    }

    private sealed class PhysicalActRollbackContextFactory(PaymentContext db, bool postgres, IHttpContextAccessor accessor) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
            => postgres
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(db.Database.GetConnectionString()).Options,
                    accessor, useObjectAuditModel: false)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options,
                    accessor, useObjectAuditModel: false);

        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

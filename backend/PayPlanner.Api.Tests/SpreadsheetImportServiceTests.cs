using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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

public partial class SpreadsheetImportServiceTests : IClassFixture<PostgreSqlModelTemplate>
{
    private readonly PostgreSqlModelTemplate bankModelTemplate;

    public SpreadsheetImportServiceTests(PostgreSqlModelTemplate bankModelTemplate)
        => this.bankModelTemplate = bankModelTemplate;

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
    public async Task ApplyAsync_UsesUserTimeZoneForImportedInvoiceOverdueStatus()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var dueDate = selectedZone.ProfileToday < selectedZone.UtcToday
            ? selectedZone.ProfileToday
            : selectedZone.UtcToday;
        var expectedStatus = dueDate < selectedZone.ProfileToday
            ? PaymentStatus.Overdue
            : PaymentStatus.Pending;

        await using (var db = CreateContext(connection))
        {
            db.Roles.Add(new Role
            {
                Id = 42,
                Name = "import-timezone-user",
                Description = "Import timezone user",
            });
            db.Users.Add(new User
            {
                Id = 42,
                Email = "import-timezone@example.com",
                PasswordHash = "hash",
                FullName = "Import Timezone User",
                RoleId = 42,
                IsActive = true,
                IsApproved = true,
            });
            db.UserSettings.Add(new UserSetting
            {
                UserId = 42,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = selectedZone.TimeZoneId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var files = new[]
        {
            CreateInvoiceFile(
                new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Оплатить до", "Номенклатурная часть" },
                new[] { FormatImportDate(dueDate), "INV-TZ-001", "1500", "ООО Часовой Пояс", "", FormatImportDate(dueDate), "Юридические услуги 1 шт x 1500 р. = 1500 р." }),
            CreateCounterpartiesFile(
                new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" },
                new[] { "ООО Часовой Пояс", "1234567890", "Иван", "+7 (999) 111-22-33", "tz@example.com", "Основной", "5", "Юр. услуги", "" }),
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

        await using var verificationDb = CreateContext(connection);
        var payment = await verificationDb.Payments.SingleAsync(item => item.Account == "INV-TZ-001");
        Assert.Equal(dueDate, payment.Date);
        Assert.Equal(dueDate, payment.PlannedDate);
        Assert.Equal(expectedStatus, payment.Status);
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

    [Fact]
    public async Task RollbackAsync_WhenCounterpartyOperationHasNoEntityId_ReturnsControlledError()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Client)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, operation.RollbackAction);
        Assert.Contains("идентификатор контрагента", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenCreatedCounterpartyIsReferencedByCase_SkipsDeletion()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var clientId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Rollback counterparty client",
                Email = "rollback-client@example.com",
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            db.ClientCases.Add(new ClientCase
            {
                ClientId = client.Id,
                Title = "Protected case",
                Description = "Blocks rollback deletion",
            });

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Client),
                entityId: client.Id,
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", client, null)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            clientId = client.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("используется связанными документами", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = CreateContext(connection);
        Assert.NotNull(await verificationDb.Clients.FindAsync(clientId));
    }

    [Fact]
    public async Task RollbackAsync_WhenCreatedCounterpartyLegalEntityIsUsedElsewhere_SkipsDeletion()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var clientId = 0;
        await using (var db = CreateContext(connection))
        {
            var legalEntity = new LegalEntity
            {
                ShortName = "Shared Legal Entity",
                FullName = "Shared Legal Entity LLC",
                Inn = "1234567890",
                CreatedAt = DateTime.UtcNow,
            };
            db.LegalEntities.Add(legalEntity);
            await db.SaveChangesAsync();

            var importedClient = new Client
            {
                Name = "Imported legal client",
                LegalEntityId = legalEntity.Id,
                LegalEntity = legalEntity,
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(importedClient);
            await db.SaveChangesAsync();

            db.Clients.Add(new Client
            {
                Name = "Existing linked client",
                LegalEntityId = legalEntity.Id,
                CreatedAt = DateTime.UtcNow,
            });

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Client),
                entityId: importedClient.Id,
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", importedClient, legalEntity),
                metadataJson: JsonSerializer.Serialize(new
                {
                    LegalEntityCreated = true,
                    LegalEntityId = legalEntity.Id,
                })));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            clientId = importedClient.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("юридическое лицо уже используется", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = CreateContext(connection);
        Assert.NotNull(await verificationDb.Clients.FindAsync(clientId));
        Assert.Equal(1, await verificationDb.LegalEntities.CountAsync());
    }

    [Fact]
    public async Task RollbackAsync_WhenCounterpartyEntityIsAlreadyMissing_SkipsGracefully()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Client),
                entityId: 9_999));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("уже отсутствует", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenCounterpartyChangedAfterImport_SkipsRollback()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var clientId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Changed client",
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Client),
                entityId: client.Id,
                afterJson: "{}"));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            clientId = client.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("были изменены после импорта", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = CreateContext(connection);
        Assert.NotNull(await verificationDb.Clients.FindAsync(clientId));
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedCounterpartySnapshotIsMissing_ReturnsError()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Counterparty without snapshot",
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Client),
                entityId: client.Id,
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", client, null)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, operation.RollbackAction);
        Assert.Contains("исходный снимок контрагента", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedCounterpartyRestoresMissingLegalEntity()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var clientId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Client without legal entity",
                Email = "without-legal@example.com",
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            var beforeClient = new Client
            {
                Id = client.Id,
                Name = "Client with restored legal entity",
                Email = "with-legal@example.com",
                CreatedAt = client.CreatedAt,
                IsActive = true,
                LegalEntityId = 4242,
            };
            var beforeLegalEntity = new LegalEntity
            {
                Id = 4242,
                ShortName = "Restored Legal Entity",
                FullName = "Restored Legal Entity LLC",
                Inn = "5555555555",
                CreatedAt = DateTime.UtcNow,
            };

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Client),
                entityId: client.Id,
                beforeJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", beforeClient, beforeLegalEntity),
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", client, null)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            clientId = client.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, operation.RollbackAction);

        await using var verificationDb = CreateContext(connection);
        var restoredClient = await verificationDb.Clients.Include(item => item.LegalEntity).SingleAsync(item => item.Id == clientId);
        Assert.Equal("Client with restored legal entity", restoredClient.Name);
        Assert.Equal("with-legal@example.com", restoredClient.Email);
        Assert.NotNull(restoredClient.LegalEntity);
        Assert.Equal("Restored Legal Entity", restoredClient.LegalEntity!.ShortName);
        Assert.Equal("5555555555", restoredClient.LegalEntity.Inn);
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedCounterpartyCreatedLegalEntityIsUsedElsewhere_SkipsRestore()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var legalEntity = new LegalEntity
            {
                ShortName = "Shared updated legal entity",
                Inn = "6666666666",
                CreatedAt = DateTime.UtcNow,
            };
            db.LegalEntities.Add(legalEntity);
            await db.SaveChangesAsync();

            var client = new Client
            {
                Name = "Updated counterparty",
                LegalEntityId = legalEntity.Id,
                LegalEntity = legalEntity,
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            db.Clients.Add(new Client
            {
                Name = "Second linked client",
                LegalEntityId = legalEntity.Id,
                CreatedAt = DateTime.UtcNow,
            });

            var beforeClient = new Client
            {
                Id = client.Id,
                Name = "Updated counterparty",
                CreatedAt = client.CreatedAt,
                IsActive = true,
            };

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Client),
                entityId: client.Id,
                beforeJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", beforeClient, null),
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", client, legalEntity),
                metadataJson: JsonSerializer.Serialize(new
                {
                    LegalEntityCreated = true,
                    LegalEntityId = legalEntity.Id,
                })));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("юридическое лицо уже используется", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedCounterpartyRestoresClientState_AndRemovesCreatedLegalEntity()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var clientId = 0;
        var legalEntityId = 0;
        await using (var db = CreateContext(connection))
        {
            var legalEntity = new LegalEntity
            {
                ShortName = "Temporary Legal Entity",
                FullName = "Temporary Legal Entity LLC",
                Inn = "9876543210",
                CreatedAt = DateTime.UtcNow,
            };
            db.LegalEntities.Add(legalEntity);
            await db.SaveChangesAsync();

            var client = new Client
            {
                Name = "Current client name",
                Email = "current-client@example.com",
                Company = "Current Company",
                LegalEntityId = legalEntity.Id,
                LegalEntity = legalEntity,
                CreatedAt = DateTime.UtcNow,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            var beforeClient = new Client
            {
                Id = client.Id,
                Name = "Original client name",
                Email = "original-client@example.com",
                Company = "Original Company",
                CreatedAt = client.CreatedAt,
                IsActive = true,
            };

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "counterparties",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Client),
                entityId: client.Id,
                beforeJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", beforeClient, null),
                afterJson: CaptureSnapshotJson("CaptureCounterpartySnapshot", client, legalEntity),
                metadataJson: JsonSerializer.Serialize(new
                {
                    LegalEntityCreated = true,
                    LegalEntityId = legalEntity.Id,
                })));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            clientId = client.Id;
            legalEntityId = legalEntity.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, operation.RollbackAction);
        Assert.Contains("юридическое лицо удалено", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = CreateContext(connection);
        var restoredClient = await verificationDb.Clients.Include(item => item.LegalEntity).SingleAsync(item => item.Id == clientId);
        Assert.Equal("Original client name", restoredClient.Name);
        Assert.Equal("original-client@example.com", restoredClient.Email);
        Assert.Equal("Original Company", restoredClient.Company);
        Assert.Null(restoredClient.LegalEntityId);
        Assert.Null(restoredClient.LegalEntity);
        Assert.Null(await verificationDb.LegalEntities.FindAsync(legalEntityId));
    }

    [Fact]
    public async Task RollbackAsync_WhenInvoiceOperationHasNoEntityId_ReturnsControlledError()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "invoices",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Payment)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, operation.RollbackAction);
        Assert.Contains("идентификатор сущности", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenInvoiceEntityIsAlreadyMissing_SkipsGracefully()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "invoices",
                action: SpreadsheetImportOperationAction.Created,
                entityType: nameof(Payment),
                entityId: 9_999));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
        Assert.Contains("уже отсутствует", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedInvoiceSnapshotIsInvalid_ReturnsError()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Invoice rollback client",
                CreatedAt = DateTime.UtcNow,
            };
            var invoice = new Invoice
            {
                Number = "INV-ERR",
                Date = new DateTime(2026, 4, 10),
            };
            var payment = new Payment
            {
                Account = "INV-ERR",
                AccountDate = invoice.Date,
                Amount = 1_500m,
                Date = invoice.Date,
                PlannedDate = invoice.Date,
                Description = "Broken snapshot invoice",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Income,
                Client = client,
                Invoice = invoice,
                CreatedAt = DateTime.UtcNow,
            };

            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "invoices",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Payment),
                entityId: payment.Id,
                afterJson: CaptureSnapshotJson("CaptureInvoiceSnapshot", payment)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, operation.RollbackAction);
        Assert.Contains("исходный снапшот", operation.RollbackMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = CreateContext(connection);
        var storedOperation = await verificationDb.SpreadsheetImportBatchOperations.SingleAsync(item => item.BatchId == batchId);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, storedOperation.RollbackAction);
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedInvoiceRestoresSnapshot_AndRemovesOrphanInvoice()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var paymentId = 0;
        var currentInvoiceId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Invoice restore client",
                CreatedAt = DateTime.UtcNow,
            };

            var currentInvoice = new Invoice
            {
                Number = "INV-NEW",
                Date = new DateTime(2026, 4, 10),
            };

            var payment = new Payment
            {
                Account = "INV-NEW",
                AccountDate = currentInvoice.Date,
                Amount = 4_500m,
                Date = currentInvoice.Date,
                PlannedDate = currentInvoice.Date,
                Description = "Current invoice snapshot",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Completed,
                Type = PaymentType.Income,
                Client = client,
                Invoice = currentInvoice,
                InvoiceLineItems =
                [
                    new InvoiceLineItem
                    {
                        SortOrder = 1,
                        Name = "Current line 1",
                        Quantity = 1m,
                        Unit = "шт",
                        Price = 3_000m,
                    },
                    new InvoiceLineItem
                    {
                        SortOrder = 2,
                        Name = "Current line 2",
                        Quantity = 1m,
                        Unit = "шт",
                        Price = 1_500m,
                    },
                ],
                CreatedAt = DateTime.UtcNow,
            };

            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var beforePayment = new Payment
            {
                Id = payment.Id,
                Account = "INV-OLD",
                AccountDate = new DateTime(2026, 4, 1),
                Amount = 2_000m,
                Date = new DateTime(2026, 4, 1),
                PlannedDate = new DateTime(2026, 4, 1),
                Description = "Original invoice snapshot",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Income,
                ClientId = client.Id,
                InvoiceLineItems =
                [
                    new InvoiceLineItem
                    {
                        SortOrder = 1,
                        Name = "Original line",
                        Quantity = 1m,
                        Unit = "шт",
                        Price = 2_000m,
                    },
                ],
            };

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "invoices",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Payment),
                entityId: payment.Id,
                beforeJson: CaptureSnapshotJson("CaptureInvoiceSnapshot", beforePayment),
                afterJson: CaptureSnapshotJson("CaptureInvoiceSnapshot", payment)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            paymentId = payment.Id;
            currentInvoiceId = currentInvoice.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, operation.RollbackAction);

        await using var verificationDb = CreateContext(connection);
        var restoredPayment = await verificationDb.Payments
            .Include(item => item.Invoice)
            .Include(item => item.InvoiceLineItems)
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal("INV-OLD", restoredPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 1), restoredPayment.AccountDate);
        Assert.Equal(2_000m, restoredPayment.Amount);
        Assert.Equal(PaymentStatus.Pending, restoredPayment.Status);
        Assert.Null(restoredPayment.InvoiceId);
        Assert.Null(restoredPayment.Invoice);
        Assert.Single(restoredPayment.InvoiceLineItems);
        Assert.Equal("Original line", restoredPayment.InvoiceLineItems.Single().Name);
        Assert.Null(await verificationDb.Invoices.FindAsync(currentInvoiceId));
    }

    [Fact]
    public async Task RollbackAsync_WhenUpdatedInvoiceRestoresMissingInvoiceEntity_CreatesNewInvoice()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        var batchId = 0;
        var paymentId = 0;
        await using (var db = CreateContext(connection))
        {
            var client = new Client
            {
                Name = "Invoice restore without entity client",
                CreatedAt = DateTime.UtcNow,
            };
            var payment = new Payment
            {
                Account = "INV-CURRENT",
                AccountDate = new DateTime(2026, 4, 20),
                Amount = 3_500m,
                Date = new DateTime(2026, 4, 20),
                PlannedDate = new DateTime(2026, 4, 20),
                Description = "Current state without invoice entity",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Completed,
                Type = PaymentType.Income,
                Client = client,
                CreatedAt = DateTime.UtcNow,
            };

            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var beforePayment = new Payment
            {
                Id = payment.Id,
                Account = "INV-RESTORED",
                AccountDate = new DateTime(2026, 4, 5),
                Amount = 2_800m,
                Date = new DateTime(2026, 4, 5),
                PlannedDate = new DateTime(2026, 4, 5),
                Description = "State with restored invoice entity",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Income,
                ClientId = client.Id,
                Invoice = new Invoice
                {
                    Id = 7_777,
                    Number = "INV-RESTORED",
                    Date = new DateTime(2026, 4, 5),
                },
            };

            var batch = CreateBatch();
            batch.Operations.Add(CreateOperation(
                sectionKey: "invoices",
                action: SpreadsheetImportOperationAction.Updated,
                entityType: nameof(Payment),
                entityId: payment.Id,
                beforeJson: CaptureSnapshotJson("CaptureInvoiceSnapshot", beforePayment),
                afterJson: CaptureSnapshotJson("CaptureInvoiceSnapshot", payment)));

            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();

            batchId = batch.Id;
            paymentId = payment.Id;
        }

        var rollback = await service.RollbackAsync(batchId);

        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        var operation = Assert.Single(rollback.Operations);
        Assert.Equal(SpreadsheetImportRollbackAction.Restored, operation.RollbackAction);

        await using var verificationDb = CreateContext(connection);
        var restoredPayment = await verificationDb.Payments
            .Include(item => item.Invoice)
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal("INV-RESTORED", restoredPayment.Account);
        Assert.NotNull(restoredPayment.Invoice);
        Assert.NotNull(restoredPayment.InvoiceId);
        Assert.Equal("INV-RESTORED", restoredPayment.Invoice!.Number);
    }

    [Fact]
    public async Task GetBatchesAsync_ReturnsLatestBatchesFirst()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);

        await using (var db = CreateContext(connection))
        {
            db.SpreadsheetImportBatches.AddRange(
                new SpreadsheetImportBatch
                {
                    Title = "Older batch",
                    Status = SpreadsheetImportBatchStatus.Applied,
                    CreatedAt = new DateTime(2026, 4, 18, 8, 0, 0, DateTimeKind.Utc),
                    Files =
                    [
                        new SpreadsheetImportBatchFile
                        {
                            FileName = "older.xlsx",
                            FileHash = "older",
                            DetectedKind = "invoices",
                            RowCount = 1,
                            CreatedAt = DateTime.UtcNow,
                        },
                    ],
                },
                new SpreadsheetImportBatch
                {
                    Title = "Newer batch",
                    Status = SpreadsheetImportBatchStatus.RolledBack,
                    CreatedAt = new DateTime(2026, 4, 19, 8, 0, 0, DateTimeKind.Utc),
                    Files =
                    [
                        new SpreadsheetImportBatchFile
                        {
                            FileName = "newer.xlsx",
                            FileHash = "newer",
                            DetectedKind = "acts",
                            RowCount = 2,
                            CreatedAt = DateTime.UtcNow,
                        },
                    ],
                });
            await db.SaveChangesAsync();
        }

        var batches = await service.GetBatchesAsync();

        Assert.True(batches.Count >= 2);
        Assert.Equal("Newer batch", batches.First().Title);
        Assert.Equal("Older batch", batches.Skip(1).First().Title);
    }

    [Fact]
    public async Task GetBatchAsync_ReturnsOnlyRequestedOperationsPage()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        int batchId;

        await using (var db = CreateContext(connection))
        {
            var createdAt = new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Utc);
            var batch = new SpreadsheetImportBatch
            {
                Title = "Paged operations batch",
                Status = SpreadsheetImportBatchStatus.Applied,
                TotalRows = 30,
                CreatedRows = 30,
                CreatedAt = createdAt,
                AppliedAt = createdAt,
                Operations = Enumerable.Range(1, 30)
                    .Select(index => new SpreadsheetImportBatchOperation
                    {
                        SectionKey = "acts",
                        SourceFileName = "audit.xml",
                        SourceFileHash = "audit-hash",
                        SourceRowId = $"row-{index}",
                        SourceRowNumber = index,
                        SourceKey = $"acts-row-{index}",
                        SourceTitle = $"Operation {index}",
                        Action = SpreadsheetImportOperationAction.Created,
                        EntityType = "act",
                        CreatedAt = createdAt.AddSeconds(index),
                    })
                    .ToList(),
            };
            db.SpreadsheetImportBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        var detail = await service.GetBatchAsync(batchId, page: 2, pageSize: 10);

        Assert.NotNull(detail);
        Assert.Equal(30, detail.OperationsTotal);
        Assert.Equal(2, detail.OperationsPage);
        Assert.Equal(10, detail.OperationsPageSize);
        Assert.Equal(10, detail.Operations.Count);
        Assert.Equal(11, detail.Operations.First().SourceRowNumber);
        Assert.Equal(20, detail.Operations.Last().SourceRowNumber);
    }

    private static SpreadsheetImportUploadFile CreateInvoiceFile(params string[][] rows)
        => CreateUploadFile("Счета.xlsx", CreateWorkbook(("Счета", rows)));

    private static SpreadsheetImportUploadFile CreateActFile(params string[][] rows)
        => CreateUploadFile("Акты.xlsx", CreateWorkbook(("Акты", rows)));

    private static SpreadsheetImportUploadFile CreateCounterpartiesFile(params string[][] rows)
        => CreateUploadFile("Контрагенты.xlsx", CreateWorkbook(("Контрагенты", rows)));

    private static SpreadsheetImportUploadFile CreateUploadFile(string fileName, byte[] content)
        => new(fileName, content);

    private static string FormatImportDate(DateTime value)
        => $"{value:dd.MM.yyyy}";

    private static (string TimeZoneId, DateTime UtcToday, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, utcToday, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC date was found.");
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

    private static SpreadsheetImportBatch CreateBatch()
        => new()
        {
            Title = "Rollback coverage batch",
            Status = SpreadsheetImportBatchStatus.Applied,
            CreatedAt = DateTime.UtcNow,
            AppliedAt = DateTime.UtcNow,
        };

    private static SpreadsheetImportBatchOperation CreateOperation(
        string sectionKey,
        string action,
        string entityType,
        int? entityId = null,
        string? beforeJson = null,
        string? afterJson = null,
        string? metadataJson = null)
        => new()
        {
            SectionKey = sectionKey,
            SourceFileName = "test.xlsx",
            SourceFileHash = "hash",
            SourceRowId = "row-1",
            SourceRowNumber = 1,
            SourceKey = $"{sectionKey}-row-1",
            SourceTitle = $"{sectionKey} rollback",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow,
        };

    private static string CaptureSnapshotJson(string captureMethodName, params object?[] args)
    {
        var captureMethod = typeof(SpreadsheetImportService).GetMethod(captureMethodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(captureMethod);

        var snapshot = captureMethod!.Invoke(null, args);
        Assert.NotNull(snapshot);

        var serializeMethod = typeof(SpreadsheetImportService).GetMethod("SerializeSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(serializeMethod);

        var genericSerialize = serializeMethod!.MakeGenericMethod(snapshot!.GetType());
        return Assert.IsType<string>(genericSerialize.Invoke(null, [snapshot]));
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

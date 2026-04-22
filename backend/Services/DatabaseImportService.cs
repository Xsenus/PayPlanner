using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IDatabaseImportService
{
    Task<DatabaseImportResultDto> ImportSqliteAsync(string sqliteFilePath, CancellationToken cancellationToken = default);
}

public sealed partial class DatabaseImportService : IDatabaseImportService
{
    private sealed class ImportMaps
    {
        public Dictionary<int, int> Roles { get; } = [];
        public Dictionary<int, int> Users { get; } = [];
        public Dictionary<int, int> DealTypes { get; } = [];
        public Dictionary<int, int> IncomeTypes { get; } = [];
        public Dictionary<int, int> PaymentSources { get; } = [];
        public Dictionary<int, int> Services { get; } = [];
        public Dictionary<int, int> ServiceCategories { get; } = [];
        public Dictionary<int, int> PaymentStatuses { get; } = [];
        public Dictionary<int, int> ClientStatuses { get; } = [];
        public Dictionary<int, int> Countries { get; } = [];
        public Dictionary<int, int> BaseCurrencies { get; } = [];
        public Dictionary<int, int> ServiceLanguages { get; } = [];
        public Dictionary<int, int> OrganizationForms { get; } = [];
        public Dictionary<int, int> Timezones { get; } = [];
        public Dictionary<int, int> TaxSystems { get; } = [];
        public Dictionary<int, int> TaxSystemModes { get; } = [];
        public Dictionary<int, int> InvoiceUnits { get; } = [];
        public Dictionary<int, int> InvoiceItemNames { get; } = [];
        public Dictionary<int, int> Okin { get; } = [];
        public Dictionary<int, int> Okso { get; } = [];
        public Dictionary<int, int> Okpdtr { get; } = [];
        public Dictionary<int, int> LegalEntities { get; } = [];
        public Dictionary<int, int> Clients { get; } = [];
        public Dictionary<int, int> ClientCases { get; } = [];
        public Dictionary<int, int> Contracts { get; } = [];
        public Dictionary<int, int> Invoices { get; } = [];
        public Dictionary<int, int> Payments { get; } = [];
        public Dictionary<int, int> Acts { get; } = [];
        public Dictionary<int, int> MoneyOperations { get; } = [];
    }

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly DatabaseProviderInfo _databaseProvider;
    private readonly ILogger<DatabaseImportService> _logger;

    public DatabaseImportService(
        IDbContextFactory<PaymentContext> contextFactory,
        DatabaseProviderInfo databaseProvider,
        ILogger<DatabaseImportService> logger)
    {
        _contextFactory = contextFactory;
        _databaseProvider = databaseProvider;
        _logger = logger;
    }

    public async Task<DatabaseImportResultDto> ImportSqliteAsync(string sqliteFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sqliteFilePath))
        {
            throw new ArgumentException("SQLite file path is required.", nameof(sqliteFilePath));
        }

        if (!File.Exists(sqliteFilePath))
        {
            throw new FileNotFoundException("SQLite import file was not found.", sqliteFilePath);
        }

        var result = new DatabaseImportResultDto
        {
            SourceProvider = "sqlite",
            TargetProvider = _databaseProvider.Key,
            TargetProviderDisplayName = _databaseProvider.DisplayName,
            StartedAt = DateTime.UtcNow
        };

        var sourceOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = sqliteFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString())
            .Options;

        await using var source = new PaymentContext(sourceOptions, useObjectAuditModel: false);
        await using var target = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var maps = new ImportMaps();

        if (!await source.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("Не удалось открыть загруженный SQLite-файл.");
        }

        _logger.LogInformation(
            "Starting SQLite import from {SourceFile} into {TargetProvider}.",
            sqliteFilePath,
            _databaseProvider.DisplayName);

        await ImportReferenceDataAsync(source, target, result, maps, cancellationToken);
        await ImportBusinessDataAsync(source, target, result, maps, cancellationToken);
        await RunTableAsync(result, target, "DerivedClientContracts", "Restore client-contract links", table =>
            RestoreDerivedClientContractsAsync(target, table, cancellationToken));
        await ImportActivityDataAsync(source, target, result, maps, cancellationToken);

        result.FinishedAt = DateTime.UtcNow;
        result.Success = result.TablesWithErrors == 0;

        _logger.LogInformation(
            "SQLite import completed. Success={Success}, tables={Tables}, tablesWithErrors={TablesWithErrors}.",
            result.Success,
            result.SourceTablesProcessed,
            result.TablesWithErrors);

        return result;
    }

    private async Task ImportReferenceDataAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportResultDto result,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        await RunTableAsync(result, target, "Roles", "Роли", table =>
            ImportRolesAsync(source, target, table, maps, cancellationToken));

        await RunTableAsync(result, target, "RolePermissions", "Права ролей", table =>
            ImportEntitiesAsync(
                source,
                target,
                table,
                ctx => ctx.RolePermissions.AsNoTracking(),
                ctx => ctx.RolePermissions,
                x => x.Id,
                x => x.Id,
                x =>
                {
                    var roleId = MapId(maps.Roles, x.RoleId);
                    return roleId.HasValue ? Key(roleId.Value, x.Section) : string.Empty;
                },
                x => Key(x.RoleId, x.Section),
                (sourceItem, targetItem) => CopyRolePermission(sourceItem, targetItem, maps),
                new Dictionary<int, int>(),
                cancellationToken));

        await RunTableAsync(result, target, "Users", "Пользователи", table =>
            ImportUsersAsync(source, target, table, maps, cancellationToken));

        await RunTableAsync(result, target, "DealTypes", "Типы сделок", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.DealTypes.AsNoTracking(), ctx => ctx.DealTypes,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyDealType, maps.DealTypes, cancellationToken));

        await RunTableAsync(result, target, "IncomeTypes", "Типы доходов", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.IncomeTypes.AsNoTracking(), ctx => ctx.IncomeTypes,
                x => x.Id, x => x.Id, x => Key(x.Name, x.PaymentType), x => Key(x.Name, x.PaymentType), CopyIncomeType, maps.IncomeTypes, cancellationToken));

        await RunTableAsync(result, target, "PaymentSources", "Источники платежей", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.PaymentSources.AsNoTracking(), ctx => ctx.PaymentSources,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyPaymentSource, maps.PaymentSources, cancellationToken));

        await RunTableAsync(result, target, "ServiceCategoryDictionaryItems", "Категории услуг", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.ServiceCategories.AsNoTracking(), ctx => ctx.ServiceCategories,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyServiceCategory, maps.ServiceCategories, cancellationToken));

        await RunTableAsync(result, target, "ServiceDictionaryItems", "Справочник услуг", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Services.AsNoTracking(), ctx => ctx.Services,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name),
                (sourceItem, targetItem) => CopyServiceDictionaryItem(sourceItem, targetItem, maps),
                maps.Services, cancellationToken));

        await RunTableAsync(result, target, "PaymentStatuses", "Статусы платежей", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.PaymentStatuses.AsNoTracking(), ctx => ctx.PaymentStatuses,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyPaymentStatus, maps.PaymentStatuses, cancellationToken));

        await RunTableAsync(result, target, "ClientStatuses", "Статусы клиентов", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.ClientStatuses.AsNoTracking(), ctx => ctx.ClientStatuses,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyClientStatus, maps.ClientStatuses, cancellationToken));

        await RunTableAsync(result, target, "Countries", "Страны", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Countries.AsNoTracking(), ctx => ctx.Countries,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyCountry, maps.Countries, cancellationToken));

        await RunTableAsync(result, target, "BaseCurrencies", "Базовые валюты", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.BaseCurrencies.AsNoTracking(), ctx => ctx.BaseCurrencies,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyBaseCurrency, maps.BaseCurrencies, cancellationToken));

        await RunTableAsync(result, target, "ServiceLanguages", "Языки сервиса", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.ServiceLanguages.AsNoTracking(), ctx => ctx.ServiceLanguages,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyServiceLanguage, maps.ServiceLanguages, cancellationToken));

        await RunTableAsync(result, target, "OrganizationForms", "Формы организаций", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.OrganizationForms.AsNoTracking(), ctx => ctx.OrganizationForms,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyOrganizationForm, maps.OrganizationForms, cancellationToken));

        await RunTableAsync(result, target, "Timezones", "Часовые пояса", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Timezones.AsNoTracking(), ctx => ctx.Timezones,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyTimezone, maps.Timezones, cancellationToken));

        await RunTableAsync(result, target, "TaxSystems", "Системы налогообложения", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.TaxSystems.AsNoTracking(), ctx => ctx.TaxSystems,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyTaxSystem, maps.TaxSystems, cancellationToken));

        await RunTableAsync(result, target, "TaxSystemModes", "Режимы налогообложения", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.TaxSystemModes.AsNoTracking(), ctx => ctx.TaxSystemModes,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyTaxSystemMode, maps.TaxSystemModes, cancellationToken));

        await RunTableAsync(result, target, "InvoiceUnits", "Единицы счета", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.InvoiceUnits.AsNoTracking(), ctx => ctx.InvoiceUnits,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyInvoiceUnit, maps.InvoiceUnits, cancellationToken));

        await RunTableAsync(result, target, "InvoiceItemNames", "Позиции счета", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.InvoiceItemNames.AsNoTracking(), ctx => ctx.InvoiceItemNames,
                x => x.Id, x => x.Id, x => Key(x.Name), x => Key(x.Name), CopyInvoiceItemName, maps.InvoiceItemNames, cancellationToken));

        await RunTableAsync(result, target, "OkinDictionaryItems", "Справочник ОКИН", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.OkinDictionaryItems.AsNoTracking(), ctx => ctx.OkinDictionaryItems,
                x => x.Id, x => x.Id, x => Key(x.Code), x => Key(x.Code), CopyOkin, maps.Okin, cancellationToken));

        await RunTableAsync(result, target, "OksoDictionaryItems", "Справочник ОКСО", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.OksoDictionaryItems.AsNoTracking(), ctx => ctx.OksoDictionaryItems,
                x => x.Id, x => x.Id, x => Key(x.Code), x => Key(x.Code), CopyOkso, maps.Okso, cancellationToken));

        await RunTableAsync(result, target, "OkpdtrDictionaryItems", "Справочник ОКПДТР", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.OkpdtrDictionaryItems.AsNoTracking(), ctx => ctx.OkpdtrDictionaryItems,
                x => x.Id, x => x.Id, x => Key(x.Code), x => Key(x.Code), CopyOkpdtr, maps.Okpdtr, cancellationToken));

        await RunTableAsync(result, target, "SystemSettings", "Системные настройки", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.SystemSettings.AsNoTracking(), ctx => ctx.SystemSettings,
                x => x.Id, x => x.Id, x => Key(x.Key), x => Key(x.Key), CopySystemSetting, new Dictionary<int, int>(), cancellationToken));
    }

    private async Task ImportBusinessDataAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportResultDto result,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        await RunTableAsync(result, target, "LegalEntities", "Юрлица", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.LegalEntities.AsNoTracking(), ctx => ctx.LegalEntities,
                x => x.Id, x => x.Id, BuildLegalEntityKey, BuildLegalEntityKey,
                (sourceItem, targetItem) => CopyLegalEntity(sourceItem, targetItem, maps),
                maps.LegalEntities, cancellationToken));

        await RunTableAsync(result, target, "Clients", "Клиенты", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Clients.AsNoTracking(), ctx => ctx.Clients,
                x => x.Id, x => x.Id, BuildClientKey, BuildClientKey,
                (sourceItem, targetItem) => CopyClient(sourceItem, targetItem, maps),
                maps.Clients, cancellationToken));

        await RunTableAsync(result, target, "ClientCases", "Дела клиентов", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.ClientCases.AsNoTracking(), ctx => ctx.ClientCases,
                x => x.Id, x => x.Id,
                x =>
                {
                    var clientId = MapId(maps.Clients, x.ClientId);
                    return clientId.HasValue ? Key(clientId.Value, x.Title) : string.Empty;
                },
                x => Key(x.ClientId, x.Title),
                (sourceItem, targetItem) => CopyClientCase(sourceItem, targetItem, maps),
                maps.ClientCases, cancellationToken));

        await RunTableAsync(result, target, "Contracts", "Договоры", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Contracts.AsNoTracking(), ctx => ctx.Contracts,
                x => x.Id, x => x.Id, BuildContractKey, BuildContractKey, CopyContract, maps.Contracts, cancellationToken));

        await RunTableAsync(result, target, "Invoices", "Канонические счета", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Invoices.AsNoTracking(), ctx => ctx.Invoices,
                x => x.Id, x => x.Id, BuildInvoiceKey, BuildInvoiceKey, CopyInvoice, maps.Invoices, cancellationToken));

        await RunTableAsync(result, target, "Payments", "Платежи", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Payments.AsNoTracking(), ctx => ctx.Payments,
                x => x.Id, x => x.Id,
                x => BuildPaymentKey(x, maps),
                BuildTargetPaymentKey,
                (sourceItem, targetItem) => CopyPayment(sourceItem, targetItem, maps),
                maps.Payments, cancellationToken));

        await RunTableAsync(result, target, "PaymentResponsibles", "Ответственные по счетам", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.PaymentResponsibles.AsNoTracking(), ctx => ctx.PaymentResponsibles.AsNoTracking(),
                x =>
                {
                    var paymentId = MapId(maps.Payments, x.PaymentId);
                    var userId = MapId(maps.Users, x.UserId);
                    return paymentId.HasValue && userId.HasValue ? Key(paymentId.Value, userId.Value) : string.Empty;
                },
                x => Key(x.PaymentId, x.UserId),
                x => new PaymentResponsible
                {
                    PaymentId = MapRequired(maps.Payments, x.PaymentId),
                    UserId = MapRequired(maps.Users, x.UserId)
                },
                cancellationToken));

        await RunTableAsync(result, target, "Acts", "Акты", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.Acts.AsNoTracking(), ctx => ctx.Acts,
                x => x.Id, x => x.Id,
                x => BuildActKey(x, maps),
                BuildTargetActKey,
                (sourceItem, targetItem) => CopyAct(sourceItem, targetItem, maps),
                maps.Acts, cancellationToken));

        await RunTableAsync(result, target, "AddressRecords", "Адреса", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.AddressRecords.AsNoTracking(), ctx => ctx.AddressRecords,
                x => x.Id, x => x.Id,
                x => BuildAddressKey(x, maps),
                BuildTargetAddressKey,
                (sourceItem, targetItem) => CopyAddressRecord(sourceItem, targetItem, maps),
                new Dictionary<int, int>(), cancellationToken));

        await RunTableAsync(result, target, "ClientPassports", "Паспорта клиентов", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.ClientPassports.AsNoTracking(), ctx => ctx.ClientPassports,
                x => x.Id, x => x.Id,
                x =>
                {
                    var clientId = MapId(maps.Clients, x.ClientId);
                    return clientId.HasValue ? Key(clientId.Value, x.Series, x.Number) : string.Empty;
                },
                x => Key(x.ClientId, x.Series, x.Number),
                (sourceItem, targetItem) => CopyClientPassport(sourceItem, targetItem, maps),
                new Dictionary<int, int>(), cancellationToken));

        await RunTableAsync(result, target, "PowerOfAttorney", "Доверенности", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.PowersOfAttorney.AsNoTracking(), ctx => ctx.PowersOfAttorney,
                x => x.Id, x => x.Id,
                x =>
                {
                    var clientId = MapId(maps.Clients, x.ClientId);
                    return clientId.HasValue ? Key(clientId.Value, x.Number, x.IssuedAt) : string.Empty;
                },
                x => Key(x.ClientId, x.Number, x.IssuedAt),
                (sourceItem, targetItem) => CopyPowerOfAttorney(sourceItem, targetItem, maps),
                new Dictionary<int, int>(), cancellationToken));

        await RunTableAsync(result, target, "ClientContracts", "Связи клиентов и договоров", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.ClientContracts.AsNoTracking(), ctx => ctx.ClientContracts.AsNoTracking(),
                x =>
                {
                    var contractId = MapId(maps.Contracts, x.ContractId);
                    var clientId = MapId(maps.Clients, x.ClientId);
                    return contractId.HasValue && clientId.HasValue ? Key(contractId.Value, clientId.Value) : string.Empty;
                },
                x => Key(x.ContractId, x.ClientId),
                x => new ClientContract
                {
                    ContractId = MapRequired(maps.Contracts, x.ContractId),
                    ClientId = MapRequired(maps.Clients, x.ClientId),
                    Comment = x.Comment
                },
                cancellationToken));

        await RunTableAsync(result, target, "ContractActs", "Связи договоров и актов", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.ContractActs.AsNoTracking(), ctx => ctx.ContractActs.AsNoTracking(),
                x =>
                {
                    var contractId = MapId(maps.Contracts, x.ContractId);
                    var actId = MapId(maps.Acts, x.ActId);
                    return contractId.HasValue && actId.HasValue ? Key(contractId.Value, actId.Value) : string.Empty;
                },
                x => Key(x.ContractId, x.ActId),
                x => new ContractAct
                {
                    ContractId = MapRequired(maps.Contracts, x.ContractId),
                    ActId = MapRequired(maps.Acts, x.ActId)
                },
                cancellationToken));

        await RunTableAsync(result, target, "ContractInvoices", "Связи договоров и счетов-операций", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.ContractInvoices.AsNoTracking(), ctx => ctx.ContractInvoices.AsNoTracking(),
                x =>
                {
                    var contractId = MapId(maps.Contracts, x.ContractId);
                    var invoiceId = MapId(maps.Payments, x.InvoiceId);
                    return contractId.HasValue && invoiceId.HasValue ? Key(contractId.Value, invoiceId.Value) : string.Empty;
                },
                x => Key(x.ContractId, x.InvoiceId),
                x => new ContractInvoice
                {
                    ContractId = MapRequired(maps.Contracts, x.ContractId),
                    InvoiceId = MapRequired(maps.Payments, x.InvoiceId)
                },
                cancellationToken));

        await RunTableAsync(result, target, "ContractPayments", "Связи договоров и платежей", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.ContractPayments.AsNoTracking(), ctx => ctx.ContractPayments.AsNoTracking(),
                x =>
                {
                    var contractId = MapId(maps.Contracts, x.ContractId);
                    var paymentId = MapId(maps.Payments, x.PaymentId);
                    return contractId.HasValue && paymentId.HasValue ? Key(contractId.Value, paymentId.Value) : string.Empty;
                },
                x => Key(x.ContractId, x.PaymentId),
                x => new ContractPayment
                {
                    ContractId = MapRequired(maps.Contracts, x.ContractId),
                    PaymentId = MapRequired(maps.Payments, x.PaymentId)
                },
                cancellationToken));

        await RunTableAsync(result, target, "InvoiceClientCases", "Связи счетов и дел клиентов", table =>
            ImportRelationsAsync(source, target, table, ctx => ctx.InvoiceClientCases.AsNoTracking(), ctx => ctx.InvoiceClientCases.AsNoTracking(),
                x =>
                {
                    var paymentId = MapId(maps.Payments, x.PaymentId);
                    var clientCaseId = MapId(maps.ClientCases, x.ClientCaseId);
                    return paymentId.HasValue && clientCaseId.HasValue ? Key(paymentId.Value, clientCaseId.Value) : string.Empty;
                },
                x => Key(x.PaymentId, x.ClientCaseId),
                x => new InvoiceClientCase
                {
                    PaymentId = MapRequired(maps.Payments, x.PaymentId),
                    ClientCaseId = MapRequired(maps.ClientCases, x.ClientCaseId)
                },
                cancellationToken));

        await RunTableAsync(result, target, "MoneyOperations", "Денежные операции", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.MoneyOperations.AsNoTracking(), ctx => ctx.MoneyOperations,
                x => x.Id, x => x.Id, BuildMoneyOperationKey, BuildMoneyOperationKey, CopyMoneyOperation, maps.MoneyOperations, cancellationToken));

        await RunTableAsync(result, target, "UserSettings", "Пользовательские настройки", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.UserSettings.AsNoTracking(), ctx => ctx.UserSettings,
                x => x.Id, x => x.Id,
                x =>
                {
                    var userId = MapId(maps.Users, x.UserId);
                    return userId.HasValue ? Key(userId.Value, x.Key) : string.Empty;
                },
                x => Key(x.UserId, x.Key),
                (sourceItem, targetItem) => CopyUserSetting(sourceItem, targetItem, maps),
                new Dictionary<int, int>(), cancellationToken));

        await RunTableAsync(result, target, "FormFieldRequirements", "Обязательные поля форм", table =>
            ImportEntitiesAsync(source, target, table, ctx => ctx.FormFieldRequirements.AsNoTracking(), ctx => ctx.FormFieldRequirements,
                x => x.Id, x => x.Id, x => Key(x.Section, x.FieldKey), x => Key(x.Section, x.FieldKey), CopyFormFieldRequirement, new Dictionary<int, int>(), cancellationToken));
    }

    private async Task ImportActivityDataAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportResultDto result,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        await RunTableAsync(result, target, "UserActivityLogs", "Журнал действий пользователей", table =>
            ImportUserActivityLogsAsync(source, target, table, maps, cancellationToken));
    }

    private async Task RunTableAsync(
        DatabaseImportResultDto result,
        PaymentContext target,
        string tableName,
        string displayName,
        Func<DatabaseImportTableResultDto, Task> action)
    {
        var table = new DatabaseImportTableResultDto
        {
            Table = tableName,
            DisplayName = displayName
        };

        result.Tables.Add(table);
        await using var transaction = await target.Database.BeginTransactionAsync();

        try
        {
            await action(table);
            table.Completed = table.Errors == 0 && table.Conflicts == 0;

            if (string.IsNullOrWhiteSpace(table.Message))
            {
                table.Message = BuildDefaultMessage(table);
            }

            await transaction.CommitAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync();
            table.Completed = true;
            table.Message = "В исходной SQLite-базе нет этой таблицы, этап пропущен.";
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            table.Errors += table.Errors == 0 ? 1 : 0;
            table.Completed = false;
            table.Message = GetDisplayErrorMessage(ex);
            _logger.LogError(ex, "SQLite import failed on table {Table}.", tableName);
        }
        finally
        {
            result.SourceTablesProcessed += 1;
            if (table.Errors > 0)
            {
                result.TablesWithErrors += 1;
            }

            target.ChangeTracker.Clear();
        }
    }

    private async Task ImportUsersAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportTableResultDto table,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        var sourceItems = await source.Users
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        table.SourceRows = sourceItems.Count;

        var targetItems = await target.Users.ToListAsync(cancellationToken);
        var existingByEmail = targetItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => NormalizeEmail(x.Email))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var roleByName = await target.Roles
            .AsNoTracking()
            .ToDictionaryAsync(x => NormalizeText(x.Name), x => x.Id, cancellationToken);

        var defaultUserRoleId = roleByName.GetValueOrDefault(NormalizeText("user"));
        var defaultAdminRoleId = roleByName.GetValueOrDefault(NormalizeText("admin"));
        var inserted = new List<(int OldId, User Entity)>();
        var importedUsers = new List<(User Source, User Target)>();

        foreach (var sourceItem in sourceItems)
        {
            var email = NormalizeEmail(sourceItem.Email);
            if (string.IsNullOrWhiteSpace(email))
            {
                table.Conflicts += 1;
                continue;
            }

            var roleId = MapId(maps.Roles, sourceItem.RoleId)
                ?? (defaultUserRoleId != 0 ? defaultUserRoleId : defaultAdminRoleId);

            if (roleId == 0)
            {
                table.Errors += 1;
                continue;
            }

            if (existingByEmail.TryGetValue(email, out var existing))
            {
                CopyUser(sourceItem, existing, roleId, null);
                maps.Users[sourceItem.Id] = existing.Id;
                importedUsers.Add((sourceItem, existing));
                table.Updated += 1;
                continue;
            }

            var created = new User();
            CopyUser(sourceItem, created, roleId, null);
            target.Users.Add(created);
            existingByEmail[email] = created;
            inserted.Add((sourceItem.Id, created));
            importedUsers.Add((sourceItem, created));
            table.Inserted += 1;
        }

        if (table.Inserted > 0 || table.Updated > 0)
        {
            await SaveImportedChangesAsync(target, cancellationToken);
        }

        foreach (var item in inserted)
        {
            maps.Users[item.OldId] = item.Entity.Id;
        }

        foreach (var (sourceItem, targetItem) in importedUsers)
        {
            targetItem.ApprovedByUserId = MapId(maps.Users, sourceItem.ApprovedByUserId);
        }

        if (importedUsers.Count > 0)
        {
            await SaveImportedChangesAsync(target, cancellationToken);
        }
    }

    private async Task ImportUserActivityLogsAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportTableResultDto table,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        var sourceItems = await source.UserActivityLogs
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        table.SourceRows = sourceItems.Count;

        var existingKeys = (await target.UserActivityLogs
                .AsNoTracking()
                .Select(x => new
                {
                    x.UserId,
                    x.Category,
                    x.Action,
                    x.Section,
                    x.ObjectType,
                    x.ObjectId,
                    x.Description,
                    x.Path,
                    x.Status,
                    x.CreatedAt
                })
                .ToListAsync(cancellationToken))
            .Select(x => Key(x.UserId, x.Category, x.Action, x.Section, x.ObjectType, x.ObjectId, x.Description, x.Path, x.Status, x.CreatedAt))
            .ToHashSet(StringComparer.Ordinal);

        var pending = new List<UserActivityLog>();

        foreach (var sourceItem in sourceItems)
        {
            var key = Key(
                MapId(maps.Users, sourceItem.UserId),
                sourceItem.Category,
                sourceItem.Action,
                sourceItem.Section,
                sourceItem.ObjectType,
                sourceItem.ObjectId,
                sourceItem.Description,
                sourceItem.Path,
                sourceItem.Status,
                sourceItem.CreatedAt);

            if (!existingKeys.Add(key))
            {
                table.Skipped += 1;
                continue;
            }

            pending.Add(new UserActivityLog
            {
                UserId = MapId(maps.Users, sourceItem.UserId),
                UserEmail = sourceItem.UserEmail,
                UserFullName = sourceItem.UserFullName,
                Category = sourceItem.Category,
                Action = sourceItem.Action,
                Section = sourceItem.Section,
                ObjectType = sourceItem.ObjectType,
                ObjectId = sourceItem.ObjectId,
                Description = sourceItem.Description,
                Status = sourceItem.Status,
                IpAddress = sourceItem.IpAddress,
                UserAgent = sourceItem.UserAgent,
                HttpMethod = sourceItem.HttpMethod,
                Path = sourceItem.Path,
                QueryString = sourceItem.QueryString,
                HttpStatusCode = sourceItem.HttpStatusCode,
                DurationMs = sourceItem.DurationMs,
                MetadataJson = sourceItem.MetadataJson,
                CreatedAt = sourceItem.CreatedAt
            });

            table.Inserted += 1;
        }

        if (pending.Count > 0)
        {
            target.UserActivityLogs.AddRange(pending);
            await SaveImportedChangesAsync(target, cancellationToken);
        }
    }

    private async Task ImportEntitiesAsync<T>(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportTableResultDto table,
        Func<PaymentContext, IQueryable<T>> sourceQuery,
        Func<PaymentContext, IQueryable<T>> targetQuery,
        Func<T, int> sourceIdSelector,
        Func<T, int> targetIdSelector,
        Func<T, string> sourceKeySelector,
        Func<T, string> targetKeySelector,
        Action<T, T> copyAction,
        Dictionary<int, int> importedIdMap,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        var sourceItems = await sourceQuery(source).ToListAsync(cancellationToken);
        table.SourceRows = sourceItems.Count;

        var targetItems = await targetQuery(target).ToListAsync(cancellationToken);
        var existingByKey = targetItems
            .Select(item => new { Item = item, Key = targetKeySelector(item) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Item, StringComparer.Ordinal);

        var inserted = new List<(int OldId, T Entity)>();
        var deferredMappings = new List<(int OldId, T Entity)>();

        foreach (var sourceItem in sourceItems.OrderBy(sourceIdSelector))
        {
            var key = sourceKeySelector(sourceItem);
            if (string.IsNullOrWhiteSpace(key))
            {
                table.Conflicts += 1;
                continue;
            }

            if (existingByKey.TryGetValue(key, out var existing))
            {
                copyAction(sourceItem, existing);
                var targetId = targetIdSelector(existing);
                if (targetId > 0)
                {
                    importedIdMap[sourceIdSelector(sourceItem)] = targetId;
                }
                else
                {
                    deferredMappings.Add((sourceIdSelector(sourceItem), existing));
                }

                table.Updated += 1;
                continue;
            }

            var created = new T();
            copyAction(sourceItem, created);
            target.Add(created);
            existingByKey[key] = created;
            inserted.Add((sourceIdSelector(sourceItem), created));
            table.Inserted += 1;
        }

        if (table.Inserted > 0 || table.Updated > 0)
        {
            await SaveImportedChangesAsync(target, cancellationToken);
        }

        foreach (var item in inserted)
        {
            importedIdMap[item.OldId] = targetIdSelector(item.Entity);
        }

        foreach (var item in deferredMappings)
        {
            importedIdMap[item.OldId] = targetIdSelector(item.Entity);
        }
    }

    private async Task ImportRelationsAsync<T>(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportTableResultDto table,
        Func<PaymentContext, IQueryable<T>> sourceQuery,
        Func<PaymentContext, IQueryable<T>> targetQuery,
        Func<T, string> sourceKeySelector,
        Func<T, string> targetKeySelector,
        Func<T, T> createEntity,
        CancellationToken cancellationToken)
        where T : class
    {
        var sourceItems = await sourceQuery(source).ToListAsync(cancellationToken);
        table.SourceRows = sourceItems.Count;

        var existingKeys = (await targetQuery(target).ToListAsync(cancellationToken))
            .Select(targetKeySelector)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        var toInsert = new List<T>();

        foreach (var sourceItem in sourceItems)
        {
            var key = sourceKeySelector(sourceItem);
            if (string.IsNullOrWhiteSpace(key))
            {
                table.Conflicts += 1;
                continue;
            }

            if (!existingKeys.Add(key))
            {
                table.Skipped += 1;
                continue;
            }

            toInsert.Add(createEntity(sourceItem));
            table.Inserted += 1;
        }

        if (toInsert.Count > 0)
        {
            target.AddRange(toInsert);
            await SaveImportedChangesAsync(target, cancellationToken);
        }
    }
}

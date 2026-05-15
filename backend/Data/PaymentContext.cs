using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PayPlanner.Api.Models;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;

namespace PayPlanner.Api.Data;

/// <summary>
/// Контекст базы данных модуля управления платежами, клиентами и пользователями.
/// Содержит наборы сущностей и конфигурацию индексов для всех доменных моделей.
/// </summary>
public class PaymentContext : DbContext
{
    private static readonly HashSet<string> AuditSystemPropertyNames = new(StringComparer.Ordinal)
    {
        "CreatedAt",
        "CreatedByUserId",
        "CreatedByUserName",
        "UpdatedAt",
        "UpdatedByUserId",
        "UpdatedByUserName",
        "IsDeleted",
        "DeletedAt",
        "DeletedByUserId",
        "DeletedByUserName",
    };

    private static readonly HashSet<string> AuditDetailedDiffExcludedPropertyNames = new(StringComparer.Ordinal)
    {
        "SystemNotes",
        "MetadataJson",
        "PhonesJson",
        "EmailsJson",
        "ContactPhonesJson",
        "ContactEmailsJson",
        "BankAccountsJson",
        "CashRegistersJson",
        "CardsJson",
        "RequisitesEnglishJson",
        "LogoDataUrl",
        "SealDataUrl",
        "DirectorSignatureDataUrl",
    };

    private static readonly JsonSerializerOptions AuditMetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<Type> SoftDeleteAuditedEntityTypes = new()
    {
        typeof(Act),
        typeof(AddressRecord),
        typeof(Client),
        typeof(ClientCase),
        typeof(ClientPassport),
        typeof(Contract),
        typeof(ExternalContact),
        typeof(Invoice),
        typeof(InvoiceLineItem),
        typeof(LegalEntity),
        typeof(MoneyAccount),
        typeof(MoneyOperation),
        typeof(Payment),
        typeof(PowerOfAttorney),
        typeof(CashOrder),
        typeof(CashInventory),
        typeof(TaskJournalItem)
    };

    private static readonly HashSet<Type> MetadataOnlyAuditedEntityTypes = new()
    {
        typeof(Role),
        typeof(User),
    };

    private static readonly HashSet<Type> AuditedEntityTypes = SoftDeleteAuditedEntityTypes
        .Concat(MetadataOnlyAuditedEntityTypes)
        .ToHashSet();

    private sealed record PendingAuditEvent(
        object Entity,
        string ObjectType,
        string Action,
        string? Description,
        int? ActorUserId,
        string? ActorUserName,
        string? MetadataJson,
        DateTime CreatedAt);

    private sealed record AuditFieldChange(
        string Field,
        string? OldValue,
        string? NewValue);

    private sealed record AuditUpdateMetadata(
        IReadOnlyCollection<string> ChangedFields,
        IReadOnlyCollection<AuditFieldChange> Changes,
        string? Reason = null);

    private readonly IHttpContextAccessor? _httpContextAccessor;
    private string? _auditReason;

    internal bool UseObjectAuditModel { get; }

    public void SetAuditReason(string? reason)
    {
        _auditReason = NormalizeAuditString(reason);
    }

    private static DateTime NormalizePostgreSqlTimestamp(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>
    /// Создает новый экземпляр контекста базы данных.
    /// </summary>
    public PaymentContext(
        DbContextOptions options,
        IHttpContextAccessor? httpContextAccessor = null,
        bool useObjectAuditModel = true)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
        UseObjectAuditModel = useObjectAuditModel;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, PaymentContextModelCacheKeyFactory>();
        base.OnConfiguring(optionsBuilder);
    }

    public DbSet<MoneyOperation> MoneyOperations => Set<MoneyOperation>();
    public DbSet<MoneyAccount> MoneyAccounts => Set<MoneyAccount>();
    public DbSet<CashOrder> CashOrders => Set<CashOrder>();
    public DbSet<CashInventory> CashInventories => Set<CashInventory>();
    public DbSet<BankStatementImportBatch> BankStatementImportBatches => Set<BankStatementImportBatch>();
    public DbSet<BankStatementImportRow> BankStatementImportRows => Set<BankStatementImportRow>();
    public DbSet<MoneyBalanceReconciliationResult> MoneyBalanceReconciliationResults => Set<MoneyBalanceReconciliationResult>();

    /// <summary>
    /// Выполняет конфигурацию сущностей, связей и индексов.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var postgresDateTimeConverter = new ValueConverter<DateTime, DateTime>(
            value => NormalizePostgreSqlTimestamp(value),
            value => NormalizePostgreSqlTimestamp(value));

        var postgresNullableDateTimeConverter = new ValueConverter<DateTime?, DateTime?>(
            value => value.HasValue ? NormalizePostgreSqlTimestamp(value.Value) : value,
            value => value.HasValue ? NormalizePostgreSqlTimestamp(value.Value) : value);

        void ConfigureSimpleDictionary<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, string baseIndexName)
            where TEntity : class
        {
            entity.HasKey("Id");
            entity.Property("Name").HasMaxLength(100).IsRequired();
            entity.Property("Description").HasMaxLength(500);
            entity.Property("ColorHex").HasMaxLength(7);
            entity.HasIndex("IsActive").HasDatabaseName($"IX_{baseIndexName}_IsActive");
            entity.HasIndex("Name").HasDatabaseName($"IX_{baseIndexName}_Name");
            entity.HasIndex("IsActive", "Name").HasDatabaseName($"IX_{baseIndexName}_IsActive_Name");
        }

        static void ConfigureAuditDateProperty<TEntity>(
            Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
            string propertyName)
            where TEntity : class
        {
            var property = entity.Metadata.FindProperty(propertyName);

            if (property?.ClrType == typeof(DateTime))
            {
                entity.Property<DateTime>(propertyName);
                return;
            }

            if (property?.ClrType == typeof(DateTime?))
            {
                entity.Property<DateTime?>(propertyName);
                return;
            }

            entity.Property<DateTime?>(propertyName);
        }

        static void ConfigureAudit<TEntity>(ModelBuilder builder, bool enableSoftDelete = true)
            where TEntity : class
        {
            var entity = builder.Entity<TEntity>();
            var name = typeof(TEntity).Name;

            ConfigureAuditDateProperty(entity, "CreatedAt");

            entity.Property<int?>("CreatedByUserId");
            entity.Property<string>("CreatedByUserName").HasMaxLength(256);
            ConfigureAuditDateProperty(entity, "UpdatedAt");
            entity.Property<int?>("UpdatedByUserId");
            entity.Property<string>("UpdatedByUserName").HasMaxLength(256);
            entity.HasIndex("UpdatedAt").HasDatabaseName($"IX_{name}_UpdatedAt_Audit");

            if (!enableSoftDelete)
            {
                return;
            }

            entity.Property<bool>("IsDeleted").HasDefaultValue(false);
            entity.Property<DateTime?>("DeletedAt");
            entity.Property<int?>("DeletedByUserId");
            entity.Property<string>("DeletedByUserName").HasMaxLength(256);

            entity.HasIndex("IsDeleted").HasDatabaseName($"IX_{name}_IsDeleted");
            entity.HasIndex("DeletedAt").HasDatabaseName($"IX_{name}_DeletedAt_Audit");

            var parameter = Expression.Parameter(typeof(TEntity), "entity");
            var isDeleted = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                new[] { typeof(bool) },
                parameter,
                Expression.Constant("IsDeleted"));
            var filter = Expression.Lambda<Func<TEntity, bool>>(
                Expression.Equal(isDeleted, Expression.Constant(false)),
                parameter);

            entity.HasQueryFilter(filter);
        }

        // ------------------ Act ------------------
        modelBuilder.Entity<Act>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.CreationMode).HasMaxLength(32).HasDefaultValue(ActCreationMode.Manual).IsRequired();
            entity.Property(e => e.InvoiceNumber).HasMaxLength(100);
            entity.Property(e => e.CounterpartyInn).HasMaxLength(32);
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.WorkflowStatus).HasMaxLength(32).HasDefaultValue(ActWorkflowStatus.Draft).IsRequired();
            entity.Property(e => e.SentForSigningAt);
            entity.Property(e => e.SignedAt);
            entity.Property(e => e.CancelledAt);
            entity.Property(e => e.CancelledByUserId);
            entity.Property(e => e.CancelledReason).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.Date).HasColumnType("date");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Acts)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LegalEntity)
                  .WithMany(le => le.Acts)
                  .HasForeignKey(e => e.LegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Responsible)
                  .WithMany(u => u.Acts)
                  .HasForeignKey(e => e.ResponsibleId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Invoice)
                  .WithMany(p => p.Acts)
                  .HasForeignKey(e => e.InvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.LineItems)
                  .WithOne(item => item.Act)
                  .HasForeignKey(item => item.ActId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Acts_Date");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_Acts_Status");
            entity.HasIndex(e => e.WorkflowStatus).HasDatabaseName("IX_Acts_WorkflowStatus");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_Acts_ClientId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_Acts_LegalEntityId");
            entity.HasIndex(e => e.ResponsibleId).HasDatabaseName("IX_Acts_ResponsibleId");
            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Acts_Number");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_Acts_InvoiceId");
        });

        modelBuilder.Entity<ActLineItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceKey).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Unit).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,4)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.ActId).HasDatabaseName("IX_ActLineItems_ActId");
            entity.HasIndex(e => new { e.ActId, e.SortOrder }).HasDatabaseName("IX_ActLineItems_ActId_SortOrder");
            entity.HasIndex(e => new { e.ActId, e.SourceKey }).HasDatabaseName("IX_ActLineItems_ActId_SourceKey");
        });

        // ------------------ Contract ------------------
        modelBuilder.Entity<Contract>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.BillingMode).HasMaxLength(32).HasDefaultValue(ContractBillingMode.Fixed).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue(ContractStatus.Draft).IsRequired();
            entity.Property(e => e.SentForSigningAt);
            entity.Property(e => e.ConcludedAt);
            entity.Property(e => e.IsOpenEnded).HasDefaultValue(false);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.ValidUntil).HasColumnType("date");
            entity.Property(e => e.TerminationDate).HasColumnType("date");
            entity.Property(e => e.TerminationReason).HasMaxLength(1000);
            entity.Property(e => e.TerminationTerms).HasMaxLength(2000);
            entity.Property(e => e.TerminationFinancialTerms).HasMaxLength(2000);
            entity.Property(e => e.CancelledAt).HasColumnType("date");
            entity.Property(e => e.CancelledReason).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasMany(e => e.Clients)
                  .WithMany(c => c.Contracts)
                  .UsingEntity<ClientContract>(
                      join => join.HasOne(cc => cc.Client)
                                   .WithMany(c => c.ClientContracts)
                                   .HasForeignKey(cc => cc.ClientId)
                                   .OnDelete(DeleteBehavior.Cascade),
                      join => join.HasOne(cc => cc.Contract)
                                   .WithMany(c => c.ClientContracts)
                                   .HasForeignKey(cc => cc.ContractId)
                                   .OnDelete(DeleteBehavior.Cascade),
                      join =>
                      {
                          join.HasKey(cc => new { cc.ContractId, cc.ClientId });
                          join.ToTable("ClientContracts");
                          join.Property(cc => cc.Comment).HasMaxLength(1000);
                          join.HasIndex(cc => cc.ClientId).HasDatabaseName("IX_ClientContracts_ClientId");
                      });

            entity.HasMany(e => e.LegalEntities)
                  .WithMany(e => e.Contracts)
                  .UsingEntity<LegalEntityContract>(
                      join => join.HasOne(link => link.LegalEntity)
                                  .WithMany(entity => entity.LegalEntityContracts)
                                  .HasForeignKey(link => link.LegalEntityId)
                                  .OnDelete(DeleteBehavior.Cascade),
                      join => join.HasOne(link => link.Contract)
                                  .WithMany(contract => contract.LegalEntityContracts)
                                  .HasForeignKey(link => link.ContractId)
                                  .OnDelete(DeleteBehavior.Cascade),
                      join =>
                      {
                          join.HasKey(link => new { link.ContractId, link.LegalEntityId });
                          join.ToTable("LegalEntityContracts");
                          join.Property(link => link.Comment).HasMaxLength(1000);
                          join.HasIndex(link => link.LegalEntityId).HasDatabaseName("IX_LegalEntityContracts_LegalEntityId");
                      });

            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Contracts_Number");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Contracts_Date");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_Contracts_Status");
            entity.HasMany(e => e.ContractActs)
                  .WithOne(x => x.Contract)
                  .HasForeignKey(x => x.ContractId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ContractInvoices)
                  .WithOne(x => x.Contract)
                  .HasForeignKey(x => x.ContractId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ContractPayments)
                  .WithOne(x => x.Contract)
                  .HasForeignKey(x => x.ContractId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContractAct>(entity =>
        {
            entity.HasKey(x => new { x.ContractId, x.ActId });
            entity.ToTable("ContractActs");
            entity.HasOne(x => x.Act)
                .WithMany()
                .HasForeignKey(x => x.ActId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.ActId).HasDatabaseName("IX_ContractActs_ActId");
        });

        modelBuilder.Entity<ContractInvoice>(entity =>
        {
            entity.HasKey(x => new { x.ContractId, x.InvoiceId });
            entity.ToTable("ContractInvoices");
            entity.HasOne(x => x.Invoice)
                .WithMany()
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.InvoiceId).HasDatabaseName("IX_ContractInvoices_InvoiceId");
        });

        modelBuilder.Entity<ContractPayment>(entity =>
        {
            entity.HasKey(x => new { x.ContractId, x.PaymentId });
            entity.ToTable("ContractPayments");
            entity.HasOne(x => x.Payment)
                .WithMany()
                .HasForeignKey(x => x.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.PaymentId).HasDatabaseName("IX_ContractPayments_PaymentId");
        });

        modelBuilder.Entity<InvoiceClientCase>(entity =>
        {
            entity.HasKey(x => new { x.PaymentId, x.ClientCaseId });
            entity.ToTable("InvoiceClientCases");
            entity.HasOne(x => x.Payment)
                .WithMany(x => x.InvoiceClientCases)
                .HasForeignKey(x => x.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ClientCase)
                .WithMany(x => x.InvoiceClientCases)
                .HasForeignKey(x => x.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.ClientCaseId).HasDatabaseName("IX_InvoiceClientCases_ClientCaseId");
        });

        // ------------------ Invoice ------------------
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Invoices_Number");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Invoices_Date");
            entity.HasIndex(e => new { e.Number, e.Date }).HasDatabaseName("IX_Invoices_Number_Date");
        });

        modelBuilder.Entity<InvoiceLineItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Unit).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne(e => e.Payment)
                .WithMany(e => e.InvoiceLineItems)
                .HasForeignKey(e => e.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Service)
                .WithMany(e => e.InvoiceLineItems)
                .HasForeignKey(e => e.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.PaymentId).HasDatabaseName("IX_InvoiceLineItems_PaymentId");
            entity.HasIndex(e => new { e.PaymentId, e.SortOrder }).HasDatabaseName("IX_InvoiceLineItems_PaymentId_SortOrder");
            entity.HasIndex(e => e.ServiceId).HasDatabaseName("IX_InvoiceLineItems_ServiceId");
        });

        // ------------------ LegalEntity ------------------
        modelBuilder.Entity<LegalEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ShortName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.FullName).HasMaxLength(500);
            entity.Property(e => e.Inn).HasMaxLength(20);
            entity.Property(e => e.Kpp).HasMaxLength(20);
            entity.Property(e => e.Ogrn).HasMaxLength(20);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Phone).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Director).HasMaxLength(200);
            entity.Property(e => e.Country).HasMaxLength(100);
            entity.Property(e => e.BaseCurrency).HasMaxLength(16);
            entity.Property(e => e.ServiceLanguage).HasMaxLength(32);
            entity.Property(e => e.OrganizationForm).HasMaxLength(64);
            entity.Property(e => e.Timezone).HasMaxLength(100);
            entity.Property(e => e.TaxSystem).HasMaxLength(64);
            entity.Property(e => e.TaxSystemMode).HasMaxLength(64);
            entity.Property(e => e.IncomeTaxRate).HasMaxLength(16);
            entity.Property(e => e.IncomeExpenseTaxRate).HasMaxLength(16);
            entity.Property(e => e.Okved).HasMaxLength(32);
            entity.Property(e => e.Okpo).HasMaxLength(32);
            entity.Property(e => e.Okato).HasMaxLength(64);
            entity.Property(e => e.Oktmo).HasMaxLength(32);
            entity.Property(e => e.Okogu).HasMaxLength(32);
            entity.Property(e => e.Okfs).HasMaxLength(16);
            entity.Property(e => e.Okopf).HasMaxLength(32);
            entity.Property(e => e.Pfr).HasMaxLength(32);
            entity.Property(e => e.Fss).HasMaxLength(32);
            entity.Property(e => e.CharterCapital).HasMaxLength(64);
            entity.Property(e => e.TaxRegistrationDate).HasMaxLength(64);
            entity.Property(e => e.TaxOfficeCode).HasMaxLength(16);
            entity.Property(e => e.TaxRegion).HasMaxLength(100);
            entity.Property(e => e.IfnsName).HasMaxLength(300);
            entity.Property(e => e.TaxOfficePhone).HasMaxLength(100);
            entity.Property(e => e.TreasuryInn).HasMaxLength(20);
            entity.Property(e => e.TreasuryKpp).HasMaxLength(20);
            entity.Property(e => e.TreasuryRecipientName).HasMaxLength(300);
            entity.Property(e => e.TreasuryBankBik).HasMaxLength(16);
            entity.Property(e => e.TreasuryBankName).HasMaxLength(300);
            entity.Property(e => e.TreasuryAccountNumber).HasMaxLength(34);
            entity.Property(e => e.TreasuryCorrAccountNumber).HasMaxLength(34);
            entity.Property(e => e.TreasuryKbk).HasMaxLength(32);
            entity.Property(e => e.BankAccountsJson).HasMaxLength(16000);
            entity.Property(e => e.CashRegistersJson).HasMaxLength(8000);
            entity.Property(e => e.CardsJson).HasMaxLength(8000);
            entity.Property(e => e.RequisitesEnglishJson).HasMaxLength(8000);
            entity.Property(e => e.LogoDataUrl).HasMaxLength(12000000);
            entity.Property(e => e.SealDataUrl).HasMaxLength(12000000);
            entity.Property(e => e.DirectorSignatureDataUrl).HasMaxLength(12000000);
            entity.Property(e => e.DocumentDirectorJson).HasMaxLength(8000);
            entity.Property(e => e.ChiefAccountantJson).HasMaxLength(8000);
            entity.Property(e => e.ContactPhonesJson).HasMaxLength(8000);
            entity.Property(e => e.ContactEmailsJson).HasMaxLength(8000);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.IsClientAndSupplier).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasMany(e => e.Clients)
                  .WithOne(c => c.LegalEntity)
                  .HasForeignKey(c => c.LegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(e => e.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ClientStatus)
                  .WithMany()
                  .HasForeignKey(e => e.ClientStatusId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ShortName).HasDatabaseName("IX_LegalEntities_ShortName");
            entity.HasIndex(e => e.Inn).HasDatabaseName("IX_LegalEntities_Inn");
            entity.HasIndex(e => e.Kpp).HasDatabaseName("IX_LegalEntities_Kpp");
            entity.HasIndex(e => e.ClientStatusId).HasDatabaseName("IX_LegalEntities_ClientStatusId");
            entity.HasIndex(e => e.IsClientAndSupplier).HasDatabaseName("IX_LegalEntities_IsClientAndSupplier");
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_LegalEntities_ResponsibleUserId");
        });

        modelBuilder.Entity<Bank>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Bik).HasMaxLength(16);
            entity.Property(e => e.CorrespondentAccount).HasMaxLength(34);
            entity.Property(e => e.City).HasMaxLength(120);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Banks_Name");
            entity.HasIndex(e => e.Bik).HasDatabaseName("IX_Banks_Bik");
            entity.HasIndex(e => new { e.Name, e.Bik }).HasDatabaseName("IX_Banks_Name_Bik");
        });

        // ------------------ ExternalContact ------------------
        modelBuilder.Entity<ExternalContact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Phone).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Position).HasMaxLength(200);
            entity.Property(e => e.PowerOfAttorney).HasMaxLength(500);
            entity.Property(e => e.PhonesJson).HasMaxLength(4000);
            entity.Property(e => e.EmailsJson).HasMaxLength(4000);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.LegalEntity)
                .WithMany()
                .HasForeignKey(e => e.LegalEntityId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ResponsibleUser)
                .WithMany()
                .HasForeignKey(e => e.ResponsibleUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.FullName).HasDatabaseName("IX_ExternalContacts_FullName");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_ExternalContacts_LegalEntityId");
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_ExternalContacts_ResponsibleUserId");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_ExternalContacts_IsActive");
            entity.HasIndex(e => e.SyncToPhonebook).HasDatabaseName("IX_ExternalContacts_SyncToPhonebook");
        });

        // ------------------ Payment ------------------
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.DocumentKind).HasConversion<string>();
            entity.Property(e => e.InvoiceWorkflowStatus).HasMaxLength(32).HasDefaultValue(InvoiceWorkflowStatus.Draft).IsRequired();
            entity.Property(e => e.InvoiceSentForSigningAt);
            entity.Property(e => e.InvoiceSignedAt);
            entity.Property(e => e.InvoiceCancelledAt);
            entity.Property(e => e.InvoiceCancelledByUserId);
            entity.Property(e => e.InvoiceCancelledReason).HasMaxLength(1000);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.CounterpartyName).HasMaxLength(200);
            entity.Property(e => e.ClientCaseReference).HasMaxLength(300);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.IsNotForAccounting).HasDefaultValue(false);
            entity.Property(p => p.SystemNotes).HasMaxLength(4000);
            entity.Property(p => p.RescheduleCount).HasDefaultValue(0);
            entity.Property(p => p.Account).HasMaxLength(120);
            entity.Property(p => p.LegacyAccountNumber).HasMaxLength(120);
            entity.Property(p => p.PaymentOrderNumber).HasMaxLength(100);
            entity.Property(p => p.AccountDate).HasColumnType("date");
            entity.Property(p => p.LegacyAccountDate).HasColumnType("date");
            entity.Property(p => p.Date).HasColumnType("date");
            entity.Property(p => p.PlannedDate).HasColumnType("date");
            entity.Property(p => p.LastPaymentDate).HasColumnType("date");
            entity.Property(p => p.PaidDate).HasColumnType("date");
            entity.Property(p => p.PaymentDocumentDate).HasColumnType("date");
            entity.Property(p => p.PaidAmount).HasColumnType("decimal(18,2)");

            // Связь с клиентом
            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Payments)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ExpenseClient)
                  .WithMany()
                  .HasForeignKey(e => e.ExpenseClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LegalEntity)
                  .WithMany(le => le.Payments)
                  .HasForeignKey(e => e.LegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ExpenseLegalEntity)
                  .WithMany()
                  .HasForeignKey(e => e.ExpenseLegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Contract)
                  .WithMany(c => c.Payments)
                  .HasForeignKey(e => e.ContractId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Invoice)
                  .WithMany(i => i.Payments)
                  .HasForeignKey(e => e.InvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь с делом клиента
            entity.HasOne(e => e.ClientCase)
                  .WithMany(cc => cc.Payments)
                  .HasForeignKey(e => e.ClientCaseId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь с типом сделки
            entity.HasOne(e => e.DealType)
                  .WithMany(d => d.Payments)
                  .HasForeignKey(e => e.DealTypeId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь с типом дохода
            entity.HasOne(e => e.IncomeType)
                  .WithMany(i => i.Payments)
                  .HasForeignKey(e => e.IncomeTypeId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь с источником платежа
            entity.HasOne(e => e.PaymentSource)
                  .WithMany(p => p.Payments)
                  .HasForeignKey(e => e.PaymentSourceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь с услугой из справочника услуг
            entity.HasOne(e => e.Service)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(e => e.ServiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Связь со статусом платежа
            entity.HasOne(e => e.PaymentStatusEntity)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(e => e.PaymentStatusId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Responsibles)
                  .WithOne(pr => pr.Payment)
                  .HasForeignKey(pr => pr.PaymentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.InvoiceClientCases)
                  .WithOne(x => x.Payment)
                  .HasForeignKey(x => x.PaymentId)
                  .OnDelete(DeleteBehavior.Cascade);

            // ---- Индексы Account
            entity.HasIndex(e => e.Account).HasDatabaseName("IX_Payments_Account");
            entity.HasIndex(e => e.LegacyAccountNumber).HasDatabaseName("IX_Payments_LegacyAccountNumber");
            entity.HasIndex(e => e.AccountDate).HasDatabaseName("IX_Payments_AccountDate");
            entity.HasIndex(e => e.LegacyAccountDate).HasDatabaseName("IX_Payments_LegacyAccountDate");
            entity.HasIndex(e => new { e.Account, e.AccountDate }).HasDatabaseName("IX_Payments_Account_AccountDate");
            entity.HasIndex(e => new { e.LegacyAccountNumber, e.LegacyAccountDate }).HasDatabaseName("IX_Payments_LegacyAccount_LegacyAccountDate");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Payments_Date");
            entity.HasIndex(e => e.PlannedDate).HasDatabaseName("IX_Payments_PlannedDate");
            entity.HasIndex(e => e.LastPaymentDate).HasDatabaseName("IX_Payments_LastPaymentDate");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_Payments_CreatedAt");
            entity.HasIndex(e => e.DealTypeId).HasDatabaseName("IX_Payments_DealTypeId");
            entity.HasIndex(e => e.IncomeTypeId).HasDatabaseName("IX_Payments_IncomeTypeId");
            entity.HasIndex(e => e.PaymentSourceId).HasDatabaseName("IX_Payments_PaymentSourceId");
            entity.HasIndex(e => e.ServiceId).HasDatabaseName("IX_Payments_ServiceId");
            entity.HasIndex(e => e.PaymentStatusId).HasDatabaseName("IX_Payments_PaymentStatusId");
            entity.HasIndex(e => e.ContractId).HasDatabaseName("IX_Payments_ContractId");
            entity.HasIndex(e => e.ExpenseClientId).HasDatabaseName("IX_Payments_ExpenseClientId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_Payments_LegalEntityId");
            entity.HasIndex(e => e.ExpenseLegalEntityId).HasDatabaseName("IX_Payments_ExpenseLegalEntityId");
            entity.HasIndex(e => e.DocumentKind).HasDatabaseName("IX_Payments_DocumentKind");
            entity.HasIndex(e => e.InvoiceWorkflowStatus).HasDatabaseName("IX_Payments_InvoiceWorkflowStatus");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_Payments_InvoiceId");
            entity.HasIndex(e => new { e.ClientId, e.Date }).HasDatabaseName("IX_Payments_ClientId_Date");
            entity.HasIndex(e => new { e.ExpenseClientId, e.Date }).HasDatabaseName("IX_Payments_ExpenseClientId_Date");
            entity.HasIndex(e => new { e.ClientCaseId, e.Date }).HasDatabaseName("IX_Payments_ClientCaseId_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Date }).HasDatabaseName("IX_Payments_IsPaid_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Status }).HasDatabaseName("IX_Payments_IsPaid_Status");
            entity.HasIndex(e => new { e.Type, e.Status, e.Date }).HasDatabaseName("IX_Payments_Type_Status_Date");
            entity.HasIndex(e => new { e.ClientId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ClientId_Date_Type_Status");
            entity.HasIndex(e => new { e.ExpenseClientId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ExpenseClientId_Date_Type_Status");
            entity.HasIndex(e => new { e.ClientCaseId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ClientCaseId_Date_Type_Status");
        });

        modelBuilder.Entity<PaymentRemainderTransfer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceOriginalAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PaidAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.RemainderAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.RemainderDate).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.SourcePayment)
                .WithMany()
                .HasForeignKey(e => e.SourcePaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.RemainderPayment)
                .WithMany()
                .HasForeignKey(e => e.RemainderPaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.SourcePaymentId)
                .IsUnique()
                .HasDatabaseName("IX_PaymentRemainderTransfers_SourcePaymentId");

            entity.HasIndex(e => e.RemainderPaymentId)
                .IsUnique()
                .HasDatabaseName("IX_PaymentRemainderTransfers_RemainderPaymentId");

            entity.HasIndex(e => e.RemainderDate)
                .HasDatabaseName("IX_PaymentRemainderTransfers_RemainderDate");
        });

        modelBuilder.Entity<PaymentResponsible>(entity =>
        {
            entity.HasKey(x => new { x.PaymentId, x.UserId });
            entity.ToTable("PaymentResponsibles");

            entity.HasOne(x => x.User)
                  .WithMany(u => u.InvoiceResponsibles)
                  .HasForeignKey(x => x.UserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => x.UserId).HasDatabaseName("IX_PaymentResponsibles_UserId");
        });

        modelBuilder.Entity<ClientBalanceTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.Direction).HasConversion<string>();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ExternalKey).HasMaxLength(160);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);

            entity.HasOne(e => e.Client)
                  .WithMany()
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.LegalEntity)
                  .WithMany()
                  .HasForeignKey(e => e.LegalEntityId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Invoice)
                  .WithMany()
                  .HasForeignKey(e => e.InvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Payment)
                  .WithMany()
                  .HasForeignKey(e => e.PaymentId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.ClientId, e.CreatedAt }).HasDatabaseName("IX_ClientBalanceTransactions_ClientId_CreatedAt");
            entity.HasIndex(e => new { e.LegalEntityId, e.CreatedAt }).HasDatabaseName("IX_ClientBalanceTransactions_LegalEntityId_CreatedAt");
            entity.HasIndex(e => e.PaymentId).HasDatabaseName("IX_ClientBalanceTransactions_PaymentId");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_ClientBalanceTransactions_InvoiceId");
            entity.HasIndex(e => e.ExternalKey).IsUnique().HasDatabaseName("IX_ClientBalanceTransactions_ExternalKey");
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ClientBalanceTransactions_Owner",
                "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)"));
        });

        // ------------------ MoneyAccount ------------------
        modelBuilder.Entity<MoneyAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Currency).HasMaxLength(16).HasDefaultValue("RUB");
            entity.Property(e => e.BankName).HasMaxLength(200);
            entity.Property(e => e.BankAccountNumber).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Name).HasDatabaseName("IX_MoneyAccounts_Name");
            entity.HasIndex(e => e.Source).HasDatabaseName("IX_MoneyAccounts_Source");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_MoneyAccounts_IsActive");
            entity.HasIndex(e => new { e.Source, e.Name }).IsUnique().HasDatabaseName("IX_MoneyAccounts_Source_Name");
        });

        // ------------------ MoneyOperation ------------------
        modelBuilder.Entity<MoneyOperation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>();
            entity.Property(e => e.Direction).HasConversion<string>();
            entity.Property(e => e.AccountingType).HasConversion<string>();
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.VatRate).HasColumnType("decimal(5,2)");
            entity.Property(e => e.VatAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PaidAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(12);
            entity.Property(e => e.AccountName).HasMaxLength(200);
            entity.Property(e => e.CounterpartyName).HasMaxLength(200);
            entity.Property(e => e.CategoryName).HasMaxLength(200);
            entity.Property(e => e.PaymentOrderNumber).HasMaxLength(100);
            entity.Property(e => e.Purpose).HasMaxLength(1000);
            entity.Property(e => e.PaidInvoiceNumber).HasMaxLength(120);
            entity.Property(e => e.ProjectName).HasMaxLength(200);
            entity.Property(e => e.DirectionName).HasMaxLength(200);
            entity.Property(e => e.SalesPointName).HasMaxLength(200);
            entity.Property(e => e.PaidAccountName).HasMaxLength(200);
            entity.Property(e => e.MatchStatus).HasConversion<string>();
            entity.Property(e => e.MatchComment).HasMaxLength(500);
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.RecognitionDate).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.LinkedInvoice)
                  .WithMany()
                  .HasForeignKey(e => e.LinkedInvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.MoneyAccount)
                  .WithMany(account => account.MoneyOperations)
                  .HasForeignKey(e => e.MoneyAccountId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.IncomeType)
                  .WithMany(incomeType => incomeType.MoneyOperations)
                  .HasForeignKey(e => e.IncomeTypeId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.BankStatementImportBatch)
                  .WithMany()
                  .HasForeignKey(e => e.BankStatementImportBatchId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.MoneyAccountId).HasDatabaseName("IX_MoneyOperations_MoneyAccountId");
            entity.HasIndex(e => e.IncomeTypeId).HasDatabaseName("IX_MoneyOperations_IncomeTypeId");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_MoneyOperations_Date");
            entity.HasIndex(e => e.Source).HasDatabaseName("IX_MoneyOperations_Source");
            entity.HasIndex(e => e.Direction).HasDatabaseName("IX_MoneyOperations_Direction");
            entity.HasIndex(e => e.AccountingType).HasDatabaseName("IX_MoneyOperations_AccountingType");
            entity.HasIndex(e => e.LinkedInvoiceId).HasDatabaseName("IX_MoneyOperations_LinkedInvoiceId");
            entity.HasIndex(e => e.MatchStatus).HasDatabaseName("IX_MoneyOperations_MatchStatus");
            entity.HasIndex(e => e.BankStatementImportBatchId).HasDatabaseName("IX_MoneyOperations_BankStatementImportBatchId");
            entity.HasIndex(e => new { e.Source, e.Date }).HasDatabaseName("IX_MoneyOperations_Source_Date");
        });

        modelBuilder.Entity<MoneyBalanceReconciliationResult>(entity =>
        {
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.Source).HasConversion<string>();
            entity.Property(e => e.AccountName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.OpeningBalance).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Income).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Expense).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ClosingBalance).HasColumnType("decimal(18,2)");

            entity.HasIndex(e => e.Date).HasDatabaseName("IX_MoneyBalanceReconciliationResults_Date");
            entity.HasIndex(e => e.CheckedAt).HasDatabaseName("IX_MoneyBalanceReconciliationResults_CheckedAt");
            entity.HasIndex(e => new { e.Source, e.Date }).HasDatabaseName("IX_MoneyBalanceReconciliationResults_Source_Date");
            entity.HasIndex(e => new { e.Source, e.AccountName, e.Date })
                .IsUnique()
                .HasDatabaseName("IX_MoneyBalanceReconciliationResults_Source_Account_Date");
        });

        modelBuilder.Entity<BankStatementImportBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.FileHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ErrorSummary).HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasMany(e => e.Rows)
                  .WithOne(row => row.Batch)
                  .HasForeignKey(row => row.BatchId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_BankStatementImportBatches_CreatedAt");
            entity.HasIndex(e => e.FileHash).HasDatabaseName("IX_BankStatementImportBatches_FileHash");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_BankStatementImportBatches_Status");
        });

        modelBuilder.Entity<BankStatementImportRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ImportKey).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(1000);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.AccountName).HasMaxLength(200);
            entity.Property(e => e.CounterpartyName).HasMaxLength(200);
            entity.Property(e => e.Purpose).HasMaxLength(1000);
            entity.Property(e => e.PaymentOrderNumber).HasMaxLength(100);
            entity.Property(e => e.Direction).HasConversion<string>();
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne(e => e.MoneyOperation)
                  .WithMany()
                  .HasForeignKey(e => e.MoneyOperationId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_BankStatementImportRows_BatchId");
            entity.HasIndex(e => e.MoneyOperationId).HasDatabaseName("IX_BankStatementImportRows_MoneyOperationId");
            entity.HasIndex(e => e.ImportKey).HasDatabaseName("IX_BankStatementImportRows_ImportKey");
        });

        modelBuilder.Entity<CashOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(12);
            entity.Property(e => e.Account).HasMaxLength(120);
            entity.Property(e => e.PayerName).HasMaxLength(200);
            entity.Property(e => e.ReceiverName).HasMaxLength(200);
            entity.Property(e => e.Basis).HasMaxLength(1000);
            entity.Property(e => e.Appendix).HasMaxLength(1000);
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.CancelledReason).HasMaxLength(500);
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Client)
                  .WithMany()
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Payment)
                  .WithMany(p => p.CashOrders)
                  .HasForeignKey(e => e.PaymentId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.MoneyOperation)
                  .WithMany(operation => operation.CashOrders)
                  .HasForeignKey(e => e.MoneyOperationId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Invoice)
                  .WithMany()
                  .HasForeignKey(e => e.InvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.Number).IsUnique().HasDatabaseName("IX_CashOrders_Number");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_CashOrders_Date");
            entity.HasIndex(e => e.Type).HasDatabaseName("IX_CashOrders_Type");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_CashOrders_Status");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_CashOrders_ClientId");
            entity.HasIndex(e => e.PaymentId).HasDatabaseName("IX_CashOrders_PaymentId");
            entity.HasIndex(e => e.MoneyOperationId).HasDatabaseName("IX_CashOrders_MoneyOperationId");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_CashOrders_InvoiceId");
        });

        modelBuilder.Entity<CashInventory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.AccountName).HasMaxLength(120).IsRequired();
            entity.Property(e => e.AccountingBalance).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ActualBalance).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Difference).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.CorrectionCashOrder)
                  .WithMany()
                  .HasForeignKey(e => e.CorrectionCashOrderId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.Number).IsUnique().HasDatabaseName("IX_CashInventories_Number");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_CashInventories_Date");
            entity.HasIndex(e => e.AccountName).HasDatabaseName("IX_CashInventories_AccountName");
            entity.HasIndex(e => e.CorrectionCashOrderId).HasDatabaseName("IX_CashInventories_CorrectionCashOrderId");
        });

        // ------------------ ClientPassport ------------------
        modelBuilder.Entity<ClientPassport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Series).HasMaxLength(20);
            entity.Property(e => e.Number).HasMaxLength(20);
            entity.Property(e => e.IssuedBy).HasMaxLength(500);
            entity.Property(e => e.DepartmentCode).HasMaxLength(20);
            entity.Property(e => e.BirthPlacePassport).HasMaxLength(500);
            entity.Property(e => e.BirthPlaceFias).HasMaxLength(120);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Passports)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_ClientPassports_ClientId");
            entity.HasIndex(e => new { e.ClientId, e.IsPrimary }).HasDatabaseName("IX_ClientPassports_ClientId_IsPrimary");
            entity.HasIndex(e => new { e.ClientId, e.Series, e.Number }).HasDatabaseName("IX_ClientPassports_ClientId_Series_Number");
        });

        // ------------------ PowerOfAttorney ------------------
        modelBuilder.Entity<PowerOfAttorney>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Issuer).HasMaxLength(300);
            entity.Property(e => e.Notes).HasMaxLength(1500);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.PowersOfAttorney)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ClientId, e.IsActive }).HasDatabaseName("IX_PowersOfAttorney_ClientId_IsActive");
            entity.HasIndex(e => e.Number).HasDatabaseName("IX_PowersOfAttorney_Number");
        });

        // ------------------ OkinDictionaryItem ------------------
        modelBuilder.Entity<OkinDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("IX_OkinDictionaryItems_Code");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_OkinDictionaryItems_IsActive_Name");
        });

        // ------------------ OksoDictionaryItem ------------------
        modelBuilder.Entity<OksoDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("IX_OksoDictionaryItems_Code");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_OksoDictionaryItems_IsActive_Name");
        });

        // ------------------ OkpdtrDictionaryItem ------------------
        modelBuilder.Entity<OkpdtrDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("IX_OkpdtrDictionaryItems_Code");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_OkpdtrDictionaryItems_IsActive_Name");
        });

        // ------------------ AddressRecord ------------------
        modelBuilder.Entity<AddressRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Value).HasMaxLength(600).IsRequired();
            entity.Property(e => e.UnrestrictedValue).HasMaxLength(600);
            entity.Property(e => e.PostalCode).HasMaxLength(20);
            entity.Property(e => e.Country).HasMaxLength(120);
            entity.Property(e => e.RegionWithType).HasMaxLength(200);
            entity.Property(e => e.AreaWithType).HasMaxLength(200);
            entity.Property(e => e.CityWithType).HasMaxLength(200);
            entity.Property(e => e.SettlementWithType).HasMaxLength(200);
            entity.Property(e => e.StreetWithType).HasMaxLength(200);
            entity.Property(e => e.House).HasMaxLength(50);
            entity.Property(e => e.Flat).HasMaxLength(50);
            entity.Property(e => e.FiasId).HasMaxLength(60);
            entity.Property(e => e.KladrId).HasMaxLength(60);
            entity.Property(e => e.GeoLat).HasMaxLength(32);
            entity.Property(e => e.GeoLon).HasMaxLength(32);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Addresses)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.LegalEntity)
                  .WithMany(le => le.Addresses)
                  .HasForeignKey(e => e.LegalEntityId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_AddressRecords_ClientId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_AddressRecords_LegalEntityId");
            entity.HasIndex(e => new { e.ClientId, e.Type, e.IsPrimary }).HasDatabaseName("IX_AddressRecords_ClientId_Type_IsPrimary");
            entity.HasIndex(e => new { e.LegalEntityId, e.Type, e.IsPrimary }).HasDatabaseName("IX_AddressRecords_LegalEntityId_Type_IsPrimary");
            entity.HasIndex(e => e.FiasId).HasDatabaseName("IX_AddressRecords_FiasId");
            entity.ToTable(t => t.HasCheckConstraint("CK_AddressRecords_Owner", "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)"));
        });

        // ------------------ Client ------------------
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Company).HasMaxLength(200);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.IsClientAndSupplier).HasDefaultValue(false);

            entity.HasOne(e => e.LegalEntity)
                  .WithMany(le => le.Clients)
                  .HasForeignKey(e => e.LegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ClientStatus)
                  .WithMany(s => s.Clients)
                  .HasForeignKey(e => e.ClientStatusId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(e => e.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // ---- Индексы ----
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Clients_Name");
            entity.HasIndex(e => e.Email).HasDatabaseName("IX_Clients_Email");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_Clients_IsActive");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_Clients_CreatedAt");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_Clients_IsActive_Name");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_Clients_LegalEntityId");
            entity.HasIndex(e => e.ClientStatusId).HasDatabaseName("IX_Clients_ClientStatusId");
            entity.HasIndex(e => e.IsClientAndSupplier).HasDatabaseName("IX_Clients_IsClientAndSupplier");
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_Clients_ResponsibleUserId");
        });

        // ------------------ ClientCase ------------------
        modelBuilder.Entity<ClientCase>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);

            // Связь с клиентом
            entity.HasOne(cc => cc.Client)
                  .WithMany(c => c.Cases)
                  .HasForeignKey(cc => cc.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(cc => cc.LegalEntity)
                  .WithMany(le => le.Cases)
                  .HasForeignKey(cc => cc.LegalEntityId)
                  .OnDelete(DeleteBehavior.Cascade);

            // ---- Индексы ----
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCases_Status");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_ClientCases_CreatedAt");
            entity.HasIndex(e => new { e.ClientId, e.CreatedAt }).HasDatabaseName("IX_ClientCases_ClientId_CreatedAt");
            entity.HasIndex(e => new { e.ClientId, e.Status }).HasDatabaseName("IX_ClientCases_ClientId_Status");
            entity.HasIndex(e => new { e.LegalEntityId, e.CreatedAt }).HasDatabaseName("IX_ClientCases_LegalEntityId_CreatedAt");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("IX_ClientCases_LegalEntityId_Status");
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ClientCases_Owner",
                "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)"));
        });

        // ------------------ DealType ------------------
        modelBuilder.Entity<DealType>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_DealTypes_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_DealTypes_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_DealTypes_IsActive_Name");
        });

        // ------------------ IncomeType ------------------
        modelBuilder.Entity<IncomeType>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);
            entity.Property(e => e.PaymentType).HasConversion<int>().IsRequired().HasDefaultValue(PaymentType.Income);

            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_IncomeTypes_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_IncomeTypes_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_IncomeTypes_IsActive_Name");
            entity.HasIndex(e => new { e.IsActive, e.PaymentType }).HasDatabaseName("IX_IncomeTypes_IsActive_PaymentType");
            entity.HasIndex(e => new { e.IsActive, e.PaymentType, e.Name }).HasDatabaseName("IX_IncomeTypes_IsActive_PaymentType_Name");
        });

        // ------------------ PaymentSource ------------------
        modelBuilder.Entity<PaymentSource>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);
            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_PaymentSources_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_PaymentSources_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_PaymentSources_IsActive_Name");
        });

        // ------------------ ServiceDictionaryItem ------------------
        modelBuilder.Entity<ServiceDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);

            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_ServiceDictionaryItems_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_ServiceDictionaryItems_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_ServiceDictionaryItems_IsActive_Name");
        });

        modelBuilder.Entity<CountryDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "CountryDictionaryItems"));
        modelBuilder.Entity<BaseCurrencyDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "BaseCurrencyDictionaryItems"));
        modelBuilder.Entity<ServiceLanguageDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "ServiceLanguageDictionaryItems"));
        modelBuilder.Entity<OrganizationFormDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "OrganizationFormDictionaryItems"));
        modelBuilder.Entity<TimezoneDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "TimezoneDictionaryItems"));
        modelBuilder.Entity<TaxSystemDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "TaxSystemDictionaryItems"));
        modelBuilder.Entity<TaxSystemModeDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "TaxSystemModeDictionaryItems"));
        modelBuilder.Entity<InvoiceUnitDictionaryItem>(entity => ConfigureSimpleDictionary(entity, "InvoiceUnitDictionaryItems"));
        modelBuilder.Entity<InvoiceItemNameDictionaryItem>(entity =>
        {
            ConfigureSimpleDictionary(entity, "InvoiceItemNameDictionaryItems");
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DefaultPrice).HasPrecision(18, 2);
        });

        // ------------------ ClientStatusEntity ------------------
        modelBuilder.Entity<ClientStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);
            entity.Property(e => e.SystemKey).HasMaxLength(50);

            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_ClientStatuses_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_ClientStatuses_Name");
            entity.HasIndex(e => e.SystemKey).HasDatabaseName("IX_ClientStatuses_SystemKey");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_ClientStatuses_IsActive_Name");
        });

        // ------------------ PaymentStatusEntity ------------------
        modelBuilder.Entity<PaymentStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);

            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_PaymentStatuses_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_PaymentStatuses_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_PaymentStatuses_IsActive_Name");
        });

        // ------------------ DocumentStatusEntity ------------------
        modelBuilder.Entity<DocumentStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(7);

            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("IX_DocumentStatuses_Code");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_DocumentStatuses_IsActive");
            entity.HasIndex(e => e.SortOrder).HasDatabaseName("IX_DocumentStatuses_SortOrder");
            entity.HasIndex(e => new { e.IsActive, e.SortOrder }).HasDatabaseName("IX_DocumentStatuses_IsActive_SortOrder");
        });

        // ================== Roles ==================
        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.PermissionsUpdatedByUserName).HasMaxLength(200);

            // ---- Индексы ----
            entity.HasIndex(e => e.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Roles_Name");
            entity.HasIndex(e => e.PermissionsUpdatedAt)
                  .HasDatabaseName("IX_Roles_PermissionsUpdatedAt");
        });

        // ================== Role Permissions ==================
        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Section).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CanView).HasDefaultValue(false);
            entity.Property(e => e.CanCreate).HasDefaultValue(false);
            entity.Property(e => e.CanEdit).HasDefaultValue(false);
            entity.Property(e => e.CanDelete).HasDefaultValue(false);
            entity.Property(e => e.CanExport).HasDefaultValue(false);
            entity.Property(e => e.CanViewAnalytics).HasDefaultValue(false);
            entity.Property(e => e.ViewScope)
                  .HasMaxLength(32)
                  .HasDefaultValue(DataVisibilityScope.All)
                  .IsRequired();

            entity.HasIndex(e => new { e.RoleId, e.Section })
                  .IsUnique()
                  .HasDatabaseName("IX_RolePermissions_Role_Section");

            entity.HasOne(e => e.Role)
                  .WithMany(r => r.Permissions)
                  .HasForeignKey(e => e.RoleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ================== Users ==================
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);

            var emailProperty = entity.Property(e => e.Email)
                .HasMaxLength(200)
                .IsRequired();

            if (Database.IsSqlite())
            {
                emailProperty.UseCollation("NOCASE");
            }

            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();

            // --- Дополнительные поля профиля ---
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.MiddleName).HasMaxLength(100);

            entity.Property(e => e.PhotoUrl).HasMaxLength(500);
            entity.Property(e => e.PhoneNumber).HasMaxLength(50);

            entity.Property(e => e.WhatsApp).HasMaxLength(100);
            entity.Property(e => e.Telegram).HasMaxLength(100);
            entity.Property(e => e.Instagram).HasMaxLength(100);
            entity.Property(e => e.Messenger).HasMaxLength(100);
            entity.Property(e => e.Viber).HasMaxLength(100);
            entity.Property(e => e.Department).HasMaxLength(120);

            // --- Даты как date ---
            entity.Property(e => e.DateOfBirth).HasColumnType("date");
            entity.Property(e => e.EmploymentStartDate).HasColumnType("date");
            entity.Property(e => e.EmploymentEndDate).HasColumnType("date");

            // --- Флаги и значения по умолчанию ---
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsApproved).HasDefaultValue(false);
            entity.Property(e => e.RequiresFinancialActionReason).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // --- Связи ---
            entity.HasOne(e => e.Role)
                  .WithMany(r => r.Users)
                  .HasForeignKey(e => e.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.DepartmentDictionary)
                  .WithMany(d => d.Users)
                  .HasForeignKey(e => e.DepartmentId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ApprovedBy)
                  .WithMany()
                  .HasForeignKey(e => e.ApprovedByUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // --- Индексы ---
            entity.HasIndex(e => e.Email)
                  .IsUnique()
                  .HasDatabaseName("IX_Users_Email");

            entity.HasIndex(e => e.RoleId).HasDatabaseName("idx_users_role");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("idx_users_active");
            entity.HasIndex(e => e.IsApproved).HasDatabaseName("idx_users_approval");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_users_created");
            entity.HasIndex(e => e.UpdatedAt).HasDatabaseName("idx_users_updated");
            entity.HasIndex(e => e.ApprovedAt).HasDatabaseName("idx_users_approvedAt");
            entity.HasIndex(e => e.IsEmployee).HasDatabaseName("idx_users_isemployee");
            entity.HasIndex(e => e.DepartmentId).HasDatabaseName("idx_users_department_id");
            entity.HasIndex(e => e.Department).HasDatabaseName("idx_users_department");
            entity.HasIndex(e => new { e.EmploymentStartDate, e.EmploymentEndDate })
                  .HasDatabaseName("idx_users_employment_range");

            // --- Проверки целостности ---
            entity.ToTable(tb =>
            {
                tb.HasCheckConstraint("CK_Users_Employment_Range",
                    "(\"EmploymentStartDate\" IS NULL OR \"EmploymentEndDate\" IS NULL OR \"EmploymentStartDate\" <= \"EmploymentEndDate\")");
            });
        });

        modelBuilder.Entity<DepartmentDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ColorHex).HasMaxLength(20).HasDefaultValue("#2563EB");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Departments_Name");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_Departments_IsActive");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_Departments_IsActive_Name");
        });

        modelBuilder.Entity<UserCompanyAccess>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.LegalEntityId });
            entity.ToTable("UserCompanyAccesses");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany(e => e.CompanyAccesses)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.LegalEntity)
                .WithMany(e => e.UserCompanyAccesses)
                .HasForeignKey(e => e.LegalEntityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.LegalEntityId)
                .HasDatabaseName("IX_UserCompanyAccesses_LegalEntityId");
        });

        modelBuilder.Entity<UserExternalIdentity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderUserId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ProviderEmail).HasMaxLength(200);
            entity.Property(e => e.ProviderUsername).HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.AvatarUrl).HasMaxLength(1000);
            entity.Property(e => e.TelegramUserId).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany(u => u.ExternalIdentities)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.Provider, e.ProviderUserId })
                .IsUnique()
                .HasDatabaseName("IX_UserExternalIdentities_Provider_UserId");
            entity.HasIndex(e => new { e.UserId, e.Provider })
                .IsUnique()
                .HasDatabaseName("IX_UserExternalIdentities_User_Provider");
            entity.HasIndex(e => e.UserId)
                .HasDatabaseName("IX_UserExternalIdentities_UserId");
        });

        modelBuilder.Entity<ExternalAuthRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.State).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ReturnUrl).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Intent).HasMaxLength(50).IsRequired().HasDefaultValue("signin");
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.State)
                .IsUnique()
                .HasDatabaseName("IX_ExternalAuthRequests_State");
            entity.HasIndex(e => new { e.Provider, e.Status })
                .HasDatabaseName("IX_ExternalAuthRequests_Provider_Status");
        });

        modelBuilder.Entity<TelegramAuthRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Intent).HasMaxLength(50).IsRequired().HasDefaultValue("signin");
            entity.Property(e => e.TelegramUserId).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.State)
                .IsUnique()
                .HasDatabaseName("IX_TelegramAuthRequests_State");
            entity.HasIndex(e => new { e.Status, e.ExpiresAt })
                .HasDatabaseName("IX_TelegramAuthRequests_Status_ExpiresAt");
        });

        // ------------------ UserActivityLog ------------------
        modelBuilder.Entity<UserActivityLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Section).HasMaxLength(160);
            entity.Property(e => e.ObjectType).HasMaxLength(160);
            entity.Property(e => e.ObjectId).HasMaxLength(160);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.UserEmail).HasMaxLength(256);
            entity.Property(e => e.UserFullName).HasMaxLength(256);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.HttpMethod).HasMaxLength(16);
            entity.Property(e => e.Path).HasMaxLength(512);
            entity.Property(e => e.QueryString).HasMaxLength(512);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.MetadataJson).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_UserActivityLogs_CreatedAt");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_UserActivityLogs_UserId");
            entity.HasIndex(e => e.Category).HasDatabaseName("IX_UserActivityLogs_Category");
            entity.HasIndex(e => e.Action).HasDatabaseName("IX_UserActivityLogs_Action");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_UserActivityLogs_Status");
        });

        modelBuilder.Entity<ObjectAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ObjectType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ActorUserName).HasMaxLength(256);
            entity.Property(e => e.MetadataJson).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.ObjectType, e.ObjectId, e.CreatedAt })
                .HasDatabaseName("IX_ObjectAuditEvents_Object_CreatedAt");
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_ObjectAuditEvents_CreatedAt");
        });

        // ------------------ UserSettings ------------------
        modelBuilder.Entity<UserSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Value).HasColumnType("text").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.UserId, e.Key })
                  .IsUnique()
                  .HasDatabaseName("IX_UserSettings_User_Key");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).HasColumnType("text").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.Key).IsUnique().HasDatabaseName("IX_SystemSettings_Key");
        });

        // ------------------ FormFieldRequirements ------------------
        modelBuilder.Entity<FormFieldRequirement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Section).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FieldKey).HasMaxLength(128).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => new { e.Section, e.FieldKey }).IsUnique().HasDatabaseName("IX_FormFieldRequirements_Section_FieldKey");
        });

        modelBuilder.Entity<SpreadsheetImportBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.SummaryJson).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_SpreadsheetImportBatches_CreatedAt");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_SpreadsheetImportBatches_Status");
        });

        modelBuilder.Entity<SpreadsheetImportBatchFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.FileHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DetectedKind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SheetName).HasMaxLength(128);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne(e => e.Batch)
                .WithMany(e => e.Files)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_SpreadsheetImportBatchFiles_BatchId");
            entity.HasIndex(e => e.FileHash).HasDatabaseName("IX_SpreadsheetImportBatchFiles_FileHash");
        });

        modelBuilder.Entity<SpreadsheetImportBatchOperation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SectionKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceFileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.SourceFileHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SourceRowId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SourceKey).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SourceTitle).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(2000);
            entity.Property(e => e.BeforeJson).HasColumnType("text");
            entity.Property(e => e.AfterJson).HasColumnType("text");
            entity.Property(e => e.MetadataJson).HasColumnType("text");
            entity.Property(e => e.RollbackAction).HasMaxLength(64);
            entity.Property(e => e.RollbackMessage).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne(e => e.Batch)
                .WithMany(e => e.Operations)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_SpreadsheetImportBatchOperations_BatchId");
            entity.HasIndex(e => new { e.BatchId, e.SectionKey }).HasDatabaseName("IX_SpreadsheetImportBatchOperations_BatchId_SectionKey");
            entity.HasIndex(e => new { e.BatchId, e.SourceRowId }).HasDatabaseName("IX_SpreadsheetImportBatchOperations_BatchId_SourceRowId");
            entity.HasIndex(e => e.EntityId).HasDatabaseName("IX_SpreadsheetImportBatchOperations_EntityId");
        });

        modelBuilder.Entity<TaskJournalItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LegalEntity)
                .WithMany()
                .HasForeignKey(e => e.LegalEntityId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ClientCase)
                .WithMany()
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(e => e.ObjectName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.AssigneeName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AuthorName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PlannedDurationMinutes).HasDefaultValue(60);
            entity.Property(e => e.Type).HasMaxLength(32).HasDefaultValue("task").IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(32).HasDefaultValue("normal").IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue("new").IsRequired();
            entity.Property(e => e.Result).HasMaxLength(4000);
            entity.Property(e => e.RemindersJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.ChannelsJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.TagsJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AssigneeUser)
                .WithMany()
                .HasForeignKey(e => e.AssigneeUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.AuthorUser)
                .WithMany()
                .HasForeignKey(e => e.AuthorUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.DueAt).HasDatabaseName("IX_TaskJournalItems_DueAt");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_TaskJournalItems_Status");
            entity.HasIndex(e => e.Type).HasDatabaseName("IX_TaskJournalItems_Type");
            entity.HasIndex(e => e.Priority).HasDatabaseName("IX_TaskJournalItems_Priority");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_TaskJournalItems_ClientId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_TaskJournalItems_LegalEntityId");
            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_TaskJournalItems_ClientCaseId");
            entity.HasIndex(e => e.AssigneeUserId).HasDatabaseName("IX_TaskJournalItems_AssigneeUserId");
            entity.HasIndex(e => e.AuthorUserId).HasDatabaseName("IX_TaskJournalItems_AuthorUserId");
        });

        modelBuilder.Entity<TaskJournalParticipant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasMaxLength(32).HasDefaultValue("assignee").IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(320).HasDefaultValue("").IsRequired();
            entity.Property(e => e.ApprovalStatus).HasMaxLength(32).HasDefaultValue("pending").IsRequired();
            entity.Property(e => e.Resolution).HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany(e => e.Participants)
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.TaskJournalItemId).HasDatabaseName("IX_TaskJournalParticipants_TaskJournalItemId");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_TaskJournalParticipants_UserId");
            entity.HasIndex(e => new { e.TaskJournalItemId, e.Role }).HasDatabaseName("IX_TaskJournalParticipants_Task_Role");
            entity.HasIndex(e => new { e.TaskJournalItemId, e.Role, e.SortOrder }).HasDatabaseName("IX_TaskJournalParticipants_Task_Role_Order");
        });

        modelBuilder.Entity<TaskJournalComment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AuthorName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).HasDefaultValue("comment").IsRequired();
            entity.Property(e => e.Text).HasMaxLength(4000).IsRequired();
            entity.Property(e => e.MetadataJson).HasColumnType("text").HasDefaultValue("{}").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany(e => e.Comments)
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AuthorUser)
                .WithMany()
                .HasForeignKey(e => e.AuthorUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.TaskJournalItemId).HasDatabaseName("IX_TaskJournalComments_TaskJournalItemId");
            entity.HasIndex(e => e.AuthorUserId).HasDatabaseName("IX_TaskJournalComments_AuthorUserId");
            entity.HasIndex(e => new { e.TaskJournalItemId, e.CreatedAt }).HasDatabaseName("IX_TaskJournalComments_Task_CreatedAt");
            entity.HasIndex(e => e.Kind).HasDatabaseName("IX_TaskJournalComments_Kind");
        });

        modelBuilder.Entity<TaskJournalFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DocumentKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.OriginalFileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.StoredFileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.StoragePath).HasMaxLength(600).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(200).HasDefaultValue("application/octet-stream").IsRequired();
            entity.Property(e => e.UploadedByName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.UploadedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany(e => e.Files)
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.UploadedByUser)
                .WithMany()
                .HasForeignKey(e => e.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.TaskJournalItemId).HasDatabaseName("IX_TaskJournalFiles_TaskJournalItemId");
            entity.HasIndex(e => e.UploadedByUserId).HasDatabaseName("IX_TaskJournalFiles_UploadedByUserId");
            entity.HasIndex(e => new { e.TaskJournalItemId, e.DocumentKey, e.Version })
                .IsUnique()
                .HasDatabaseName("IX_TaskJournalFiles_Task_Document_Version");
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventKey).HasMaxLength(240).IsRequired();
            entity.Property(e => e.Channel).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SenderName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RecipientAddress).HasMaxLength(320).IsRequired();
            entity.Property(e => e.Subject).HasMaxLength(300).IsRequired();
            entity.Property(e => e.BodyPreview).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.RecipientUser)
                .WithMany()
                .HasForeignKey(e => e.RecipientUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.NotificationSender)
                .WithMany()
                .HasForeignKey(e => e.NotificationSenderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.EventKey).HasDatabaseName("IX_NotificationDeliveries_EventKey");
            entity.HasIndex(e => new { e.EventKey, e.Channel, e.RecipientUserId, e.NotificationSenderId })
                .HasDatabaseName("IX_NotificationDeliveries_Event_Channel_User_Sender");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_NotificationDeliveries_CreatedAt");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_NotificationDeliveries_Status");
        });

        modelBuilder.Entity<NotificationSender>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Channel).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PurposesJson).IsRequired();
            entity.Property(e => e.ConfigJson).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Channel).HasDatabaseName("IX_NotificationSenders_Channel");
            entity.HasIndex(e => new { e.Channel, e.IsEnabled, e.SortOrder })
                .HasDatabaseName("IX_NotificationSenders_Channel_Enabled_Order");
        });

        modelBuilder.Entity<NotificationBotSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TelegramUserId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Username).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.NotificationSender)
                .WithMany()
                .HasForeignKey(e => e.NotificationSenderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.NotificationSenderId, e.TelegramUserId })
                .HasDatabaseName("IX_NotificationBotSubscriptions_Sender_TelegramUser")
                .IsUnique();
            entity.HasIndex(e => new { e.NotificationSenderId, e.UserId })
                .HasDatabaseName("IX_NotificationBotSubscriptions_Sender_User");
            entity.HasIndex(e => e.ChatId).HasDatabaseName("IX_NotificationBotSubscriptions_ChatId");
        });

        if (UseObjectAuditModel)
        {
            ConfigureAudit<Act>(modelBuilder);
            ConfigureAudit<AddressRecord>(modelBuilder);
            ConfigureAudit<Client>(modelBuilder);
            ConfigureAudit<ClientCase>(modelBuilder);
            ConfigureAudit<ClientPassport>(modelBuilder);
            ConfigureAudit<Contract>(modelBuilder);
            ConfigureAudit<ExternalContact>(modelBuilder);
            ConfigureAudit<Invoice>(modelBuilder);
            ConfigureAudit<InvoiceLineItem>(modelBuilder);
            ConfigureAudit<LegalEntity>(modelBuilder);
            ConfigureAudit<MoneyOperation>(modelBuilder);
            ConfigureAudit<Payment>(modelBuilder);
            ConfigureAudit<PowerOfAttorney>(modelBuilder);
            ConfigureAudit<CashOrder>(modelBuilder);
            ConfigureAudit<CashInventory>(modelBuilder);
            ConfigureAudit<TaskJournalItem>(modelBuilder);
            ConfigureAudit<Role>(modelBuilder, enableSoftDelete: false);
            ConfigureAudit<User>(modelBuilder, enableSoftDelete: false);
        }

        if (Database.IsNpgsql())
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                    if (clrType != typeof(DateTime))
                    {
                        continue;
                    }

                    if (string.Equals(property.GetColumnType(), "date", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    property.SetColumnType("timestamp without time zone");

                    if (property.ClrType == typeof(DateTime))
                    {
                        property.SetValueConverter(postgresDateTimeConverter);
                    }
                    else if (property.ClrType == typeof(DateTime?))
                    {
                        property.SetValueConverter(postgresNullableDateTimeConverter);
                    }
                }
            }

            modelBuilder.Entity<Client>()
                .Property(e => e.Notes)
                .HasColumnType("text");
        }
    }

    public override int SaveChanges()
    {
        var pendingAuditEvents = ApplyAuditMetadata();
        var result = base.SaveChanges();
        PersistAuditEvents(pendingAuditEvents);
        return result;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pendingAuditEvents = ApplyAuditMetadata();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        PersistAuditEvents(pendingAuditEvents, acceptAllChangesOnSuccess);
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        return SaveChangesCoreAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task<int> SaveChangesCoreAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var pendingAuditEvents = ApplyAuditMetadata();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await PersistAuditEventsAsync(pendingAuditEvents, acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    private List<PendingAuditEvent> ApplyAuditMetadata()
    {
        var pendingAuditEvents = new List<PendingAuditEvent>();

        if (!UseObjectAuditModel)
        {
            return pendingAuditEvents;
        }

        var now = DateTime.UtcNow;
        var actor = GetCurrentAuditActor();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (!AuditedEntityTypes.Contains(entry.Metadata.ClrType))
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                SetDateTimeIfDefault(entry, "CreatedAt", now);
                SetAuditActor(entry, "CreatedByUserId", "CreatedByUserName", actor);
                SetNullableDateTime(entry, "UpdatedAt", now);
                SetAuditActor(entry, "UpdatedByUserId", "UpdatedByUserName", actor);
                if (HasSoftDeleteAudit(entry))
                {
                    SetBoolean(entry, "IsDeleted", false);
                }

                QueueAuditEvent(pendingAuditEvents, entry, "Created", "Объект создан", actor, now);
            }
            else if (entry.State == EntityState.Modified)
            {
                var isDeletedProperty = HasSoftDeleteAudit(entry)
                    ? entry.Property("IsDeleted")
                    : null;
                var isRestored =
                    isDeletedProperty?.IsModified == true
                    && isDeletedProperty.OriginalValue is bool originalDeleted
                    && isDeletedProperty.CurrentValue is bool currentDeleted
                    && originalDeleted
                    && !currentDeleted;

                SetNullableDateTime(entry, "UpdatedAt", now);
                SetAuditActor(entry, "UpdatedByUserId", "UpdatedByUserName", actor);

                if (isRestored)
                {
                    QueueAuditEvent(pendingAuditEvents, entry, "Restored", "Объект восстановлен", actor, now);
                    continue;
                }

                var changedFields = GetChangedBusinessPropertyNames(entry);
                if (changedFields.Count > 0)
                {
                    var description = changedFields.Count == 1
                        ? $"Изменено поле: {changedFields[0]}"
                        : $"Изменено полей: {changedFields.Count}";

                    var metadata = new AuditUpdateMetadata(
                        changedFields,
                        GetChangedBusinessPropertyDiffs(entry),
                        _auditReason);

                    QueueAuditEvent(
                        pendingAuditEvents,
                        entry,
                        "Updated",
                        description,
                        actor,
                        now,
                        JsonSerializer.Serialize(metadata, AuditMetadataJsonOptions));
                }
            }
            else if (entry.State == EntityState.Deleted)
            {
                if (HasSoftDeleteAudit(entry))
                {
                    entry.State = EntityState.Modified;
                    SetBoolean(entry, "IsDeleted", true);
                    SetNullableDateTime(entry, "DeletedAt", now);
                    SetAuditActor(entry, "DeletedByUserId", "DeletedByUserName", actor);
                    SetNullableDateTime(entry, "UpdatedAt", now);
                    SetAuditActor(entry, "UpdatedByUserId", "UpdatedByUserName", actor);
                    QueueAuditEvent(pendingAuditEvents, entry, "Deleted", "Объект помечен удаленным", actor, now);
                }
                else
                {
                    QueueAuditEvent(pendingAuditEvents, entry, "Deleted", "Объект удален", actor, now);
                }
            }
        }

        _auditReason = null;

        return pendingAuditEvents;
    }

    private void PersistAuditEvents(IReadOnlyCollection<PendingAuditEvent> pendingAuditEvents, bool acceptAllChangesOnSuccess = true)
    {
        if (pendingAuditEvents.Count == 0)
        {
            return;
        }

        var materializedEvents = MaterializeAuditEvents(pendingAuditEvents);
        if (materializedEvents.Count == 0)
        {
            return;
        }

        Set<ObjectAuditEvent>().AddRange(materializedEvents);
        base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private async Task PersistAuditEventsAsync(
        IReadOnlyCollection<PendingAuditEvent> pendingAuditEvents,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        if (pendingAuditEvents.Count == 0)
        {
            return;
        }

        var materializedEvents = MaterializeAuditEvents(pendingAuditEvents);
        if (materializedEvents.Count == 0)
        {
            return;
        }

        Set<ObjectAuditEvent>().AddRange(materializedEvents);
        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static List<ObjectAuditEvent> MaterializeAuditEvents(IReadOnlyCollection<PendingAuditEvent> pendingAuditEvents)
    {
        var result = new List<ObjectAuditEvent>(pendingAuditEvents.Count);

        foreach (var pendingEvent in pendingAuditEvents)
        {
            var objectId = TryReadEntityId(pendingEvent.Entity);
            if (!objectId.HasValue)
            {
                continue;
            }

            result.Add(new ObjectAuditEvent
            {
                ObjectType = pendingEvent.ObjectType,
                ObjectId = objectId.Value,
                Action = pendingEvent.Action,
                Description = pendingEvent.Description,
                ActorUserId = pendingEvent.ActorUserId,
                ActorUserName = pendingEvent.ActorUserName,
                MetadataJson = pendingEvent.MetadataJson,
                CreatedAt = pendingEvent.CreatedAt,
            });
        }

        return result;
    }

    private static void QueueAuditEvent(
        ICollection<PendingAuditEvent> pendingAuditEvents,
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string action,
        string? description,
        (int? UserId, string? UserName) actor,
        DateTime createdAt,
        string? metadataJson = null)
    {
        var objectType = ResolveAuditObjectType(entry.Metadata.ClrType);
        if (objectType is null)
        {
            return;
        }

        pendingAuditEvents.Add(new PendingAuditEvent(
            entry.Entity,
            objectType,
            action,
            description,
            actor.UserId,
            actor.UserName,
            metadataJson,
            createdAt));
    }

    private static List<string> GetChangedBusinessPropertyNames(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        return entry.Properties
            .Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .Where(name => !AuditSystemPropertyNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<AuditFieldChange> GetChangedBusinessPropertyDiffs(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        return entry.Properties
            .Where(property => property.IsModified)
            .Where(property => !AuditSystemPropertyNames.Contains(property.Metadata.Name))
            .Where(property => !AuditDetailedDiffExcludedPropertyNames.Contains(property.Metadata.Name))
            .Select(property => new AuditFieldChange(
                property.Metadata.Name,
                FormatAuditValue(property.OriginalValue),
                FormatAuditValue(property.CurrentValue)))
            .Where(change =>
                !string.Equals(change.OldValue, change.NewValue, StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(change.OldValue) || !string.IsNullOrWhiteSpace(change.NewValue)))
            .OrderBy(change => change.Field, StringComparer.Ordinal)
            .ToList();
    }

    private static string? FormatAuditValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string stringValue => NormalizeAuditString(stringValue),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            bool boolValue => boolValue ? "Да" : "Нет",
            Enum enumValue => enumValue.ToString(),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
            float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
            Guid guidValue => guidValue.ToString(),
            _ => NormalizeAuditString(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    private static string? NormalizeAuditString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        const int maxLength = 160;

        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}…";
    }

    private static int? TryReadEntityId(object entity)
    {
        var property = entity.GetType().GetProperty("Id");
        if (property?.PropertyType != typeof(int))
        {
            return null;
        }

        var value = property.GetValue(entity);
        return value is int id && id > 0 ? id : null;
    }

    private static string? ResolveAuditObjectType(Type clrType)
    {
        if (clrType == typeof(Client)) return "client";
        if (clrType == typeof(ClientCase)) return "client-case";
        if (clrType == typeof(User)) return "user";
        if (clrType == typeof(Role)) return "role";
        if (clrType == typeof(Payment)) return "payment";
        if (clrType == typeof(Invoice)) return "invoice";
        if (clrType == typeof(InvoiceLineItem)) return "invoice-line-item";
        if (clrType == typeof(Act)) return "act";
        if (clrType == typeof(Contract)) return "contract";
        if (clrType == typeof(LegalEntity)) return "legal-entity";
        if (clrType == typeof(ExternalContact)) return "contact";
        if (clrType == typeof(AddressRecord)) return "address";
        if (clrType == typeof(ClientPassport)) return "client-passport";
        if (clrType == typeof(PowerOfAttorney)) return "power-of-attorney";
        if (clrType == typeof(MoneyOperation)) return "money-operation";
        if (clrType == typeof(CashOrder)) return "cash-order";
        if (clrType == typeof(CashInventory)) return "cash-inventory";
        if (clrType == typeof(TaskJournalItem)) return "task-journal-item";

        return null;
    }

    private static bool HasSoftDeleteAudit(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        => entry.Metadata.FindProperty("IsDeleted") is not null;

    private (int? UserId, string? UserName) GetCurrentAuditActor()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        var userId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
            ? parsed
            : (int?)null;

        var userName = user.FindFirstValue(ClaimTypes.Name)
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? user.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(userName) ? null : userName);
    }

    private static void SetDateTimeIfDefault(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName,
        DateTime value)
    {
        if (entry.Metadata.FindProperty(propertyName) is null)
        {
            return;
        }

        var property = entry.Property(propertyName);
        var clrType = Nullable.GetUnderlyingType(property.Metadata.ClrType) ?? property.Metadata.ClrType;
        if (clrType == typeof(DateTime)
            && (property.CurrentValue is null || property.CurrentValue is DateTime date && date == default))
        {
            property.CurrentValue = value;
        }
    }

    private static void SetNullableDateTime(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName,
        DateTime? value)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).CurrentValue = value;
        }
    }

    private static void SetBoolean(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName,
        bool value)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).CurrentValue = value;
        }
    }

    private static void SetAuditActor(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string userIdPropertyName,
        string userNamePropertyName,
        (int? UserId, string? UserName) actor)
    {
        if (entry.Metadata.FindProperty(userIdPropertyName) is not null)
        {
            entry.Property(userIdPropertyName).CurrentValue = actor.UserId;
        }

        if (entry.Metadata.FindProperty(userNamePropertyName) is not null)
        {
            entry.Property(userNamePropertyName).CurrentValue = actor.UserName;
        }
    }

    /// <summary>
    /// Юридические лица, связанные с клиентами.
    /// </summary>
    public DbSet<LegalEntity> LegalEntities { get; set; }

    public DbSet<ExternalContact> ExternalContacts { get; set; }

    public DbSet<AddressRecord> AddressRecords { get; set; }

    public DbSet<ClientPassport> ClientPassports { get; set; }

    public DbSet<PowerOfAttorney> PowersOfAttorney { get; set; }

    public DbSet<OkinDictionaryItem> OkinDictionaryItems { get; set; }

    public DbSet<OksoDictionaryItem> OksoDictionaryItems { get; set; }

    public DbSet<OkpdtrDictionaryItem> OkpdtrDictionaryItems { get; set; }


    /// <summary>
    /// Дела (кейсы) клиентов.
    /// </summary>
    public DbSet<ClientCase> ClientCases { get; set; }

    /// <summary>
    /// Карточки клиентов.
    /// </summary>
    public DbSet<Client> Clients { get; set; }

    /// <summary>
    /// Акты оказанных услуг.
    /// </summary>
    public DbSet<Act> Acts { get; set; }

    /// <summary>
    /// Договоры с клиентами.
    /// </summary>
    public DbSet<Contract> Contracts { get; set; }

    /// <summary>
    /// Связи клиентов и договоров.
    /// </summary>
    public DbSet<ClientContract> ClientContracts { get; set; }

    public DbSet<LegalEntityContract> LegalEntityContracts { get; set; }

    public DbSet<ContractAct> ContractActs { get; set; }

    public DbSet<ContractInvoice> ContractInvoices { get; set; }

    public DbSet<ContractPayment> ContractPayments { get; set; }

    public DbSet<InvoiceClientCase> InvoiceClientCases { get; set; }

    /// <summary>
    /// Счета.
    /// </summary>
    public DbSet<Invoice> Invoices { get; set; }

    public DbSet<InvoiceLineItem> InvoiceLineItems { get; set; }

    /// <summary>
    /// Типы сделок.
    /// </summary>
    public DbSet<DealType> DealTypes { get; set; }

    /// <summary>
    /// Типы доходов.
    /// </summary>
    public DbSet<IncomeType> IncomeTypes { get; set; }

    /// <summary>
    /// Платежи клиентов.
    /// </summary>
    public DbSet<Payment> Payments { get; set; }

    public DbSet<PaymentRemainderTransfer> PaymentRemainderTransfers { get; set; }

    public DbSet<ClientBalanceTransaction> ClientBalanceTransactions { get; set; }

    /// <summary>
    /// Источники платежей.
    /// </summary>
    public DbSet<PaymentSource> PaymentSources { get; set; }

    /// <summary>
    /// Справочник услуг.
    /// </summary>
    public DbSet<ServiceDictionaryItem> Services { get; set; }
    public DbSet<ServiceCategoryDictionaryItem> ServiceCategories { get; set; }

    public DbSet<CountryDictionaryItem> Countries { get; set; }
    public DbSet<BaseCurrencyDictionaryItem> BaseCurrencies { get; set; }
    public DbSet<ServiceLanguageDictionaryItem> ServiceLanguages { get; set; }
    public DbSet<OrganizationFormDictionaryItem> OrganizationForms { get; set; }
    public DbSet<TimezoneDictionaryItem> Timezones { get; set; }
    public DbSet<TaxSystemDictionaryItem> TaxSystems { get; set; }
    public DbSet<TaxSystemModeDictionaryItem> TaxSystemModes { get; set; }
    public DbSet<InvoiceUnitDictionaryItem> InvoiceUnits { get; set; }
    public DbSet<InvoiceItemNameDictionaryItem> InvoiceItemNames { get; set; }
    public DbSet<DepartmentDictionaryItem> Departments { get; set; }
    public DbSet<Bank> Banks { get; set; }

    /// <summary>
    /// Статусы клиентов.
    /// </summary>
    public DbSet<ClientStatusEntity> ClientStatuses { get; set; }

    /// <summary>
    /// Статусы платежей.
    /// </summary>
    public DbSet<PaymentStatusEntity> PaymentStatuses { get; set; }
    public DbSet<DocumentStatusEntity> DocumentStatuses { get; set; }
    public DbSet<PaymentResponsible> PaymentResponsibles { get; set; }

    /// <summary>
    /// Роли пользователей.
    /// </summary>
    public DbSet<Role> Roles { get; set; }

    /// <summary>
    /// Настройки прав ролей по разделам.
    /// </summary>
    public DbSet<RolePermission> RolePermissions { get; set; }

    /// <summary>
    /// Журнал действий пользователей.
    /// </summary>
    public DbSet<UserActivityLog> UserActivityLogs { get; set; }
    public DbSet<ObjectAuditEvent> ObjectAuditEvents { get; set; }
    public DbSet<TaskJournalItem> TaskJournalItems { get; set; }
    public DbSet<TaskJournalParticipant> TaskJournalParticipants { get; set; }
    public DbSet<TaskJournalComment> TaskJournalComments { get; set; }
    public DbSet<TaskJournalFile> TaskJournalFiles { get; set; }
    public DbSet<NotificationDelivery> NotificationDeliveries { get; set; }
    public DbSet<NotificationSender> NotificationSenders { get; set; }
    public DbSet<NotificationBotSubscription> NotificationBotSubscriptions { get; set; }

    /// <summary>
    /// Пользовательские настройки.
    /// </summary>
    public DbSet<UserSetting> UserSettings { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<FormFieldRequirement> FormFieldRequirements { get; set; }
    public DbSet<SpreadsheetImportBatch> SpreadsheetImportBatches { get; set; }
    public DbSet<SpreadsheetImportBatchFile> SpreadsheetImportBatchFiles { get; set; }
    public DbSet<SpreadsheetImportBatchOperation> SpreadsheetImportBatchOperations { get; set; }
    public DbSet<UserCompanyAccess> UserCompanyAccesses { get; set; }
    public DbSet<UserExternalIdentity> UserExternalIdentities { get; set; }
    public DbSet<ExternalAuthRequest> ExternalAuthRequests { get; set; }
    public DbSet<TelegramAuthRequest> TelegramAuthRequests { get; set; }

    /// <summary>
    /// Пользователи системы.
    /// </summary>
    public DbSet<User> Users { get; set; }
}

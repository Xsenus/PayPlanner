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
        typeof(TaskJournalItem),
        typeof(TaskJournalTemplate),
        typeof(StickyNoteBoard),
        typeof(StickyNote)
    };

    private static readonly HashSet<Type> MetadataOnlyAuditedEntityTypes = new()
    {
        typeof(ClientCaseRelatedParty),
        typeof(ClientCaseWorkOrder),
        typeof(ClientCaseWorkOrderItem),
        typeof(ClientCaseCourtEvent),
        typeof(ClientCaseCourtParty),
        typeof(ClientCaseCourt),
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
    public DbSet<StickyNoteBoard> StickyNoteBoards => Set<StickyNoteBoard>();
    public DbSet<StickyNoteBoardMember> StickyNoteBoardMembers => Set<StickyNoteBoardMember>();
    public DbSet<StickyNote> StickyNotes => Set<StickyNote>();

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
            entity.Property(e => e.Comment);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.OriginalActId);
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

            entity.HasOne(e => e.OriginalAct)
                  .WithMany(e => e.CorrectionActs)
                  .HasForeignKey(e => e.OriginalActId)
                  .OnDelete(DeleteBehavior.Restrict);

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
            entity.HasIndex(e => e.OriginalActId).HasDatabaseName("IX_Acts_OriginalActId");
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
                .WithMany(x => x.ContractActs)
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
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,4)");
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
            entity.Property(e => e.AdvanceReviewStatus).HasConversion<string>().HasMaxLength(32).HasDefaultValue(PaymentAdvanceReviewStatus.None).IsRequired();
            entity.Property(e => e.AdvanceReviewReason).HasMaxLength(1000);
            entity.Property(e => e.AdvanceReviewedByUserName).HasMaxLength(256);
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
            entity.HasIndex(e => e.AdvanceReviewStatus).HasDatabaseName("IX_Payments_AdvanceReviewStatus");
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
            entity.Property(e => e.VatRate).HasColumnType("decimal(18,4)");
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

            entity.HasOne(e => e.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(e => e.ResponsibleUserId)
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
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_MoneyOperations_ResponsibleUserId");
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
            entity.Property(e => e.CaseNumber).HasMaxLength(64);
            entity.Property(e => e.CaseType).HasMaxLength(120);
            entity.Property(e => e.Stage).HasMaxLength(160);
            entity.Property(e => e.FeeAmount).HasPrecision(18, 2);
            entity.Property(e => e.Priority).HasMaxLength(32).HasDefaultValue("normal");
            entity.Property(e => e.CourtName).HasMaxLength(200);
            entity.Property(e => e.CourtCaseNumber).HasMaxLength(100);
            entity.Property(e => e.CourtUrl).HasMaxLength(1000);
            entity.Property(e => e.Claimant).HasMaxLength(300);
            entity.Property(e => e.Defendant).HasMaxLength(300);
            entity.Property(e => e.ThirdParties).HasMaxLength(1000);
            entity.Property(e => e.Representatives).HasMaxLength(1000);
            entity.Property(e => e.JudgeName).HasMaxLength(200);
            entity.Property(e => e.Courtroom).HasMaxLength(120);
            entity.Property(e => e.CourtSecretaryContacts).HasMaxLength(500);
            entity.Property(e => e.DocumentsSummary).HasMaxLength(4000);
            entity.Property(e => e.EventHistory).HasMaxLength(4000);
            entity.Property(e => e.Strategy).HasMaxLength(4000);
            entity.Property(e => e.LastViewedByUserName).HasMaxLength(256);
            entity.Property(e => e.ActivityConfirmedByUserName).HasMaxLength(256);
            entity.Property(e => e.InactivityRemindersEnabled).HasDefaultValue(true);
            entity.Property(e => e.IsUnderControl).HasDefaultValue(false);
            entity.Property(e => e.ControlMarkedByUserName).HasMaxLength(256);
            entity.Property(e => e.IsHiddenFromExecutor).HasDefaultValue(false);
            entity.Property(e => e.HiddenFromExecutorByUserName).HasMaxLength(256);
            entity.Property(e => e.AccessRequestedByUserName).HasMaxLength(256);
            entity.Property(e => e.AccessRequestReason).HasMaxLength(1000);

            // Связь с клиентом
            entity.HasOne(cc => cc.Client)
                  .WithMany(c => c.Cases)
                  .HasForeignKey(cc => cc.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(cc => cc.LegalEntity)
                  .WithMany(le => le.Cases)
                  .HasForeignKey(cc => cc.LegalEntityId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(cc => cc.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(cc => cc.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(cc => cc.ControlMarkedByUser)
                  .WithMany()
                  .HasForeignKey(cc => cc.ControlMarkedByUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // ---- Индексы ----
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCases_Status");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_ClientCases_CreatedAt");
            entity.HasIndex(e => e.CaseNumber).HasDatabaseName("IX_ClientCases_CaseNumber");
            entity.HasIndex(e => e.CaseType).HasDatabaseName("IX_ClientCases_CaseType");
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_ClientCases_ResponsibleUserId");
            entity.HasIndex(e => e.DueDate).HasDatabaseName("IX_ClientCases_DueDate");
            entity.HasIndex(e => e.Priority).HasDatabaseName("IX_ClientCases_Priority");
            entity.HasIndex(e => e.CourtName).HasDatabaseName("IX_ClientCases_CourtName");
            entity.HasIndex(e => e.LastViewedAt).HasDatabaseName("IX_ClientCases_LastViewedAt");
            entity.HasIndex(e => e.InactivityRemindersEnabled).HasDatabaseName("IX_ClientCases_InactivityRemindersEnabled");
            entity.HasIndex(e => e.IsUnderControl).HasDatabaseName("IX_ClientCases_IsUnderControl");
            entity.HasIndex(e => e.ControlMarkedByUserId).HasDatabaseName("IX_ClientCases_ControlMarkedByUserId");
            entity.HasIndex(e => e.IsHiddenFromExecutor).HasDatabaseName("IX_ClientCases_IsHiddenFromExecutor");
            entity.HasIndex(e => e.AccessRequestedAt).HasDatabaseName("IX_ClientCases_AccessRequestedAt");
            entity.HasIndex(e => e.ClaimFiledAt).HasDatabaseName("IX_ClientCases_ClaimFiledAt");
            entity.HasIndex(e => e.CourtDecisionAt).HasDatabaseName("IX_ClientCases_CourtDecisionAt");
            entity.HasIndex(e => e.AppealDeadlineAt).HasDatabaseName("IX_ClientCases_AppealDeadlineAt");
            entity.HasIndex(e => e.CassationDeadlineAt).HasDatabaseName("IX_ClientCases_CassationDeadlineAt");
            entity.HasIndex(e => e.MotionDeadlineAt).HasDatabaseName("IX_ClientCases_MotionDeadlineAt");
            entity.HasIndex(e => e.ObjectionDeadlineAt).HasDatabaseName("IX_ClientCases_ObjectionDeadlineAt");
            entity.HasIndex(e => new { e.ClientId, e.CreatedAt }).HasDatabaseName("IX_ClientCases_ClientId_CreatedAt");
            entity.HasIndex(e => new { e.ClientId, e.Status }).HasDatabaseName("IX_ClientCases_ClientId_Status");
            entity.HasIndex(e => new { e.LegalEntityId, e.CreatedAt }).HasDatabaseName("IX_ClientCases_LegalEntityId_CreatedAt");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("IX_ClientCases_LegalEntityId_Status");
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ClientCases_Owner",
                "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)"));
        });

        // ------------------ ClientCaseDocument ------------------
        modelBuilder.Entity<ClientCaseDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DocumentKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DocumentType).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(120).IsRequired();
            entity.Property(e => e.OriginalFileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.StoredFileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.StoragePath).HasMaxLength(600).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(200).IsRequired();
            entity.Property(e => e.UploadedByName).HasMaxLength(256).IsRequired();

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.Documents)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.UploadedByUser)
                .WithMany()
                .HasForeignKey(e => e.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseDocuments_ClientCaseId");
            entity.HasIndex(e => e.UploadedByUserId).HasDatabaseName("IX_ClientCaseDocuments_UploadedByUserId");
            entity.HasIndex(e => e.DocumentType).HasDatabaseName("IX_ClientCaseDocuments_DocumentType");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCaseDocuments_Status");
            entity.HasIndex(e => new { e.ClientCaseId, e.DocumentKey, e.Version })
                .HasDatabaseName("IX_ClientCaseDocuments_Case_Document_Version");
        });

        // ------------------ ClientCaseRelatedParty ------------------
        modelBuilder.Entity<ClientCaseRelatedParty>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(120);
            entity.Property(e => e.OrganizationName).HasMaxLength(500);
            entity.Property(e => e.Phone).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.ArchivedByUserName).HasMaxLength(256);
            entity.Property(e => e.ArchiveReason).HasMaxLength(1000);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.RelatedParties)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ExternalContact)
                .WithMany()
                .HasForeignKey(e => e.ExternalContactId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LegalEntity)
                .WithMany()
                .HasForeignKey(e => e.LegalEntityId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseRelatedParties_ClientCaseId");
            entity.HasIndex(e => new { e.ClientCaseId, e.IsArchived }).HasDatabaseName("IX_ClientCaseRelatedParties_ClientCaseId_IsArchived");
            entity.HasIndex(e => e.SourceType).HasDatabaseName("IX_ClientCaseRelatedParties_SourceType");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_ClientCaseRelatedParties_ClientId");
            entity.HasIndex(e => e.ExternalContactId).HasDatabaseName("IX_ClientCaseRelatedParties_ExternalContactId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_ClientCaseRelatedParties_LegalEntityId");
        });

        // ------------------ ClientCaseWorkOrder ------------------
        modelBuilder.Entity<ClientCaseWorkOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);
            entity.Property(e => e.ApprovalRequestedByUserName).HasMaxLength(256);
            entity.Property(e => e.ApprovalDecisionByUserName).HasMaxLength(256);
            entity.Property(e => e.ApprovalComment).HasMaxLength(1000);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.WorkOrders)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ClientCaseId)
                .IsUnique()
                .HasDatabaseName("IX_ClientCaseWorkOrders_ClientCaseId");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCaseWorkOrders_Status");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_ClientCaseWorkOrders_CreatedAt");
            entity.HasIndex(e => e.ApprovalRequestedAt).HasDatabaseName("IX_ClientCaseWorkOrders_ApprovalRequestedAt");
            entity.HasIndex(e => e.ApprovalDecisionAt).HasDatabaseName("IX_ClientCaseWorkOrders_ApprovalDecisionAt");
        });

        // ------------------ ClientCaseWorkOrderItem ------------------
        modelBuilder.Entity<ClientCaseWorkOrderItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ServiceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ExecutorName).HasMaxLength(256);
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,4)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");

            entity.HasOne(e => e.WorkOrder)
                .WithMany(e => e.Items)
                .HasForeignKey(e => e.ClientCaseWorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ExecutorUser)
                .WithMany()
                .HasForeignKey(e => e.ExecutorUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseWorkOrderId).HasDatabaseName("IX_ClientCaseWorkOrderItems_WorkOrderId");
            entity.HasIndex(e => e.ExecutorUserId).HasDatabaseName("IX_ClientCaseWorkOrderItems_ExecutorUserId");
            entity.HasIndex(e => e.DueDate).HasDatabaseName("IX_ClientCaseWorkOrderItems_DueDate");
        });

        // ------------------ ClientCaseProceduralDeadline ------------------
        modelBuilder.Entity<ClientCaseProceduralDeadline>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeadlineType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SourceType).HasMaxLength(64);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);
            entity.Property(e => e.ClosedByUserName).HasMaxLength(256);
            entity.Property(e => e.CloseReason).HasMaxLength(1000);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.ProceduralDeadlines)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ClientCaseDocument)
                .WithMany()
                .HasForeignKey(e => e.ClientCaseDocumentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseProceduralDeadlines_ClientCaseId");
            entity.HasIndex(e => e.DueAt).HasDatabaseName("IX_ClientCaseProceduralDeadlines_DueAt");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCaseProceduralDeadlines_Status");
            entity.HasIndex(e => e.DeadlineType).HasDatabaseName("IX_ClientCaseProceduralDeadlines_DeadlineType");
            entity.HasIndex(e => e.TaskJournalItemId).HasDatabaseName("IX_ClientCaseProceduralDeadlines_TaskJournalItemId");
            entity.HasIndex(e => e.ClientCaseDocumentId).HasDatabaseName("IX_ClientCaseProceduralDeadlines_DocumentId");
            entity.HasIndex(e => new { e.ClientCaseId, e.Status, e.DueAt }).HasDatabaseName("IX_ClientCaseProceduralDeadlines_Case_Status_DueAt");
        });

        // ------------------ ClientCaseCourtEvent ------------------
        modelBuilder.Entity<ClientCaseCourtEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CourtInstance).HasMaxLength(64);
            entity.Property(e => e.CourtName).HasMaxLength(200);
            entity.Property(e => e.CourtCaseNumber).HasMaxLength(100);
            entity.Property(e => e.Courtroom).HasMaxLength(120);
            entity.Property(e => e.JudgeName).HasMaxLength(200);
            entity.Property(e => e.SecretaryName).HasMaxLength(200);
            entity.Property(e => e.MovementStatus).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ResultSummary).HasMaxLength(1000);
            entity.Property(e => e.SourceType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);

            entity.HasOne(e => e.Court)
                .WithMany()
                .HasForeignKey(e => e.CourtId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.CourtEvents)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ClientCaseDocument)
                .WithMany()
                .HasForeignKey(e => e.ClientCaseDocumentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ProceduralDeadline)
                .WithMany()
                .HasForeignKey(e => e.ProceduralDeadlineId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseCourtEvents_ClientCaseId");
            entity.HasIndex(e => e.ScheduledAt).HasDatabaseName("IX_ClientCaseCourtEvents_ScheduledAt");
            entity.HasIndex(e => e.EventType).HasDatabaseName("IX_ClientCaseCourtEvents_EventType");
            entity.HasIndex(e => e.MovementStatus).HasDatabaseName("IX_ClientCaseCourtEvents_MovementStatus");
            entity.HasIndex(e => e.CourtId).HasDatabaseName("IX_ClientCaseCourtEvents_CourtId");
            entity.HasIndex(e => e.ClientCaseDocumentId).HasDatabaseName("IX_ClientCaseCourtEvents_DocumentId");
            entity.HasIndex(e => e.ProceduralDeadlineId).HasDatabaseName("IX_ClientCaseCourtEvents_DeadlineId");
            entity.HasIndex(e => new { e.ClientCaseId, e.MovementStatus, e.ScheduledAt }).HasDatabaseName("IX_ClientCaseCourtEvents_Case_Status_ScheduledAt");
        });

        // ------------------ ClientCaseCourt ------------------
        modelBuilder.Entity<ClientCaseCourt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Instance).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(100);
            entity.Property(e => e.Url).HasMaxLength(1000);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Contacts).HasMaxLength(500);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);

            entity.HasIndex(e => e.Name).HasDatabaseName("IX_ClientCaseCourts_Name");
            entity.HasIndex(e => e.Instance).HasDatabaseName("IX_ClientCaseCourts_Instance");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_ClientCaseCourts_IsActive");
            entity.HasIndex(e => new { e.IsActive, e.SortOrder, e.Name }).HasDatabaseName("IX_ClientCaseCourts_Active_Sort_Name");
        });

        // ------------------ ClientCaseCourtInstance ------------------
        modelBuilder.Entity<ClientCaseCourtInstance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Instance).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CourtName).HasMaxLength(200);
            entity.Property(e => e.CourtCaseNumber).HasMaxLength(100);
            entity.Property(e => e.CourtUrl).HasMaxLength(1000);
            entity.Property(e => e.JudgeName).HasMaxLength(200);
            entity.Property(e => e.Courtroom).HasMaxLength(120);
            entity.Property(e => e.SecretaryName).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.CourtInstances)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Court)
                .WithMany()
                .HasForeignKey(e => e.CourtId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseCourtInstances_ClientCaseId");
            entity.HasIndex(e => e.Instance).HasDatabaseName("IX_ClientCaseCourtInstances_Instance");
            entity.HasIndex(e => new { e.ClientCaseId, e.Instance }).IsUnique().HasDatabaseName("IX_ClientCaseCourtInstances_Case_Instance");
            entity.HasIndex(e => e.CourtId).HasDatabaseName("IX_ClientCaseCourtInstances_CourtId");
        });

        // ------------------ ClientCaseCourtComplaint ------------------
        modelBuilder.Entity<ClientCaseCourtComplaint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ComplaintType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TargetInstance).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.CourtComplaints)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CourtInstance)
                .WithMany(e => e.Complaints)
                .HasForeignKey(e => e.CourtInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ClientCaseDocument)
                .WithMany()
                .HasForeignKey(e => e.ClientCaseDocumentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseCourtComplaints_ClientCaseId");
            entity.HasIndex(e => e.CourtInstanceId).HasDatabaseName("IX_ClientCaseCourtComplaints_CourtInstanceId");
            entity.HasIndex(e => e.ClientCaseDocumentId).HasDatabaseName("IX_ClientCaseCourtComplaints_DocumentId");
            entity.HasIndex(e => new { e.ClientCaseId, e.TargetInstance, e.IsArchived }).HasDatabaseName("IX_ClientCaseCourtComplaints_Case_Target_Archived");
        });

        // ------------------ ClientCaseCourtEventHistory ------------------
        modelBuilder.Entity<ClientCaseCourtEventHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Summary).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.ActorUserName).HasMaxLength(256);

            entity.HasOne(e => e.ClientCaseCourtEvent)
                .WithMany(e => e.History)
                .HasForeignKey(e => e.ClientCaseCourtEventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.CourtEventHistory)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ClientCaseCourtEventId).HasDatabaseName("IX_ClientCaseCourtEventHistory_CourtEventId");
            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseCourtEventHistory_ClientCaseId");
            entity.HasIndex(e => e.OccurredAt).HasDatabaseName("IX_ClientCaseCourtEventHistory_OccurredAt");
            entity.HasIndex(e => new { e.ClientCaseId, e.OccurredAt }).HasDatabaseName("IX_ClientCaseCourtEventHistory_Case_OccurredAt");
        });

        // ------------------ ClientCaseCourtParty ------------------
        modelBuilder.Entity<ClientCaseCourtParty>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DisplayName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Side).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProcessRole).HasMaxLength(120).IsRequired();
            entity.Property(e => e.RepresentativeName).HasMaxLength(300);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);

            entity.HasOne(e => e.ClientCase)
                .WithMany(e => e.CourtParties)
                .HasForeignKey(e => e.ClientCaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.RelatedParty)
                .WithMany()
                .HasForeignKey(e => e.RelatedPartyId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_ClientCaseCourtParties_ClientCaseId");
            entity.HasIndex(e => e.RelatedPartyId).HasDatabaseName("IX_ClientCaseCourtParties_RelatedPartyId");
            entity.HasIndex(e => e.Side).HasDatabaseName("IX_ClientCaseCourtParties_Side");
            entity.HasIndex(e => new { e.ClientCaseId, e.Side, e.IsArchived }).HasDatabaseName("IX_ClientCaseCourtParties_Case_Side_Archived");
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
            entity.Property(e => e.Name).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);
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
            entity.Property(e => e.CanEditOthers).HasDefaultValue(false);
            entity.Property(e => e.CanCompleteOthers).HasDefaultValue(false);
            entity.Property(e => e.CanDeleteOthers).HasDefaultValue(false);
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

        // ================== Role Calendar Visible Users ==================
        modelBuilder.Entity<RoleCalendarVisibleUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.CreatedByUserName).HasMaxLength(200);

            entity.HasIndex(e => new { e.RoleId, e.UserId })
                  .IsUnique()
                  .HasDatabaseName("IX_RoleCalendarVisibleUsers_Role_User");

            entity.HasIndex(e => e.UserId)
                  .HasDatabaseName("IX_RoleCalendarVisibleUsers_UserId");

            entity.HasOne(e => e.Role)
                  .WithMany(r => r.CalendarVisibleUsers)
                  .HasForeignKey(e => e.RoleId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany(u => u.CalendarVisibleInRoles)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(e => e.LastActivityAction).HasMaxLength(160);
            entity.Property(e => e.LastActivitySection).HasMaxLength(160);
            entity.Property(e => e.LastActivityIpAddress).HasMaxLength(64);
            entity.Property(e => e.LastActivityUserAgent).HasMaxLength(512);

            // --- Даты как date ---
            entity.Property(e => e.DateOfBirth).HasColumnType("date");
            entity.Property(e => e.EmploymentStartDate).HasColumnType("date");
            entity.Property(e => e.EmploymentEndDate).HasColumnType("date");

            // --- Флаги и значения по умолчанию ---
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsApproved).HasDefaultValue(false);
            entity.Property(e => e.IsExternalContractor).HasDefaultValue(false);
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
            entity.HasIndex(e => e.LastActivityAt).HasDatabaseName("idx_users_last_activity");
            entity.HasIndex(e => e.LastLoginAt).HasDatabaseName("idx_users_last_login");
            entity.HasIndex(e => e.IsEmployee).HasDatabaseName("idx_users_isemployee");
            entity.HasIndex(e => e.IsExternalContractor).HasDatabaseName("idx_users_isexternalcontractor");
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

        modelBuilder.Entity<GoogleCalendarAuthRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ReturnUrl).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue("pending").IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.State)
                .IsUnique()
                .HasDatabaseName("IX_GoogleCalendarAuthRequests_State");
            entity.HasIndex(e => new { e.UserId, e.Status })
                .HasDatabaseName("IX_GoogleCalendarAuthRequests_User_Status");
        });

        modelBuilder.Entity<GoogleCalendarConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GoogleUserId).HasMaxLength(128);
            entity.Property(e => e.GoogleEmail).HasMaxLength(320);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.Scope).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.SyncEnabled).HasDefaultValue(true);
            entity.Property(e => e.CalendarMode).HasMaxLength(32).HasDefaultValue("personal").IsRequired();
            entity.Property(e => e.ConflictResolutionMode).HasMaxLength(32).HasDefaultValue("latestWins").IsRequired();
            entity.Property(e => e.DefaultCalendarId).HasMaxLength(256);
            entity.Property(e => e.DefaultCalendarName).HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId)
                .IsUnique()
                .HasDatabaseName("IX_GoogleCalendarConnections_UserId");
            entity.HasIndex(e => e.GoogleUserId)
                .HasDatabaseName("IX_GoogleCalendarConnections_GoogleUserId");
        });

        modelBuilder.Entity<GoogleCalendarSyncCalendar>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GoogleCalendarId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WatchChannelId).HasMaxLength(128);
            entity.Property(e => e.WatchResourceId).HasMaxLength(256);
            entity.Property(e => e.WatchToken).HasMaxLength(128);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany(e => e.Calendars)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.OwnerUser)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.ConnectionId, e.OwnerUserId })
                .HasDatabaseName("IX_GoogleCalendarSyncCalendars_Connection_Owner");
            entity.HasIndex(e => new { e.ConnectionId, e.GoogleCalendarId })
                .IsUnique()
                .HasDatabaseName("IX_GoogleCalendarSyncCalendars_Connection_GoogleCalendar");
            entity.HasIndex(e => e.WatchChannelId)
                .HasDatabaseName("IX_GoogleCalendarSyncCalendars_WatchChannelId");
        });

        modelBuilder.Entity<GoogleCalendarTaskLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GoogleCalendarId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.GoogleEventId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.GoogleEtag).HasMaxLength(256);
            entity.Property(e => e.LastCrmHash).HasMaxLength(128);
            entity.Property(e => e.LastGoogleHash).HasMaxLength(128);
            entity.Property(e => e.LastDirection).HasMaxLength(32);
            entity.Property(e => e.SyncStatus).HasMaxLength(32).HasDefaultValue("active").IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany(e => e.TaskLinks)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CalendarSyncCalendar)
                .WithMany(e => e.TaskLinks)
                .HasForeignKey(e => e.CalendarSyncCalendarId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ConnectionId, e.TaskJournalItemId })
                .IsUnique()
                .HasDatabaseName("IX_GoogleCalendarTaskLinks_Connection_Task");
            entity.HasIndex(e => new { e.GoogleCalendarId, e.GoogleEventId })
                .HasDatabaseName("IX_GoogleCalendarTaskLinks_GoogleEvent");
            entity.HasIndex(e => e.TaskJournalItemId)
                .HasDatabaseName("IX_GoogleCalendarTaskLinks_TaskJournalItemId");
        });

        modelBuilder.Entity<GoogleCalendarSyncLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Direction).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.DetailsJson).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany()
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.CalendarSyncCalendar)
                .WithMany()
                .HasForeignKey(e => e.CalendarSyncCalendarId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.ConnectionId, e.CreatedAt })
                .HasDatabaseName("IX_GoogleCalendarSyncLogs_Connection_CreatedAt");
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_GoogleCalendarSyncLogs_CreatedAt");
            entity.HasIndex(e => e.TaskJournalItemId)
                .HasDatabaseName("IX_GoogleCalendarSyncLogs_TaskJournalItemId");
        });

        modelBuilder.Entity<YandexCalendarAuthRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ReturnUrl).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue("pending").IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.State)
                .IsUnique()
                .HasDatabaseName("IX_YandexCalendarAuthRequests_State");
            entity.HasIndex(e => new { e.UserId, e.Status })
                .HasDatabaseName("IX_YandexCalendarAuthRequests_User_Status");
        });

        modelBuilder.Entity<YandexCalendarConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.YandexUserId).HasMaxLength(128);
            entity.Property(e => e.YandexEmail).HasMaxLength(320);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.Scope).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.SyncEnabled).HasDefaultValue(true);
            entity.Property(e => e.CalendarMode).HasMaxLength(32).HasDefaultValue("personal").IsRequired();
            entity.Property(e => e.ConflictResolutionMode).HasMaxLength(32).HasDefaultValue("latestWins").IsRequired();
            entity.Property(e => e.DefaultCalendarId).HasMaxLength(512);
            entity.Property(e => e.DefaultCalendarName).HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId)
                .IsUnique()
                .HasDatabaseName("IX_YandexCalendarConnections_UserId");
            entity.HasIndex(e => e.YandexUserId)
                .HasDatabaseName("IX_YandexCalendarConnections_YandexUserId");
        });

        modelBuilder.Entity<YandexCalendarSyncCalendar>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.YandexCalendarId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CalendarUrl).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany(e => e.Calendars)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.OwnerUser)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.ConnectionId, e.OwnerUserId })
                .HasDatabaseName("IX_YandexCalendarSyncCalendars_Connection_Owner");
            entity.HasIndex(e => new { e.ConnectionId, e.YandexCalendarId })
                .IsUnique()
                .HasDatabaseName("IX_YandexCalendarSyncCalendars_Connection_YandexCalendar");
        });

        modelBuilder.Entity<YandexCalendarTaskLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.YandexCalendarId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.YandexEventId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.YandexEventUrl).HasMaxLength(1000);
            entity.Property(e => e.YandexEtag).HasMaxLength(256);
            entity.Property(e => e.LastCrmHash).HasMaxLength(128);
            entity.Property(e => e.LastYandexHash).HasMaxLength(128);
            entity.Property(e => e.LastDirection).HasMaxLength(32);
            entity.Property(e => e.SyncStatus).HasMaxLength(32).HasDefaultValue("active").IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany(e => e.TaskLinks)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CalendarSyncCalendar)
                .WithMany(e => e.TaskLinks)
                .HasForeignKey(e => e.CalendarSyncCalendarId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ConnectionId, e.TaskJournalItemId })
                .IsUnique()
                .HasDatabaseName("IX_YandexCalendarTaskLinks_Connection_Task");
            entity.HasIndex(e => new { e.YandexCalendarId, e.YandexEventId })
                .HasDatabaseName("IX_YandexCalendarTaskLinks_YandexEvent");
            entity.HasIndex(e => e.TaskJournalItemId)
                .HasDatabaseName("IX_YandexCalendarTaskLinks_TaskJournalItemId");
        });

        modelBuilder.Entity<YandexCalendarSyncLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Direction).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.DetailsJson).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Connection)
                .WithMany()
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.CalendarSyncCalendar)
                .WithMany()
                .HasForeignKey(e => e.CalendarSyncCalendarId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.ConnectionId, e.CreatedAt })
                .HasDatabaseName("IX_YandexCalendarSyncLogs_Connection_CreatedAt");
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_YandexCalendarSyncLogs_CreatedAt");
            entity.HasIndex(e => e.TaskJournalItemId)
                .HasDatabaseName("IX_YandexCalendarSyncLogs_TaskJournalItemId");
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

        modelBuilder.Entity<YandexDiskCrmSyncState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DocumentKind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ObjectType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FileName).HasMaxLength(512);
            entity.Property(e => e.YandexDiskPath).HasMaxLength(1024);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.DocumentKind, e.ObjectType, e.ObjectId })
                .IsUnique()
                .HasDatabaseName("IX_YandexDiskCrmSyncStates_Document_Object");
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_YandexDiskCrmSyncStates_Status");
            entity.HasIndex(e => e.LastSyncedAt)
                .HasDatabaseName("IX_YandexDiskCrmSyncStates_LastSyncedAt");
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

        // ------------------ App releases ------------------
        modelBuilder.Entity<AppRelease>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReleaseId).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Summary).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(40).HasDefaultValue("manual").IsRequired();
            entity.Property(e => e.CreatedByUserName).HasMaxLength(256);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(256);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.ReleaseId).IsUnique().HasDatabaseName("IX_AppReleases_ReleaseId");
            entity.HasIndex(e => new { e.IsActive, e.ReleasedAt }).HasDatabaseName("IX_AppReleases_IsActive_ReleasedAt");
        });

        modelBuilder.Entity<AppReleaseItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Text).HasMaxLength(1000).IsRequired();
            entity.HasIndex(e => new { e.AppReleaseId, e.SortOrder }).HasDatabaseName("IX_AppReleaseItems_Release_SortOrder");

            entity.HasOne(e => e.AppRelease)
                  .WithMany(e => e.Items)
                  .HasForeignKey(e => e.AppReleaseId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppReleaseSeen>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SeenAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => new { e.UserId, e.AppReleaseId }).IsUnique().HasDatabaseName("IX_AppReleaseSeen_User_Release");
            entity.HasIndex(e => e.AppReleaseId).HasDatabaseName("IX_AppReleaseSeen_ReleaseId");

            entity.HasOne(e => e.AppRelease)
                  .WithMany(e => e.SeenByUsers)
                  .HasForeignKey(e => e.AppReleaseId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
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

            entity.Property(e => e.ClientBillingClassification).HasMaxLength(32).HasDefaultValue(TaskClientBillingClassification.Legacy).IsRequired();
            entity.Property(e => e.BillingModeSnapshot).HasMaxLength(32);

            entity.HasOne(e => e.BillingContract)
                .WithMany()
                .HasForeignKey(e => e.BillingContractId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.BillingInvoice)
                .WithMany()
                .HasForeignKey(e => e.BillingInvoiceId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(e => e.ObjectName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.AssigneeName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AuthorName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PlannedDurationMinutes).HasDefaultValue(60);
            entity.Property(e => e.ContractorPaymentEnabled).HasDefaultValue(false);
            entity.Property(e => e.ContractorPaymentAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ContractorPaymentDueDate).HasColumnType("date");
            entity.Property(e => e.ContractorPaymentOnResult).HasDefaultValue(false);
            entity.Property(e => e.ContractorPaymentResultDelayDays);
            entity.Property(e => e.Type).HasMaxLength(32).HasDefaultValue("task").IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(32).HasDefaultValue("normal").IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue("new").IsRequired();
            entity.Property(e => e.IsUnderControl).HasDefaultValue(false);
            entity.Property(e => e.ControlMarkedByUserName).HasMaxLength(256);
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

            entity.HasOne(e => e.ControlMarkedByUser)
                .WithMany()
                .HasForeignKey(e => e.ControlMarkedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ContractorPayment)
                .WithMany()
                .HasForeignKey(e => e.ContractorPaymentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.DueAt).HasDatabaseName("IX_TaskJournalItems_DueAt");
            entity.HasIndex(e => e.ApprovalDueAt).HasDatabaseName("IX_TaskJournalItems_ApprovalDueAt");
            entity.HasIndex(e => e.ContractorPaymentId).HasDatabaseName("IX_TaskJournalItems_ContractorPaymentId");
            entity.HasIndex(e => e.ContractorPaymentDueDate).HasDatabaseName("IX_TaskJournalItems_ContractorPaymentDueDate");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_TaskJournalItems_Status");
            entity.HasIndex(e => e.Type).HasDatabaseName("IX_TaskJournalItems_Type");
            entity.HasIndex(e => e.Priority).HasDatabaseName("IX_TaskJournalItems_Priority");
            entity.HasIndex(e => e.IsUnderControl).HasDatabaseName("IX_TaskJournalItems_IsUnderControl");
            entity.HasIndex(e => e.ControlMarkedByUserId).HasDatabaseName("IX_TaskJournalItems_ControlMarkedByUserId");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_TaskJournalItems_ClientId");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_TaskJournalItems_LegalEntityId");
            entity.HasIndex(e => e.ClientCaseId).HasDatabaseName("IX_TaskJournalItems_ClientCaseId");
            entity.HasIndex(e => e.ClientBillingClassification).HasDatabaseName("IX_TaskJournalItems_ClientBillingClassification");
            entity.HasIndex(e => e.BillingContractId).HasDatabaseName("IX_TaskJournalItems_BillingContractId");
            entity.HasIndex(e => e.BillingInvoiceId).HasDatabaseName("IX_TaskJournalItems_BillingInvoiceId");
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

        modelBuilder.Entity<TaskJournalTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.TaskDescription).HasMaxLength(4000);
            entity.Property(e => e.Type).HasMaxLength(32).HasDefaultValue("task").IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(32).HasDefaultValue("normal").IsRequired();
            entity.Property(e => e.PlannedDurationMinutes).HasDefaultValue(60);
            entity.Property(e => e.DueOffsetMinutes).HasDefaultValue(1440);
            entity.Property(e => e.AssigneeName).HasMaxLength(256).HasDefaultValue("").IsRequired();
            entity.Property(e => e.BindingType).HasMaxLength(32).HasDefaultValue("none").IsRequired();
            entity.Property(e => e.BindingKey).HasMaxLength(120).HasDefaultValue("").IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.RemindersJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.ChannelsJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.TagsJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            entity.Property(e => e.CreatedByName).HasMaxLength(256).HasDefaultValue("").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AssigneeUser)
                .WithMany()
                .HasForeignKey(e => e.AssigneeUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.Name).HasDatabaseName("IX_TaskJournalTemplates_Name");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_TaskJournalTemplates_IsActive");
            entity.HasIndex(e => e.Type).HasDatabaseName("IX_TaskJournalTemplates_Type");
            entity.HasIndex(e => e.Priority).HasDatabaseName("IX_TaskJournalTemplates_Priority");
            entity.HasIndex(e => new { e.BindingType, e.BindingKey }).HasDatabaseName("IX_TaskJournalTemplates_Binding");
        });

        modelBuilder.Entity<TaskJournalTemplateParticipant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasMaxLength(32).HasDefaultValue("observer").IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(320).HasDefaultValue("").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.TaskJournalTemplate)
                .WithMany(e => e.Participants)
                .HasForeignKey(e => e.TaskJournalTemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.TaskJournalTemplateId).HasDatabaseName("IX_TaskJournalTemplateParticipants_TemplateId");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_TaskJournalTemplateParticipants_UserId");
            entity.HasIndex(e => new { e.TaskJournalTemplateId, e.Role, e.SortOrder }).HasDatabaseName("IX_TaskJournalTemplateParticipants_Template_Role_Order");
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

        modelBuilder.Entity<TaskJournalDeadlineChangeRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RequestedByName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ReviewerName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).HasDefaultValue("pending").IsRequired();
            entity.Property(e => e.DecisionComment).HasMaxLength(1000);
            entity.Property(e => e.DecidedByName).HasMaxLength(256);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany(e => e.DeadlineChangeRequests)
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.RequestedByUser)
                .WithMany()
                .HasForeignKey(e => e.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ReviewerUser)
                .WithMany()
                .HasForeignKey(e => e.ReviewerUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.DecidedByUser)
                .WithMany()
                .HasForeignKey(e => e.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.CreatedComment)
                .WithMany()
                .HasForeignKey(e => e.CreatedCommentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.DecisionCommentEntry)
                .WithMany()
                .HasForeignKey(e => e.DecisionCommentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.TaskJournalItemId).HasDatabaseName("IX_TaskDeadlineChangeRequests_TaskJournalItemId");
            entity.HasIndex(e => e.RequestedByUserId).HasDatabaseName("IX_TaskDeadlineChangeRequests_RequestedByUserId");
            entity.HasIndex(e => e.ReviewerUserId).HasDatabaseName("IX_TaskDeadlineChangeRequests_ReviewerUserId");
            entity.HasIndex(e => e.DecidedByUserId).HasDatabaseName("IX_TaskDeadlineChangeRequests_DecidedByUserId");
            entity.HasIndex(e => e.CreatedCommentId).HasDatabaseName("IX_TaskDeadlineChangeRequests_CreatedCommentId");
            entity.HasIndex(e => e.DecisionCommentId).HasDatabaseName("IX_TaskDeadlineChangeRequests_DecisionCommentId");
            entity.HasIndex(e => new { e.TaskJournalItemId, e.Status }).HasDatabaseName("IX_TaskDeadlineChangeRequests_Task_Status");
            entity.HasIndex(e => new { e.ReviewerUserId, e.Status, e.CreatedAt }).HasDatabaseName("IX_TaskDeadlineChangeRequests_Reviewer_Status_Created");
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
            entity.Property(e => e.RecipientAddress).HasMaxLength(2048).IsRequired();
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

        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EventKey).HasMaxLength(240).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(300).IsRequired();
            entity.Property(e => e.BodyPreview).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Tone).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ObjectType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.MetadataJson).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.RecipientUser)
                .WithMany()
                .HasForeignKey(e => e.RecipientUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TaskJournalItem)
                .WithMany()
                .HasForeignKey(e => e.TaskJournalItemId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.RecipientUserId, e.IsArchived, e.DeletedAt, e.CreatedAt })
                .HasDatabaseName("IX_UserNotifications_User_Archived_Deleted_Created");
            entity.HasIndex(e => new { e.RecipientUserId, e.IsRead, e.DeletedAt })
                .HasDatabaseName("IX_UserNotifications_User_Read_Deleted");
            entity.HasIndex(e => new { e.RecipientUserId, e.Type, e.DeletedAt })
                .HasDatabaseName("IX_UserNotifications_User_Type_Deleted");
            entity.HasIndex(e => new { e.RecipientUserId, e.EventKey })
                .IsUnique()
                .HasDatabaseName("IX_UserNotifications_User_EventKey");
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

        modelBuilder.Entity<UserPushSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Endpoint).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.P256dh).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Auth).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Browser).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Platform).HasMaxLength(120).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(160).IsRequired();
            entity.Property(e => e.UserAgent).HasMaxLength(512).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Endpoint)
                .IsUnique()
                .HasDatabaseName("IX_UserPushSubscriptions_Endpoint");
            entity.HasIndex(e => new { e.UserId, e.IsActive, e.LastSeenAt })
                .HasDatabaseName("IX_UserPushSubscriptions_User_Active_LastSeen");
            entity.HasIndex(e => e.LastErrorAt)
                .HasDatabaseName("IX_UserPushSubscriptions_LastErrorAt");
        });

        modelBuilder.Entity<StickyNoteBoard>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Visibility).HasMaxLength(24).HasDefaultValue(StickyNoteBoardVisibility.Personal).IsRequired();
            entity.Property(e => e.OwnerUserName).HasMaxLength(256);
            entity.Property(e => e.Background).HasMaxLength(32).HasDefaultValue("cork").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.OwnerUser)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Notes)
                .WithOne(e => e.Board)
                .HasForeignKey(e => e.BoardId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Members)
                .WithOne(e => e.Board)
                .HasForeignKey(e => e.BoardId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Visibility).HasDatabaseName("IX_StickyNoteBoards_Visibility");
            entity.HasIndex(e => e.OwnerUserId).HasDatabaseName("IX_StickyNoteBoards_OwnerUserId");
            entity.HasIndex(e => new { e.Visibility, e.SortOrder }).HasDatabaseName("IX_StickyNoteBoards_Visibility_SortOrder");
        });

        modelBuilder.Entity<StickyNoteBoardMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasMaxLength(24).HasDefaultValue("editor").IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.BoardId, e.UserId })
                .HasDatabaseName("IX_StickyNoteBoardMembers_Board_User")
                .IsUnique();
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_StickyNoteBoardMembers_UserId");
        });

        modelBuilder.Entity<StickyNote>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).HasMaxLength(4000);
            entity.Property(e => e.Color).HasMaxLength(24).HasDefaultValue("yellow").IsRequired();
            entity.Property(e => e.FontSize).HasDefaultValue(14);
            entity.Property(e => e.Opacity).HasDefaultValue(100);
            entity.Property(e => e.AssignedUserName).HasMaxLength(256);
            entity.Property(e => e.AuthorUserName).HasMaxLength(256);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AuthorUser)
                .WithMany()
                .HasForeignKey(e => e.AuthorUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.AssignedUser)
                .WithMany()
                .HasForeignKey(e => e.AssignedUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.BoardId).HasDatabaseName("IX_StickyNotes_BoardId");
            entity.HasIndex(e => e.AssignedUserId).HasDatabaseName("IX_StickyNotes_AssignedUserId");
            entity.HasIndex(e => new { e.ReminderAt, e.ReminderSentAt }).HasDatabaseName("IX_StickyNotes_ReminderAt_ReminderSentAt");
            entity.HasIndex(e => new { e.BoardId, e.ZIndex }).HasDatabaseName("IX_StickyNotes_BoardId_ZIndex");
            entity.HasIndex(e => e.UpdatedAt).HasDatabaseName("IX_StickyNotes_UpdatedAt");
        });

        if (UseObjectAuditModel)
        {
            ConfigureAudit<Act>(modelBuilder);
            ConfigureAudit<AddressRecord>(modelBuilder);
            ConfigureAudit<Client>(modelBuilder);
            ConfigureAudit<ClientCase>(modelBuilder);
            ConfigureAudit<ClientCaseProceduralDeadline>(modelBuilder);
            ConfigureAudit<ClientCaseCourtEvent>(modelBuilder);
            ConfigureAudit<ClientCaseCourtParty>(modelBuilder);
            ConfigureAudit<ClientCaseCourt>(modelBuilder);
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
            modelBuilder.Entity<TaskJournalItem>()
                .HasIndex("IsDeleted", nameof(TaskJournalItem.DueAt), nameof(TaskJournalItem.Id))
                .HasDatabaseName("IX_TaskJournalItems_IsDeleted_DueAt_Id");
            modelBuilder.Entity<TaskJournalItem>()
                .HasIndex("IsDeleted", nameof(TaskJournalItem.Status), nameof(TaskJournalItem.DueAt), nameof(TaskJournalItem.Id))
                .HasDatabaseName("IX_TaskJournalItems_IsDeleted_Status_DueAt_Id");
            ConfigureAudit<TaskJournalTemplate>(modelBuilder);
            ConfigureAudit<StickyNoteBoard>(modelBuilder);
            ConfigureAudit<StickyNote>(modelBuilder);
            ConfigureAudit<Role>(modelBuilder, enableSoftDelete: false);
            ConfigureAudit<User>(modelBuilder, enableSoftDelete: false);
        }

        modelBuilder.Entity<ActLineItem>()
            .HasQueryFilter(item => Acts.Any(parent => parent.Id == item.ActId));
        modelBuilder.Entity<ClientCaseCourtComplaint>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId)
                && ClientCaseCourtInstances.Any(parent => parent.Id == item.CourtInstanceId));
        modelBuilder.Entity<ClientCaseCourtEventHistory>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId)
                && ClientCaseCourtEvents.Any(parent => parent.Id == item.ClientCaseCourtEventId));
        modelBuilder.Entity<ClientCaseCourtInstance>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId));
        modelBuilder.Entity<ClientCaseDocument>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId));
        modelBuilder.Entity<ClientCaseRelatedParty>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId));
        modelBuilder.Entity<ClientCaseWorkOrder>()
            .HasQueryFilter(item => ClientCases.Any(parent => parent.Id == item.ClientCaseId));
        modelBuilder.Entity<ClientCaseWorkOrderItem>()
            .HasQueryFilter(item => ClientCaseWorkOrders.Any(parent => parent.Id == item.ClientCaseWorkOrderId));
        modelBuilder.Entity<ClientContract>()
            .HasQueryFilter(item => Clients.Any(parent => parent.Id == item.ClientId)
                && Contracts.Any(parent => parent.Id == item.ContractId));
        modelBuilder.Entity<ContractAct>()
            .HasQueryFilter(item => Contracts.Any(parent => parent.Id == item.ContractId)
                && Acts.Any(parent => parent.Id == item.ActId));
        modelBuilder.Entity<ContractInvoice>()
            .HasQueryFilter(item => Contracts.Any(parent => parent.Id == item.ContractId)
                && Payments.Any(parent => parent.Id == item.InvoiceId));
        modelBuilder.Entity<ContractPayment>()
            .HasQueryFilter(item => Contracts.Any(parent => parent.Id == item.ContractId)
                && Payments.Any(parent => parent.Id == item.PaymentId));
        modelBuilder.Entity<GoogleCalendarTaskLink>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));
        modelBuilder.Entity<InvoiceClientCase>()
            .HasQueryFilter(item => Payments.Any(parent => parent.Id == item.PaymentId)
                && ClientCases.Any(parent => parent.Id == item.ClientCaseId));
        modelBuilder.Entity<LegalEntityContract>()
            .HasQueryFilter(item => LegalEntities.Any(parent => parent.Id == item.LegalEntityId)
                && Contracts.Any(parent => parent.Id == item.ContractId));
        modelBuilder.Entity<PaymentRemainderTransfer>()
            .HasQueryFilter(item => Payments.Any(parent => parent.Id == item.SourcePaymentId)
                && Payments.Any(parent => parent.Id == item.RemainderPaymentId));
        modelBuilder.Entity<PaymentResponsible>()
            .HasQueryFilter(item => Payments.Any(parent => parent.Id == item.PaymentId));
        modelBuilder.Entity<StickyNoteBoardMember>()
            .HasQueryFilter(item => StickyNoteBoards.Any(parent => parent.Id == item.BoardId));
        modelBuilder.Entity<TaskJournalComment>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));
        modelBuilder.Entity<TaskJournalDeadlineChangeRequest>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));
        modelBuilder.Entity<TaskJournalFile>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));
        modelBuilder.Entity<TaskJournalParticipant>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));
        modelBuilder.Entity<TaskJournalTemplateParticipant>()
            .HasQueryFilter(item => TaskJournalTemplates.Any(parent => parent.Id == item.TaskJournalTemplateId));
        modelBuilder.Entity<UserCompanyAccess>()
            .HasQueryFilter(item => LegalEntities.Any(parent => parent.Id == item.LegalEntityId));
        modelBuilder.Entity<YandexCalendarTaskLink>()
            .HasQueryFilter(item => TaskJournalItems.Any(parent => parent.Id == item.TaskJournalItemId));

        ConfigureDecimalPrecision(modelBuilder);

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

    private static void ConfigureDecimalPrecision(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType != typeof(decimal))
                {
                    continue;
                }

                property.SetPrecision(property.GetPrecision() ?? 18);
                property.SetScale(property.GetScale() ?? GetDecimalScale(property));
            }
        }
    }

    private static int GetDecimalScale(IMutableProperty property)
        => UsesExtendedDecimalScale(property.Name) ? 4 : 2;

    private static bool UsesExtendedDecimalScale(string propertyName)
        => propertyName.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Rate", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Share", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Percent", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Hours", StringComparison.OrdinalIgnoreCase);

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
        if (clrType == typeof(ClientCaseProceduralDeadline)) return "client-case-procedural-deadline";
        if (clrType == typeof(ClientCaseCourtEvent)) return "client-case-court-event";
        if (clrType == typeof(ClientCaseCourtParty)) return "client-case-court-party";
        if (clrType == typeof(ClientCaseCourt)) return "client-case-court";
        if (clrType == typeof(ClientCaseRelatedParty)) return "client-case-related-party";
        if (clrType == typeof(ClientCaseWorkOrder)) return "client-case-work-order";
        if (clrType == typeof(ClientCaseWorkOrderItem)) return "client-case-work-order-item";
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
        if (clrType == typeof(TaskJournalTemplate)) return "task-journal-template";
        if (clrType == typeof(StickyNoteBoard)) return "sticky-note-board";
        if (clrType == typeof(StickyNote)) return "sticky-note";

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
        if (actor.UserId.HasValue && entry.Metadata.FindProperty(userIdPropertyName) is not null)
        {
            entry.Property(userIdPropertyName).CurrentValue = actor.UserId;
        }

        if (!string.IsNullOrWhiteSpace(actor.UserName) && entry.Metadata.FindProperty(userNamePropertyName) is not null)
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

    public DbSet<ClientCaseDocument> ClientCaseDocuments { get; set; }

    public DbSet<ClientCaseRelatedParty> ClientCaseRelatedParties { get; set; }

    public DbSet<ClientCaseWorkOrder> ClientCaseWorkOrders { get; set; }

    public DbSet<ClientCaseWorkOrderItem> ClientCaseWorkOrderItems { get; set; }

    public DbSet<ClientCaseProceduralDeadline> ClientCaseProceduralDeadlines { get; set; }

    public DbSet<ClientCaseCourtEvent> ClientCaseCourtEvents { get; set; }

    public DbSet<ClientCaseCourtEventHistory> ClientCaseCourtEventHistory { get; set; }

    public DbSet<ClientCaseCourtParty> ClientCaseCourtParties { get; set; }

    public DbSet<ClientCaseCourt> ClientCaseCourts { get; set; }

    public DbSet<ClientCaseCourtInstance> ClientCaseCourtInstances { get; set; }

    public DbSet<ClientCaseCourtComplaint> ClientCaseCourtComplaints { get; set; }

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

    public DbSet<RoleCalendarVisibleUser> RoleCalendarVisibleUsers { get; set; }

    /// <summary>
    /// Журнал действий пользователей.
    /// </summary>
    public DbSet<UserActivityLog> UserActivityLogs { get; set; }
    public DbSet<ObjectAuditEvent> ObjectAuditEvents { get; set; }
    public DbSet<YandexDiskCrmSyncState> YandexDiskCrmSyncStates { get; set; }
    public DbSet<TaskJournalItem> TaskJournalItems { get; set; }
    public DbSet<TaskJournalParticipant> TaskJournalParticipants { get; set; }
    public DbSet<TaskJournalComment> TaskJournalComments { get; set; }
    public DbSet<TaskJournalDeadlineChangeRequest> TaskJournalDeadlineChangeRequests { get; set; }
    public DbSet<TaskJournalFile> TaskJournalFiles { get; set; }
    public DbSet<TaskJournalTemplate> TaskJournalTemplates { get; set; }
    public DbSet<TaskJournalTemplateParticipant> TaskJournalTemplateParticipants { get; set; }
    public DbSet<NotificationDelivery> NotificationDeliveries { get; set; }
    public DbSet<UserNotification> UserNotifications { get; set; }
    public DbSet<NotificationSender> NotificationSenders { get; set; }
    public DbSet<NotificationBotSubscription> NotificationBotSubscriptions { get; set; }
    public DbSet<UserPushSubscription> UserPushSubscriptions { get; set; }

    /// <summary>
    /// Пользовательские настройки.
    /// </summary>
    public DbSet<UserSetting> UserSettings { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<AppRelease> AppReleases { get; set; }
    public DbSet<AppReleaseItem> AppReleaseItems { get; set; }
    public DbSet<AppReleaseSeen> AppReleaseSeen { get; set; }
    public DbSet<FormFieldRequirement> FormFieldRequirements { get; set; }
    public DbSet<SpreadsheetImportBatch> SpreadsheetImportBatches { get; set; }
    public DbSet<SpreadsheetImportBatchFile> SpreadsheetImportBatchFiles { get; set; }
    public DbSet<SpreadsheetImportBatchOperation> SpreadsheetImportBatchOperations { get; set; }
    public DbSet<UserCompanyAccess> UserCompanyAccesses { get; set; }
    public DbSet<UserExternalIdentity> UserExternalIdentities { get; set; }
    public DbSet<ExternalAuthRequest> ExternalAuthRequests { get; set; }
    public DbSet<TelegramAuthRequest> TelegramAuthRequests { get; set; }
    public DbSet<GoogleCalendarAuthRequest> GoogleCalendarAuthRequests { get; set; }
    public DbSet<GoogleCalendarConnection> GoogleCalendarConnections { get; set; }
    public DbSet<GoogleCalendarSyncCalendar> GoogleCalendarSyncCalendars { get; set; }
    public DbSet<GoogleCalendarTaskLink> GoogleCalendarTaskLinks { get; set; }
    public DbSet<GoogleCalendarSyncLog> GoogleCalendarSyncLogs { get; set; }
    public DbSet<YandexCalendarAuthRequest> YandexCalendarAuthRequests { get; set; }
    public DbSet<YandexCalendarConnection> YandexCalendarConnections { get; set; }
    public DbSet<YandexCalendarSyncCalendar> YandexCalendarSyncCalendars { get; set; }
    public DbSet<YandexCalendarTaskLink> YandexCalendarTaskLinks { get; set; }
    public DbSet<YandexCalendarSyncLog> YandexCalendarSyncLogs { get; set; }

    /// <summary>
    /// Пользователи системы.
    /// </summary>
    public DbSet<User> Users { get; set; }
}

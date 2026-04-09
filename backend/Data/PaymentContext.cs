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

    private static readonly HashSet<Type> AuditedEntityTypes = new()
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
        typeof(MoneyOperation),
        typeof(Payment),
        typeof(PowerOfAttorney)
    };

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
        IReadOnlyCollection<AuditFieldChange> Changes);

    private readonly IHttpContextAccessor? _httpContextAccessor;

    internal bool UseObjectAuditModel { get; }

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

        static void ConfigureAudit<TEntity>(ModelBuilder builder)
            where TEntity : class
        {
            var entity = builder.Entity<TEntity>();
            var name = typeof(TEntity).Name;

            if (entity.Metadata.FindProperty("CreatedAt") is null)
            {
                entity.Property<DateTime?>("CreatedAt");
            }

            entity.Property<int?>("CreatedByUserId");
            entity.Property<string>("CreatedByUserName").HasMaxLength(256);
            entity.Property<DateTime?>("UpdatedAt");
            entity.Property<int?>("UpdatedByUserId");
            entity.Property<string>("UpdatedByUserName").HasMaxLength(256);
            entity.Property<bool>("IsDeleted").HasDefaultValue(false);
            entity.Property<DateTime?>("DeletedAt");
            entity.Property<int?>("DeletedByUserId");
            entity.Property<string>("DeletedByUserName").HasMaxLength(256);

            entity.HasIndex("IsDeleted").HasDatabaseName($"IX_{name}_IsDeleted");
            entity.HasIndex("UpdatedAt").HasDatabaseName($"IX_{name}_UpdatedAt_Audit");
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
            entity.Property(e => e.InvoiceNumber).HasMaxLength(100);
            entity.Property(e => e.CounterpartyInn).HasMaxLength(32);
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.Date).HasColumnType("date");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Acts)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Responsible)
                  .WithMany(u => u.Acts)
                  .HasForeignKey(e => e.ResponsibleId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Invoice)
                  .WithMany(p => p.Acts)
                  .HasForeignKey(e => e.InvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Acts_Date");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_Acts_Status");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_Acts_ClientId");
            entity.HasIndex(e => e.ResponsibleId).HasDatabaseName("IX_Acts_ResponsibleId");
            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Acts_Number");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("IX_Acts_InvoiceId");
        });

        // ------------------ Contract ------------------
        modelBuilder.Entity<Contract>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.ValidUntil).HasColumnType("date");
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

            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Contracts_Number");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Contracts_Date");
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
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Unit).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne(e => e.Payment)
                .WithMany(e => e.InvoiceLineItems)
                .HasForeignKey(e => e.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.PaymentId).HasDatabaseName("IX_InvoiceLineItems_PaymentId");
            entity.HasIndex(e => new { e.PaymentId, e.SortOrder }).HasDatabaseName("IX_InvoiceLineItems_PaymentId_SortOrder");
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
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasMany(e => e.Clients)
                  .WithOne(c => c.LegalEntity)
                  .HasForeignKey(c => c.LegalEntityId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(e => e.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ShortName).HasDatabaseName("IX_LegalEntities_ShortName");
            entity.HasIndex(e => e.Inn).HasDatabaseName("IX_LegalEntities_Inn");
            entity.HasIndex(e => e.Kpp).HasDatabaseName("IX_LegalEntities_Kpp");
            entity.HasIndex(e => e.ResponsibleUserId).HasDatabaseName("IX_LegalEntities_ResponsibleUserId");
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
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.CounterpartyName).HasMaxLength(200);
            entity.Property(e => e.ClientCaseReference).HasMaxLength(300);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(p => p.SystemNotes).HasMaxLength(4000);
            entity.Property(p => p.RescheduleCount).HasDefaultValue(0);
            entity.Property(p => p.Account).HasMaxLength(120);
            entity.Property(p => p.LegacyAccountNumber).HasMaxLength(120);
            entity.Property(p => p.AccountDate).HasColumnType("date");
            entity.Property(p => p.LegacyAccountDate).HasColumnType("date");
            entity.Property(p => p.Date).HasColumnType("date");
            entity.Property(p => p.PlannedDate).HasColumnType("date");
            entity.Property(p => p.LastPaymentDate).HasColumnType("date");
            entity.Property(p => p.PaidDate).HasColumnType("date");
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

            entity.HasOne(e => e.Contract)
                  .WithMany()
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
            entity.HasIndex(e => e.DocumentKind).HasDatabaseName("IX_Payments_DocumentKind");
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

        // ------------------ MoneyOperation ------------------
        modelBuilder.Entity<MoneyOperation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>();
            entity.Property(e => e.Direction).HasConversion<string>();
            entity.Property(e => e.AccountingType).HasConversion<string>();
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
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
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.RecognitionDate).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Date).HasDatabaseName("IX_MoneyOperations_Date");
            entity.HasIndex(e => e.Source).HasDatabaseName("IX_MoneyOperations_Source");
            entity.HasIndex(e => e.Direction).HasDatabaseName("IX_MoneyOperations_Direction");
            entity.HasIndex(e => new { e.Source, e.Date }).HasDatabaseName("IX_MoneyOperations_Source_Date");
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
            entity.Property(e => e.CityWithType).HasMaxLength(200);
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

            // ---- Индексы ----
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_ClientCases_Status");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_ClientCases_CreatedAt");
            entity.HasIndex(e => new { e.ClientId, e.CreatedAt }).HasDatabaseName("IX_ClientCases_ClientId_CreatedAt");
            entity.HasIndex(e => new { e.ClientId, e.Status }).HasDatabaseName("IX_ClientCases_ClientId_Status");
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
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
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

            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_ClientStatuses_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_ClientStatuses_Name");
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

            // --- Даты как date ---
            entity.Property(e => e.DateOfBirth).HasColumnType("date");
            entity.Property(e => e.EmploymentStartDate).HasColumnType("date");
            entity.Property(e => e.EmploymentEndDate).HasColumnType("date");

            // --- Флаги и значения по умолчанию ---
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsApproved).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // --- Связи ---
            entity.HasOne(e => e.Role)
                  .WithMany(r => r.Users)
                  .HasForeignKey(e => e.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);

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
            entity.HasIndex(e => new { e.EmploymentStartDate, e.EmploymentEndDate })
                  .HasDatabaseName("idx_users_employment_range");

            // --- Проверки целостности ---
            entity.ToTable(tb =>
            {
                tb.HasCheckConstraint("CK_Users_Employment_Range",
                    "(\"EmploymentStartDate\" IS NULL OR \"EmploymentEndDate\" IS NULL OR \"EmploymentStartDate\" <= \"EmploymentEndDate\")");
            });
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
                SetBoolean(entry, "IsDeleted", false);
                QueueAuditEvent(pendingAuditEvents, entry, "Created", "Объект создан", actor, now);
            }
            else if (entry.State == EntityState.Modified)
            {
                var isDeletedProperty = entry.Metadata.FindProperty("IsDeleted") is not null
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
                        GetChangedBusinessPropertyDiffs(entry));

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
                entry.State = EntityState.Modified;
                SetBoolean(entry, "IsDeleted", true);
                SetNullableDateTime(entry, "DeletedAt", now);
                SetAuditActor(entry, "DeletedByUserId", "DeletedByUserName", actor);
                SetNullableDateTime(entry, "UpdatedAt", now);
                SetAuditActor(entry, "UpdatedByUserId", "UpdatedByUserName", actor);
                QueueAuditEvent(pendingAuditEvents, entry, "Deleted", "Объект помечен удаленным", actor, now);
            }
        }

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

        return null;
    }

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

    /// <summary>
    /// Источники платежей.
    /// </summary>
    public DbSet<PaymentSource> PaymentSources { get; set; }

    /// <summary>
    /// Справочник услуг.
    /// </summary>
    public DbSet<ServiceDictionaryItem> Services { get; set; }

    public DbSet<CountryDictionaryItem> Countries { get; set; }
    public DbSet<BaseCurrencyDictionaryItem> BaseCurrencies { get; set; }
    public DbSet<ServiceLanguageDictionaryItem> ServiceLanguages { get; set; }
    public DbSet<OrganizationFormDictionaryItem> OrganizationForms { get; set; }
    public DbSet<TimezoneDictionaryItem> Timezones { get; set; }
    public DbSet<TaxSystemDictionaryItem> TaxSystems { get; set; }
    public DbSet<TaxSystemModeDictionaryItem> TaxSystemModes { get; set; }
    public DbSet<InvoiceUnitDictionaryItem> InvoiceUnits { get; set; }
    public DbSet<InvoiceItemNameDictionaryItem> InvoiceItemNames { get; set; }

    /// <summary>
    /// Статусы клиентов.
    /// </summary>
    public DbSet<ClientStatusEntity> ClientStatuses { get; set; }

    /// <summary>
    /// Статусы платежей.
    /// </summary>
    public DbSet<PaymentStatusEntity> PaymentStatuses { get; set; }
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

    /// <summary>
    /// Пользовательские настройки.
    /// </summary>
    public DbSet<UserSetting> UserSettings { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<FormFieldRequirement> FormFieldRequirements { get; set; }
    public DbSet<UserExternalIdentity> UserExternalIdentities { get; set; }
    public DbSet<ExternalAuthRequest> ExternalAuthRequests { get; set; }
    public DbSet<TelegramAuthRequest> TelegramAuthRequests { get; set; }

    /// <summary>
    /// Пользователи системы.
    /// </summary>
    public DbSet<User> Users { get; set; }
}

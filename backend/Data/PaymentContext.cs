using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Data;

/// <summary>
/// Контекст базы данных модуля управления платежами, клиентами и пользователями.
/// Содержит наборы сущностей и конфигурацию индексов для всех доменных моделей.
/// </summary>
public class PaymentContext : DbContext
{
    /// <summary>
    /// Создает новый экземпляр контекста базы данных.
    /// </summary>
    public PaymentContext(DbContextOptions<PaymentContext> options) : base(options) { }

    public DbSet<MoneyOperation> MoneyOperations => Set<MoneyOperation>();

    /// <summary>
    /// Выполняет конфигурацию сущностей, связей и индексов.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
                  .WithMany()
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

            entity.HasIndex(e => e.ShortName).HasDatabaseName("IX_LegalEntities_ShortName");
            entity.HasIndex(e => e.Inn).HasDatabaseName("IX_LegalEntities_Inn");
            entity.HasIndex(e => e.Kpp).HasDatabaseName("IX_LegalEntities_Kpp");
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
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(p => p.SystemNotes).HasMaxLength(4000);
            entity.Property(p => p.RescheduleCount).HasDefaultValue(0);
            entity.Property(p => p.Account).HasMaxLength(120);
            entity.Property(p => p.AccountDate).HasColumnType("date");
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

            entity.HasOne(e => e.Contract)
                  .WithMany()
                  .HasForeignKey(e => e.ContractId)
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

            // ---- Индексы Account
            entity.HasIndex(e => e.Account).HasDatabaseName("IX_Payments_Account");
            entity.HasIndex(e => e.AccountDate).HasDatabaseName("IX_Payments_AccountDate");
            entity.HasIndex(e => new { e.Account, e.AccountDate }).HasDatabaseName("IX_Payments_Account_AccountDate");
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
            entity.HasIndex(e => e.DocumentKind).HasDatabaseName("IX_Payments_DocumentKind");
            entity.HasIndex(e => new { e.ClientId, e.Date }).HasDatabaseName("IX_Payments_ClientId_Date");
            entity.HasIndex(e => new { e.ClientCaseId, e.Date }).HasDatabaseName("IX_Payments_ClientCaseId_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Date }).HasDatabaseName("IX_Payments_IsPaid_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Status }).HasDatabaseName("IX_Payments_IsPaid_Status");
            entity.HasIndex(e => new { e.Type, e.Status, e.Date }).HasDatabaseName("IX_Payments_Type_Status_Date");
            entity.HasIndex(e => new { e.ClientId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ClientId_Date_Type_Status");
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
            entity.HasCheckConstraint("CK_AddressRecords_Owner", "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)");
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

            // ---- Индексы ----
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Clients_Name");
            entity.HasIndex(e => e.Email).HasDatabaseName("IX_Clients_Email");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_Clients_IsActive");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_Clients_CreatedAt");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_Clients_IsActive_Name");
            entity.HasIndex(e => e.LegalEntityId).HasDatabaseName("IX_Clients_LegalEntityId");
            entity.HasIndex(e => e.ClientStatusId).HasDatabaseName("IX_Clients_ClientStatusId");
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

            // ---- Индексы ----
            entity.HasIndex(e => e.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Roles_Name");
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

            entity.Property(e => e.Email)
                  .HasMaxLength(200)
                  .IsRequired()
                  .UseCollation("NOCASE");

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
                    "(EmploymentStartDate IS NULL OR EmploymentEndDate IS NULL OR EmploymentStartDate <= EmploymentEndDate)");
            });
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
    }

    /// <summary>
    /// Юридические лица, связанные с клиентами.
    /// </summary>
    public DbSet<LegalEntity> LegalEntities { get; set; }

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

    /// <summary>
    /// Пользовательские настройки.
    /// </summary>
    public DbSet<UserSetting> UserSettings { get; set; }

    /// <summary>
    /// Пользователи системы.
    /// </summary>
    public DbSet<User> Users { get; set; }
}

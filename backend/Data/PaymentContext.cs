using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Data;

/// <summary>
/// Контекст базы данных для модуля управления платежами и клиентами.
/// Содержит DbSet для всех основных сущностей и конфигурацию индексов.
/// </summary>
public class PaymentContext : DbContext
{
    /// <summary>
    /// Создаёт новый экземпляр контекста базы данных.
    /// </summary>
    public PaymentContext(DbContextOptions<PaymentContext> options) : base(options) { }

    /// <summary>
    /// Конфигурация сущностей, связей и индексов.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ------------------ Payment ------------------
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(p => p.Account).HasMaxLength(120);

            // Связь с клиентом
            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Payments)
                  .HasForeignKey(e => e.ClientId)
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

            // Связь со статусом платежа
            entity.HasOne(e => e.PaymentStatusEntity)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(e => e.PaymentStatusId)
                  .OnDelete(DeleteBehavior.SetNull);

            // ---- Индексы Account
            entity.HasIndex(e => e.Account).HasDatabaseName("IX_Payments_Account");
            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Payments_Date");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_Payments_CreatedAt");
            entity.HasIndex(e => e.DealTypeId).HasDatabaseName("IX_Payments_DealTypeId");
            entity.HasIndex(e => e.IncomeTypeId).HasDatabaseName("IX_Payments_IncomeTypeId");
            entity.HasIndex(e => e.PaymentSourceId).HasDatabaseName("IX_Payments_PaymentSourceId");
            entity.HasIndex(e => e.PaymentStatusId).HasDatabaseName("IX_Payments_PaymentStatusId");
            entity.HasIndex(e => new { e.ClientId, e.Date }).HasDatabaseName("IX_Payments_ClientId_Date");
            entity.HasIndex(e => new { e.ClientCaseId, e.Date }).HasDatabaseName("IX_Payments_ClientCaseId_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Date }).HasDatabaseName("IX_Payments_IsPaid_Date");
            entity.HasIndex(e => new { e.IsPaid, e.Status }).HasDatabaseName("IX_Payments_IsPaid_Status");
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
            entity.Property(e => e.Notes).HasMaxLength(1000);

            // ---- Индексы ----
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Clients_Name");
            entity.HasIndex(e => e.Email).HasDatabaseName("IX_Clients_Email");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_Clients_IsActive");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_Clients_CreatedAt");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_Clients_IsActive_Name");
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

            // ---- Индексы ----
            entity.HasIndex(e => e.IsActive).HasDatabaseName("IX_IncomeTypes_IsActive");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_IncomeTypes_Name");
            entity.HasIndex(e => new { e.IsActive, e.Name }).HasDatabaseName("IX_IncomeTypes_IsActive_Name");
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
    }

    /// <summary>
    /// Дела/кейсы клиентов.
    /// </summary>
    public DbSet<ClientCase> ClientCases { get; set; }

    /// <summary>
    /// Клиенты.
    /// </summary>
    public DbSet<Client> Clients { get; set; }

    /// <summary>
    /// Типы сделок.
    /// </summary>
    public DbSet<DealType> DealTypes { get; set; }

    /// <summary>
    /// Типы доходов.
    /// </summary>
    public DbSet<IncomeType> IncomeTypes { get; set; }

    /// <summary>
    /// Платежи.
    /// </summary>
    public DbSet<Payment> Payments { get; set; }

    /// <summary>
    /// Источники платежей.
    /// </summary>
    public DbSet<PaymentSource> PaymentSources { get; set; }

    /// <summary>
    /// Статусы платежей.
    /// </summary>
    public DbSet<PaymentStatusEntity> PaymentStatuses { get; set; }
}

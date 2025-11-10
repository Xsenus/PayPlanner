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

            entity.HasIndex(e => e.Date).HasDatabaseName("IX_Acts_Date");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_Acts_Status");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("IX_Acts_ClientId");
            entity.HasIndex(e => e.ResponsibleId).HasDatabaseName("IX_Acts_ResponsibleId");
            entity.HasIndex(e => e.Number).HasDatabaseName("IX_Acts_Number");
        });

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
            entity.Property(p => p.AccountDate).HasColumnType("date");

            // Ñâÿçü ñ êëèåíòîì
            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Payments)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ñâÿçü ñ äåëîì êëèåíòà
            entity.HasOne(e => e.ClientCase)
                  .WithMany(cc => cc.Payments)
                  .HasForeignKey(e => e.ClientCaseId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ñâÿçü ñ òèïîì ñäåëêè
            entity.HasOne(e => e.DealType)
                  .WithMany(d => d.Payments)
                  .HasForeignKey(e => e.DealTypeId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ñâÿçü ñ òèïîì äîõîäà
            entity.HasOne(e => e.IncomeType)
                  .WithMany(i => i.Payments)
                  .HasForeignKey(e => e.IncomeTypeId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ñâÿçü ñ èñòî÷íèêîì ïëàòåæà
            entity.HasOne(e => e.PaymentSource)
                  .WithMany(p => p.Payments)
                  .HasForeignKey(e => e.PaymentSourceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Ñâÿçü ñî ñòàòóñîì ïëàòåæà
            entity.HasOne(e => e.PaymentStatusEntity)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(e => e.PaymentStatusId)
                  .OnDelete(DeleteBehavior.SetNull);

            // ---- Èíäåêñû Account
            entity.HasIndex(e => e.Account).HasDatabaseName("IX_Payments_Account");
            entity.HasIndex(e => e.AccountDate).HasDatabaseName("IX_Payments_AccountDate");
            entity.HasIndex(e => new { e.Account, e.AccountDate }).HasDatabaseName("IX_Payments_Account_AccountDate");
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
            entity.HasIndex(e => new { e.Type, e.Status, e.Date }).HasDatabaseName("IX_Payments_Type_Status_Date");
            entity.HasIndex(e => new { e.ClientId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ClientId_Date_Type_Status");
            entity.HasIndex(e => new { e.ClientCaseId, e.Date, e.Type, e.Status }).HasDatabaseName("IX_Payments_ClientCaseId_Date_Type_Status");
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

            // ---- Èíäåêñû ----
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

            // Ñâÿçü ñ êëèåíòîì
            entity.HasOne(cc => cc.Client)
                  .WithMany(c => c.Cases)
                  .HasForeignKey(cc => cc.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            // ---- Èíäåêñû ----
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

            // ---- Èíäåêñû ----
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

            // ---- Èíäåêñû ----
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

            // ---- Èíäåêñû ----
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

            // ---- Èíäåêñû ----
            entity.HasIndex(e => e.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Roles_Name");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Section).HasMaxLength(100).IsRequired();

            entity.HasIndex(e => new { e.RoleId, e.Section })
                  .IsUnique()
                  .HasDatabaseName("IX_RolePermissions_RoleId_Section");

            entity.Property(e => e.CanView).HasDefaultValue(true);
            entity.Property(e => e.CanCreate).HasDefaultValue(true);
            entity.Property(e => e.CanEdit).HasDefaultValue(true);
            entity.Property(e => e.CanDelete).HasDefaultValue(true);
            entity.Property(e => e.CanExport).HasDefaultValue(true);

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

            // --- äîïîëíèòåëüíûå ïîëÿ ïðîôèëÿ ---
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

            // --- äàòû êàê date ---
            entity.Property(e => e.DateOfBirth).HasColumnType("date");
            entity.Property(e => e.EmploymentStartDate).HasColumnType("date");
            entity.Property(e => e.EmploymentEndDate).HasColumnType("date");

            // --- ôëàãè/äåôîëòû ---
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsApproved).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // --- ñâÿçè ---
            entity.HasOne(e => e.Role)
                  .WithMany(r => r.Users)
                  .HasForeignKey(e => e.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ApprovedBy)
                  .WithMany()
                  .HasForeignKey(e => e.ApprovedByUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // --- èíäåêñû ---
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

            // --- ïðîâåðêè öåëîñòíîñòè ---
            entity.ToTable(tb =>
            {
                tb.HasCheckConstraint("CK_Users_Employment_Range",
                    "(EmploymentStartDate IS NULL OR EmploymentEndDate IS NULL OR EmploymentStartDate <= EmploymentEndDate)");
            });
        });
    }

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

    public DbSet<RolePermission> RolePermissions { get; set; }

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
    /// Статусы платежей.
    /// </summary>
    public DbSet<PaymentStatusEntity> PaymentStatuses { get; set; }

    /// <summary>
    /// Роли пользователей.
    /// </summary>
    public DbSet<Role> Roles { get; set; }

    /// <summary>
    /// Пользователи системы.
    /// </summary>
    public DbSet<User> Users { get; set; }
}

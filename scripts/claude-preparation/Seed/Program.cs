using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

// Preparation utility only. Never opens a caller-provided database or copies production.
var repository = Path.GetFullPath(args.Length == 0 ? Environment.CurrentDirectory : args[0]);
if (!File.Exists(Path.Combine(repository, "global.json")) || !File.Exists(Path.Combine(repository, "AGENTS.md")))
    throw new InvalidOperationException("Укажите корень репозитория PayPlanner.");
var state = Path.Combine(repository, ".codex-build", "claude-synthetic");
Directory.CreateDirectory(state);
for (var entry = new DirectoryInfo(state); entry != null; entry = entry.Parent)
    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidOperationException("Каталог среды не должен содержать symlink/junction.");
var database = Path.Combine(state, "synthetic.db");
if (File.Exists(database) || File.Exists(Path.Combine(state, "runtime.json")))
    throw new InvalidOperationException("Среда уже существует. Этот инструмент не изменяет существующие базы; используйте отдельный checkout для новой среды.");
var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
var instanceId = Guid.NewGuid();
var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
Environment.SetEnvironmentVariable("PAYPLANNER_SKIP_CLIENT_SEED", "1");
Environment.SetEnvironmentVariable("PAYPLANNER_ADMIN_EMAIL", "admin@claude.local");
Environment.SetEnvironmentVariable("PAYPLANNER_ADMIN_FULLNAME", "Синтетический администратор");
Environment.SetEnvironmentVariable("PAYPLANNER_ADMIN_PASSWORD", password);
var options = new DbContextOptionsBuilder<PaymentContext>()
    .UseSqlite(connectionString)
    .UsePayPlannerSqliteMigrations()
    .Options;
await using var db = new PaymentContext(options);
await db.Database.MigrateAsync();
await SeedDataService.SeedAsync(db);
await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(db);
await AppReleaseSeedService.SyncAsync(db, Path.Combine(repository, "backend"));
var credentials = new List<object> { new { role = "admin", email = "admin@claude.local", password } };
var roles = new[] { "accountant", "secretary", "executor", "observer" };
var readSections = new[] { "tasks", "clients", "legalEntities", "cases", "accounts", "acts", "payments", "contracts", "calendar" };
foreach (var roleName in roles)
{
    var role = new Role { Name = roleName, Description = "Синтетическая роль для разработки: " + roleName };
    db.Roles.Add(role);
    await db.SaveChangesAsync();
    foreach (var section in readSections)
    {
        var taskWriter = (roleName is "secretary" or "executor") && section == "tasks";
        var financeWriter = roleName == "accountant" && (section is "accounts" or "acts" or "payments");
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = section, CanView = true,
            CanCreate = taskWriter || financeWriter, CanEdit = taskWriter || financeWriter,
            CanDelete = false, CanExport = false, CanViewAnalytics = false });
    }
    var user = new User { Email = roleName + "@claude.local", FullName = "Синтетический " + roleName,
        RoleId = role.Id, IsEmployee = true, IsApproved = true, IsActive = true, ApprovedAt = DateTime.UtcNow };
    var userPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    user.PasswordHash = new PasswordHasher<User>().HashPassword(user, userPassword);
    db.Users.Add(user); credentials.Add(new { role = roleName, email = user.Email, password = userPassword });
}
await db.SaveChangesAsync();
var executor = await db.Users.SingleAsync(u => u.Email == "executor@claude.local");
var admin = await db.Users.SingleAsync(u => u.Email == "admin@claude.local");
var persons = Enumerable.Range(1, 3).Select(i => new Client { Name = "Синтетический клиент " + i,
    Email = $"client{i}@claude.local",
    Phone = "", Address = "Синтетический адрес " + i, ResponsibleUserId = executor.Id }).ToList();
db.Clients.AddRange(persons);
db.LegalEntities.AddRange(Enumerable.Range(1, 2).Select(i => new LegalEntity
    { ShortName = "ООО Синтетическая компания " + i, FullName = "Синтетическая компания " + i }));
await db.SaveChangesAsync();
var cases = persons.Select((person, i) => new ClientCase { ClientId = person.Id,
    Title = "Синтетическое дело " + (i+1), CaseNumber = "SYNTH-" + (i+1),
    CourtName = "Синтетический суд", ResponsibleUserId = executor.Id, FeeAmount = 1000m }).ToList();
db.ClientCases.AddRange(cases); await db.SaveChangesAsync();
foreach (var clientCase in cases)
    foreach (var instance in new[] { "first", "appeal", "cassation", "supreme" })
        db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance { ClientCaseId = clientCase.Id,
            Instance = instance, CourtName = "Синтетический суд " + instance,
            CourtCaseNumber = clientCase.CaseNumber + "-" + instance, Notes = "Демонстрация UI, не судебный источник" });
var taskStatuses = new[] { "new", "inProgress", "onApproval", "done", "cancelled" };
foreach (var (status, i) in taskStatuses.Select((s,i)=>(s,i)))
    db.TaskJournalItems.Add(new TaskJournalItem { Title = "Синтетическая задача " + status,
        Status = status, ClientId = persons[0].Id, ClientCaseId = cases[0].Id,
        ClientBillingClassification = TaskClientBillingClassification.Billable, DueAt = DateTime.UtcNow.AddDays(i-2),
        AuthorUserId = admin.Id, AuthorName = admin.FullName, AssigneeUserId = executor.Id,
        AssigneeName = executor.FullName, ChannelsJson = "[]", RemindersJson = "[]" });
var invoice = new Invoice { Number = "SYNTH-001", Date = DateTime.UtcNow.Date };
db.Invoices.Add(invoice); await db.SaveChangesAsync();
db.Payments.Add(new Payment { InvoiceId = invoice.Id, ClientId = persons[0].Id, ClientCaseId = cases[0].Id,
    DocumentKind = DocumentKind.Invoice,
    Account = invoice.Number, AccountDate = invoice.Date, Amount = 1000m,
    Type = PaymentType.Income, Status = PaymentStatus.Pending, Date = DateTime.UtcNow.Date,
    Description = "Синтетический счёт без реального перевода" });
db.Payments.Add(new Payment { ClientId = persons[0].Id, ClientCaseId = cases[0].Id,
    Amount = 200m, Type = PaymentType.Income, Status = PaymentStatus.Completed, Date = DateTime.UtcNow.Date,
    Description = "Синтетическое поступление" });
await db.SaveChangesAsync();
foreach (var key in new[] { "notifications.email.enabled", "notifications.max.enabled", "notifications.telegram.enabled" })
{
    var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == key);
    if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = "false" });
    else setting.Value = "false";
}
await db.SaveChangesAsync();
await db.Database.OpenConnectionAsync();
await using (var transaction = await db.Database.GetDbConnection().BeginTransactionAsync())
{
    await RestoredRuntimeDatabaseIdentity.StampAsync(db.Database.GetDbConnection(), transaction, instanceId, default);
    await transaction.CommitAsync();
}
var keys = Path.Combine(state, "keys"); Directory.CreateDirectory(keys);
var services = new ServiceCollection(); services.AddLogging();
services.AddDataProtection().SetApplicationName("PayPlanner.Claude.Synthetic").PersistKeysToFileSystem(new DirectoryInfo(keys));
services.AddSingleton<DataProtectionRecoveryInventory>();
using var provider = services.BuildServiceProvider();
provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-preparation").Protect("synthetic-only");
var metadata = provider.GetRequiredService<DataProtectionRecoveryInventory>().Capture();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var artifact = Path.Combine(state, "storage-settings.json");
await File.WriteAllTextAsync(artifact, JsonSerializer.Serialize(new
    { formatVersion = 1, runtimeInstanceId = instanceId.ToString("N"), dataProtectionRecovery = metadata }, jsonOptions));
var environment = new Dictionary<string, string>
{
    ["ASPNETCORE_ENVIRONMENT"] = "ClaudeSynthetic", ["ASPNETCORE_URLS"] = "http://127.0.0.1:5300",
    ["BackgroundServices__Enabled"] = "false", ["Email__Enabled"] = "false",
    ["Database__Provider"] = "sqlite", ["ConnectionStrings__Default"] = connectionString,
    ["RestoredRuntimeIsolation__Enabled"] = "true", ["RestoredRuntimeIsolation__InstanceId"] = instanceId.ToString("N"),
    ["RestoredRuntimeIsolation__KeysPath"] = keys, ["RestoredRuntimeIsolation__StorageSettingsPath"] = artifact,
    ["DataProtection__ApplicationName"] = "PayPlanner.Claude.Synthetic",
    ["FieldEncryption__Enabled"] = "false", ["FieldEncryption__ClientPassportDualWriteEnabled"] = "false",
    ["MessengerDocumentDelivery__Enabled"] = "false", ["MessengerDocumentDelivery__Providers__Max__Enabled"] = "false",
    ["ElectronicSignature__Enabled"] = "false", ["PAYPLANNER_SKIP_CLIENT_SEED"] = "1",
    ["VITE_API_URL"] = "http://127.0.0.1:5300/api", ["VITE_RESTORED_RUNTIME_ISOLATION"] = "true",
    ["VITE_RESTORED_RUNTIME_INSTANCE_ID"] = instanceId.ToString("N"), ["VITE_DEV_HOST"] = "127.0.0.1"
};
await File.WriteAllTextAsync(Path.Combine(state, "runtime.json"), JsonSerializer.Serialize(environment, jsonOptions));
await File.WriteAllTextAsync(Path.Combine(state, "credentials.local.json"), JsonSerializer.Serialize(credentials, jsonOptions));
var counts = new { clients = await db.Clients.CountAsync(), legalEntities = await db.LegalEntities.CountAsync(),
    cases = await db.ClientCases.CountAsync(), courtInstances = await db.ClientCaseCourtInstances.CountAsync(),
    tasks = await db.TaskJournalItems.CountAsync(), users = await db.Users.CountAsync(), invoices = await db.Invoices.CountAsync(), payments = await db.Payments.CountAsync() };
await File.WriteAllTextAsync(Path.Combine(state, "seed-summary.json"), JsonSerializer.Serialize(counts, jsonOptions));
Console.WriteLine(JsonSerializer.Serialize(counts));
Console.WriteLine("Синтетическая среда подготовлена. Пароли и ключи только в игнорируемом .codex-build/claude-synthetic; не публиковать этот каталог.");

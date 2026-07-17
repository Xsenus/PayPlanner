using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalEntities;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.UserActivity;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var databaseProvider = DatabaseProviderResolver.Resolve(builder.Configuration);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<UserActivityLoggingFilter>();
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSignalR();
var dataProtectionBuilder = builder.Services.AddDataProtection();
var dataProtectionApplicationName = builder.Configuration["DataProtection:ApplicationName"];
if (!string.IsNullOrWhiteSpace(dataProtectionApplicationName))
{
    dataProtectionBuilder.SetApplicationName(dataProtectionApplicationName);
}

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? builder.Configuration["DataProtection:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<SlowRequestLoggingOptions>(
    builder.Configuration.GetSection(SlowRequestLoggingOptions.SectionName));

void ConfigureSqlite(DbContextOptionsBuilder options) => options
    .UseSqlite(
        databaseProvider.ConnectionString,
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .UsePayPlannerSqliteMigrations()
    .ConfigureWarnings(warnings =>
    {
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
        ConfigureEfDiagnostics(warnings);
    });
void ConfigurePostgreSql(DbContextOptionsBuilder options) => options
    .UseNpgsql(
        databaseProvider.ConnectionString,
        npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .ConfigureWarnings(warnings =>
    {
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
        ConfigureEfDiagnostics(warnings);
    });

void ConfigureEfDiagnostics(WarningsConfigurationBuilder warnings)
{
    if (!builder.Configuration.GetValue<bool>("Diagnostics:ThrowEfRuntimeWarnings"))
    {
        warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning);
        warnings.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning);
        return;
    }

    warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning);
    warnings.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning);
}

static void EnsureWalMode(string connectionString)
{
    try
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SQLite] Failed to enable WAL: {ex.Message}");
    }
}

if (databaseProvider.IsSqlite)
{
    EnsureWalMode(databaseProvider.ConnectionString);
}

builder.Services.AddSingleton(databaseProvider);

if (databaseProvider.IsSqlite)
{
    builder.Services.AddDbContextFactory<PaymentContext>(ConfigureSqlite);
}
else
{
    builder.Services.AddDbContextFactory<PostgreSqlPaymentContext>(ConfigurePostgreSql);
    builder.Services.AddSingleton<IDbContextFactory<PaymentContext>, PaymentContextFactoryAdapter<PostgreSqlPaymentContext>>();
}

builder.Services.AddScoped<PaymentContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContext());

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IClaimsTransformation, CurrentUserClaimsTransformation>();

builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.Configure<PasswordHasherOptions>(opt =>
{
    opt.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    opt.IterationCount = 210_000;
});

builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ITelegramNotificationService, TelegramNotificationService>();
builder.Services.AddScoped<ISmsNotificationService, SystemSmsNotificationService>();
builder.Services.AddScoped<ITelegramBotUpdateService, TelegramBotUpdateService>();
builder.Services.AddScoped<IRealtimeNotificationPublisher, SignalRRealtimeNotificationPublisher>();
builder.Services.AddScoped<IWebPushNotificationService, WebPushNotificationService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ExternalAuthService>();
builder.Services.AddScoped<RoleManagementAccessService>();
builder.Services.AddScoped<IUserActivityService, UserActivityService>();
builder.Services.AddScoped<IDatabaseImportService, DatabaseImportService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
builder.Services.AddScoped<ILegalIntegrationDiagnosticsService, LegalIntegrationDiagnosticsService>();
builder.Services.AddScoped<InstallmentService>();
builder.Services.AddScoped<StatsSummaryService>();
builder.Services.AddScoped<IFormRequirementsService, FormRequirementsService>();
builder.Services.AddSingleton<ICaseDocumentStorage, LocalCaseDocumentStorage>();
builder.Services.AddSingleton<IInvoiceDocumentPdfService, InvoiceDocumentPdfService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddSingleton<ISystemJobRunStateService, SystemJobRunStateService>();
builder.Services.Configure<AppFeatureFlagsOptions>(
    builder.Configuration.GetSection(AppFeatureFlagsOptions.SectionName));
builder.Services.Configure<DadataOptions>(builder.Configuration.GetSection("DaData"));
builder.Services.AddHttpClient<ILegalEntityEnrichmentService, DadataLegalEntityEnrichmentService>();
builder.Services.AddHttpClient<ISudrfCourtSyncService, SudrfCourtSyncService>();
builder.Services.AddHttpClient<IYandexDiskService, YandexDiskService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IExternalCalendarSyncCoordinator, ExternalCalendarSyncCoordinator>();
builder.Services.AddScoped<IGoogleCalendarSyncService, GoogleCalendarSyncService>();
builder.Services.AddScoped<IYandexCalendarSyncService, YandexCalendarSyncService>();
builder.Services.AddSingleton<IS3StorageClientFactory, S3StorageClientFactory>();
builder.Services.AddScoped<IS3StorageService, S3StorageService>();
builder.Services.AddScoped<ICourtSyncProvider, SudrfCourtSyncProvider>();
builder.Services.AddScoped<ICourtSyncProviderRegistry, CourtSyncProviderRegistry>();

if (databaseProvider.IsSqlite)
{
    builder.Services.AddSingleton<ISqliteMaintenanceService, SqliteMaintenanceService>();
}
else
{
    builder.Services.AddSingleton<ISqliteMaintenanceService, NoOpSqliteMaintenanceService>();
}

builder.Services.AddHostedService<DatabaseBackupSchedulerService>();
builder.Services.AddHostedService<PaymentStatusUpdater>();
builder.Services.AddHostedService<InvoicePaymentStateReconciliationService>();
builder.Services.AddSingleton<TaskJournalReminderNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskJournalReminderNotificationService>());
builder.Services.AddSingleton<StickyNoteReminderNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StickyNoteReminderNotificationService>());
builder.Services.AddSingleton<NotificationRetrySchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationRetrySchedulerService>());
builder.Services.AddSingleton<NotificationDeliveryRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationDeliveryRetentionService>());
builder.Services.AddSingleton<PaymentAdvanceReviewMonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PaymentAdvanceReviewMonitoringService>());
builder.Services.AddSingleton<ContractExpirationNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContractExpirationNotificationService>());
builder.Services.AddSingleton<CaseProceduralDeadlineReminderNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CaseProceduralDeadlineReminderNotificationService>());
builder.Services.AddSingleton<CalendarSyncSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CalendarSyncSchedulerService>());
builder.Services.AddSingleton<GoogleCalendarBackgroundSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GoogleCalendarBackgroundSyncService>());
builder.Services.AddSingleton<YandexCalendarBackgroundSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YandexCalendarBackgroundSyncService>());
builder.Services.AddSingleton<TaskJournalAutomationSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskJournalAutomationSchedulerService>());
builder.Services.AddHostedService<TelegramBotPollingService>();
builder.Services.AddSingleton<MoneyBalanceReconciliationSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MoneyBalanceReconciliationSchedulerService>());
builder.Services.AddSingleton<ClientCaseCourtSyncSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientCaseCourtSyncSchedulerService>());
builder.Services.AddSingleton<UserActivityRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UserActivityRetentionService>());
builder.Services.AddSingleton<YandexDiskCrmSyncSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YandexDiskCrmSyncSchedulerService>());

builder.Services.AddCors(options =>
    CorsPolicyConfiguration.ConfigureDefaultPolicy(options, builder.Configuration, builder.Environment));

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtSecret = JwtConfiguration.GetRequiredSecret(builder.Configuration);
var jwtIssuer = jwtSection.GetValue<string>("Issuer") ?? "PayPlanner";
var jwtAudience = jwtSection.GetValue<string>("Audience") ?? "PayPlanner";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrWhiteSpace(accessToken)
                    && (path.StartsWithSegments(NotificationsHub.Path)
                        || path.StartsWithSegments(NotesHub.Path)))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Admin", p => p.RequireRole("admin"));
});

var urlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var urlsCfg = builder.Configuration["urls"] ?? builder.Configuration["Urls"];
var effectiveUrls = !string.IsNullOrWhiteSpace(urlsEnv)
    ? urlsEnv
    : (!string.IsNullOrWhiteSpace(urlsCfg) ? urlsCfg : "http://127.0.0.1:5080");

builder.WebHost.UseUrls(effectiveUrls);
Console.WriteLine(
    $"[Startup] Effective URLs: {effectiveUrls} " +
    $"(ASPNETCORE_URLS={(string.IsNullOrWhiteSpace(urlsEnv) ? "<empty>" : urlsEnv)}, " +
    $"configUrls={(string.IsNullOrWhiteSpace(urlsCfg) ? "<empty>" : urlsCfg)})");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseApiErrorContract(app.Environment);
app.UseRouting();
app.UseMiddleware<SlowRequestLoggingMiddleware>();
app.UseCors();
app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupDataMaintenance");
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");

    if (databaseProvider.IsSqlite)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await RunStartupStepAsync(startupLogger, "SQLite database migrations", async () => await ctx.Database.MigrateAsync());
        await RunStartupStepAsync(startupLogger, "SQLite seed data", async () => await SeedDataService.SeedAsync(ctx, seedClients));
        await RunStartupStepAsync(startupLogger, "SQLite app release sync", async () => await AppReleaseSeedService.SyncAsync(ctx, app.Environment.ContentRootPath));
        await RunStartupStepAsync(startupLogger, "SQLite payment remainder transfer backfill", async () => await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite task contractor payment backfill", async () => await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite legacy expense recipient backfill", async () => await LegacyExpensePaymentRecipientBackfillService.EnsureRecognizedRecipientsAsync(ctx));
    }
    else
    {
        var pgFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostgreSqlPaymentContext>>();
        await using var pgContext = await pgFactory.CreateDbContextAsync();
        await RunStartupStepAsync(startupLogger, "PostgreSQL database migrations", async () => await pgContext.Database.MigrateAsync());
        await RunStartupStepAsync(startupLogger, "PostgreSQL seed data", async () => await SeedDataService.SeedAsync(pgContext, seedClients));
        await RunStartupStepAsync(startupLogger, "PostgreSQL app release sync", async () => await AppReleaseSeedService.SyncAsync(pgContext, app.Environment.ContentRootPath));
        await RunStartupStepAsync(startupLogger, "PostgreSQL payment remainder transfer backfill", async () => await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(pgContext));
        await RunStartupStepAsync(startupLogger, "PostgreSQL task contractor payment backfill", async () => await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(pgContext));
        await RunStartupStepAsync(startupLogger, "PostgreSQL legacy expense recipient backfill", async () => await LegacyExpensePaymentRecipientBackfillService.EnsureRecognizedRecipientsAsync(pgContext));
    }
}

app.MapControllers();
app.MapHub<NotificationsHub>(NotificationsHub.Path);
app.MapHub<NotesHub>(NotesHub.Path);

app.Run();

static async Task RunStartupStepAsync(ILogger logger, string stepName, Func<Task> action)
{
    var startedAt = Stopwatch.GetTimestamp();
    logger.LogInformation("Startup data step {StepName} started.", stepName);

    try
    {
        await action();
        logger.LogInformation(
            "Startup data step {StepName} completed in {ElapsedMs} ms.",
            stepName,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Startup data step {StepName} failed after {ElapsedMs} ms.",
            stepName,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        throw;
    }
}

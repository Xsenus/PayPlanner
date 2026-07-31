using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
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
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
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
builder.Services.Configure<SlowDbCommandLoggingOptions>(
    builder.Configuration.GetSection(SlowDbCommandLoggingOptions.SectionName));
builder.Services.AddSingleton<SlowDbCommandInterceptor>();

void ConfigureSqlite(IServiceProvider services, DbContextOptionsBuilder options) => options
    .UseSqlite(
        databaseProvider.ConnectionString,
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .UsePayPlannerSqliteMigrations()
    .AddInterceptors(services.GetRequiredService<SlowDbCommandInterceptor>())
    .ConfigureWarnings(warnings =>
    {
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
        ConfigureEfDiagnostics(warnings);
    });
void ConfigurePostgreSql(IServiceProvider services, DbContextOptionsBuilder options) => options
    .UseNpgsql(
        databaseProvider.ConnectionString,
        npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .AddInterceptors(services.GetRequiredService<SlowDbCommandInterceptor>())
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
builder.Services.AddScoped<IMaxNotificationService, SystemMaxNotificationService>();
builder.Services.AddScoped<IMaxWebhookService, MaxWebhookService>();
builder.Services.AddScoped<IMaxCorrespondenceService, MaxCorrespondenceService>();
builder.Services.AddScoped<IMaxOutboxService, MaxOutboxService>();
builder.Services.AddScoped<IMaxOutboxProcessor, MaxOutboxProcessor>();
builder.Services.AddScoped<IExternalWorkOrderApprovalService, ExternalWorkOrderApprovalService>();
builder.Services.AddScoped<IExternalWorkOrderApprovalNotifier, ExternalWorkOrderApprovalNotifier>();
builder.Services.AddScoped<IClientPortalService, ClientPortalService>();
builder.Services.AddScoped<ITelegramBotUpdateService, TelegramBotUpdateService>();
builder.Services.AddScoped<IRealtimeNotificationPublisher, SignalRRealtimeNotificationPublisher>();
builder.Services.AddScoped<IWebPushNotificationService, WebPushNotificationService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISmsChannelWarningPublisher, SmsChannelWarningPublisher>();
builder.Services.AddScoped<ISmsProviderCredentialStore, SmsProviderCredentialStore>();
builder.Services.AddHttpClient<SmsRuProvider>(client =>
{
    client.BaseAddress = new Uri("https://sms.ru/");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient<SmscRuProvider>(client =>
{
    client.BaseAddress = new Uri("https://smsc.ru/");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient<SmsAeroProvider>(client =>
{
    client.BaseAddress = new Uri("https://gate.smsaero.ru/");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<SmsRuProvider>());
builder.Services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<SmscRuProvider>());
builder.Services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<SmsAeroProvider>());
builder.Services.AddHttpClient<IMaxApiClient, MaxApiClient>(client =>
{
    client.BaseAddress = new Uri("https://platform-api2.max.ru/");
    client.Timeout = TimeSpan.FromSeconds(20);
});
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
builder.Services.AddScoped<IMessengerDocumentDeliveryService, MessengerDocumentDeliveryService>();
builder.Services.AddScoped<IMessengerDocumentProvider, TelegramMessengerDocumentProvider>();
builder.Services.AddScoped<IMessengerDocumentProvider, MaxMessengerDocumentProvider>();
builder.Services.AddScoped<IMessengerDocumentProvider>(_ => new DisabledMessengerDocumentProvider(MessengerDocumentChannels.WhatsAppBusiness));
builder.Services.AddScoped<IMessengerDocumentOutboxProcessor, MessengerDocumentOutboxProcessor>();
builder.Services.AddScoped<IElectronicSignatureService, ElectronicSignatureService>();
builder.Services.AddScoped<IElectronicSignatureProvider>(_ => new DisabledElectronicSignatureProvider("KonturDiadoc"));
builder.Services.AddScoped<IElectronicSignatureProvider>(_ => new DisabledElectronicSignatureProvider("Sbis"));
builder.Services.AddScoped<IElectronicSignatureProvider>(_ => new DisabledElectronicSignatureProvider("Taxcom"));
builder.Services.AddSingleton<IFieldEncryptionKeyStore, EnvironmentFieldEncryptionKeyStore>();
builder.Services.AddSingleton<IFieldEncryptor, AeadFieldEncryptor>();
builder.Services.AddScoped<IClientPassportEncryptionBackfillService, ClientPassportEncryptionBackfillService>();
builder.Services.AddSingleton<IInvoiceDocumentPdfService, InvoiceDocumentPdfService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddSingleton<ISystemJobRunStateService, SystemJobRunStateService>();
builder.Services.AddSingleton<CourtSyncMetrics>();
builder.Services.AddSingleton<CourtSyncMetricsRollupService>();
builder.Services.Configure<AppFeatureFlagsOptions>(
    builder.Configuration.GetSection(AppFeatureFlagsOptions.SectionName));
builder.Services.Configure<DadataOptions>(builder.Configuration.GetSection("DaData"));
builder.Services.Configure<SudrfCourtRequestPolicyOptions>(
    builder.Configuration.GetSection(SudrfCourtRequestPolicyOptions.SectionName));
builder.Services.AddSingleton(sp => new SudrfCourtRequestPolicy(
    sp.GetRequiredService<IOptions<SudrfCourtRequestPolicyOptions>>(),
    sp.GetRequiredService<CourtSyncMetrics>()));
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
builder.Services.AddScoped<ICourtSyncProvider, KadArbitrCourtSyncProvider>();
builder.Services.AddScoped<ICourtSyncProvider, MosGorSudCourtSyncProvider>();
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
builder.Services.AddHostedService<MessengerDocumentOutboxWorker>();
builder.Services.AddHostedService<MaxOutboxWorker>();
builder.Services.AddHostedService<ExternalWorkOrderApprovalExpirationService>();
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
builder.Services.AddSingleton<SmsChannelMonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SmsChannelMonitoringService>());
builder.Services.AddSingleton<SmsDeliveryStatusReconciliationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SmsDeliveryStatusReconciliationService>());
builder.Services.AddSingleton<CalendarSyncLogRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CalendarSyncLogRetentionService>());
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<CourtSyncMetricsRollupService>());
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-work-order-approval", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("client-portal", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupDataMaintenance");
    var dataProtectionProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");

    if (databaseProvider.IsSqlite)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await RunStartupStepAsync(startupLogger, "SQLite database migrations", async () => await ctx.Database.MigrateAsync());
        await RunStartupStepAsync(startupLogger, "SQLite seed data", async () => await SeedDataService.SeedAsync(ctx, seedClients));
        await RunStartupStepAsync(startupLogger, "SQLite app release sync", async () => await AppReleaseSeedService.SyncAsync(ctx, app.Environment.ContentRootPath));
        await RunStartupStepAsync(startupLogger, "SQLite SMS credential protection", async () => await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(ctx, dataProtectionProvider));
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
        await RunStartupStepAsync(startupLogger, "PostgreSQL SMS credential protection", async () => await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(pgContext, dataProtectionProvider));
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

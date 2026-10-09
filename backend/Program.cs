using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
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
using PayPlanner.Api.Services.Storage;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var urlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var urlsCfg = builder.Configuration["urls"] ?? builder.Configuration["Urls"];
var effectiveUrls = !string.IsNullOrWhiteSpace(urlsEnv)
    ? urlsEnv
    : (!string.IsNullOrWhiteSpace(urlsCfg) ? urlsCfg : "http://127.0.0.1:5080");
RestoredRuntimeLaunchSafety.Validate(builder.Configuration, effectiveUrls);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStorageProvider, LocalStorageProvider>();
builder.Services.AddSingleton<LegacyLocalStorageProvider>();
builder.Services.AddSingleton<ILegacyLocalStorageLocator>(sp => sp.GetRequiredService<LegacyLocalStorageProvider>());
builder.Services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<LegacyLocalStorageProvider>());
builder.Services.AddScoped<IStorageProvider, ManagedCompatibilityStorageProvider>();
builder.Services.AddScoped<IStorageProvider, S3StorageProvider>();
builder.Services.AddScoped<IStorageProvider, YandexDiskStorageProvider>();
builder.Services.AddScoped<IStorageProviderRegistry, StorageProviderRegistry>();
builder.Services.AddSingleton<IStorageProviderHealthTracker, StorageProviderHealthTracker>();
builder.Services.Configure<StorageOrchestrationOptions>(
    builder.Configuration.GetSection(StorageOrchestrationOptions.SectionName));
builder.Services.AddScoped<IStoragePolicyService, StoragePolicyService>();
builder.Services.AddScoped<PayPlanner.Api.Services.Companies.IDocumentSellerService, PayPlanner.Api.Services.Companies.DocumentSellerService>();
builder.Services.AddScoped<PayPlanner.Api.Services.Banking.IBankSyncService, PayPlanner.Api.Services.Banking.BankSyncService>();
builder.Services.AddScoped<PayPlanner.Api.Services.Tax.ITaxRegisterService, PayPlanner.Api.Services.Tax.TaxRegisterService>();
builder.Services.AddScoped<IStorageOperationProcessor, StorageOperationProcessor>();
builder.Services.AddScoped<IStorageObjectService, StorageObjectService>();
builder.Services.AddScoped<StorageStageCapacityService>();
builder.Services.AddScoped<IStorageStageCapacityService>(services => services.GetRequiredService<StorageStageCapacityService>());
builder.Services.AddScoped<IStorageStageWriterMarkerRecoveryService>(services => services.GetRequiredService<StorageStageCapacityService>());
builder.Services.AddSingleton<StorageStageMarkerMaintenance>();
builder.Services.AddScoped<IStorageOriginalReadStagingService, StorageOriginalReadStagingService>();
builder.Services.AddScoped<IProductStorageReader, ProductStorageReader>();
builder.Services.AddScoped<IProductStorageBindingService, ProductStorageBindingService>();
builder.Services.AddScoped<IProductStorageDeletionService, ProductStorageDeletionService>();
builder.Services.AddScoped<IManagedProfilePhotoUploadService, ManagedProfilePhotoUploadService>();
builder.Services.AddScoped<IManagedTaskFileUploadService, ManagedTaskFileUploadService>();
builder.Services.AddScoped<IManagedCaseDocumentUploadService, ManagedCaseDocumentUploadService>();
builder.Services.AddScoped<ITaskFilePublicationAuthorizationService, TaskFilePublicationAuthorizationService>();
builder.Services.AddScoped<ITaskFilePublicationProcessor, TaskFilePublicationProcessor>();
builder.Services.AddScoped<ICaseDocumentPublicationProcessor, CaseDocumentPublicationProcessor>();
builder.Services.AddScoped<ITaskFileUploadStatusReader, TaskFileUploadStatusReader>();
builder.Services.AddScoped<ICaseDocumentUploadStatusReader, CaseDocumentUploadStatusReader>();
builder.Services.AddScoped<CaseDocumentUploadRetirementService>();
builder.Services.AddScoped<GeneratedFinancialAttemptRecovery>();
builder.Services.AddInternalChatStorageRecovery();
builder.Services.AddScoped<IStorageReconciliationService, StorageReconciliationService>();
builder.Services.AddScoped<ILegacyStorageCatalogBackfillService, LegacyStorageCatalogBackfillService>();
builder.Services.AddScoped<ILegacyStorageReplicaMigrationService, LegacyStorageReplicaMigrationService>();
var databaseProvider = DatabaseProviderResolver.Resolve(builder.Configuration);
await RestoredRuntimeDatabaseIdentity.VerifyAsync(builder.Configuration, databaseProvider, CancellationToken.None);
RestoredRuntimeAuthentication.Configure(builder.Configuration);

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
var restoredDataProtection = RestoredRuntimeDataProtection.ReadConfiguration(builder.Configuration);
var dataProtectionBuilder = builder.Services.AddDataProtection();
var dataProtectionApplicationName = builder.Configuration["DataProtection:ApplicationName"];
if (!string.IsNullOrWhiteSpace(dataProtectionApplicationName))
{
    dataProtectionBuilder.SetApplicationName(dataProtectionApplicationName);
}

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? builder.Configuration["DataProtection:KeysDirectory"];
if (restoredDataProtection is not null)
{
    dataProtectionBuilder.DisableAutomaticKeyGeneration();
    builder.Services.Configure<DataProtectionOptions>(options =>
        options.ApplicationDiscriminator = restoredDataProtection.Expected.ApplicationDiscriminator);
    dataProtectionKeysPath = restoredDataProtection.KeysPath;
}
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    if (restoredDataProtection is null) Directory.CreateDirectory(dataProtectionKeysPath);
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
builder.Services.AddTransient<CurrentUserClaimsTransformation>();

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
builder.Services.AddScoped<ICaseDocumentNotificationLiveDispatcher>(sp => (ICaseDocumentNotificationLiveDispatcher)sp.GetRequiredService<INotificationService>());
builder.Services.AddScoped<CaseDocumentNotificationDispatchProcessor>();
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
builder.Services.AddScoped<CaseDocumentNotificationService>();
builder.Services.AddScoped<CaseDocumentPublicationRecords>();
builder.Services.AddScoped<GeneratedWorkOrderDocumentPublication>();
builder.Services.AddScoped<GeneratedTemplateDocumentPublication>();
builder.Services.AddScoped<CourtDocumentSourceRegistry>();
builder.Services.AddScoped<GeneratedCourtDocumentPublication>();
builder.Services.AddScoped<GeneratedFinancialDocumentRegistry>();
builder.Services.AddScoped<GeneratedFinancialDocumentPublication>();
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
builder.Services.AddSingleton<IClientPassportEncryptionDualWriteService, ClientPassportEncryptionDualWriteService>();
builder.Services.AddScoped<IClientPassportEncryptionBackfillService, ClientPassportEncryptionBackfillService>();
builder.Services.AddSingleton<IInvoiceDocumentPdfService, InvoiceDocumentPdfService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddSingleton<DataProtectionRecoveryInventory>();
builder.Services.Configure<DatabaseRestoreDrillOptions>(
    builder.Configuration.GetSection(DatabaseRestoreDrillOptions.SectionName));
builder.Services.AddSingleton<DatabaseRestoreDrillService>();
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
builder.Services.AddRestoredRuntimeIsolation();
builder.Services.AddScoped<IExternalCalendarSyncCoordinator, ExternalCalendarSyncCoordinator>();
builder.Services.AddScoped<IExternalCalendarTaskSyncOutbox, ExternalCalendarTaskSyncOutbox>();
builder.Services.AddScoped<IExternalCalendarTaskSyncOutboxProcessor, ExternalCalendarTaskSyncOutboxProcessor>();
builder.Services.AddSingleton<ExternalCalendarTaskSyncWakeSignal>();
builder.Services.AddScoped<IGoogleCalendarSyncService, GoogleCalendarSyncService>();
builder.Services.AddScoped<IYandexCalendarSyncService, YandexCalendarSyncService>();
builder.Services.AddSingleton<IS3StorageClientFactory, S3StorageClientFactory>();
builder.Services.Configure<S3EndpointSecurityOptions>(
    builder.Configuration.GetSection(S3EndpointSecurityOptions.SectionName));
builder.Services.AddSingleton<IS3EndpointSecurityPolicy, S3EndpointSecurityPolicy>();
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

builder.Services.AddSingleton<TaskJournalReminderNotificationService>();
builder.Services.AddSingleton<InternalChatNoReplyReminderService>();
builder.Services.AddSingleton<InternalChatNotificationRecoveryService>();
builder.Services.AddSingleton<StickyNoteReminderNotificationService>();
builder.Services.AddSingleton<NotificationRetrySchedulerService>();
builder.Services.AddSingleton<NotificationDeliveryRetentionService>();
builder.Services.AddSingleton<SmsChannelMonitoringService>();
builder.Services.AddSingleton<SmsDeliveryStatusReconciliationService>();
builder.Services.AddSingleton<CalendarSyncLogRetentionService>();
builder.Services.AddSingleton<PaymentAdvanceReviewMonitoringService>();
builder.Services.AddSingleton<ContractExpirationNotificationService>();
builder.Services.AddSingleton<CaseProceduralDeadlineReminderNotificationService>();
builder.Services.AddSingleton<CalendarSyncSchedulerService>();
builder.Services.AddSingleton<GoogleCalendarBackgroundSyncService>();
builder.Services.AddSingleton<YandexCalendarBackgroundSyncService>();
builder.Services.AddSingleton<CaseInactivityAutomationMutationGate>();
builder.Services.AddSingleton<TaskJournalAutomationSchedulerService>();
builder.Services.AddSingleton<ClientBirthdayTaskSchedulerService>();
builder.Services.AddSingleton<MoneyBalanceReconciliationSchedulerService>();
builder.Services.AddSingleton<ClientCaseCourtSyncSchedulerService>();
builder.Services.AddSingleton<UserActivityRetentionService>();
builder.Services.AddSingleton<YandexDiskCrmSyncSchedulerService>();

if (builder.Configuration.GetValue("BackgroundServices:Enabled", true))
{
    builder.Services.AddHostedService<DatabaseBackupSchedulerService>();
    builder.Services.AddHostedService<StorageOperationWorker>();
    builder.Services.AddHostedService<MessengerDocumentOutboxWorker>();
    builder.Services.AddHostedService<MaxOutboxWorker>();
    builder.Services.AddHostedService<CaseDocumentNotificationDispatchWorker>();
    builder.Services.AddHostedService<ExternalCalendarTaskSyncOutboxWorker>();
    builder.Services.AddHostedService<ExternalWorkOrderApprovalExpirationService>();
    builder.Services.AddHostedService<PaymentStatusUpdater>();
    builder.Services.AddHostedService<InvoicePaymentStateReconciliationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskJournalReminderNotificationService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InternalChatNoReplyReminderService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InternalChatNotificationRecoveryService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StickyNoteReminderNotificationService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationRetrySchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationDeliveryRetentionService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SmsChannelMonitoringService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SmsDeliveryStatusReconciliationService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CalendarSyncLogRetentionService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PaymentAdvanceReviewMonitoringService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ContractExpirationNotificationService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CaseProceduralDeadlineReminderNotificationService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CalendarSyncSchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GoogleCalendarBackgroundSyncService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<YandexCalendarBackgroundSyncService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskJournalAutomationSchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientBirthdayTaskSchedulerService>());
    builder.Services.AddHostedService<TelegramBotPollingService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MoneyBalanceReconciliationSchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientCaseCourtSyncSchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CourtSyncMetricsRollupService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UserActivityRetentionService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<YandexDiskCrmSyncSchedulerService>());
}

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

builder.Services.AddSingleton(StorageWorkerAvailability.Capture(builder.Services));

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
            },
            OnTokenValidated = async context =>
            {
                if (context.Principal is null)
                {
                    context.Fail("Authenticated principal is missing.");
                    return;
                }

                var transformer = context.HttpContext.RequestServices
                    .GetRequiredService<CurrentUserClaimsTransformation>();
                var refreshedPrincipal = await transformer.TransformAsync(context.Principal);
                if (refreshedPrincipal.Identity?.IsAuthenticated != true)
                {
                    context.Fail("User account is missing, disabled or not approved.");
                    return;
                }

                context.Principal = refreshedPrincipal;
            },
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Admin", p => p.RequireRole("admin"));
});
builder.Services.Configure<ForwardedHeadersOptions>(LocalReverseProxyForwarding.Configure);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    AuthRateLimitPolicies.AddPolicies(options);
    PublicAccessRateLimitPolicies.AddPolicies(options);
    AnonymousIntegrationRateLimitPolicies.AddPolicies(options);
});

builder.WebHost.UseUrls(effectiveUrls);
Console.WriteLine(
    $"[Startup] Effective URLs: {effectiveUrls} " +
    $"(ASPNETCORE_URLS={(string.IsNullOrWhiteSpace(urlsEnv) ? "<empty>" : urlsEnv)}, " +
    $"configUrls={(string.IsNullOrWhiteSpace(urlsCfg) ? "<empty>" : urlsCfg)})");

var app = builder.Build();
await RestoredRuntimeDataProtection.VerifyAsync(app.Services, builder.Configuration, databaseProvider,
    restoredDataProtection, CancellationToken.None);

if (app.Environment.IsDevelopment() && !builder.Configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseApiErrorContract(app.Environment);
app.UseRouting();
app.UseMiddleware<SlowRequestLoggingMiddleware>();
app.UseCors();
app.UseMiddleware<RestoredRuntimeRequestGuard>();
app.UseResponseCompression();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<ManagedProfilePhotoMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
var userPhotoStorageRoot = UserPhotoStoragePaths.ResolveRoot(app.Environment, app.Configuration);
Directory.CreateDirectory(userPhotoStorageRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(userPhotoStorageRoot),
    RequestPath = UserPhotoStoragePaths.RequestPath,
});

// Prepared clones must not be changed by production startup migrations/seed/backfills.
if (!builder.Configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false))
{
    using var scope = app.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupDataMaintenance");
    var dataProtectionProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");

    if (databaseProvider.IsSqlite)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await RunStartupStepAsync(startupLogger, "SQLite database migrations", async () => await ctx.Database.MigrateAsync());
        await RunStartupStepAsync(startupLogger, "SQLite seed data", async () => await SeedDataService.SeedAsync(ctx, seedClients));
        await RunStartupStepAsync(startupLogger, "SQLite navigation group seed", async () => await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite app release sync", async () => await AppReleaseSeedService.SyncAsync(ctx, app.Environment.ContentRootPath));
        await RunStartupStepAsync(startupLogger, "SQLite SMS credential protection", async () => await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(ctx, dataProtectionProvider));
        await RunStartupStepAsync(startupLogger, "SQLite payment remainder transfer backfill", async () => await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite task contractor payment backfill", async () => await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite task billing classification backfill", async () => await TaskBillingClassificationBackfillService.EnsureLegacyTasksClassifiedAsync(ctx));
        await RunStartupStepAsync(startupLogger, "SQLite legacy expense recipient backfill", async () => await LegacyExpensePaymentRecipientBackfillService.EnsureRecognizedRecipientsAsync(ctx));
    }
    else
    {
        var pgFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostgreSqlPaymentContext>>();
        await using var pgContext = await pgFactory.CreateDbContextAsync();
        await RunStartupStepAsync(startupLogger, "PostgreSQL database migrations", async () => await pgContext.Database.MigrateAsync());
        await RunStartupStepAsync(startupLogger, "PostgreSQL seed data", async () => await SeedDataService.SeedAsync(pgContext, seedClients));
        await RunStartupStepAsync(startupLogger, "PostgreSQL navigation group seed", async () => await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(pgContext));
        await RunStartupStepAsync(startupLogger, "PostgreSQL app release sync", async () => await AppReleaseSeedService.SyncAsync(pgContext, app.Environment.ContentRootPath));
        await RunStartupStepAsync(startupLogger, "PostgreSQL SMS credential protection", async () => await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(pgContext, dataProtectionProvider));
        await RunStartupStepAsync(startupLogger, "PostgreSQL payment remainder transfer backfill", async () => await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(pgContext));
        await RunStartupStepAsync(startupLogger, "PostgreSQL task contractor payment backfill", async () => await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(pgContext));
        await RunStartupStepAsync(startupLogger, "PostgreSQL task billing classification backfill", async () => await TaskBillingClassificationBackfillService.EnsureLegacyTasksClassifiedAsync(pgContext));
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

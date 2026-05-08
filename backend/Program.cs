using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.LegalEntities;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.UserActivity;
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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

void ConfigureSqlite(DbContextOptionsBuilder options) => options
    .UseSqlite(databaseProvider.ConnectionString)
    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
void ConfigurePostgreSql(DbContextOptionsBuilder options) => options.UseNpgsql(databaseProvider.ConnectionString);

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
builder.Services.AddScoped<ITelegramBotUpdateService, TelegramBotUpdateService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ExternalAuthService>();
builder.Services.AddScoped<RoleManagementAccessService>();
builder.Services.AddScoped<IUserActivityService, UserActivityService>();
builder.Services.AddScoped<IDatabaseImportService, DatabaseImportService>();
builder.Services.AddScoped<ISpreadsheetImportService, SpreadsheetImportService>();
builder.Services.AddScoped<InstallmentService>();
builder.Services.AddScoped<StatsSummaryService>();
builder.Services.AddScoped<IFormRequirementsService, FormRequirementsService>();
builder.Services.AddSingleton<IInvoiceDocumentPdfService, InvoiceDocumentPdfService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.Configure<DadataOptions>(builder.Configuration.GetSection("DaData"));
builder.Services.AddHttpClient<ILegalEntityEnrichmentService, DadataLegalEntityEnrichmentService>();

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
builder.Services.AddHostedService<TaskJournalReminderNotificationService>();
builder.Services.AddHostedService<TelegramBotPollingService>();
builder.Services.AddHostedService<MoneyBalanceReconciliationSchedulerService>();
builder.Services.AddHostedService<UserActivityRetentionService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

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
var jwtSecret = jwtSection.GetValue<string>("Secret") ?? "your-secret-key-min-32-characters-long!";
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

app.UseCors();
app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");

    if (databaseProvider.IsSqlite)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await ctx.Database.MigrateAsync();
        await SeedDataService.SeedAsync(ctx, seedClients);
        await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(ctx);
    }
    else
    {
        var pgFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostgreSqlPaymentContext>>();
        await using var pgContext = await pgFactory.CreateDbContextAsync();
        await pgContext.Database.MigrateAsync();
        await SeedDataService.SeedAsync(pgContext, seedClients);
        await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(pgContext);
    }
}

app.MapControllers();

app.Run();

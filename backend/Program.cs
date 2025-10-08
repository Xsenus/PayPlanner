// File: Program.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ================= JSON + MVC =================
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ================= Swagger =================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ================= DbContext (SQLite) =================
static string NormalizeSqliteConnection(string raw)
{
    var b = new SqliteConnectionStringBuilder(raw);
    if (!Path.IsPathRooted(b.DataSource))
        b.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, b.DataSource));
    return b.ToString();
}
var rawCs = builder.Configuration.GetConnectionString("Default") ?? "Data Source=payplanner.db";
var normalizedCs = NormalizeSqliteConnection(rawCs);
builder.Services.AddDbContext<PaymentContext>(options => options.UseSqlite(normalizedCs));

// ================= Services & Backgrounds =================
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.Configure<PasswordHasherOptions>(opt =>
{
    opt.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    opt.IterationCount = 210_000;
});

builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<InstallmentService>();
builder.Services.AddHostedService<PaymentStatusUpdater>();
builder.Services.AddHostedService<DatabaseBackupService>();
builder.Services.AddScoped<StatsSummaryService>();

// ================= CORS =================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// ================= Response compression =================
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});

// ================= JWT Auth =================
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

// ================= Urls override =================
var urls = builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(urls))
    builder.WebHost.UseUrls(urls);

var app = builder.Build();

// ================= Middleware =================
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

// ================= DB init & seed =================
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
    await ctx.Database.MigrateAsync();

    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");
    await SeedDataService.SeedAsync(ctx, seedClients);
}

// ================= MVC Controllers =================
app.MapControllers();

app.Run();

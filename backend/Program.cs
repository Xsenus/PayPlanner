using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ================= JSON =================
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
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

// ================= Health =================
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// ======================================================================
// ============================ AUTH ====================================
// ======================================================================
var auth = app.MapGroup("/api/auth");

// Helper: id текущего пользователя из токена
static int? GetUserId(ClaimsPrincipal user)
    => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

// Публичные
auth.MapPost("/login", async (AuthService svc, LoginRequest req) =>
{
    var res = await svc.LoginAsync(req.Email, req.Password);
    return res is null ? Results.Unauthorized() : Results.Ok(res);
}).AllowAnonymous();

auth.MapPost("/register", async (AuthService svc, RegisterRequest req) =>
{
    var dto = await svc.RegisterAsync(req);
    return dto is null
        ? Results.Conflict(new { message = "User with this email already exists" })
        : Results.Created($"/api/auth/users/{dto.Id}", dto);
}).AllowAnonymous();

// Профиль
auth.MapGet("/me", async (AuthService svc, ClaimsPrincipal principal) =>
{
    var id = GetUserId(principal);
    if (id is null) return Results.Unauthorized();

    var user = await svc.GetUserByIdAsync(id.Value);
    return user is null ? Results.NotFound() : Results.Ok(user);
}).RequireAuthorization();

// ADMIN
var admin = auth.MapGroup("/admin").RequireAuthorization("Admin");

admin.MapGet("/users", async (AuthService svc, string? status) =>
{
    var users = await svc.GetAllUsersAsync(status);
    return Results.Ok(users);
});

admin.MapGet("/users/{id:int}", async (AuthService svc, int id) =>
{
    var user = await svc.GetUserByIdAsync(id);
    return user is null ? Results.NotFound() : Results.Ok(user);
});

admin.MapPost("/users", async (AuthService svc, CreateUserRequest req) =>
{
    var dto = await svc.CreateUserAsync(req);
    return dto is null
        ? Results.Conflict(new { message = "User with this email already exists" })
        : Results.Created($"/api/auth/users/{dto.Id}", dto);
});

admin.MapPut("/users/{id:int}", async (AuthService svc, int id, UpdateUserRequest req) =>
{
    var dto = await svc.UpdateUserAsync(id, req);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

admin.MapDelete("/users/{id:int}", async (AuthService svc, int id) =>
{
    var ok = await svc.DeleteUserAsync(id);
    return ok ? Results.NoContent() : Results.NotFound();
});

admin.MapPost("/users/{id:int}/approve", async (AuthService svc, ClaimsPrincipal principal, int id) =>
{
    var approverId = GetUserId(principal) ?? 0;
    var ok = await svc.ApproveUserAsync(id, approverId);
    return ok ? Results.Ok(new { approved = true }) : Results.NotFound();
});

admin.MapPost("/users/{id:int}/reject", async (AuthService svc, int id, string? reason) =>
{
    var ok = await svc.RejectUserAsync(id, reason);
    return ok ? Results.Ok(new { rejected = true }) : Results.NotFound();
});

admin.MapGet("/roles", async (AuthService svc) =>
{
    var roles = await svc.GetAllRolesAsync();
    return Results.Ok(roles);
});

// ======================================================================
// ========================= PAYMENTS (CRUD) =============================
// ======================================================================
// Все эндпоинты ниже требуют авторизации (ограничение на CRUD навешано)
var paymentsGroup = app.MapGroup("/api/payments").RequireAuthorization();

paymentsGroup.MapGet("", async (PaymentContext context, DateTime? from, DateTime? to, int? clientId, int? caseId,
    CancellationToken ct) =>
{
    var query = context.Payments
        .AsQueryable()
        .WithPaymentIncludes();

    if (from.HasValue) query = query.Where(p => p.Date >= from.Value);
    if (to.HasValue) query = query.Where(p => p.Date <= to.Value);
    if (clientId.HasValue) query = query.Where(p => p.ClientId == clientId.Value);
    if (caseId.HasValue) query = query.Where(p => p.ClientCaseId == caseId.Value);

    return await query.AsNoTracking().OrderBy(p => p.Date).ToListAsync(ct);
});

paymentsGroup.MapGet("/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var payment = await context.Payments
        .AsQueryable()
        .WithPaymentIncludes()
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id, ct);

    return payment is not null ? Results.Ok(payment) : Results.NotFound();
});

paymentsGroup.MapPost("", async (PaymentContext context, Payment payment) =>
{
    if (payment.IncomeTypeId.HasValue)
    {
        var it = await context.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == payment.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != payment.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    payment.Account = string.IsNullOrWhiteSpace(payment.Account) ? null : payment.Account.Trim();
    context.Payments.Add(payment);
    await context.SaveChangesAsync();
    return Results.Created($"/api/payments/{payment.Id}", payment);
});

paymentsGroup.MapPut("/{id}", async (PaymentContext context, int id, Payment payment) =>
{
    var existing = await context.Payments.FindAsync(id);
    if (existing is null) return Results.NotFound();

    if (payment.IncomeTypeId.HasValue)
    {
        var it = await context.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == payment.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != payment.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    existing.Date = payment.Date;
    existing.Amount = payment.Amount;
    existing.Type = payment.Type;
    existing.Status = payment.Status;
    existing.Description = payment.Description;
    existing.IsPaid = payment.IsPaid;
    existing.PaidDate = payment.PaidDate;
    existing.Notes = payment.Notes;
    existing.ClientId = payment.ClientId;
    existing.ClientCaseId = payment.ClientCaseId;
    existing.DealTypeId = payment.DealTypeId;
    existing.IncomeTypeId = payment.IncomeTypeId;
    existing.PaymentSourceId = payment.PaymentSourceId;
    existing.PaymentStatusId = payment.PaymentStatusId;
    existing.Account = string.IsNullOrWhiteSpace(payment.Account) ? null : payment.Account.Trim();
    existing.AccountDate = payment.AccountDate;

    await context.SaveChangesAsync();
    return Results.Ok(existing);
});

paymentsGroup.MapDelete("/{id}", async (PaymentContext context, int id) =>
{
    var payment = await context.Payments.FindAsync(id);
    if (payment is null) return Results.NotFound();

    context.Payments.Remove(payment);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ===================== ACCOUNTS (подбор счетов) =======================
var accountsGroup = app.MapGroup("/api/accounts").RequireAuthorization();

accountsGroup.MapGet("", async (PaymentContext db, int? clientId, int? caseId, string? q, bool withDate = false,
    bool dedupe = false, int take = 50, CancellationToken ct = default) =>
{
    var query = db.Payments.AsNoTracking()
        .Where(p => p.Account != null && p.Account != "");

    if (clientId.HasValue) query = query.Where(p => p.ClientId == clientId.Value);
    if (caseId.HasValue) query = query.Where(p => p.ClientCaseId == caseId.Value);

    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim().ToLower();
        query = query.Where(p => p.Account!.ToLower().Contains(term));
    }

    if (!withDate)
    {
        var accounts = await query
            .GroupBy(p => p.Account!)
            .Select(g => new { Account = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Account)
            .Select(x => x.Account)
            .ToListAsync(ct);

        return Results.Ok(accounts);
    }

    var pairsQuery = query
        .Select(p => new
        {
            Account = p.Account!,
            AccountDate = p.AccountDate,
            SortDate = p.AccountDate ?? p.Date
        });

    if (dedupe)
        pairsQuery = pairsQuery.Distinct();

    var result = await pairsQuery
        .OrderByDescending(x => x.SortDate)
        .ThenBy(x => x.Account)
        .Select(x => new { account = x.Account, accountDate = x.AccountDate })
        .OrderByDescending(o => o.accountDate)
        .ToListAsync(ct);

    return Results.Ok(result);
});

// ======================================================================
// =========================== CLIENTS ==================================
// ======================================================================
var clientsGroup = app.MapGroup("/api/clients").RequireAuthorization();

clientsGroup.MapGet("", async (PaymentContext context, CancellationToken ct) =>
    await context.Clients.Include(c => c.Cases).AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct));

clientsGroup.MapGet("/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var client = await context.Clients
        .Include(c => c.Cases)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return client is not null ? Results.Ok(client) : Results.NotFound();
});

clientsGroup.MapGet("/{id}/stats", async (PaymentContext context, int id, int? caseId, CancellationToken ct) =>
{
    var client = await context.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    if (client is null) return Results.NotFound();

    var query = context.Payments
        .Where(p => p.ClientId == id)
        .Include(p => p.DealType)
        .Include(p => p.IncomeType)
        .Include(p => p.PaymentSource)
        .Include(p => p.PaymentStatusEntity)
        .AsQueryable();

    if (caseId.HasValue)
        query = query.Where(p => p.ClientCaseId == caseId.Value);

    var payments = await query
        .OrderByDescending(p => p.Date)
        .AsNoTracking()
        .ToListAsync(ct);

    var stats = new ClientStats
    {
        ClientId = client.Id,
        ClientName = client.Name,
        TotalIncome = payments.Where(p => p.Type == PaymentType.Income && p.IsPaid).Sum(p => p.Amount),
        TotalExpenses = payments.Where(p => p.Type == PaymentType.Expense && p.IsPaid).Sum(p => p.Amount),
        TotalPayments = payments.Count,
        PaidPayments = payments.Count(p => p.IsPaid),
        PendingPayments = payments.Count(p => !p.IsPaid),
        LastPaymentDate = payments.FirstOrDefault()?.Date,
        RecentPayments = payments.ToList()
    };

    stats.NetAmount = stats.TotalIncome - stats.TotalExpenses;
    return Results.Ok(stats);
});

clientsGroup.MapPost("", async (PaymentContext context, Client client) =>
{
    context.Clients.Add(client);
    await context.SaveChangesAsync();
    return Results.Created($"/api/clients/{client.Id}", client);
});

clientsGroup.MapPut("/{id}", async (PaymentContext context, int id, Client client) =>
{
    var existing = await context.Clients.FindAsync(id);
    if (existing is null) return Results.NotFound();

    existing.Name = client.Name;
    existing.Email = client.Email;
    existing.Phone = client.Phone;
    existing.Company = client.Company;
    existing.Address = client.Address;
    existing.Notes = client.Notes;
    existing.IsActive = client.IsActive;

    await context.SaveChangesAsync();
    return Results.Ok(existing);
});

clientsGroup.MapDelete("/{id}", async (PaymentContext context, int id) =>
{
    var client = await context.Clients.FindAsync(id);
    if (client is null) return Results.NotFound();

    var payments = await context.Payments.Where(p => p.ClientId == id).ToListAsync();
    foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
    await context.SaveChangesAsync();

    context.Clients.Remove(client);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ======================================================================
// ========================= CLIENT CASES ================================
// ======================================================================
var casesGroup = app.MapGroup("/api/cases").RequireAuthorization();

casesGroup.MapGet("", async (PaymentContext context, int? clientId, CancellationToken ct) =>
{
    var q = context.ClientCases
        .Include(c => c.Client)
        .AsQueryable();

    if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);

    return await q.AsNoTracking()
        .OrderBy(c => c.ClientId)
        .ThenBy(c => c.CreatedAt)
        .ToListAsync(ct);
});

casesGroup.MapGet("/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var entity = await context.ClientCases
        .Include(c => c.Client)
        .Include(c => c.Payments)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);

    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

casesGroup.MapPost("", async (PaymentContext context, ClientCase model) =>
{
    context.ClientCases.Add(model);
    await context.SaveChangesAsync();
    return Results.Created($"/api/cases/{model.Id}", model);
});

casesGroup.MapPut("/{id}", async (PaymentContext context, int id, ClientCase model) =>
{
    var existing = await context.ClientCases.FindAsync(id);
    if (existing is null) return Results.NotFound();

    existing.Title = model.Title;
    existing.Description = model.Description;
    existing.Status = model.Status;
    existing.ClientId = model.ClientId;

    await context.SaveChangesAsync();
    return Results.Ok(existing);
});

casesGroup.MapDelete("/{id}", async (PaymentContext context, int id) =>
{
    var existing = await context.ClientCases.FindAsync(id);
    if (existing is null) return Results.NotFound();

    var payments = await context.Payments.Where(p => p.ClientCaseId == id).ToListAsync();
    foreach (var p in payments) p.ClientCaseId = null;

    await context.SaveChangesAsync();

    context.ClientCases.Remove(existing);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ======================================================================
// =========================== DICTIONARIES ==============================
// ======================================================================
// Чтение словарей — авторизованные пользователи
var dictRead = app.MapGroup("/api/dictionaries").RequireAuthorization();

dictRead.MapGet("/deal-types", async (PaymentContext context, CancellationToken ct) =>
    await context.DealTypes.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct));

dictRead.MapGet("/income-types", async (PaymentContext context, PaymentType? paymentType, bool? isActive, CancellationToken ct) =>
{
    var q = context.IncomeTypes.AsNoTracking().AsQueryable();

    if (isActive.HasValue) q = q.Where(i => i.IsActive == isActive.Value);
    if (paymentType.HasValue) q = q.Where(i => i.PaymentType == paymentType.Value);

    return await q.OrderBy(i => i.Name).ToListAsync(ct);
});

dictRead.MapGet("/payment-sources", async (PaymentContext context, CancellationToken ct) =>
    await context.PaymentSources.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct));

dictRead.MapGet("/payment-statuses", async (PaymentContext context, CancellationToken ct) =>
    await context.PaymentStatuses.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct));

// Админские операции со словарями — только admin
var dictAdmin = app.MapGroup("/api/dictionaries").RequireAuthorization("Admin");

dictAdmin
    .MapDictionaryCrud<DealType>("/deal-types", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<DealType>();

dictAdmin
    .MapDictionaryCrud<IncomeType>("/income-types", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        e.PaymentType = m.PaymentType;
        return e;
    })
    .MapToggleActive<IncomeType>();

dictAdmin
    .MapDictionaryCrud<PaymentSource>("/payment-sources", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<PaymentSource>();

dictAdmin
    .MapDictionaryCrud<PaymentStatusEntity>("/payment-statuses", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<PaymentStatusEntity>();

// ======================================================================
// ============================= STATS ==================================
// ======================================================================
var stats = app.MapGroup("/api/stats").RequireAuthorization();

stats.MapGet("/month", async (PaymentContext context, int year, int month, CancellationToken ct) =>
{
    var startDate = new DateTime(year, month, 1);
    var endDate = startDate.AddMonths(1).AddDays(-1);

    var payments = await context.Payments
        .Where(p => p.Date >= startDate && p.Date <= endDate)
        .AsNoTracking()
        .ToListAsync(ct);

    var income = payments.Where(p => p.Type == PaymentType.Income && p.IsPaid).Sum(p => p.Amount);
    var expense = payments.Where(p => p.Type == PaymentType.Expense && p.IsPaid).Sum(p => p.Amount);
    var profit = income - expense;

    var completed = payments.Count(p => p.IsPaid);
    var pending = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Pending);
    var overdue = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Overdue);
    var total = payments.Count;

    var completionRate = total > 0 ? (double)completed / total * 100 : 0;

    return new MonthlyStats
    {
        Income = income,
        Expense = expense,
        Profit = profit,
        CompletionRate = Math.Round(completionRate, 1),
        Counts = new StatusCounts
        {
            Completed = completed,
            Pending = pending,
            Overdue = overdue,
            Total = total
        }
    };
});

// ======================================================================
// =========================== INSTALLMENTS ==============================
// ======================================================================
app.MapPost("/api/installments/calc",
    (InstallmentService service, InstallmentRequest request) => service.CalculateInstallment(request)
).RequireAuthorization();

// ======================================================================
// ============================== V1 ====================================
// ======================================================================
var apiV1 = app.MapGroup("/api/v1").RequireAuthorization();

// ---- payments (V1) ----
var paymentsV1 = apiV1.MapGroup("/payments").RequireAuthorization();

paymentsV1.MapGet("", async (
    PaymentContext db,
    DateTime? from,
    DateTime? to,
    int? clientId,
    int? caseId,
    string? search,
    string? sortBy,
    string? sortDir,
    CancellationToken ct
) =>
{
    var q = db.Payments
        .AsQueryable()
        .WithPaymentIncludes()
        .ApplyPaymentFilters(from, to, clientId, caseId, search)
        .ApplyPaymentSort(sortBy, sortDir);

    return Results.Ok(await q.AsNoTracking().ToListAsync(ct));
});

paymentsV1.MapGet("/{id:int}", async (PaymentContext db, int id, CancellationToken ct) =>
{
    var entity = await db.Payments
        .WithPaymentIncludes()
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id, ct);
    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

paymentsV1.MapPost("", async (PaymentContext db, Payment model) =>
{
    if (model.IncomeTypeId.HasValue)
    {
        var it = await db.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != model.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    model.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
    db.Payments.Add(model);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/payments/{model.Id}", model);
});

paymentsV1.MapPut("/{id:int}", async (PaymentContext db, int id, Payment model) =>
{
    var e = await db.Payments.FindAsync(id);
    if (e is null) return Results.NotFound();

    if (model.IncomeTypeId.HasValue)
    {
        var it = await db.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != model.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    e.Date = model.Date;
    e.Amount = model.Amount;
    e.Type = model.Type;
    e.Status = model.Status;
    e.Description = model.Description;
    e.IsPaid = model.IsPaid;
    e.PaidDate = model.PaidDate;
    e.Notes = model.Notes;
    e.ClientId = model.ClientId;
    e.ClientCaseId = model.ClientCaseId;
    e.DealTypeId = model.DealTypeId;
    e.IncomeTypeId = model.IncomeTypeId;
    e.PaymentSourceId = model.PaymentSourceId;
    e.PaymentStatusId = model.PaymentStatusId;
    e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
    e.AccountDate = model.AccountDate;

    await db.SaveChangesAsync();
    return Results.Ok(e);
});

paymentsV1.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
{
    var e = await db.Payments.FindAsync(id);
    if (e is null) return Results.NotFound();
    db.Payments.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---- clients (V1) ----
var clientsV1 = apiV1.MapGroup("/clients").RequireAuthorization();

clientsV1.MapGet("", async (
    PaymentContext db,
    string? search,
    bool? isActive,
    string? sortBy,
    string? sortDir,
    CancellationToken ct
) =>
{
    var q = db.Clients
        .AsQueryable()
        .ApplyClientFilters(search, isActive)
        .ApplyClientSort(sortBy, sortDir);

    return Results.Ok(await q.AsNoTracking().ToListAsync(ct));
});

clientsV1.MapGet("/{id:int}", async (PaymentContext db, int id, CancellationToken ct) =>
{
    var client = await db.Clients
        .Include(c => c.Cases)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return client is not null ? Results.Ok(client) : Results.NotFound();
});

clientsV1.MapPost("", async (PaymentContext db, Client model) =>
{
    db.Clients.Add(model);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/clients/{model.Id}", model);
});

clientsV1.MapPut("/{id:int}", async (PaymentContext db, int id, Client model) =>
{
    var e = await db.Clients.FindAsync(id);
    if (e is null) return Results.NotFound();

    e.Name = model.Name;
    e.Email = model.Email;
    e.Phone = model.Phone;
    e.Company = model.Company;
    e.Address = model.Address;
    e.Notes = model.Notes;
    e.IsActive = model.IsActive;

    await db.SaveChangesAsync();
    return Results.Ok(e);
});

clientsV1.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
{
    var e = await db.Clients.FindAsync(id);
    if (e is null) return Results.NotFound();

    var payments = await db.Payments.Where(p => p.ClientId == id).ToListAsync();
    foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
    await db.SaveChangesAsync();

    db.Clients.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---- cases (V1) ----
var casesV1 = apiV1.MapGroup("/cases").RequireAuthorization();

casesV1.MapGet("", async (
    PaymentContext db,
    int? clientId,
    ClientCaseStatus? status,
    string? search,
    string? sortBy,
    string? sortDir,
    CancellationToken ct
) =>
{
    var q = db.ClientCases
        .AsQueryable()
        .WithCaseIncludes()
        .ApplyCaseFilters(clientId, status, search)
        .ApplyCaseSort(sortBy, sortDir);

    return Results.Ok(await q.AsNoTracking().ToListAsync(ct));
});

casesV1.MapGet("/{id:int}", async (PaymentContext db, int id, CancellationToken ct) =>
{
    var entity = await db.ClientCases
        .WithCaseIncludes(includePayments: true)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

casesV1.MapPost("", async (PaymentContext db, ClientCase model) =>
{
    db.ClientCases.Add(model);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/cases/{model.Id}", model);
});

casesV1.MapPut("/{id:int}", async (PaymentContext db, int id, ClientCase model) =>
{
    var e = await db.ClientCases.FindAsync(id);
    if (e is null) return Results.NotFound();

    e.Title = model.Title;
    e.Description = model.Description;
    e.Status = model.Status;
    e.ClientId = model.ClientId;

    await db.SaveChangesAsync();
    return Results.Ok(e);
});

casesV1.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
{
    var e = await db.ClientCases.FindAsync(id);
    if (e is null) return Results.NotFound();

    var payments = await db.Payments.Where(p => p.ClientCaseId == id).ToListAsync();
    foreach (var p in payments) p.ClientCaseId = null;
    await db.SaveChangesAsync();

    db.ClientCases.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ======================================================================
// ============================== V2 (Paged) ============================
// ======================================================================
var apiV2 = app.MapGroup("/api/v2").RequireAuthorization();

// ---- payments (V2) ----
var paymentsV2 = apiV2.MapGroup("/payments").RequireAuthorization();

paymentsV2.MapGet("", async (
    PaymentContext db,
    DateTime? from,
    DateTime? to,
    int? clientId,
    int? caseId,
    string? search,
    string? sortBy,
    string? sortDir,
    int page = 1,
    int pageSize = 50,
    CancellationToken ct = default) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 500);

    var q = db.Payments.AsQueryable()
        .WithPaymentIncludes()
        .ApplyPaymentFilters(from, to, clientId, caseId, search)
        .ApplyPaymentSort(sortBy, sortDir);

    var total = await q.CountAsync(ct);
    var items = await q
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .AsNoTracking()
        .ToListAsync(ct);

    return Results.Ok(new { items, total, page, pageSize });
});

paymentsV2.MapGet("/{id:int}", async (PaymentContext db, int id, CancellationToken ct) =>
{
    var entity = await db.Payments
        .WithPaymentIncludes()
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id, ct);
    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

paymentsV2.MapPost("", async (PaymentContext db, Payment model) =>
{
    if (model.IncomeTypeId.HasValue)
    {
        var it = await db.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != model.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    model.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
    db.Payments.Add(model);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v2/payments/{model.Id}", model);
});

paymentsV2.MapPut("/{id:int}", async (PaymentContext db, int id, Payment model) =>
{
    var e = await db.Payments.FindAsync(id);
    if (e is null) return Results.NotFound();

    if (model.IncomeTypeId.HasValue)
    {
        var it = await db.IncomeTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
        if (it is null) return Results.BadRequest("Unknown IncomeTypeId");
        if (it.PaymentType != model.Type)
            return Results.BadRequest("IncomeType.PaymentType mismatches payment.Type");
    }

    e.Date = model.Date;
    e.Amount = model.Amount;
    e.Type = model.Type;
    e.Status = model.Status;
    e.Description = model.Description;
    e.IsPaid = model.IsPaid;
    e.PaidDate = model.PaidDate;
    e.Notes = model.Notes;
    e.ClientId = model.ClientId;
    e.ClientCaseId = model.ClientCaseId;
    e.DealTypeId = model.DealTypeId;
    e.IncomeTypeId = model.IncomeTypeId;
    e.PaymentSourceId = model.PaymentSourceId;
    e.PaymentStatusId = model.PaymentStatusId;
    e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
    e.AccountDate = model.AccountDate;

    await db.SaveChangesAsync();
    return Results.Ok(e);
});

paymentsV2.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
{
    var e = await db.Payments.FindAsync(id);
    if (e is null) return Results.NotFound();
    db.Payments.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---- cases (V2) ----
var casesV2 = apiV2.MapGroup("/cases").RequireAuthorization();

casesV2.MapGet("", async (
    PaymentContext db,
    int? clientId,
    ClientCaseStatus? status,
    string? search,
    string? sortBy,
    string? sortDir,
    int page = 1,
    int pageSize = 50,
    CancellationToken ct = default) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 500);

    var q = db.ClientCases.AsQueryable()
        .WithCaseIncludes()
        .ApplyCaseFilters(clientId, status, search)
        .ApplyCaseSort(sortBy, sortDir);

    var total = await q.CountAsync(ct);
    var items = await q
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .AsNoTracking()
        .ToListAsync(ct);

    return Results.Ok(new { items, total, page, pageSize });
});

casesV2.MapGet("/{id:int}", async (PaymentContext db, int id, CancellationToken ct) =>
{
    var entity = await db.ClientCases
        .WithCaseIncludes(includePayments: true)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

casesV2.MapPost("", async (PaymentContext db, ClientCase model) =>
{
    db.ClientCases.Add(model);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v2/cases/{model.Id}", model);
});

casesV2.MapPut("/{id:int}", async (PaymentContext db, int id, ClientCase model) =>
{
    var e = await db.ClientCases.FindAsync(id);
    if (e is null) return Results.NotFound();

    e.Title = model.Title;
    e.Description = model.Description;
    e.Status = model.Status;
    e.ClientId = model.ClientId;

    await db.SaveChangesAsync();
    return Results.Ok(e);
});

casesV2.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
{
    var e = await db.ClientCases.FindAsync(id);
    if (e is null) return Results.NotFound();

    var payments = await db.Payments.Where(p => p.ClientCaseId == id).ToListAsync();
    foreach (var p in payments) p.ClientCaseId = null;
    await db.SaveChangesAsync();

    db.ClientCases.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---- clients (V2) ----
var clientsV2 = apiV2.MapGroup("/clients").RequireAuthorization();

clientsV2.MapGet("", async (
    PaymentContext context,
    string? search,
    bool? isActive,
    string? sortBy,
    string? sortDir,
    int page = 1,
    int pageSize = 50,
    CancellationToken ct = default) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 500);

    var q = context.Clients.AsQueryable();

    if (!string.IsNullOrWhiteSpace(search))
    {
        search = search.Trim();
        q = q.Where(c =>
            (c.Name != null && EF.Functions.Like(c.Name, $"%{search}%")) ||
            (c.Email != null && EF.Functions.Like(c.Email, $"%{search}%")) ||
            (c.Phone != null && EF.Functions.Like(c.Phone, $"%{search}%")) ||
            (c.Company != null && EF.Functions.Like(c.Company, $"%{search}%")) ||
            (c.Address != null && EF.Functions.Like(c.Address, $"%{search}%")));
    }

    if (isActive.HasValue)
        q = q.Where(c => c.IsActive == isActive.Value);

    bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
    q = (sortBy?.ToLowerInvariant()) switch
    {
        "createdat" => (desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt)),
        _ => (desc ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
    };

    var total = await q.CountAsync(ct);
    var items = await q.AsNoTracking().Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

    return Results.Ok(new { items, total, page, pageSize });
});

clientsV2.MapGet("/{id:int}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var client = await context.Clients
        .Include(c => c.Cases)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return client is not null ? Results.Ok(client) : Results.NotFound();
});

clientsV2.MapPost("", async (PaymentContext context, Client client) =>
{
    context.Clients.Add(client);
    await context.SaveChangesAsync();
    return Results.Created($"/api/v2/clients/{client.Id}", client);
});

clientsV2.MapPut("/{id:int}", async (PaymentContext context, int id, Client client) =>
{
    var existing = await context.Clients.FindAsync(id);
    if (existing is null) return Results.NotFound();

    existing.Name = client.Name;
    existing.Email = client.Email;
    existing.Phone = client.Phone;
    existing.Company = client.Company;
    existing.Address = client.Address;
    existing.Notes = client.Notes;
    existing.IsActive = client.IsActive;

    await context.SaveChangesAsync();
    return Results.Ok(existing);
});

clientsV2.MapDelete("/{id:int}", async (PaymentContext context, int id) =>
{
    var client = await context.Clients.FindAsync(id);
    if (client is null) return Results.NotFound();

    var payments = await context.Payments.Where(p => p.ClientId == id).ToListAsync();
    foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
    await context.SaveChangesAsync();

    context.Clients.Remove(client);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ======================================================================
app.MapFallbackToFile("index.html");

app.Run();

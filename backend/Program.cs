using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

static string NormalizeSqliteConnection(string raw)
{
    var b = new SqliteConnectionStringBuilder(raw);
    if (!Path.IsPathRooted(b.DataSource))
    {
        b.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, b.DataSource));
    }
    return b.ToString();
}

// DbContext (SQLite)
var rawCs = builder.Configuration.GetConnectionString("Default") ?? "Data Source=payplanner.db";
var normalizedCs = NormalizeSqliteConnection(rawCs);
builder.Services.AddDbContext<PaymentContext>(options => options.UseSqlite(normalizedCs));

// Services
builder.Services.AddScoped<InstallmentService>(); 
builder.Services.AddHostedService<PaymentStatusUpdater>(); 
builder.Services.AddHostedService<DatabaseBackupService>(); 
builder.Services.AddScoped<StatsSummaryService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Response compression (в т.ч. JSON)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});

// URLs override
var urls = builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(urls))
{
    builder.WebHost.UseUrls(urls);
}

var app = builder.Build();

// Swagger UI
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseResponseCompression();

app.UseDefaultFiles();
app.UseStaticFiles();

// DB init
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<PaymentContext>();
    await ctx.Database.MigrateAsync();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>(); 
    var seedClients = cfg.GetValue<bool>("Seed:ClientsAndPayments");
    await SeedDataService.SeedAsync(ctx, seedClients);
}

// Health
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// -------------------- PAYMENTS --------------------

app.MapGet("/api/payments", async (PaymentContext context, DateTime? from, DateTime? to, int? clientId, int? caseId,
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

app.MapGet("/api/payments/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var payment = await context.Payments
        .AsQueryable()
        .WithPaymentIncludes()
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id, ct);

    return payment is not null ? Results.Ok(payment) : Results.NotFound();
});

app.MapPost("/api/payments", async (PaymentContext context, Payment payment) =>
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

app.MapPut("/api/payments/{id}", async (PaymentContext context, int id, Payment payment) =>
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

app.MapDelete("/api/payments/{id}", async (PaymentContext context, int id) =>
{
    var payment = await context.Payments.FindAsync(id);
    if (payment is null) return Results.NotFound();

    context.Payments.Remove(payment);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// -------------------- ACCOUNTS --------------------
app.MapGet("/api/accounts", async (PaymentContext db, int? clientId, int? caseId, string? q, bool withDate = false,
    bool dedupe = false, int take = 50, CancellationToken ct = default) =>
{
    var query = db.Payments.AsNoTracking()
        .Where(p => p.Account != null && p.Account != "");

    if (clientId.HasValue)
        query = query.Where(p => p.ClientId == clientId.Value);

    if (caseId.HasValue)
        query = query.Where(p => p.ClientCaseId == caseId.Value);

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
            //.Take(Math.Clamp(take, 1, 200))
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
        //.Take(Math.Clamp(take, 1, 500))
        .Select(x => new { account = x.Account, accountDate = x.AccountDate })
        .OrderByDescending(o => o.accountDate)
        .ToListAsync(ct);

    return Results.Ok(result);
});

// -------------------- CLIENTS --------------------
app.MapGet("/api/clients", async (PaymentContext context, CancellationToken ct) =>
    await context.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct));

app.MapGet("/api/clients/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var client = await context.Clients
        .Include(c => c.Cases)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return client is not null ? Results.Ok(client) : Results.NotFound();
});

app.MapGet("/api/clients/{id}/stats", async (PaymentContext context, int id, int? caseId, CancellationToken ct) =>
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
        TotalIncome = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.Amount),
        TotalExpenses = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.Amount),
        TotalPayments = payments.Count,
        PaidPayments = payments.Count(p => p.IsPaid),
        PendingPayments = payments.Count(p => !p.IsPaid),
        LastPaymentDate = payments.FirstOrDefault()?.Date,
        RecentPayments = payments/*.Take(10)*/.ToList()
    };

    stats.NetAmount = stats.TotalIncome - stats.TotalExpenses;
    return Results.Ok(stats);
});

app.MapPost("/api/clients", async (PaymentContext context, Client client) =>
{
    context.Clients.Add(client);
    await context.SaveChangesAsync();
    return Results.Created($"/api/clients/{client.Id}", client);
});

app.MapPut("/api/clients/{id}", async (PaymentContext context, int id, Client client) =>
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

app.MapDelete("/api/clients/{id}", async (PaymentContext context, int id) =>
{
    var client = await context.Clients.FindAsync(id);
    if (client is null) return Results.NotFound();

    // —охран€ем историю: обнул€ем ссылки у платежей, дела удал€тс€ каскадом
    var payments = await context.Payments.Where(p => p.ClientId == id).ToListAsync();
    foreach (var p in payments)
    {
        p.ClientId = null;
        p.ClientCaseId = null;
    }

    await context.SaveChangesAsync();

    context.Clients.Remove(client);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// -------------------- CLIENT CASES --------------------

app.MapGet("/api/cases", async (PaymentContext context, int? clientId, CancellationToken ct) =>
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

app.MapGet("/api/cases/{id}", async (PaymentContext context, int id, CancellationToken ct) =>
{
    var entity = await context.ClientCases
        .Include(c => c.Client)
        .Include(c => c.Payments)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);

    return entity is not null ? Results.Ok(entity) : Results.NotFound();
});

app.MapPost("/api/cases", async (PaymentContext context, ClientCase model) =>
{
    context.ClientCases.Add(model);
    await context.SaveChangesAsync();
    return Results.Created($"/api/cases/{model.Id}", model);
});

app.MapPut("/api/cases/{id}", async (PaymentContext context, int id, ClientCase model) =>
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

app.MapDelete("/api/cases/{id}", async (PaymentContext context, int id) =>
{
    var existing = await context.ClientCases.FindAsync(id);
    if (existing is null) return Results.NotFound();

    // ѕри удалении дела Ч отцепл€ем платежи (ClientCaseId = null)
    var payments = await context.Payments.Where(p => p.ClientCaseId == id).ToListAsync();
    foreach (var p in payments) p.ClientCaseId = null;

    await context.SaveChangesAsync();

    context.ClientCases.Remove(existing);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// -------------------- DICTIONARIES --------------------

app.MapGet("/api/dictionaries/deal-types", async (PaymentContext context, CancellationToken ct) =>
    await context.DealTypes/*.Where(d => d.IsActive)*/.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct));

app.MapGet("/api/dictionaries/income-types", async (PaymentContext context, PaymentType? paymentType, bool? isActive,
    CancellationToken ct) =>
{
    var q = context.IncomeTypes.AsNoTracking().AsQueryable();

    if (isActive.HasValue)
        q = q.Where(i => i.IsActive == isActive.Value);

    if (paymentType.HasValue)
        q = q.Where(i => i.PaymentType == paymentType.Value);

    return await q.OrderBy(i => i.Name).ToListAsync(ct);
});

app.MapGet("/api/dictionaries/payment-sources", async (PaymentContext context, CancellationToken ct) =>
    await context.PaymentSources/*.Where(p => p.IsActive)*/.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct));

app.MapGet("/api/dictionaries/payment-statuses", async (PaymentContext context, CancellationToken ct) =>
    await context.PaymentStatuses/*.Where(s => s.IsActive)*/.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct));

// -------------------- STATS --------------------

app.MapGet("/api/stats/month", async (PaymentContext context, int year, int month, CancellationToken ct) =>
{
    var startDate = new DateTime(year, month, 1);
    var endDate = startDate.AddMonths(1).AddDays(-1);

    var payments = await context.Payments
        .Where(p => p.Date >= startDate && p.Date <= endDate)
        .AsNoTracking()
        .ToListAsync(ct);

    var income = payments.Where(p => p.Type == PaymentType.Income && p.IsPaid).Sum(p => p.Amount);
    var expense = payments.Where(p => p.Type == PaymentType.Expense && p.IsPaid).Sum(p => p.Amount);
    //var profit = income - expense;

    var completed = payments.Count(p => p.IsPaid);
    var pending = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Pending);
    var overdue = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Overdue);
    var profit = payments.Count(p => p.Status == PaymentStatus.Completed);
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

// -------------------- INSTALLMENTS --------------------

app.MapPost("/api/installments/calc", (InstallmentService service, InstallmentRequest request) =>
    service.CalculateInstallment(request));

// ==================== V1: те же CRUD, но фильтры без пагинации ====================
var apiV1 = app.MapGroup("/api/v1");

// ---- payments (V1) ----
var paymentsV1 = apiV1.MapGroup("/payments");

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
var clientsV1 = apiV1.MapGroup("/clients");

clientsV1.MapGet("", async (
    PaymentContext db,
    string? search,          // Name/Email/Phone/Company/Address
    bool? isActive,          // фильтр активности
    string? sortBy,          // name|createdAt (по умолчанию name)
    string? sortDir,         // asc|desc (по умолчанию asc)
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
var casesV1 = apiV1.MapGroup("/cases");

casesV1.MapGet("", async (
    PaymentContext db,
    int? clientId,
    ClientCaseStatus? status,
    string? search,          // Title/Description
    string? sortBy,          // createdAt|title|status (по умолчанию createdAt)
    string? sortDir,         // asc|desc (по умолчанию asc)
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

// ==================== V2: PAGED payments/cases/clients ====================
var apiV2 = app.MapGroup("/api/v2");

// ---- payments (V2) ----
var paymentsV2 = apiV2.MapGroup("/payments");

paymentsV2.MapGet("", async (
    PaymentContext db,
    DateTime? from,
    DateTime? to,
    int? clientId,
    int? caseId,
    string? search,          // Description/Notes
    string? sortBy,          // date|amount|createdAt (по умолчанию date)
    string? sortDir,         // asc|desc (по умолчанию asc)
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
var casesV2 = apiV2.MapGroup("/cases");

casesV2.MapGet("", async (
    PaymentContext db,
    int? clientId,
    ClientCaseStatus? status, // фильтр по статусу
    string? search,           // Title/Description
    string? sortBy,           // createdAt|title|status (по умолчанию createdAt)
    string? sortDir,          // asc|desc (по умолчанию asc)
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
var clientsV2 = apiV2.MapGroup("/clients");

clientsV2.MapGet("", async (
    PaymentContext context,
    string? search,          // ищем по Name/Email/Phone/Company/Address
    bool? isActive,          // фильтр активности
    string? sortBy,          // name|createdAt (по умолчанию name)
    string? sortDir,         // asc|desc (по умолчанию asc)
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
        _ => (desc ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)), // name по умолчанию
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

// ==================== V2: STATS Ч сводка по нескольким мес€цам ====================
// ѕример: /api/v2/stats/months?startYear=2025&startMonth=1&endYear=2025&endMonth=6
var statsV2 = apiV2.MapGroup("/stats");

statsV2.MapGet("/summary", async (
    StatsSummaryService svc, int? clientId, int? caseId, DateTime? from, DateTime? to, string? period, PaymentType? type,
    CancellationToken ct) =>
{
    var res = await svc.GetAsync(clientId, caseId, from, to, period, type, ct);
    return Results.Ok(res);
});

statsV2.MapGet("/months", async (PaymentContext context, int startYear, int startMonth, int endYear, int endMonth,
    CancellationToken ct) =>
{
    if (startMonth is < 1 or > 12 || endMonth is < 1 or > 12)
        return Results.BadRequest("ћес€ц должен быть 1..12.");

    var start = new DateTime(startYear, startMonth, 1);
    var end = new DateTime(endYear, endMonth, 1).AddMonths(1).AddDays(-1);

    if (end < start) return Results.BadRequest("ƒиапазон мес€цев указан некорректно.");

    var rows = await context.Payments
        .Where(p => p.Date >= start && p.Date <= end)
        .Select(p => new
        {
            Year = p.Date.Year,
            Month = p.Date.Month,
            p.Type,
            p.IsPaid,
            p.Status,
            p.Amount
        })
        .AsNoTracking()
        .ToListAsync(ct);

    var grouped = rows
        .GroupBy(x => new { x.Year, x.Month })
        .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
        .Select(g =>
        {
            var total = g.Count();
            var completed = g.Count(x => x.IsPaid);
            var pending = g.Count(x => !x.IsPaid && x.Status == PaymentStatus.Pending);
            var overdue = g.Count(x => !x.IsPaid && x.Status == PaymentStatus.Overdue);

            var income = g.Where(x => x.Type == PaymentType.Income && x.IsPaid).Sum(x => x.Amount);
            var expense = g.Where(x => x.Type == PaymentType.Expense && x.IsPaid).Sum(x => x.Amount);
            var profit = income - expense;
            var rate = total > 0 ? Math.Round((double)completed / total * 100, 1) : 0;

            return new
            {
                year = g.Key.Year,
                month = g.Key.Month,
                period = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                income,
                expense,
                profit,
                completionRate = rate,
                counts = new
                {
                    completed,
                    pending,
                    overdue,
                    total
                }
            };
        })
        .ToList();

    return Results.Ok(new
    {
        start = new { year = start.Year, month = start.Month },
        end = new { year = end.Year, month = end.Month },
        items = grouped
    });
});

// ==================== DICTIONARIES ADMIN: CRUD + toggle-active ====================
var dictsGroup = app.MapGroup("/api/dictionaries");

// DealType
dictsGroup
    .MapDictionaryCrud<DealType>("/deal-types", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<DealType>();

// IncomeType
dictsGroup
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

// PaymentSource
dictsGroup
    .MapDictionaryCrud<PaymentSource>("/payment-sources", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<PaymentSource>();

// PaymentStatusEntity
dictsGroup
    .MapDictionaryCrud<PaymentStatusEntity>("/payment-statuses", (e, m) =>
    {
        e.Name = m.Name;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        return e;
    })
    .MapToggleActive<PaymentStatusEntity>();

// SPA fallback
app.MapFallbackToFile("index.html");

app.Run();
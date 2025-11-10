using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Ïåðâè÷íîå íàïîëíåíèå ÁÄ òåñòîâûìè äàííûìè: ðîëè, àäìèí, êëèåíòû, äåëà, ñëîâàðè è ïëàòåæè.
    /// </summary>
    public static class SeedDataService
    {
        /// <summary>
        /// Âûïîëíèòü ñèäû.
        /// </summary>
        /// <param name="context">DbContext</param>
        /// <param name="seedClientsAndPayments">
        /// Åñëè true — äîáàâèòü êëèåíòîâ, äåëà è ïëàòåæè (äàæå åñëè òàáëèöà íå ïóñòàÿ).
        /// Åñëè false — êëèåíòû/äåëà/ïëàòåæè ñèäÿòñÿ òîëüêî êîãäà Clients ïóñò.
        /// </param>
        public static async Task SeedAsync(PaymentContext context, bool seedClientsAndPayments = false)
        {
            await SeedDictionariesAsync(context);          // ñëîâàðè
            await SeedRolesAsync(context);                 // ðîëè (admin, user)
            await SeedAdminUserAsync(context);             // àäìèíèñòðàòîð

            // Âîçìîæíîñòü îòêëþ÷èòü êëèåíòñêèå ñèäû íà ïðîäå:
            var skipClientsByEnv =
                string.Equals(Environment.GetEnvironmentVariable("PAYPLANNER_SKIP_CLIENT_SEED"), "1",
                    StringComparison.OrdinalIgnoreCase);

            var shouldSeedClients =
                !skipClientsByEnv && (seedClientsAndPayments || !await context.Clients.AnyAsync());

            if (shouldSeedClients)
            {
                try
                {
                    await SeedClientsCasesPaymentsAsync(context);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SEED][Clients] skipped due to error: {ex.GetType().Name}: {ex.Message}");
                    // Íå âàëèì ïðîöåññ — ïðîñòî ïðîïóñòèì êëèåíòñêóþ ÷àñòü
                }
            }
        }

        /// <summary>
        /// Ñëîâàðè (deal types, income types, sources, statuses).
        /// </summary>
        private static async Task SeedDictionariesAsync(PaymentContext context)
        {
            if (!await context.DealTypes.AnyAsync())
            {
                var dealTypes = new[]
                {
                    new DealType { Name = "Êîíñàëòèíã",      Description = "Ïðîôåññèîíàëüíûå êîíñóëüòàöèîííûå óñëóãè", ColorHex = "#3B82F6" },
                    new DealType { Name = "Ïðîäàæà òîâàðà",   Description = "Ïðÿìàÿ ïðîäàæà ïðîäóêöèè",                ColorHex = "#10B981" },
                    new DealType { Name = "Ïîäïèñêà",         Description = "Ðåãóëÿðíûå ïîäïèñíûå óñëóãè",             ColorHex = "#8B5CF6" },
                    new DealType { Name = "Ïðîåêò",           Description = "Ðàáîòà ïî ïðîåêòó ñ ôèêñèðîâàííûì îáú¸ìîì", ColorHex = "#F59E0B" },
                    new DealType { Name = "Îáñëóæèâàíèå",     Description = "Äîëãîñðî÷íûå êîíòðàêòû íà îáñëóæèâàíèå",  ColorHex = "#EF4444" }
                };
                context.DealTypes.AddRange(dealTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Income).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    new IncomeType { Name = "Äîõîä îò óñëóã",  Description = "Äîõîä îò îêàçàíèÿ óñëóã",       ColorHex = "#10B981", PaymentType = PaymentType.Income },
                    new IncomeType { Name = "Ïðî÷èå äîõîäû",    Description = "Ïðî÷èå äîõîäû",                ColorHex = "#064E3B", PaymentType = PaymentType.Income },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Expense).AnyAsync())
            {
                var expenseTypes = new[]
                {
                    new IncomeType { Name = "Ïðî÷èå ðàñõîäû", Description = "Èíûå ðàñõîäû", ColorHex = "#991B1B", PaymentType = PaymentType.Expense },
                };
                context.IncomeTypes.AddRange(expenseTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.PaymentSources.AnyAsync())
            {
                var paymentSources = new[]
                {
                    new PaymentSource { Name = "Áàíêîâñêèé ïåðåâîä", Description = "Ïðÿìîé áàíêîâñêèé ïåðåâîä", ColorHex = "#6B7280" },
                    new PaymentSource { Name = "Áàíêîâñêàÿ êàðòà",   Description = "Îïëàòà áàíêîâñêîé êàðòîé",  ColorHex = "#4B5563" },
                    new PaymentSource { Name = "PayPal",             Description = "Îïëàòà ÷åðåç PayPal",       ColorHex = "#374151" },
                    new PaymentSource { Name = "×åê",                Description = "Îïëàòà áàíêîâñêèì ÷åêîì",   ColorHex = "#1F2937" },
                    new PaymentSource { Name = "Íàëè÷íûå",           Description = "Îïëàòà íàëè÷íûìè",          ColorHex = "#111827" }
                };
                context.PaymentSources.AddRange(paymentSources);
                await context.SaveChangesAsync();
            }

            if (!await context.PaymentStatuses.AnyAsync())
            {
                var paymentStatuses = new[]
                {
                    new PaymentStatusEntity { Name = "Îæèäàåòñÿ",  Description = "Îïëàòà íàõîäèòñÿ â îæèäàíèè",  ColorHex = "#F59E0B" },
                    new PaymentStatusEntity { Name = "Âûïîëíåíî",  Description = "Îïëàòà óñïåøíî çàâåðøåíà",    ColorHex = "#10B981" },
                    new PaymentStatusEntity { Name = "Ïðîñðî÷åíî", Description = "Ñðîê îïëàòû èñò¸ê",           ColorHex = "#EF4444" }
                };
                context.PaymentStatuses.AddRange(paymentStatuses);
                await context.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Ñîçäà¸ò ðîëè admin è user (åñëè îòñóòñòâóþò).
        /// </summary>
        private static async Task SeedRolesAsync(PaymentContext context)
        {
            if (!await context.Roles.AnyAsync())
            {
                context.Roles.AddRange(
                    new Role { Name = "admin", Description = "Àäìèíèñòðàòîð ñèñòåìû" },
                    new Role { Name = "user", Description = "Îáû÷íûé ïîëüçîâàòåëü" }
                );
                await context.SaveChangesAsync();
                return;
            }

            // Äîñåÿòü íåäîñòàþùèå ðîëè
            await EnsureRoleAsync(context, "admin", "Àäìèíèñòðàòîð ñèñòåìû");
            await EnsureRoleAsync(context, "user", "Îáû÷íûé ïîëüçîâàòåëü");
        }

        /// <summary>
        /// Ñîçäà¸ò ïîëüçîâàòåëÿ-àäìèíèñòðàòîðà, åñëè åãî åù¸ íåò.
        /// </summary>
        private static async Task SeedAdminUserAsync(PaymentContext context)
        {
            var adminEmail = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_EMAIL") ?? "admin@payplanner.local";
            var adminPassword = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_PASSWORD") ?? "ChangeMeAdmin#12345";
            var adminFullName = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_FULLNAME") ?? "System Administrator";

            var exists = await context.Users.AsNoTracking().AnyAsync(u => u.Email == adminEmail);
            if (exists) return;

            var adminRoleId = await context.Roles
                .Where(r => r.Name == "admin")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (adminRoleId == 0)
            {
                context.Roles.Add(new Role { Name = "admin", Description = "Àäìèíèñòðàòîð ñèñòåìû" });
                await context.SaveChangesAsync();
                adminRoleId = await context.Roles.Where(r => r.Name == "admin").Select(r => r.Id).FirstAsync();
            }

            var hasher = new PasswordHasher<User>(
                Options.Create(new PasswordHasherOptions
                {
                    CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                    IterationCount = 210_000
                })
            );

            var now = DateTime.UtcNow;

            var admin = new User
            {
                Email = adminEmail,
                FullName = adminFullName,
                RoleId = adminRoleId,
                IsActive = true,
                IsApproved = true,
                ApprovedAt = now,
                ApprovedByUserId = null,
                CreatedAt = now,
                UpdatedAt = now
            };

            admin.PasswordHash = hasher.HashPassword(admin, adminPassword);

            context.Users.Add(admin);
            await context.SaveChangesAsync();
        }

        // ----------------------- íèæå — ñèä êëèåíòîâ/äåë, ïëàòåæåé -----------------------

        private static async Task SeedClientsCasesPaymentsAsync(PaymentContext context)
        {
            // ×èòàåì ñïðàâî÷íèêè è ñòðîèì áåçîïàñíûå êàðòû (áåç ïàäåíèÿ íà äóáëÿõ)
            var dealTypes = await context.DealTypes.AsNoTracking().ToListAsync();
            var dealTypeByName = BuildNameMap(dealTypes, d => d.Name, d => d.Id);

            var incomeTypes = await context.IncomeTypes.AsNoTracking().ToListAsync();
            var incomeTypeByName = BuildNameMap(incomeTypes, i => i.Name, i => i.Id);

            var sources = await context.PaymentSources.AsNoTracking().ToListAsync();
            var sourceByName = BuildNameMap(sources, s => s.Name, s => s.Id);

            var statuses = await context.PaymentStatuses.AsNoTracking().ToListAsync();
            var statusByName = BuildNameMap(statuses, s => s.Name, s => s.Id);

            // Èäåìïîòåíòíûå êëèåíòû
            var clients = new[]
            {
                new Client { Name = "Èâàí Èâàíîâ",      Email = "ivan@example.com",  Phone = "+7-900-010-01-01", Company = "ÒåõÊîðï",                Address = "óë. Ëåíèíà, ä. 10, Ìîñêâà" },
                new Client { Name = "Ìàðèÿ Ïåòðîâà",    Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "Ñòóäèÿ Äèçàéíà",         Address = "ïð. Ìèðà, ä. 25, Ñàíêò-Ïåòåðáóðã" },
                new Client { Name = "Àëåêñåé Ñìèðíîâ",  Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "Ñìèðíîâ Èíäàñòðèç",      Address = "óë. Ñîâåòñêàÿ, ä. 5, Åêàòåðèíáóðã" },
                new Client { Name = "Åêàòåðèíà Ñèäîðîâà", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "Èííîâàöèîííûå Ëàáîðàòîðèè", Address = "óë. Ãàãàðèíà, ä. 12, Íîâîñèáèðñê" },
                new Client { Name = "Äìèòðèé Êóçíåöîâ", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "Êóçíåöîâ Êîíñàëòèíã", Address = "óë. Ïóøêèíà, ä. 8, Êàçàíü" }
            };

            var existingClients = await context.Clients.AsNoTracking().ToListAsync();
            var addedClients = new List<Client>();

            foreach (var c in clients)
            {
                var found = existingClients.FirstOrDefault(ec =>
                    (!string.IsNullOrWhiteSpace(c.Email) &&
                        string.Equals(ec.Email, c.Email, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(ec.Name, c.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ec.Phone, c.Phone, StringComparison.OrdinalIgnoreCase))
                );

                if (found is null)
                {
                    context.Clients.Add(c);
                    addedClients.Add(c);
                }
            }
            if (addedClients.Count > 0) await context.SaveChangesAsync();

            existingClients = await context.Clients.AsNoTracking().ToListAsync();

            var casesToEnsure = new List<(string clientEmail, string title, string? desc, ClientCaseStatus status)>
            {
                ("ivan@example.com",   "Ïîäãîòîâêà äîãîâîðà êîíñàëòèíãà", "Àíàëèòèêà + ÒÇ", ClientCaseStatus.Open),
                ("ivan@example.com",   "Ïîääåðæêà ïîäïèñêè SaaS",         "Ìåñÿ÷íàÿ ïîäïèñêà", ClientCaseStatus.Open),
                ("maria@company.com",  "Ðåäèçàéí ñàéòà",                  "UI/UX + â¸ðñòêà", ClientCaseStatus.OnHold),
                ("alex@business.com",  "Ïîñòàâêà ïàðòèè ¹101",            "Îòãðóçêà 50 øò.", ClientCaseStatus.Open),
                ("katya@startup.com",  "Íàó÷íûé ïðîåêò R&D",              "Ýòàï 1 — ÍÈÐ", ClientCaseStatus.Open),
                ("dmitry@consulting.com","Ñîïðîâîæäåíèå 2025",            "Ïîìåñÿ÷íîå îáñëóæèâàíèå", ClientCaseStatus.Closed)
            };

            var caseEntities = new List<ClientCase>();

            foreach (var (clientEmail, title, desc, status) in casesToEnsure)
            {
                var cli = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, clientEmail, StringComparison.OrdinalIgnoreCase));
                if (cli is null)
                {
                    Console.Error.WriteLine($"[SEED][Cases] client '{clientEmail}' not found, skip case '{title}'");
                    continue;
                }

                var exists = await context.ClientCases.AsNoTracking()
                    .AnyAsync(cc => cc.ClientId == cli.Id && cc.Title == title);

                if (!exists)
                {
                    var cc = new ClientCase
                    {
                        ClientId = cli.Id,
                        Title = title,
                        Description = desc ?? string.Empty,
                        Status = status
                    };
                    context.ClientCases.Add(cc);
                    caseEntities.Add(cc);
                }
            }

            if (caseEntities.Count > 0) await context.SaveChangesAsync();

            var allCases = await context.ClientCases.AsNoTracking().ToListAsync();

            var today = DateTime.UtcNow.Date;
            var paymentsToEnsure = new[]
            {
                // Èâàí Èâàíîâ — Ïîäãîòîâêà äîãîâîðà êîíñàëòèíãà
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "Ïîäãîòîâêà äîãîâîðà êîíñàëòèíãà",
                    Date = today.AddDays(-10),
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Àâàíñ ïî äîãîâîðó",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-10),
                    DealType = "Êîíñàëòèíã",
                    IncomeType = "Äîõîä îò óñëóã",
                    Source = "Áàíêîâñêèé ïåðåâîä",
                    StatusName = "Âûïîëíåíî"
                },
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "Ïîäãîòîâêà äîãîâîðà êîíñàëòèíãà",
                    Date = today.AddDays(20),
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Îêîí÷àòåëüíûé ðàñ÷¸ò",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Êîíñàëòèíã",
                    IncomeType = "Äîõîä îò óñëóã",
                    Source = "Áàíêîâñêàÿ êàðòà",
                    StatusName = "Îæèäàåòñÿ"
                },

                // Ìàðèÿ Ïåòðîâà — Ðåäèçàéí ñàéòà
                new {
                    Email = "maria@company.com",
                    CaseTitle = "Ðåäèçàéí ñàéòà",
                    Date = today.AddDays(-5),
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Ýòàï 1 — ïðîòîòèïû",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-5),
                    DealType = "Ïðîåêò",
                    IncomeType = "Äîõîä îò óñëóã",
                    Source = "PayPal",
                    StatusName = "Âûïîëíåíî"
                },
                new {
                    Email = "maria@company.com",
                    CaseTitle = "Ðåäèçàéí ñàéòà",
                    Date = today.AddDays(-1),
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "Ýòàï 2 — UI",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Ïðîåêò",
                    IncomeType = "Äîõîä îò óñëóã",
                    Source = "Áàíêîâñêèé ïåðåâîä",
                    StatusName = "Ïðîñðî÷åíî"
                },

                // Àëåêñåé Ñìèðíîâ — Ïîñòàâêà ïàðòèè ¹101
                new {
                    Email = "alex@business.com",
                    CaseTitle = "Ïîñòàâêà ïàðòèè ¹101",
                    Date = today.AddDays(7),
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Îïëàòà çà ïîñòàâêó",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Ïðîäàæà òîâàðà",
                    IncomeType = "Ïðî÷èå äîõîäû",
                    Source = "×åê",
                    StatusName = "Îæèäàåòñÿ"
                },

                // Åêàòåðèíà Ñèäîðîâà — Íàó÷íûé ïðîåêò R&D
                new {
                    Email = "katya@startup.com",
                    CaseTitle = "Íàó÷íûé ïðîåêò R&D",
                    Date = today.AddDays(30),
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Ýòàï 1 — ÍÈÐ",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Ïðîåêò",
                    IncomeType = "Ïðî÷èå äîõîäû",
                    Source = "Áàíêîâñêèé ïåðåâîä",
                    StatusName = "Îæèäàåòñÿ"
                },

                // Äìèòðèé Êóçíåöîâ — Ñîïðîâîæäåíèå 2025 (ÐÀÑÕÎÄ)
                new {
                    Email = "dmitry@consulting.com",
                    CaseTitle = "Ñîïðîâîæäåíèå 2025",
                    Date = today.AddDays(-3),
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "Çàêóïêà ÏÎ äëÿ ñîïðîâîæäåíèÿ",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-3),
                    DealType = "Îáñëóæèâàíèå",
                    IncomeType = "Ïðî÷èå ðàñõîäû",
                    Source = "Íàëè÷íûå",
                    StatusName = "Âûïîëíåíî"
                },
            };

            var toAdd = new List<Payment>();

            foreach (var p in paymentsToEnsure)
            {
                var cli = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, p.Email, StringComparison.OrdinalIgnoreCase));
                if (cli is null)
                {
                    Console.Error.WriteLine($"[SEED][Payments] client '{p.Email}' not found, skip payment '{p.Description}'");
                    continue;
                }

                var caseEnt = allCases.FirstOrDefault(c => c.ClientId == cli.Id && c.Title == p.CaseTitle);
                if (caseEnt is null)
                {
                    Console.Error.WriteLine($"[SEED][Payments] case '{p.CaseTitle}' for '{p.Email}' not found, skip");
                    continue;
                }

                var exists = await context.Payments.AsNoTracking().AnyAsync(x =>
                    x.ClientId == cli.Id &&
                    x.ClientCaseId == caseEnt.Id &&
                    x.Date == p.Date &&
                    x.Amount == p.Amount &&
                    x.Type == p.Type &&
                    x.Description == p.Description
                );

                if (exists) continue;

                var payment = new Payment
                {
                    ClientId = cli.Id,
                    ClientCaseId = caseEnt.Id,
                    Date = p.Date,
                    Amount = p.Amount,
                    Type = p.Type,
                    Status = p.Status,
                    Description = p.Description,
                    IsPaid = p.IsPaid,
                    PaidDate = p.PaidDate,
                    DealTypeId = GetIdOrThrow(dealTypeByName, p.DealType, "DealType"),
                    IncomeTypeId = GetIdOrThrow(incomeTypeByName, p.IncomeType, "IncomeType"),
                    PaymentSourceId = GetIdOrThrow(sourceByName, p.Source, "PaymentSource"),
                    PaymentStatusId = GetIdOrThrow(statusByName, p.StatusName, "PaymentStatus")
                };

                toAdd.Add(payment);
            }

            if (toAdd.Count > 0)
            {
                context.Payments.AddRange(toAdd);
                await context.SaveChangesAsync();
            }

            var responsible = await context.Users.AsNoTracking()
                .Where(u => u.IsActive && u.IsApproved)
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync();

            var actsToEnsure = new[]
            {
                new
                {
                    Number = "ACT-001",
                    Title = "   ",
                    Date = today.AddDays(-15),
                    Amount = 51721.64m,
                    Invoice = "1305",
                    ClientEmail = "maria@company.com",
                    Status = ActStatus.Transferred,
                    Inn = "7721234567"
                },
                new
                {
                    Number = "ACT-002",
                    Title = " ",
                    Date = today.AddDays(-5),
                    Amount = 118000m,
                    Invoice = "1306",
                    ClientEmail = "ivan@example.com",
                    Status = ActStatus.Created,
                    Inn = "7705123456"
                },
                new
                {
                    Number = "ACT-003",
                    Title = " ",
                    Date = today.AddDays(-2),
                    Amount = 90000m,
                    Invoice = "1307",
                    ClientEmail = "alex@business.com",
                    Status = ActStatus.Signed,
                    Inn = "7805456789"
                }
            };

            var existingActs = await context.Acts.AsNoTracking().ToListAsync();
            var actsToAdd = new List<Act>();

            foreach (var act in actsToEnsure)
            {
                var client = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, act.ClientEmail, StringComparison.OrdinalIgnoreCase));
                if (client is null)
                {
                    Console.Error.WriteLine($"[SEED][Acts] client '{act.ClientEmail}' not found, skip act '{act.Number}'");
                    continue;
                }

                var existsAct = existingActs.Any(a => a.Number == act.Number && a.ClientId == client.Id);
                if (existsAct) continue;

                actsToAdd.Add(new Act
                {
                    Number = act.Number,
                    Title = act.Title,
                    Date = act.Date,
                    Amount = act.Amount,
                    InvoiceNumber = act.Invoice,
                    CounterpartyInn = act.Inn,
                    Status = act.Status,
                    ClientId = client.Id,
                    ResponsibleId = responsible?.Id,
                    Comment = "",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            if (actsToAdd.Count > 0)
            {
                context.Acts.AddRange(actsToAdd);
                await context.SaveChangesAsync();
            }
        }

        // ----------------------- helpers -----------------------

        private static async Task EnsureRoleAsync(PaymentContext ctx, string name, string? desc)
        {
            var exists = await ctx.Roles.AsNoTracking().AnyAsync(r => r.Name == name);
            if (!exists)
            {
                ctx.Roles.Add(new Role { Name = name, Description = desc ?? string.Empty });
                await ctx.SaveChangesAsync();
            }
        }

        private static async Task EnsureIncomeTypeAsync(PaymentContext ctx, string name, string color, PaymentType paymentType, string? desc)
        {
            var found = await ctx.IncomeTypes.FirstOrDefaultAsync(x => x.Name == name);
            if (found is null)
            {
                ctx.IncomeTypes.Add(new IncomeType
                {
                    Name = name,
                    ColorHex = color,
                    Description = desc ?? string.Empty,
                    IsActive = true,
                    PaymentType = paymentType
                });
                await ctx.SaveChangesAsync();
            }
            else
            {
                if (found.PaymentType != paymentType)
                {
                    found.PaymentType = paymentType;
                    await ctx.SaveChangesAsync();
                }
            }
        }

        private static async Task EnsurePaymentSourceAsync(PaymentContext ctx, string name, string color, string? desc)
        {
            var exists = await ctx.PaymentSources.AsNoTracking().AnyAsync(x => x.Name == name);
            if (!exists)
            {
                ctx.PaymentSources.Add(new PaymentSource { Name = name, ColorHex = color, Description = desc ?? string.Empty, IsActive = true });
                await ctx.SaveChangesAsync();
            }
        }

        private static async Task EnsurePaymentStatusAsync(PaymentContext ctx, string name, string color, string? desc)
        {
            var exists = await ctx.PaymentStatuses.AsNoTracking().AnyAsync(x => x.Name == name);
            if (!exists)
            {
                ctx.PaymentStatuses.Add(new PaymentStatusEntity { Name = name, ColorHex = color, Description = desc ?? string.Empty, IsActive = true });
                await ctx.SaveChangesAsync();
            }
        }

        // --- íîðìàëèçàöèÿ è áåçîïàñíûå êàðòû èì¸í ---

        private static string Norm(string? s) =>
            (s ?? string.Empty).Trim().ToUpperInvariant();

        private static Dictionary<string, int> BuildNameMap<T>(
            IEnumerable<T> src,
            Func<T, string?> nameSelector,
            Func<T, int> idSelector)
        {
            return src
                .Where(x => !string.IsNullOrWhiteSpace(nameSelector(x)))
                .GroupBy(x => Norm(nameSelector(x)))
                .ToDictionary(g => g.Key, g => idSelector(g.First()));
        }

        private static int GetIdOrThrow(Dictionary<string, int> map, string key, string entityName)
        {
            if (map.TryGetValue(Norm(key), out var id))
                return id;

            throw new InvalidOperationException($"[SEED] {entityName} '{key}' not found (check dictionaries seeding/data).");
        }
    }
}

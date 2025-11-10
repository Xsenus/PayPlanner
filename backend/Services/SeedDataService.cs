using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Первичное наполнение БД тестовыми данными: роли, админ, клиенты, дела, словари и платежи.
    /// </summary>
    public static class SeedDataService
    {
        /// <summary>
        /// Выполнить сиды.
        /// </summary>
        /// <param name="context">DbContext</param>
        /// <param name="seedClientsAndPayments">
        /// Если true — добавить клиентов, дела и платежи (даже если таблица не пустая).
        /// Если false — клиенты/дела/платежи сидятся только когда Clients пуст.
        /// </param>
        public static async Task SeedAsync(PaymentContext context, bool seedClientsAndPayments = false)
        {
            await SeedDictionariesAsync(context);          // словари
            await SeedRolesAsync(context);                 // роли (admin, user)
            await SeedRolePermissionsAsync(context);       // права по ролям
            await SeedAdminUserAsync(context);             // администратор

            // Возможность отключить клиентские сиды на проде:
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
                    // Не валим процесс — просто пропустим клиентскую часть
                }
            }
        }

        /// <summary>
        /// Словари (deal types, income types, sources, statuses).
        /// </summary>
        private static async Task SeedDictionariesAsync(PaymentContext context)
        {
            if (!await context.DealTypes.AnyAsync())
            {
                var dealTypes = new[]
                {
                    new DealType { Name = "Консалтинг",      Description = "Профессиональные консультационные услуги", ColorHex = "#3B82F6" },
                    new DealType { Name = "Продажа товара",   Description = "Прямая продажа продукции",                ColorHex = "#10B981" },
                    new DealType { Name = "Подписка",         Description = "Регулярные подписные услуги",             ColorHex = "#8B5CF6" },
                    new DealType { Name = "Проект",           Description = "Работа по проекту с фиксированным объёмом", ColorHex = "#F59E0B" },
                    new DealType { Name = "Обслуживание",     Description = "Долгосрочные контракты на обслуживание",  ColorHex = "#EF4444" }
                };
                context.DealTypes.AddRange(dealTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Income).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    new IncomeType { Name = "Доход от услуг",  Description = "Доход от оказания услуг",       ColorHex = "#10B981", PaymentType = PaymentType.Income },
                    new IncomeType { Name = "Прочие доходы",    Description = "Прочие доходы",                ColorHex = "#064E3B", PaymentType = PaymentType.Income },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Expense).AnyAsync())
            {
                var expenseTypes = new[]
                {
                    new IncomeType { Name = "Прочие расходы", Description = "Иные расходы", ColorHex = "#991B1B", PaymentType = PaymentType.Expense },
                };
                context.IncomeTypes.AddRange(expenseTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.PaymentSources.AnyAsync())
            {
                var paymentSources = new[]
                {
                    new PaymentSource { Name = "Банковский перевод", Description = "Прямой банковский перевод", ColorHex = "#6B7280" },
                    new PaymentSource { Name = "Банковская карта",   Description = "Оплата банковской картой",  ColorHex = "#4B5563" },
                    new PaymentSource { Name = "PayPal",             Description = "Оплата через PayPal",       ColorHex = "#374151" },
                    new PaymentSource { Name = "Чек",                Description = "Оплата банковским чеком",   ColorHex = "#1F2937" },
                    new PaymentSource { Name = "Наличные",           Description = "Оплата наличными",          ColorHex = "#111827" }
                };
                context.PaymentSources.AddRange(paymentSources);
                await context.SaveChangesAsync();
            }

            if (!await context.PaymentStatuses.AnyAsync())
            {
                var paymentStatuses = new[]
                {
                    new PaymentStatusEntity { Name = "Ожидается",  Description = "Оплата находится в ожидании",  ColorHex = "#F59E0B" },
                    new PaymentStatusEntity { Name = "Выполнено",  Description = "Оплата успешно завершена",    ColorHex = "#10B981" },
                    new PaymentStatusEntity { Name = "Просрочено", Description = "Срок оплаты истёк",           ColorHex = "#EF4444" }
                };
                context.PaymentStatuses.AddRange(paymentStatuses);
                await context.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Создаёт роли admin и user (если отсутствуют).
        /// </summary>
        private static async Task SeedRolesAsync(PaymentContext context)
        {
            if (!await context.Roles.AnyAsync())
            {
                context.Roles.AddRange(
                    new Role { Name = "admin", Description = "Администратор системы" },
                    new Role { Name = "user", Description = "Обычный пользователь" }
                );
                await context.SaveChangesAsync();
                return;
            }

            // Досеять недостающие роли
            await EnsureRoleAsync(context, "admin", "Администратор системы");
            await EnsureRoleAsync(context, "user", "Обычный пользователь");
        }

        private static async Task SeedRolePermissionsAsync(PaymentContext context)
        {
            var roles = await context.Roles.AsNoTracking().Select(r => new { r.Id }).ToListAsync();
            if (roles.Count == 0)
            {
                return;
            }

            var sections = new[]
            {
                "calendar",
                "reports",
                "calculator",
                "clients",
                "accounts",
                "acts",
                "contracts",
                "dictionaries",
            };

            var existing = await context.RolePermissions
                .AsNoTracking()
                .Select(rp => new { rp.RoleId, rp.Section })
                .ToListAsync();

            foreach (var role in roles)
            {
                foreach (var section in sections)
                {
                    if (existing.Any(rp => rp.RoleId == role.Id && rp.Section == section))
                    {
                        continue;
                    }

                    context.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        Section = section,
                        CanView = true,
                        CanCreate = true,
                        CanEdit = true,
                        CanDelete = true,
                        CanExport = true,
                        CanViewAnalytics = section == "calendar",
                    });
                }
            }

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Создаёт пользователя-администратора, если его ещё нет.
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
                context.Roles.Add(new Role { Name = "admin", Description = "Администратор системы" });
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

        // ----------------------- ниже — сид клиентов/дел, платежей -----------------------

        private static async Task SeedClientsCasesPaymentsAsync(PaymentContext context)
        {
            // Читаем справочники и строим безопасные карты (без падения на дублях)
            var dealTypes = await context.DealTypes.AsNoTracking().ToListAsync();
            var dealTypeByName = BuildNameMap(dealTypes, d => d.Name, d => d.Id);

            var incomeTypes = await context.IncomeTypes.AsNoTracking().ToListAsync();
            var incomeTypeByName = BuildNameMap(incomeTypes, i => i.Name, i => i.Id);

            var sources = await context.PaymentSources.AsNoTracking().ToListAsync();
            var sourceByName = BuildNameMap(sources, s => s.Name, s => s.Id);

            var statuses = await context.PaymentStatuses.AsNoTracking().ToListAsync();
            var statusByName = BuildNameMap(statuses, s => s.Name, s => s.Id);

            // Идемпотентные клиенты
            var clients = new[]
            {
                new Client { Name = "Иван Иванов",      Email = "ivan@example.com",  Phone = "+7-900-010-01-01", Company = "ТехКорп",                Address = "ул. Ленина, д. 10, Москва" },
                new Client { Name = "Мария Петрова",    Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "Студия Дизайна",         Address = "пр. Мира, д. 25, Санкт-Петербург" },
                new Client { Name = "Алексей Смирнов",  Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "Смирнов Индастриз",      Address = "ул. Советская, д. 5, Екатеринбург" },
                new Client { Name = "Екатерина Сидорова", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "Инновационные Лаборатории", Address = "ул. Гагарина, д. 12, Новосибирск" },
                new Client { Name = "Дмитрий Кузнецов", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "Кузнецов Консалтинг", Address = "ул. Пушкина, д. 8, Казань" }
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
                ("ivan@example.com",   "Подготовка договора консалтинга", "Аналитика + ТЗ", ClientCaseStatus.Open),
                ("ivan@example.com",   "Поддержка подписки SaaS",         "Месячная подписка", ClientCaseStatus.Open),
                ("maria@company.com",  "Редизайн сайта",                  "UI/UX + вёрстка", ClientCaseStatus.OnHold),
                ("alex@business.com",  "Поставка партии №101",            "Отгрузка 50 шт.", ClientCaseStatus.Open),
                ("katya@startup.com",  "Научный проект R&D",              "Этап 1 — НИР", ClientCaseStatus.Open),
                ("dmitry@consulting.com","Сопровождение 2025",            "Помесячное обслуживание", ClientCaseStatus.Closed)
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
                new
                {
                    Email = "ivan@example.com",
                    CaseTitle = "Подготовка договора консалтинга",
                    Date = today.AddDays(-10),
                    AccountDate = (DateTime?)today.AddDays(-12),
                    Account = "1306",
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Аванс по договору",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-10),
                    DealType = "Консалтинг",
                    IncomeType = "Доход от услуг",
                    Source = "Банковский перевод",
                    StatusName = "Выполнено",
                    ActReference = (string?)null
                },
                new
                {
                    Email = "ivan@example.com",
                    CaseTitle = "Подготовка договора консалтинга",
                    Date = today.AddDays(20),
                    AccountDate = (DateTime?)today.AddDays(15),
                    Account = "INV-2026",
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Окончательный расчёт",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Консалтинг",
                    IncomeType = "Доход от услуг",
                    Source = "Банковская карта",
                    StatusName = "Ожидается",
                    ActReference = (string?)null
                },

                // Мария Петрова — Редизайн сайта
                new
                {
                    Email = "maria@company.com",
                    CaseTitle = "Редизайн сайта",
                    Date = today.AddDays(-5),
                    AccountDate = (DateTime?)today.AddDays(-7),
                    Account = "1305",
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Этап 1 — прототипы",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-5),
                    DealType = "Проект",
                    IncomeType = "Доход от услуг",
                    Source = "PayPal",
                    StatusName = "Выполнено",
                    ActReference = (string?)null
                },
                new
                {
                    Email = "maria@company.com",
                    CaseTitle = "Редизайн сайта",
                    Date = today.AddDays(-1),
                    AccountDate = (DateTime?)today.AddDays(-3),
                    Account = "INV-2042",
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "Этап 2 — UI",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Проект",
                    IncomeType = "Доход от услуг",
                    Source = "Банковский перевод",
                    StatusName = "Просрочено",
                    ActReference = "Акт ожидается"
                },
                new
                {
                    Email = "maria@company.com",
                    CaseTitle = "Редизайн сайта",
                    Date = today.AddDays(-2),
                    AccountDate = (DateTime?)today.AddDays(-4),
                    Account = "154",
                    Amount = 65_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Счёт за тестирование",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Проект",
                    IncomeType = "Доход от услуг",
                    Source = "Банковский перевод",
                    StatusName = "Ожидается",
                    ActReference = "Акт будет подписан в ноябре"
                },

                // Алексей Смирнов — Поставка партии №101
                new
                {
                    Email = "alex@business.com",
                    CaseTitle = "Поставка партии №101",
                    Date = today.AddDays(7),
                    AccountDate = (DateTime?)today.AddDays(2),
                    Account = "1307",
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Оплата за поставку",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Продажа товара",
                    IncomeType = "Прочие доходы",
                    Source = "Чек",
                    StatusName = "Ожидается",
                    ActReference = (string?)null
                },

                // Екатерина Сидорова — Научный проект R&D
                new
                {
                    Email = "katya@startup.com",
                    CaseTitle = "Научный проект R&D",
                    Date = today.AddDays(30),
                    AccountDate = (DateTime?)today.AddDays(28),
                    Account = "INV-RD-001",
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Этап 1 — НИР",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Проект",
                    IncomeType = "Прочие доходы",
                    Source = "Банковский перевод",
                    StatusName = "Ожидается",
                    ActReference = (string?)null
                },

                // Дмитрий Кузнецов — Сопровождение 2025 (РАСХОД)
                new
                {
                    Email = "dmitry@consulting.com",
                    CaseTitle = "Сопровождение 2025",
                    Date = today.AddDays(-3),
                    AccountDate = (DateTime?)null,
                    Account = (string?)null,
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "Закупка ПО для сопровождения",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-3),
                    DealType = "Обслуживание",
                    IncomeType = "Прочие расходы",
                    Source = "Наличные",
                    StatusName = "Выполнено",
                    ActReference = (string?)null
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
                    AccountDate = p.AccountDate,
                    Account = string.IsNullOrWhiteSpace(p.Account) ? null : p.Account,
                    Amount = p.Amount,
                    Type = p.Type,
                    Status = p.Status,
                    Description = p.Description,
                    Notes = p.ActReference ?? string.Empty,
                    IsPaid = p.IsPaid,
                    PaidDate = p.PaidDate,
                    DealTypeId = GetIdOrThrow(dealTypeByName, p.DealType, "DealType"),
                    IncomeTypeId = GetIdOrThrow(incomeTypeByName, p.IncomeType, "IncomeType"),
                    PaymentSourceId = GetIdOrThrow(sourceByName, p.Source, "PaymentSource"),
                    PaymentStatusId = GetIdOrThrow(statusByName, p.StatusName, "PaymentStatus")
                };

                PaymentBusinessLogic.PrepareForCreate(payment, DateTime.UtcNow);
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
                    Title = "Акт передачи макетов по редизайну сайта",
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
                    Title = "Акт оказанных услуг по консалтингу",
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
                    Title = "Акт приёма-передачи партии №101",
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

        // --- нормализация и безопасные карты имён ---

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

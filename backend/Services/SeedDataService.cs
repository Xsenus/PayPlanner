using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Первичное наполнение БД тестовыми данными: клиенты, дела, словари и привязанные платежи.
    /// </summary>
    public static class SeedDataService
    {
        /// <summary>
        /// Выполнить сиды.
        /// </summary>
        /// <param name="context">DbContext</param>
        /// <param name="seedClientsAndPayments">
        /// Если true — добавить клиентов, дела и платежи (даже если таблица не пустая).
        /// Обычно используем true только вручную (для демо/добавки примеров).
        /// Если false — клиенты/дела/платежи сидятся только когда Clients пуст.
        /// </param>
        public static async Task SeedAsync(PaymentContext context, bool seedClientsAndPayments = false)
        {
            await SeedDictionariesAsync(context);
            var shouldSeedClients = seedClientsAndPayments || !await context.Clients.AnyAsync();
            if (shouldSeedClients)
            {
                await SeedClientsCasesPaymentsAsync(context);
            }
        }

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
            else
            {
                //await EnsureDealTypeAsync(context, "Консалтинг", "#3B82F6", "Профессиональные консультационные услуги");
                //await EnsureDealTypeAsync(context, "Продажа товара", "#10B981", "Прямая продажа продукции");
                //await EnsureDealTypeAsync(context, "Подписка", "#8B5CF6", "Регулярные подписные услуги");
                //await EnsureDealTypeAsync(context, "Проект", "#F59E0B", "Работа по проекту с фиксированным объёмом");
                //await EnsureDealTypeAsync(context, "Обслуживание", "#EF4444", "Долгосрочные контракты на обслуживание");
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Income).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    new IncomeType { Name = "Доход от услуг",  Description = "Доход от оказания услуг",       ColorHex = "#10B981", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "Продажа товаров",  Description = "Доход от продажи товаров",     ColorHex = "#059669", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "Лицензирование",   Description = "Доход от лицензий и роялти",   ColorHex = "#047857", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "Проценты",         Description = "Доход от процентов/инвестиций",ColorHex = "#065F46", PaymentType = PaymentType.Income },
                    new IncomeType { Name = "Прочие доходы",    Description = "Прочие доходы",                ColorHex = "#064E3B", PaymentType = PaymentType.Income },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsureIncomeTypeAsync(context, "Доход от услуг", "#10B981", PaymentType.Income, "Доход от оказания услуг");
                //await EnsureIncomeTypeAsync(context, "Продажа товаров", "#059669", PaymentType.Income, "Доход от продажи товаров");
                //await EnsureIncomeTypeAsync(context, "Лицензирование", "#047857", PaymentType.Income, "Доход от лицензий и роялти");
                //await EnsureIncomeTypeAsync(context, "Проценты", "#065F46", PaymentType.Income, "Доход от процентов/инвестиций");
                //await EnsureIncomeTypeAsync(context, "Прочие доходы", "#064E3B", PaymentType.Income, "Прочие доходы");
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Expense).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    //new IncomeType { Name = "Госпошлина",       Description = "Государственные пошлины",      ColorHex = "#EF4444", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "Нотариус",         Description = "Нотариальные расходы",         ColorHex = "#F43F5E", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "Софт/подписки",    Description = "Подписки на ПО и сервисы",     ColorHex = "#DC2626", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "Инфраструктура",   Description = "Серверы/хостинг/CDN",          ColorHex = "#B91C1C", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "Маркетинг",        Description = "Реклама и продвижение",        ColorHex = "#7F1D1D", PaymentType = PaymentType.Expense },
                    new IncomeType { Name = "Прочие расходы",   Description = "Иные расходы",                 ColorHex = "#991B1B", PaymentType = PaymentType.Expense },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsureIncomeTypeAsync(context, "Госпошлина", "#EF4444", PaymentType.Expense, "Государственные пошлины");
                //await EnsureIncomeTypeAsync(context, "Нотариус", "#F43F5E", PaymentType.Expense, "Нотариальные расходы");
                //await EnsureIncomeTypeAsync(context, "Софт/подписки", "#DC2626", PaymentType.Expense, "Подписки на ПО и сервисы");
                //await EnsureIncomeTypeAsync(context, "Инфраструктура", "#B91C1C", PaymentType.Expense, "Серверы/хостинг/CDN");
                //await EnsureIncomeTypeAsync(context, "Маркетинг", "#7F1D1D", PaymentType.Expense, "Реклама и продвижение");
                //await EnsureIncomeTypeAsync(context, "Прочие расходы", "#991B1B", PaymentType.Expense, "Иные расходы");
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
            else
            {
                //await EnsurePaymentSourceAsync(context, "Банковский перевод", "#6B7280", "Прямой банковский перевод");
                //await EnsurePaymentSourceAsync(context, "Банковская карта", "#4B5563", "Оплата банковской картой");
                //await EnsurePaymentSourceAsync(context, "PayPal", "#374151", "Оплата через PayPal");
                //await EnsurePaymentSourceAsync(context, "Чек", "#1F2937", "Оплата банковским чеком");
                //await EnsurePaymentSourceAsync(context, "Наличные", "#111827", "Оплата наличными");
            }

            if (!await context.PaymentStatuses.AnyAsync())
            {
                var paymentStatuses = new[]
                {
                    new PaymentStatusEntity { Name = "Ожидается", Description = "Оплата находится в ожидании",  ColorHex = "#F59E0B" },
                    new PaymentStatusEntity { Name = "Выполнено", Description = "Оплата успешно завершена",    ColorHex = "#10B981" },
                    new PaymentStatusEntity { Name = "Просрочено", Description = "Срок оплаты истёк",          ColorHex = "#EF4444" }
                };
                context.PaymentStatuses.AddRange(paymentStatuses);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsurePaymentStatusAsync(context, "Ожидается", "#F59E0B", "Оплата находится в ожидании");
                //await EnsurePaymentStatusAsync(context, "Выполнено", "#10B981", "Оплата успешно завершена");
                //await EnsurePaymentStatusAsync(context, "Просрочено", "#EF4444", "Срок оплаты истёк");
            }
        }

        private static async Task SeedClientsCasesPaymentsAsync(PaymentContext context)
        {
            var dealTypes = await context.DealTypes.AsNoTracking().ToListAsync();
            var dealTypeByName = dealTypes.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);

            var incomeTypes = await context.IncomeTypes.AsNoTracking().ToListAsync();
            var incomeTypeByName = incomeTypes.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);

            var sources = await context.PaymentSources.AsNoTracking().ToListAsync();
            var sourceByName = sources.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

            var statuses = await context.PaymentStatuses.AsNoTracking().ToListAsync();
            var statusByName = statuses.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

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
                    (!string.IsNullOrWhiteSpace(c.Email) && string.Equals(ec.Email, c.Email, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(ec.Name, c.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(ec.Phone, c.Phone, StringComparison.OrdinalIgnoreCase))
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
                var clientId = existingClients.First(c => string.Equals(c.Email, clientEmail, StringComparison.OrdinalIgnoreCase)).Id;

                var exists = await context.ClientCases.AsNoTracking()
                    .AnyAsync(cc => cc.ClientId == clientId && cc.Title == title);

                if (!exists)
                {
                    var cc = new ClientCase
                    {
                        ClientId = clientId,
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
                // Иван Иванов — Подготовка договора консалтинга
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "Подготовка договора консалтинга",
                    Date = today.AddDays(-10),
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Аванс по договору",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-10),
                    DealType = "Консалтинг",
                    IncomeType = "Доход от услуг",
                    Source = "Банковский перевод",
                    StatusName = "Выполнено"
                },
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "Подготовка договора консалтинга",
                    Date = today.AddDays(20),
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Окончательный расчёт",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Консалтинг",
                    IncomeType = "Доход от услуг",
                    Source = "Банковская карта",
                    StatusName = "Ожидается"
                },

                // Мария Петрова — Редизайн сайта
                new {
                    Email = "maria@company.com",
                    CaseTitle = "Редизайн сайта",
                    Date = today.AddDays(-5),
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Этап 1 — прототипы",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-5),
                    DealType = "Проект",
                    IncomeType = "Доход от услуг",
                    Source = "PayPal",
                    StatusName = "Выполнено"
                },
                new {
                    Email = "maria@company.com",
                    CaseTitle = "Редизайн сайта",
                    Date = today.AddDays(-1),
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "Этап 2 — UI",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Проект",
                    IncomeType = "Доход от услуг",
                    Source = "Банковский перевод",
                    StatusName = "Просрочено"
                },

                // Алексей Смирнов — Поставка партии №101
                new {
                    Email = "alex@business.com",
                    CaseTitle = "Поставка партии №101",
                    Date = today.AddDays(7),
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Оплата за поставку",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Продажа товара",
                    IncomeType = "Прочие доходы",
                    Source = "Чек",
                    StatusName = "Ожидается"
                },

                // Екатерина Сидорова — Научный проект R&D
                new {
                    Email = "katya@startup.com",
                    CaseTitle = "Научный проект R&D",
                    Date = today.AddDays(30),
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Этап 1 — НИР",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "Проект",
                    IncomeType = "Прочие доходы",
                    Source = "Банковский перевод",
                    StatusName = "Ожидается"
                },

                // Дмитрий Кузнецов — Сопровождение 2025 (РАСХОД)
                new {
                    Email = "dmitry@consulting.com",
                    CaseTitle = "Сопровождение 2025",
                    Date = today.AddDays(-3),
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "Закупка ПО для сопровождения",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-3),
                    DealType = "Обслуживание",
                    IncomeType = "Прочие расходы",
                    Source = "Наличные",
                    StatusName = "Выполнено"
                },
            };

            var toAdd = new List<Payment>();

            foreach (var p in paymentsToEnsure)
            {
                var clientId = existingClients.First(c => string.Equals(c.Email, p.Email, StringComparison.OrdinalIgnoreCase)).Id;
                var caseId = allCases.First(c => c.ClientId == clientId && c.Title == p.CaseTitle).Id;

                var exists = await context.Payments.AsNoTracking().AnyAsync(x =>
                    x.ClientId == clientId &&
                    x.ClientCaseId == caseId &&
                    x.Date == p.Date &&
                    x.Amount == p.Amount &&
                    x.Type == p.Type &&
                    x.Description == p.Description
                );

                if (exists) continue;

                var payment = new Payment
                {
                    ClientId = clientId,
                    ClientCaseId = caseId,
                    Date = p.Date,
                    Amount = p.Amount,
                    Type = p.Type,
                    Status = p.Status,
                    Description = p.Description,
                    IsPaid = p.IsPaid,
                    PaidDate = p.PaidDate,
                    DealTypeId = dealTypeByName[p.DealType],
                    IncomeTypeId = incomeTypeByName[p.IncomeType],
                    PaymentSourceId = sourceByName[p.Source],
                    PaymentStatusId = statusByName[p.StatusName]
                };

                toAdd.Add(payment);
            }

            if (toAdd.Count > 0)
            {
                context.Payments.AddRange(toAdd);
                await context.SaveChangesAsync();
            }
        }

        private static async Task EnsureDealTypeAsync(PaymentContext ctx, string name, string color, string? desc)
        {
            var exists = await ctx.DealTypes.AsNoTracking().AnyAsync(x => x.Name == name);
            if (!exists)
            {
                ctx.DealTypes.Add(new DealType { Name = name, ColorHex = color, Description = desc ?? string.Empty, IsActive = true });
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
    }
}
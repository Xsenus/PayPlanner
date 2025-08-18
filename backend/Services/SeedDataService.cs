using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Первичное наполнение БД тестовыми данными: клиенты, дела, справочники и привязанные платежи.
    /// </summary>
    public class SeedDataService
    {
        public static async Task SeedAsync(PaymentContext context)
        {
            // Если уже есть клиенты, считаем, что сиды залиты
            if (await context.Clients.AnyAsync()) return;

            // ---------- Клиенты ----------
            var clients = new[]
            {
                new Client { Name = "Иван Иванов", Email = "ivan@example.com", Phone = "+7-900-010-01-01", Company = "ТехКорп", Address = "ул. Ленина, д. 10, Москва" },
                new Client { Name = "Мария Петрова", Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "Студия Дизайна", Address = "пр. Мира, д. 25, Санкт-Петербург" },
                new Client { Name = "Алексей Смирнов", Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "Смирнов Индастриз", Address = "ул. Советская, д. 5, Екатеринбург" },
                new Client { Name = "Екатерина Сидорова", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "Инновационные Лаборатории", Address = "ул. Гагарина, д. 12, Новосибирск" },
                new Client { Name = "Дмитрий Кузнецов", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "Кузнецов Консалтинг", Address = "ул. Пушкина, д. 8, Казань" }
            };
            context.Clients.AddRange(clients);

            // ---------- Типы сделок ----------
            var dealTypes = new[]
            {
                new DealType { Name = "Консалтинг", Description = "Профессиональные консультационные услуги", ColorHex = "#3B82F6" },
                new DealType { Name = "Продажа товара", Description = "Прямая продажа продукции", ColorHex = "#10B981" },
                new DealType { Name = "Подписка", Description = "Регулярные подписные услуги", ColorHex = "#8B5CF6" },
                new DealType { Name = "Проект", Description = "Работа по проекту с фиксированным объёмом", ColorHex = "#F59E0B" },
                new DealType { Name = "Обслуживание", Description = "Долгосрочные контракты на обслуживание", ColorHex = "#EF4444" }
            };
            context.DealTypes.AddRange(dealTypes);

            // ---------- Типы дохода ----------
            var incomeTypes = new[]
            {
                new IncomeType { Name = "Доход от услуг", Description = "Доход от оказания услуг", ColorHex = "#10B981" },
                new IncomeType { Name = "Продажа товаров", Description = "Доход от продажи товаров", ColorHex = "#059669" },
                new IncomeType { Name = "Лицензирование", Description = "Доход от лицензий и роялти", ColorHex = "#047857" },
                new IncomeType { Name = "Проценты", Description = "Доход от процентов по инвестициям", ColorHex = "#065F46" },
                new IncomeType { Name = "Прочие доходы", Description = "Доходы из других источников", ColorHex = "#064E3B" }
            };
            context.IncomeTypes.AddRange(incomeTypes);

            // ---------- Источники оплаты ----------
            var paymentSources = new[]
            {
                new PaymentSource { Name = "Банковский перевод", Description = "Прямой банковский перевод", ColorHex = "#6B7280" },
                new PaymentSource { Name = "Банковская карта", Description = "Оплата банковской картой", ColorHex = "#4B5563" },
                new PaymentSource { Name = "PayPal", Description = "Оплата через PayPal", ColorHex = "#374151" },
                new PaymentSource { Name = "Чек", Description = "Оплата банковским чеком", ColorHex = "#1F2937" },
                new PaymentSource { Name = "Наличные", Description = "Оплата наличными", ColorHex = "#111827" }
            };
            context.PaymentSources.AddRange(paymentSources);

            // ---------- Статусы оплаты (справочник) ----------
            var paymentStatuses = new[]
            {
                new PaymentStatusEntity { Name = "Ожидается", Description = "Оплата находится в ожидании", ColorHex = "#F59E0B" },
                new PaymentStatusEntity { Name = "Выполнено", Description = "Оплата успешно завершена", ColorHex = "#10B981" },
                new PaymentStatusEntity { Name = "Просрочено", Description = "Срок оплаты истёк", ColorHex = "#EF4444" }
            };
            context.PaymentStatuses.AddRange(paymentStatuses);

            // Сохраняем, чтобы получить Id у справочников и клиентов
            await context.SaveChangesAsync();

            // ---------- Дела клиентов (ClientCase) ----------
            var cases = new List<ClientCase>
            {
                new ClientCase { ClientId = clients[0].Id, Title = "Подготовка договора консалтинга", Description = "Аналитика + ТЗ", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[0].Id, Title = "Поддержка подписки SaaS", Description = "Месячная подписка", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[1].Id, Title = "Редизайн сайта", Description = "UI/UX + вёрстка", Status = ClientCaseStatus.OnHold },
                new ClientCase { ClientId = clients[2].Id, Title = "Поставка партии №101", Description = "Отгрузка 50 шт.", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[3].Id, Title = "Научный проект R&D", Description = "Этап 1 — НИР", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[4].Id, Title = "Сопровождение 2025", Description = "Помесячное обслуживание", Status = ClientCaseStatus.Closed }
            };
            context.ClientCases.AddRange(cases);

            await context.SaveChangesAsync();

            // Быстрые словари по именам (для удобной привязки)
            var dealTypeByName = dealTypes.ToDictionary(d => d.Name, d => d.Id);
            var incomeTypeByName = incomeTypes.ToDictionary(d => d.Name, d => d.Id);
            var sourceByName = paymentSources.ToDictionary(s => s.Name, s => s.Id);
            var statusByName = paymentStatuses.ToDictionary(s => s.Name, s => s.Id);

            // ---------- Платежи, привязанные к делам ----------
            var today = DateTime.UtcNow.Date;

            var payments = new List<Payment>
            {
                // Иван Иванов — Дело: Подготовка договора консалтинга
                new Payment
                {
                    ClientId = clients[0].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[0].Id && c.Title.Contains("консалтинга")).Id,
                    Date = today.AddDays(-10),
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Аванс по договору",
                    IsPaid = true,
                    PaidDate = today.AddDays(-10),
                    DealTypeId = dealTypeByName["Консалтинг"],
                    IncomeTypeId = incomeTypeByName["Доход от услуг"],
                    PaymentSourceId = sourceByName["Банковский перевод"],
                    PaymentStatusId = statusByName["Выполнено"]
                },
                new Payment
                {
                    ClientId = clients[0].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[0].Id && c.Title.Contains("консалтинга")).Id,
                    Date = today.AddDays(20),
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Окончательный расчёт",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["Консалтинг"],
                    IncomeTypeId = incomeTypeByName["Доход от услуг"],
                    PaymentSourceId = sourceByName["Банковская карта"],
                    PaymentStatusId = statusByName["Ожидается"]
                },

                // Мария Петрова — Дело: Редизайн сайта
                new Payment
                {
                    ClientId = clients[1].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[1].Id && c.Title.Contains("Редизайн")).Id,
                    Date = today.AddDays(-5),
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Этап 1 — прототипы",
                    IsPaid = true,
                    PaidDate = today.AddDays(-5),
                    DealTypeId = dealTypeByName["Проект"],
                    IncomeTypeId = incomeTypeByName["Доход от услуг"],
                    PaymentSourceId = sourceByName["PayPal"],
                    PaymentStatusId = statusByName["Выполнено"]
                },
                new Payment
                {
                    ClientId = clients[1].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[1].Id && c.Title.Contains("Редизайн")).Id,
                    Date = today.AddDays(-1),
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "Этап 2 — UI",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["Проект"],
                    IncomeTypeId = incomeTypeByName["Доход от услуг"],
                    PaymentSourceId = sourceByName["Банковский перевод"],
                    PaymentStatusId = statusByName["Просрочено"]
                },

                // Алексей Смирнов — Дело: Поставка партии №101
                new Payment
                {
                    ClientId = clients[2].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[2].Id && c.Title.Contains("Поставка")).Id,
                    Date = today.AddDays(7),
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Оплата за поставку",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["Продажа товара"],
                    IncomeTypeId = incomeTypeByName["Продажа товаров"],
                    PaymentSourceId = sourceByName["Чек"],
                    PaymentStatusId = statusByName["Ожидается"]
                },

                // Екатерина Сидорова — Дело: Научный проект R&D
                new Payment
                {
                    ClientId = clients[3].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[3].Id && c.Title.Contains("R&D")).Id,
                    Date = today.AddDays(30),
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Этап 1 — НИР",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["Проект"],
                    IncomeTypeId = incomeTypeByName["Прочие доходы"],
                    PaymentSourceId = sourceByName["Банковский перевод"],
                    PaymentStatusId = statusByName["Ожидается"]
                },

                // Дмитрий Кузнецов — Дело: Сопровождение 2025 (расход)
                new Payment
                {
                    ClientId = clients[4].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[4].Id && c.Title.Contains("Сопровождение")).Id,
                    Date = today.AddDays(-3),
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "Закупка ПО для сопровождения",
                    IsPaid = true,
                    PaidDate = today.AddDays(-3),
                    DealTypeId = dealTypeByName["Обслуживание"],
                    PaymentSourceId = sourceByName["Наличные"],
                    PaymentStatusId = statusByName["Выполнено"]
                },
            };

            context.Payments.AddRange(payments);
            await context.SaveChangesAsync();
        }
    }
}
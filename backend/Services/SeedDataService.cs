using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// ��������� ���������� �� ��������� �������: �������, ����, ������� � ����������� �������.
    /// </summary>
    public static class SeedDataService
    {
        /// <summary>
        /// ��������� ����.
        /// </summary>
        /// <param name="context">DbContext</param>
        /// <param name="seedClientsAndPayments">
        /// ���� true � �������� ��������, ���� � ������� (���� ���� ������� �� ������).
        /// ������ ���������� true ������ ������� (��� ����/������� ��������).
        /// ���� false � �������/����/������� ������� ������ ����� Clients ����.
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
                    new DealType { Name = "����������",      Description = "���������������� ���������������� ������", ColorHex = "#3B82F6" },
                    new DealType { Name = "������� ������",   Description = "������ ������� ���������",                ColorHex = "#10B981" },
                    new DealType { Name = "��������",         Description = "���������� ��������� ������",             ColorHex = "#8B5CF6" },
                    new DealType { Name = "������",           Description = "������ �� ������� � ������������� �������", ColorHex = "#F59E0B" },
                    new DealType { Name = "������������",     Description = "������������ ��������� �� ������������",  ColorHex = "#EF4444" }
                };
                context.DealTypes.AddRange(dealTypes);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsureDealTypeAsync(context, "����������", "#3B82F6", "���������������� ���������������� ������");
                //await EnsureDealTypeAsync(context, "������� ������", "#10B981", "������ ������� ���������");
                //await EnsureDealTypeAsync(context, "��������", "#8B5CF6", "���������� ��������� ������");
                //await EnsureDealTypeAsync(context, "������", "#F59E0B", "������ �� ������� � ������������� �������");
                //await EnsureDealTypeAsync(context, "������������", "#EF4444", "������������ ��������� �� ������������");
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Income).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    new IncomeType { Name = "����� �� �����",  Description = "����� �� �������� �����",       ColorHex = "#10B981", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "������� �������",  Description = "����� �� ������� �������",     ColorHex = "#059669", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "��������������",   Description = "����� �� �������� � ������",   ColorHex = "#047857", PaymentType = PaymentType.Income },
                    //new IncomeType { Name = "��������",         Description = "����� �� ���������/����������",ColorHex = "#065F46", PaymentType = PaymentType.Income },
                    new IncomeType { Name = "������ ������",    Description = "������ ������",                ColorHex = "#064E3B", PaymentType = PaymentType.Income },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsureIncomeTypeAsync(context, "����� �� �����", "#10B981", PaymentType.Income, "����� �� �������� �����");
                //await EnsureIncomeTypeAsync(context, "������� �������", "#059669", PaymentType.Income, "����� �� ������� �������");
                //await EnsureIncomeTypeAsync(context, "��������������", "#047857", PaymentType.Income, "����� �� �������� � ������");
                //await EnsureIncomeTypeAsync(context, "��������", "#065F46", PaymentType.Income, "����� �� ���������/����������");
                //await EnsureIncomeTypeAsync(context, "������ ������", "#064E3B", PaymentType.Income, "������ ������");
            }

            if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Expense).AnyAsync())
            {
                var incomeTypes = new[]
                {
                    //new IncomeType { Name = "����������",       Description = "��������������� �������",      ColorHex = "#EF4444", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "��������",         Description = "������������ �������",         ColorHex = "#F43F5E", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "����/��������",    Description = "�������� �� �� � �������",     ColorHex = "#DC2626", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "��������������",   Description = "�������/�������/CDN",          ColorHex = "#B91C1C", PaymentType = PaymentType.Expense },
                    //new IncomeType { Name = "���������",        Description = "������� � �����������",        ColorHex = "#7F1D1D", PaymentType = PaymentType.Expense },
                    new IncomeType { Name = "������ �������",   Description = "���� �������",                 ColorHex = "#991B1B", PaymentType = PaymentType.Expense },
                };
                context.IncomeTypes.AddRange(incomeTypes);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsureIncomeTypeAsync(context, "����������", "#EF4444", PaymentType.Expense, "��������������� �������");
                //await EnsureIncomeTypeAsync(context, "��������", "#F43F5E", PaymentType.Expense, "������������ �������");
                //await EnsureIncomeTypeAsync(context, "����/��������", "#DC2626", PaymentType.Expense, "�������� �� �� � �������");
                //await EnsureIncomeTypeAsync(context, "��������������", "#B91C1C", PaymentType.Expense, "�������/�������/CDN");
                //await EnsureIncomeTypeAsync(context, "���������", "#7F1D1D", PaymentType.Expense, "������� � �����������");
                //await EnsureIncomeTypeAsync(context, "������ �������", "#991B1B", PaymentType.Expense, "���� �������");
            }

            if (!await context.PaymentSources.AnyAsync())
            {
                var paymentSources = new[]
                {
                    new PaymentSource { Name = "���������� �������", Description = "������ ���������� �������", ColorHex = "#6B7280" },
                    new PaymentSource { Name = "���������� �����",   Description = "������ ���������� ������",  ColorHex = "#4B5563" },
                    new PaymentSource { Name = "PayPal",             Description = "������ ����� PayPal",       ColorHex = "#374151" },
                    new PaymentSource { Name = "���",                Description = "������ ���������� �����",   ColorHex = "#1F2937" },
                    new PaymentSource { Name = "��������",           Description = "������ ���������",          ColorHex = "#111827" }
                };
                context.PaymentSources.AddRange(paymentSources);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsurePaymentSourceAsync(context, "���������� �������", "#6B7280", "������ ���������� �������");
                //await EnsurePaymentSourceAsync(context, "���������� �����", "#4B5563", "������ ���������� ������");
                //await EnsurePaymentSourceAsync(context, "PayPal", "#374151", "������ ����� PayPal");
                //await EnsurePaymentSourceAsync(context, "���", "#1F2937", "������ ���������� �����");
                //await EnsurePaymentSourceAsync(context, "��������", "#111827", "������ ���������");
            }

            if (!await context.PaymentStatuses.AnyAsync())
            {
                var paymentStatuses = new[]
                {
                    new PaymentStatusEntity { Name = "���������", Description = "������ ��������� � ��������",  ColorHex = "#F59E0B" },
                    new PaymentStatusEntity { Name = "���������", Description = "������ ������� ���������",    ColorHex = "#10B981" },
                    new PaymentStatusEntity { Name = "����������", Description = "���� ������ ����",          ColorHex = "#EF4444" }
                };
                context.PaymentStatuses.AddRange(paymentStatuses);
                await context.SaveChangesAsync();
            }
            else
            {
                //await EnsurePaymentStatusAsync(context, "���������", "#F59E0B", "������ ��������� � ��������");
                //await EnsurePaymentStatusAsync(context, "���������", "#10B981", "������ ������� ���������");
                //await EnsurePaymentStatusAsync(context, "����������", "#EF4444", "���� ������ ����");
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
                new Client { Name = "���� ������",      Email = "ivan@example.com",  Phone = "+7-900-010-01-01", Company = "�������",                Address = "��. ������, �. 10, ������" },
                new Client { Name = "����� �������",    Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "������ �������",         Address = "��. ����, �. 25, �����-���������" },
                new Client { Name = "������� �������",  Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "������� ���������",      Address = "��. ���������, �. 5, ������������" },
                new Client { Name = "��������� ��������", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "������������� �����������", Address = "��. ��������, �. 12, �����������" },
                new Client { Name = "������� ��������", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "�������� ����������", Address = "��. �������, �. 8, ������" }
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
                ("ivan@example.com",   "���������� �������� �����������", "��������� + ��", ClientCaseStatus.Open),
                ("ivan@example.com",   "��������� �������� SaaS",         "�������� ��������", ClientCaseStatus.Open),
                ("maria@company.com",  "�������� �����",                  "UI/UX + ������", ClientCaseStatus.OnHold),
                ("alex@business.com",  "�������� ������ �101",            "�������� 50 ��.", ClientCaseStatus.Open),
                ("katya@startup.com",  "������� ������ R&D",              "���� 1 � ���", ClientCaseStatus.Open),
                ("dmitry@consulting.com","������������� 2025",            "���������� ������������", ClientCaseStatus.Closed)
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
                // ���� ������ � ���������� �������� �����������
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "���������� �������� �����������",
                    Date = today.AddDays(-10),
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "����� �� ��������",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-10),
                    DealType = "����������",
                    IncomeType = "����� �� �����",
                    Source = "���������� �������",
                    StatusName = "���������"
                },
                new {
                    Email = "ivan@example.com",
                    CaseTitle = "���������� �������� �����������",
                    Date = today.AddDays(20),
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "������������� ������",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "����������",
                    IncomeType = "����� �� �����",
                    Source = "���������� �����",
                    StatusName = "���������"
                },

                // ����� ������� � �������� �����
                new {
                    Email = "maria@company.com",
                    CaseTitle = "�������� �����",
                    Date = today.AddDays(-5),
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "���� 1 � ���������",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-5),
                    DealType = "������",
                    IncomeType = "����� �� �����",
                    Source = "PayPal",
                    StatusName = "���������"
                },
                new {
                    Email = "maria@company.com",
                    CaseTitle = "�������� �����",
                    Date = today.AddDays(-1),
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "���� 2 � UI",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "������",
                    IncomeType = "����� �� �����",
                    Source = "���������� �������",
                    StatusName = "����������"
                },

                // ������� ������� � �������� ������ �101
                new {
                    Email = "alex@business.com",
                    CaseTitle = "�������� ������ �101",
                    Date = today.AddDays(7),
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "������ �� ��������",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "������� ������",
                    IncomeType = "������ ������",
                    Source = "���",
                    StatusName = "���������"
                },

                // ��������� �������� � ������� ������ R&D
                new {
                    Email = "katya@startup.com",
                    CaseTitle = "������� ������ R&D",
                    Date = today.AddDays(30),
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "���� 1 � ���",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "������",
                    IncomeType = "������ ������",
                    Source = "���������� �������",
                    StatusName = "���������"
                },

                // ������� �������� � ������������� 2025 (������)
                new {
                    Email = "dmitry@consulting.com",
                    CaseTitle = "������������� 2025",
                    Date = today.AddDays(-3),
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "������� �� ��� �������������",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-3),
                    DealType = "������������",
                    IncomeType = "������ �������",
                    Source = "��������",
                    StatusName = "���������"
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
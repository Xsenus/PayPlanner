using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// ��������� ���������� �� ��������� �������: �������, ����, ����������� � ����������� �������.
    /// </summary>
    public class SeedDataService
    {
        public static async Task SeedAsync(PaymentContext context)
        {
            // ���� ��� ���� �������, �������, ��� ���� ������
            if (await context.Clients.AnyAsync()) return;

            // ---------- ������� ----------
            var clients = new[]
            {
                new Client { Name = "���� ������", Email = "ivan@example.com", Phone = "+7-900-010-01-01", Company = "�������", Address = "��. ������, �. 10, ������" },
                new Client { Name = "����� �������", Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "������ �������", Address = "��. ����, �. 25, �����-���������" },
                new Client { Name = "������� �������", Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "������� ���������", Address = "��. ���������, �. 5, ������������" },
                new Client { Name = "��������� ��������", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "������������� �����������", Address = "��. ��������, �. 12, �����������" },
                new Client { Name = "������� ��������", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "�������� ����������", Address = "��. �������, �. 8, ������" }
            };
            context.Clients.AddRange(clients);

            // ---------- ���� ������ ----------
            var dealTypes = new[]
            {
                new DealType { Name = "����������", Description = "���������������� ���������������� ������", ColorHex = "#3B82F6" },
                new DealType { Name = "������� ������", Description = "������ ������� ���������", ColorHex = "#10B981" },
                new DealType { Name = "��������", Description = "���������� ��������� ������", ColorHex = "#8B5CF6" },
                new DealType { Name = "������", Description = "������ �� ������� � ������������� �������", ColorHex = "#F59E0B" },
                new DealType { Name = "������������", Description = "������������ ��������� �� ������������", ColorHex = "#EF4444" }
            };
            context.DealTypes.AddRange(dealTypes);

            // ---------- ���� ������ ----------
            var incomeTypes = new[]
            {
                new IncomeType { Name = "����� �� �����", Description = "����� �� �������� �����", ColorHex = "#10B981" },
                new IncomeType { Name = "������� �������", Description = "����� �� ������� �������", ColorHex = "#059669" },
                new IncomeType { Name = "��������������", Description = "����� �� �������� � ������", ColorHex = "#047857" },
                new IncomeType { Name = "��������", Description = "����� �� ��������� �� �����������", ColorHex = "#065F46" },
                new IncomeType { Name = "������ ������", Description = "������ �� ������ ����������", ColorHex = "#064E3B" }
            };
            context.IncomeTypes.AddRange(incomeTypes);

            // ---------- ��������� ������ ----------
            var paymentSources = new[]
            {
                new PaymentSource { Name = "���������� �������", Description = "������ ���������� �������", ColorHex = "#6B7280" },
                new PaymentSource { Name = "���������� �����", Description = "������ ���������� ������", ColorHex = "#4B5563" },
                new PaymentSource { Name = "PayPal", Description = "������ ����� PayPal", ColorHex = "#374151" },
                new PaymentSource { Name = "���", Description = "������ ���������� �����", ColorHex = "#1F2937" },
                new PaymentSource { Name = "��������", Description = "������ ���������", ColorHex = "#111827" }
            };
            context.PaymentSources.AddRange(paymentSources);

            // ---------- ������� ������ (����������) ----------
            var paymentStatuses = new[]
            {
                new PaymentStatusEntity { Name = "���������", Description = "������ ��������� � ��������", ColorHex = "#F59E0B" },
                new PaymentStatusEntity { Name = "���������", Description = "������ ������� ���������", ColorHex = "#10B981" },
                new PaymentStatusEntity { Name = "����������", Description = "���� ������ ����", ColorHex = "#EF4444" }
            };
            context.PaymentStatuses.AddRange(paymentStatuses);

            // ���������, ����� �������� Id � ������������ � ��������
            await context.SaveChangesAsync();

            // ---------- ���� �������� (ClientCase) ----------
            var cases = new List<ClientCase>
            {
                new ClientCase { ClientId = clients[0].Id, Title = "���������� �������� �����������", Description = "��������� + ��", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[0].Id, Title = "��������� �������� SaaS", Description = "�������� ��������", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[1].Id, Title = "�������� �����", Description = "UI/UX + ������", Status = ClientCaseStatus.OnHold },
                new ClientCase { ClientId = clients[2].Id, Title = "�������� ������ �101", Description = "�������� 50 ��.", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[3].Id, Title = "������� ������ R&D", Description = "���� 1 � ���", Status = ClientCaseStatus.Open },
                new ClientCase { ClientId = clients[4].Id, Title = "������������� 2025", Description = "���������� ������������", Status = ClientCaseStatus.Closed }
            };
            context.ClientCases.AddRange(cases);

            await context.SaveChangesAsync();

            // ������� ������� �� ������ (��� ������� ��������)
            var dealTypeByName = dealTypes.ToDictionary(d => d.Name, d => d.Id);
            var incomeTypeByName = incomeTypes.ToDictionary(d => d.Name, d => d.Id);
            var sourceByName = paymentSources.ToDictionary(s => s.Name, s => s.Id);
            var statusByName = paymentStatuses.ToDictionary(s => s.Name, s => s.Id);

            // ---------- �������, ����������� � ����� ----------
            var today = DateTime.UtcNow.Date;

            var payments = new List<Payment>
            {
                // ���� ������ � ����: ���������� �������� �����������
                new Payment
                {
                    ClientId = clients[0].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[0].Id && c.Title.Contains("�����������")).Id,
                    Date = today.AddDays(-10),
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "����� �� ��������",
                    IsPaid = true,
                    PaidDate = today.AddDays(-10),
                    DealTypeId = dealTypeByName["����������"],
                    IncomeTypeId = incomeTypeByName["����� �� �����"],
                    PaymentSourceId = sourceByName["���������� �������"],
                    PaymentStatusId = statusByName["���������"]
                },
                new Payment
                {
                    ClientId = clients[0].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[0].Id && c.Title.Contains("�����������")).Id,
                    Date = today.AddDays(20),
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "������������� ������",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["����������"],
                    IncomeTypeId = incomeTypeByName["����� �� �����"],
                    PaymentSourceId = sourceByName["���������� �����"],
                    PaymentStatusId = statusByName["���������"]
                },

                // ����� ������� � ����: �������� �����
                new Payment
                {
                    ClientId = clients[1].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[1].Id && c.Title.Contains("��������")).Id,
                    Date = today.AddDays(-5),
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "���� 1 � ���������",
                    IsPaid = true,
                    PaidDate = today.AddDays(-5),
                    DealTypeId = dealTypeByName["������"],
                    IncomeTypeId = incomeTypeByName["����� �� �����"],
                    PaymentSourceId = sourceByName["PayPal"],
                    PaymentStatusId = statusByName["���������"]
                },
                new Payment
                {
                    ClientId = clients[1].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[1].Id && c.Title.Contains("��������")).Id,
                    Date = today.AddDays(-1),
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "���� 2 � UI",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["������"],
                    IncomeTypeId = incomeTypeByName["����� �� �����"],
                    PaymentSourceId = sourceByName["���������� �������"],
                    PaymentStatusId = statusByName["����������"]
                },

                // ������� ������� � ����: �������� ������ �101
                new Payment
                {
                    ClientId = clients[2].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[2].Id && c.Title.Contains("��������")).Id,
                    Date = today.AddDays(7),
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "������ �� ��������",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["������� ������"],
                    IncomeTypeId = incomeTypeByName["������� �������"],
                    PaymentSourceId = sourceByName["���"],
                    PaymentStatusId = statusByName["���������"]
                },

                // ��������� �������� � ����: ������� ������ R&D
                new Payment
                {
                    ClientId = clients[3].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[3].Id && c.Title.Contains("R&D")).Id,
                    Date = today.AddDays(30),
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "���� 1 � ���",
                    IsPaid = false,
                    DealTypeId = dealTypeByName["������"],
                    IncomeTypeId = incomeTypeByName["������ ������"],
                    PaymentSourceId = sourceByName["���������� �������"],
                    PaymentStatusId = statusByName["���������"]
                },

                // ������� �������� � ����: ������������� 2025 (������)
                new Payment
                {
                    ClientId = clients[4].Id,
                    ClientCaseId = cases.First(c => c.ClientId == clients[4].Id && c.Title.Contains("�������������")).Id,
                    Date = today.AddDays(-3),
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "������� �� ��� �������������",
                    IsPaid = true,
                    PaidDate = today.AddDays(-3),
                    DealTypeId = dealTypeByName["������������"],
                    PaymentSourceId = sourceByName["��������"],
                    PaymentStatusId = statusByName["���������"]
                },
            };

            context.Payments.AddRange(payments);
            await context.SaveChangesAsync();
        }
    }
}
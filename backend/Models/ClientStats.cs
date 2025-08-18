namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ���������� �� �������, ������� �����, ���������� �������� � ����.
    /// </summary>
    public class ClientStats
    {
        /// <summary>
        /// ������������� �������.
        /// </summary>
        public int ClientId { get; set; }

        /// <summary>
        /// ��� �������.
        /// </summary>
        public string ClientName { get; set; } = string.Empty;

        /// <summary>
        /// ���� ���������� �������.
        /// </summary>
        public DateTime? LastPaymentDate { get; set; }

        /// <summary>
        /// ������ ����� (������ ����� �������).
        /// </summary>
        public decimal NetAmount { get; set; }

        /// <summary>
        /// ���������� ���������� ��������.
        /// </summary>
        public int PaidPayments { get; set; }

        /// <summary>
        /// ���������� ��������� ��������.
        /// </summary>
        public int PendingPayments { get; set; }

        /// <summary>
        /// ������ ��������� ��������.
        /// </summary>
        public List<Payment> RecentPayments { get; set; } = new();

        /// <summary>
        /// ����� ����� �������� �������.
        /// </summary>
        public decimal TotalExpenses { get; set; }

        /// <summary>
        /// ����� ����� ������� �������.
        /// </summary>
        public decimal TotalIncome { get; set; }

        /// <summary>
        /// ����� ���������� ��������.
        /// </summary>
        public int TotalPayments { get; set; }
    }
}
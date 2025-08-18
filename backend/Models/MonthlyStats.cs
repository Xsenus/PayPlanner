namespace PayPlanner.Api.Models
{
    /// <summary>
    /// �������� ���������� �� �������, �������� � �������� ��������.
    /// </summary>
    public class MonthlyStats
    {
        /// <summary>
        /// ������� ���������� (�� 0 �� 100).
        /// </summary>
        public double CompletionRate { get; set; }

        /// <summary>
        /// ���������� �� �������� ��������.
        /// </summary>
        public StatusCounts Counts { get; set; } = new();

        /// <summary>
        /// ����� ����� ��������.
        /// </summary>
        public decimal Expense { get; set; }

        /// <summary>
        /// ����� ����� �������.
        /// </summary>
        public decimal Income { get; set; }

        /// <summary>
        /// ������� (������ ����� �������).
        /// </summary>
        public decimal Profit { get; set; }
    }
}
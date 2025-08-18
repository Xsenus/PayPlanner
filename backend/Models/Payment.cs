namespace PayPlanner.Api.Models
{
    /// <summary>
    /// �������� ������� (������ ��� ������).
    /// </summary>
    public class Payment
    {
        /// <summary>
        /// ����� ������� (� ������ �����).
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// ��������� �� �������.
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        /// ��������� �� ���� �������.
        /// </summary>
        public ClientCase? ClientCase { get; set; }

        /// <summary>
        /// ������� ���� �� ���� �������, � �������� ��������� ������.
        /// </summary>
        public int? ClientCaseId { get; set; }

        /// <summary>
        /// ������� ���� �� �������.
        /// </summary>
        public int? ClientId { get; set; }

        /// <summary>
        /// ���� � ����� �������� ������ (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// �������� ��� ����������� ���� �������.
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// ��������� �� ��� ������.
        /// </summary>
        public DealType? DealType { get; set; }

        /// <summary>
        /// ������� ���� �� ��� ������ (���� ������������ ����������).
        /// </summary>
        public int? DealTypeId { get; set; }

        /// <summary>
        /// �������� �������� �������.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// ���������� ������������� �������.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// ��������� �� ��� ������.
        /// </summary>
        public IncomeType? IncomeType { get; set; }

        /// <summary>
        /// ������� ���� �� ��� ������ (���� ������������ ����������).
        /// </summary>
        public int? IncomeTypeId { get; set; }

        /// <summary>
        /// �������, ��� ������ �������.
        /// </summary>
        public bool IsPaid { get; set; } = false;

        /// <summary>
        /// ������������ ������� �� �������.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// ���� ����������� ������ (���� ��������).
        /// </summary>
        public DateTime? PaidDate { get; set; }

        /// <summary>
        /// ��������� �� �������� �������.
        /// </summary>
        public PaymentSource? PaymentSource { get; set; }

        /// <summary>
        /// ������� ���� �� �������� ������� (���� ������������ ����������).
        /// </summary>
        public int? PaymentSourceId { get; set; }

        /// <summary>
        /// ��������� �� �������� ������� �������.
        /// </summary>
        public PaymentStatusEntity? PaymentStatusEntity { get; set; }

        /// <summary>
        /// ������� ���� �� �������� ������� ������� (���� ������������ ����������).
        /// </summary>
        public int? PaymentStatusId { get; set; }

        /// <summary>
        /// ������ ������� (���������/��������/���������).
        /// </summary>
        public PaymentStatus Status { get; set; }

        /// <summary>
        /// ��� ������� (������/������).
        /// </summary>
        public PaymentType Type { get; set; }
    }
}

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ���������� �������� �������.
    /// </summary>
    public class PaymentStatusEntity
    {
        /// <summary>
        /// ���� � HEX-�������.
        /// </summary>
        public string ColorHex { get; set; } = "#6B7280";

        /// <summary>
        /// ���� ��������.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// �������� �������.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// ������������� �������.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// ������� ����������.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// �������� �������.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// �������, ����������� � ������� �������.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
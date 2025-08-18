namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ��� ������ (��������� �������).
    /// </summary>
    public class DealType
    {
        /// <summary>
        /// ���� � HEX-�������.
        /// </summary>
        public string ColorHex { get; set; } = "#3B82F6";

        /// <summary>
        /// ���� ��������.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// �������� ���� ������.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// ������������� ���� ������.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// ������� ����������.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// �������� ���� ������.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// �������, ����������� � ������� ���� ������.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
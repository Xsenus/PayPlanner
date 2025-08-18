using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ����/������ ������� (���c), � �������� ����� ����������� �������.
    /// </summary>
    public class ClientCase
    {
        /// <summary>
        /// ��������� �� �������-���������.
        /// </summary>
        public Client? Client { get; set; }

        /// <summary>
        /// ������������� �������-��������� ����.
        /// </summary>
        public int ClientId { get; set; }

        /// <summary>
        /// ���� �������� ���� (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// �������� ��� ���������� �� ����.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// ���������� ������������� ����.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// �������, ����������� � ����� ����.
        /// </summary>
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        /// <summary>
        /// ������ ���� (��������/� ������/�������).
        /// </summary>
        public ClientCaseStatus Status { get; set; } = ClientCaseStatus.Open;

        /// <summary>
        /// �������� ��������/���� ����.
        /// </summary>
        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;
    }
}
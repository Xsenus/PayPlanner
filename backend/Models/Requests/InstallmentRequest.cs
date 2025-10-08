using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests
{
    /// <summary>
    /// <para>������� ��������� ��� ������� ��������� (��������).</para>
    /// <para>��� �������� �������� ����������� � ������, ������ � � ��������� �������.</para>
    /// <para>��� �������� �������� ������������ ��� <c>decimal</c>.</para>
    /// </summary>
    public class InstallmentRequest
    {
        /// <summary>
        /// <para>������� ���������� ������, % ������� (��������, 12.5).</para>
        /// </summary>
        [Range(0, 1000, ErrorMessage = "AnnualRate ������ ���� � ��������� 0..1000%.")]
        public decimal AnnualRate { get; set; }

        /// <summary>
        /// <para>�������������� �����, ���������� �� ����� ���������.</para>
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "DownPayment �� ����� ���� �������������.")]
        public decimal DownPayment { get; set; }

        /// <summary>
        /// <para>���������� ������� ��������� (����� ����� &gt; 0).</para>
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Months ������ ���� ������ 0.")]
        public int Months { get; set; }

        /// <summary>
        /// <para>���� ������� ������� (������ �������).</para>
        /// </summary>
        [DataType(DataType.Date)]
        public DateTime StartDate { get; set; }

        /// <summary>
        /// <para>����� ��������� (����� ��������) �� ������ ��������������� ������.</para>
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "Total �� ����� ���� �������������.")]
        public decimal Total { get; set; }
    }
}
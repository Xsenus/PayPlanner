namespace PayPlanner.Api.Models.Responses
{
    /// <summary>
    /// <para>��������� �������: ���������, ���� � ������ � ���������� ������.</para>
    /// </summary>
    public class InstallmentResponse
    {
        /// <summary>
        /// <para>�������� ������� �� �������.</para>
        /// </summary>
        public List<InstallmentItem> Items { get; set; } = new();

        /// <summary>
        /// <para>��������� ��������� (����� ���� �������� ����� ���� �������).</para>
        /// </summary>
        public decimal Overpay { get; set; }

        /// <summary>
        /// <para>����� � ������ (������� �������������� �����).</para>
        /// </summary>
        public decimal ToPay { get; set; }
    }
}
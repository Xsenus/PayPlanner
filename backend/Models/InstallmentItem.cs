namespace PayPlanner.Api.Models
{
    /// <summary>
    /// <para>������� ������� �������� �� �����.</para>
    /// </summary>
    public class InstallmentItem
    {
        /// <summary>
        /// <para>������� ����� ����� ����� �������.</para>
        /// </summary>
        public decimal Balance { get; set; }

        /// <summary>
        /// <para>���� �������.</para>
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// <para>�������� �� �����.</para>
        /// </summary>
        public decimal Interest { get; set; }

        /// <summary>
        /// <para>�������� ����� �� ����� (�������� ���� + ��������).</para>
        /// </summary>
        public decimal Payment { get; set; }

        /// <summary>
        /// <para>��������� ��������� ����� �� �����.</para>
        /// </summary>
        public decimal Principal { get; set; }
    }
}
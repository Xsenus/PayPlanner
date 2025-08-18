namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ������ �������: ���������, �������� ��� ���������.
    /// </summary>
    public enum PaymentStatus
    {
        /// <summary>
        /// ����� ���������.
        /// </summary>
        Pending,

        /// <summary>
        /// ����� ��������.
        /// </summary>
        Completed,

        /// <summary>
        /// ����� ���������.
        /// </summary>
        Overdue
    }
}

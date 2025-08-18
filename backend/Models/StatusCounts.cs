namespace PayPlanner.Api.Models
{
    /// <summary>
    /// �������������� ���������� �� �������� ��������.
    /// </summary>
    public class StatusCounts
    {
        /// <summary>
        /// ���������� ����������� ��������.
        /// </summary>
        public int Completed { get; set; }

        /// <summary>
        /// ���������� ������������ ��������.
        /// </summary>
        public int Overdue { get; set; }

        /// <summary>
        /// ���������� ��������� ��������.
        /// </summary>
        public int Pending { get; set; }

        /// <summary>
        /// ����� ���������� ��������.
        /// </summary>
        public int Total { get; set; }
    }
}
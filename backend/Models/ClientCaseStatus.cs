namespace PayPlanner.Api.Models
{
    /// <summary>
    /// ������������ �������� ���� �������.
    /// </summary>
    public enum ClientCaseStatus
    {
        /// <summary>
        /// ���� ������� � ��������� � ������.
        /// </summary>
        Open = 0,

        /// <summary>
        /// ���� ��������������.
        /// </summary>
        OnHold = 1,

        /// <summary>
        /// ���� �������.
        /// </summary>
        Closed = 2
    }
}
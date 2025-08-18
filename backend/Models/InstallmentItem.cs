namespace PayPlanner.Api.Models
{
    /// <summary>
    /// <para>Элемент графика платежей за месяц.</para>
    /// </summary>
    public class InstallmentItem
    {
        /// <summary>
        /// <para>Остаток долга после учёта платежа.</para>
        /// </summary>
        public decimal Balance { get; set; }

        /// <summary>
        /// <para>Дата платежа.</para>
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// <para>Проценты за месяц.</para>
        /// </summary>
        public decimal Interest { get; set; }

        /// <summary>
        /// <para>Итоговый платёж за месяц (основной долг + проценты).</para>
        /// </summary>
        public decimal Payment { get; set; }

        /// <summary>
        /// <para>Погашение основного долга за месяц.</para>
        /// </summary>
        public decimal Principal { get; set; }
    }
}
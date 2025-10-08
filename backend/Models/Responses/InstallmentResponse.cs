namespace PayPlanner.Api.Models.Responses
{
    /// <summary>
    /// <para>–езультат расчЄта: переплата, итог к оплате и построчный график.</para>
    /// </summary>
    public class InstallmentResponse
    {
        /// <summary>
        /// <para>Ёлементы графика по мес€цам.</para>
        /// </summary>
        public List<InstallmentItem> Items { get; set; } = new();

        /// <summary>
        /// <para>—уммарна€ переплата (сумма всех платежей минус тело кредита).</para>
        /// </summary>
        public decimal Overpay { get; set; }

        /// <summary>
        /// <para>»того к оплате (включа€ первоначальный взнос).</para>
        /// </summary>
        public decimal ToPay { get; set; }
    }
}
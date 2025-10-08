using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests
{
    /// <summary>
    /// <para>Входные параметры для расчёта рассрочки (аннуитет).</para>
    /// <para>Все денежные значения указываются в рублях, ставка — в процентах годовых.</para>
    /// <para>Для денежных расчётов используется тип <c>decimal</c>.</para>
    /// </summary>
    public class InstallmentRequest
    {
        /// <summary>
        /// <para>Годовая процентная ставка, % годовых (например, 12.5).</para>
        /// </summary>
        [Range(0, 1000, ErrorMessage = "AnnualRate должна быть в диапазоне 0..1000%.")]
        public decimal AnnualRate { get; set; }

        /// <summary>
        /// <para>Первоначальный взнос, вычитается из общей стоимости.</para>
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "DownPayment не может быть отрицательным.")]
        public decimal DownPayment { get; set; }

        /// <summary>
        /// <para>Количество месяцев погашения (целое число &gt; 0).</para>
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Months должен быть больше 0.")]
        public int Months { get; set; }

        /// <summary>
        /// <para>Дата первого платежа (начало графика).</para>
        /// </summary>
        [DataType(DataType.Date)]
        public DateTime StartDate { get; set; }

        /// <summary>
        /// <para>Общая стоимость (сумма договора) до вычета первоначального взноса.</para>
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "Total не может быть отрицательным.")]
        public decimal Total { get; set; }
    }
}
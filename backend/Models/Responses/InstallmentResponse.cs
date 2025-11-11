using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses
{
    /// <summary>
    /// Ответ сервиса расчёта рассрочки.
    /// </summary>
    public class InstallmentResponse
    {
        public List<InstallmentItem> Items { get; set; } = new();

        public decimal Overpay { get; set; }

        public decimal ToPay { get; set; }

        public decimal LoanAmount { get; set; }

        public decimal BaseMonthlyPayment { get; set; }

        public decimal? RoundedMonthlyPayment { get; set; }

        public InstallmentRoundingMode RoundingMode { get; set; }

        public decimal? RoundingStep { get; set; }
    }
}

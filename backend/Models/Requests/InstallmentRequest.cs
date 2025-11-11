using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests
{
    /// <summary>
    /// Запрос на расчёт графика рассрочки.
    /// </summary>
    public class InstallmentRequest
    {
        [Range(0, 1000, ErrorMessage = "AnnualRate     0..1000%.")]
        public decimal AnnualRate { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "DownPayment    .")]
        public decimal DownPayment { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Months    0.")]
        public int Months { get; set; }

        [DataType(DataType.Date)]
        public DateTime StartDate { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Total    .")]
        public decimal Total { get; set; }

        public InstallmentRoundingMode RoundingMode { get; set; } = InstallmentRoundingMode.None;

        [Range(0, double.MaxValue, ErrorMessage = "RoundingStep    .")]
        public decimal? RoundingStep { get; set; }
    }
}

using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Сервис расчёта рассрочки (аннуитетная схема).
    /// Все денежные вычисления выполняются в <see cref="decimal"/> с округлением до 2 знаков.
    /// </summary>
    public class InstallmentService
    {
        /// <summary>
        /// Максимально допустимый срок в месяцах (защита от переполнений при возведении в степень).
        /// </summary>
        private const int MaxMonths = 600;

        /// <summary>
        /// Возведение <see cref="decimal"/> в целую степень n ≥ 0 (быстрое возведение в степень).
        /// </summary>
        private static decimal Pow(decimal x, int n)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "Степень должна быть неотрицательной.");
            if (n == 0) return 1m;

            decimal result = 1m;
            decimal baseVal = x;
            int exp = n;

            while (exp > 0)
            {
                if ((exp & 1) == 1)
                    result *= baseVal;

                baseVal *= baseVal;
                exp >>= 1;
            }

            return result;
        }

        /// <summary>
        /// Округление до 2 знаков (по умолчанию MidpointToEven).
        /// </summary>
        private static decimal Round2(decimal value) => Math.Round(value, 2);

        /// <summary>
        /// Деление <see cref="decimal"/> на <see cref="int"/> с защитой от деления на ноль.
        /// </summary>
        private static decimal SafeDiv(decimal value, int divisor)
        {
            if (divisor == 0) throw new DivideByZeroException("Деление на ноль.");
            return value / divisor;
        }

        /// <summary>
        /// Выполняет расчёт графика рассрочки по аннуитетной схеме.
        /// </summary>
        /// <param name="request">Входные параметры: сумма, ставка, срок, дата старта и т.д.</param>
        /// <returns>Итоговая переплата, сумма к оплате и помесячный график.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Если срок ≤ 0 или срок слишком большой.</exception>
        public InstallmentResponse CalculateInstallment(InstallmentRequest request)
        {
            if (request.Months <= 0)
                throw new ArgumentOutOfRangeException(nameof(request.Months), "Срок в месяцах должен быть больше нуля.");
            if (request.Months > MaxMonths)
                throw new ArgumentOutOfRangeException(nameof(request.Months), $"Срок не должен превышать {MaxMonths} месяцев.");

            // Сумма кредита после вычета первоначального взноса (не даём уйти в минус)
            decimal loanAmount = request.Total - request.DownPayment;
            if (loanAmount < 0m)
                loanAmount = 0m;

            // Месячная процентная ставка в долях
            decimal monthlyRate = (request.AnnualRate / 100m) / 12m;

            // Расчёт ежемесячного платежа; фиксируем "платёж к списанию" в копейках,
            // чтобы сумма principal+interest каждый месяц совпадала с ним (кроме последнего месяца).
            decimal monthlyPaymentRaw;
            if (monthlyRate == 0m)
            {
                monthlyPaymentRaw = SafeDiv(loanAmount, request.Months);
            }
            else
            {
                decimal onePlusI = 1m + monthlyRate;
                decimal pow = Pow(onePlusI, request.Months);     // (1 + i)^n
                decimal numerator = loanAmount * monthlyRate * pow;
                decimal denominator = pow - 1m;
                monthlyPaymentRaw = numerator / denominator;
            }
            decimal monthlyPayment = Round2(monthlyPaymentRaw);

            var items = new List<InstallmentItem>(capacity: request.Months);
            decimal balance = loanAmount;

            // Нормализуем дату к "дате без времени"
            DateTime currentDate = request.StartDate.Date;

            for (int i = 0; i < request.Months; i++)
            {
                // Проценты за месяц от текущего остатка
                decimal interestPayment = Round2(balance * monthlyRate);

                // Базовый principal как разница между фиксированным платёжом и процентами
                decimal principalPayment = Round2(monthlyPayment - interestPayment);

                bool isLast = i == request.Months - 1;

                if (isLast)
                {
                    // В последний месяц закрываем остаток без остаточных копеек
                    principalPayment = Round2(balance);
                }

                // Итоговый платёж месяца:
                // - для всех, кроме последнего, фиксированный "кассовый" платёж;
                // - для последнего — сумма процент+тело (может отличаться на копейку из-за выравнивания).
                decimal paymentThisMonth = isLast ? Round2(principalPayment + interestPayment) : monthlyPayment;

                // Обновляем остаток
                balance = Round2(balance - principalPayment);
                if (balance < 0m) balance = 0m;

                items.Add(new InstallmentItem
                {
                    Date = currentDate,
                    Principal = principalPayment,
                    Interest = interestPayment,
                    Payment = paymentThisMonth,
                    Balance = balance
                });

                currentDate = currentDate.AddMonths(1);
            }

            decimal totalPayments = Round2(items.Sum(x => x.Payment));
            decimal overpay = Round2(totalPayments - loanAmount);

            return new InstallmentResponse
            {
                Overpay = overpay,
                ToPay = Round2(totalPayments + request.DownPayment),
                Items = items
            };
        }
    }
}
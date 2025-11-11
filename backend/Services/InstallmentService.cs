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

        private static decimal ApplyRounding(decimal value, decimal step, InstallmentRoundingMode mode)
        {
            if (mode == InstallmentRoundingMode.None)
                return Round2(value);

            decimal normalizedStep = Math.Abs(step);
            if (normalizedStep <= 0m)
                return Round2(value);

            decimal scaled = value / normalizedStep;
            decimal adjusted = mode switch
            {
                InstallmentRoundingMode.Down => Math.Floor(scaled),
                InstallmentRoundingMode.Up => Math.Ceiling(scaled),
                _ => Math.Round(scaled, MidpointRounding.AwayFromZero),
            };

            return Round2(adjusted * normalizedStep);
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

            InstallmentRoundingMode roundingMode = request.RoundingMode;
            decimal? roundingStep = request.RoundingStep.HasValue && request.RoundingStep.Value > 0m
                ? Math.Abs(request.RoundingStep.Value)
                : null;
            bool roundingEnabled = roundingMode != InstallmentRoundingMode.None && roundingStep.HasValue;

            decimal? roundedMonthlyPayment = null;
            if (roundingEnabled)
            {
                decimal candidate = ApplyRounding(monthlyPayment, roundingStep!.Value, roundingMode);
                if (candidate <= 0m)
                {
                    candidate = monthlyPayment;
                }

                decimal firstMonthInterest = Round2(loanAmount * monthlyRate);
                if (candidate < firstMonthInterest)
                {
                    candidate = firstMonthInterest;
                }

                roundedMonthlyPayment = Round2(candidate);
            }

            var items = new List<InstallmentItem>(capacity: request.Months);
            decimal balance = loanAmount;

            // Нормализуем дату к "дате без времени"
            DateTime currentDate = request.StartDate.Date;

            for (int i = 0; i < request.Months; i++)
            {
                decimal interestPayment = Round2(balance * monthlyRate);
                bool isLast = i == request.Months - 1;

                decimal paymentThisMonth;
                if (isLast)
                {
                    paymentThisMonth = Round2(balance + interestPayment);
                }
                else
                {
                    paymentThisMonth = roundingEnabled && roundedMonthlyPayment.HasValue
                        ? roundedMonthlyPayment.Value
                        : monthlyPayment;

                    if (paymentThisMonth < interestPayment)
                    {
                        paymentThisMonth = interestPayment;
                    }
                }

                decimal principalPayment = Round2(paymentThisMonth - interestPayment);

                if (isLast)
                {
                    principalPayment = Round2(balance);
                    paymentThisMonth = Round2(principalPayment + interestPayment);
                }
                else if (principalPayment > balance)
                {
                    principalPayment = Round2(balance);
                    paymentThisMonth = Round2(principalPayment + interestPayment);
                }

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
                LoanAmount = Round2(loanAmount),
                BaseMonthlyPayment = monthlyPayment,
                RoundedMonthlyPayment = roundingEnabled ? roundedMonthlyPayment : null,
                RoundingMode = roundingEnabled ? roundingMode : InstallmentRoundingMode.None,
                RoundingStep = roundingEnabled ? roundingStep : null,
                Items = items
            };
        }
    }
}
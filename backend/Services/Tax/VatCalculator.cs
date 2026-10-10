using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace PayPlanner.Api.Services.Tax;

/// <summary>
/// Коды ставок НДС в позициях счёта и акта. <c>null</c> у позиции — «ставка не задана»: документ печатается
/// по прежним правилам (признак продавца «без НДС»). Ставка «none» означает «без НДС», «0» — нулевую ставку:
/// это разные вещи (освобождение и ставка 0% по-разному учитываются).
/// </summary>
public static class VatRates
{
    public const string None = "none";
    public static readonly IReadOnlyList<string> All = [None, "0", "5", "7", "10", "20", "22"];

    public static bool IsValid(string? code) => code is null || All.Contains(code, StringComparer.Ordinal);

    /// <summary>Приводит ввод («22», «22%», «Без НДС») к коду; пустое значение — <c>null</c>; неизвестное — тоже <c>null</c>, проверяйте <see cref="IsValid"/> заранее.</summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim().TrimEnd('%').Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (string.Equals(text, "без ндс", StringComparison.OrdinalIgnoreCase) || string.Equals(text, None, StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        return All.FirstOrDefault(code => string.Equals(code, text, StringComparison.Ordinal));
    }

    /// <summary>Процент ставки; для «none» и <c>null</c> — 0.</summary>
    public static decimal Percent(string? code)
        => code is null || code == None ? 0m : decimal.Parse(code, CultureInfo.InvariantCulture);

    public static string Label(string? code) => code switch
    {
        null => "—",
        None => "Без НДС",
        _ => code + "%",
    };
}

/// <summary>Проверка кода ставки в запросах: неизвестное значение даёт ошибку проверки модели (HTTP 400).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class VatRateCodeAttribute : ValidationAttribute
{
    public VatRateCodeAttribute() : base("Неизвестная ставка НДС.")
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is string text && VatRates.IsValid(string.IsNullOrWhiteSpace(text) ? null : text.Trim()));
}

public sealed record VatLine(decimal Quantity, decimal Price, string? VatRate);

public sealed record VatLineResult(decimal Total, decimal Vat);

public sealed record VatTotals(decimal Total, decimal Vat, IReadOnlyList<VatLineResult> Lines)
{
    /// <summary>Хотя бы у одной позиции задана ставка — НДС по документу считается построчно.</summary>
    public bool HasRates { get; init; }

    /// <summary>У всех позиций со ставкой выбрано «без НДС».</summary>
    public bool AllWithoutVat { get; init; }
}

/// <summary>
/// Расчёт НДС «в том числе» в сумме позиции: сумма позиции остаётся ценой, выделяется доля налога
/// <c>сумма × ставка / (100 + ставка)</c>, округление до копеек от нуля, построчное. Итог НДС — сумма строк,
/// чтобы печатные формы сходились с построчными значениями. Режимы «сверху» и «без налога» не рассчитываются.
/// </summary>
public static class VatCalculator
{
    /// <summary>New documents with explicit line rates use rounded line totals; legacy documents retain their previous arithmetic.</summary>
    public static decimal DocumentTotal(IEnumerable<VatLine> lines)
    {
        var items = lines.ToList();
        return items.Any(item => item.VatRate is not null)
            ? items.Sum(item => LineTotal(item.Quantity, item.Price))
            : items.Sum(item => item.Quantity * item.Price);
    }

    public static decimal LineTotal(decimal quantity, decimal price)
        => Math.Round(quantity * price, 2, MidpointRounding.AwayFromZero);

    public static decimal IncludedVat(decimal total, string? rate)
    {
        var percent = VatRates.Percent(rate);
        if (percent <= 0m || total <= 0m)
        {
            return 0m;
        }

        return Math.Round(total * percent / (100m + percent), 2, MidpointRounding.AwayFromZero);
    }

    public static VatTotals Calculate(IEnumerable<VatLine> lines)
    {
        var results = new List<VatLineResult>();
        decimal total = 0m, vat = 0m;
        var hasRates = false;
        var withRates = 0;
        var withoutVat = 0;
        foreach (var line in lines)
        {
            var lineTotal = LineTotal(line.Quantity, line.Price);
            var lineVat = IncludedVat(lineTotal, line.VatRate);
            results.Add(new VatLineResult(lineTotal, lineVat));
            total += lineTotal;
            vat += lineVat;
            if (line.VatRate is not null)
            {
                hasRates = true;
                withRates++;
                if (line.VatRate == VatRates.None)
                {
                    withoutVat++;
                }
            }
        }

        return new VatTotals(total, vat, results)
        {
            HasRates = hasRates,
            AllWithoutVat = withRates > 0 && withRates == withoutVat,
        };
    }
}

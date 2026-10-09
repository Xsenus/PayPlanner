namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Проверка форматов и контрольных разрядов российских реквизитов (ИНН, КПП, ОГРН, БИК, счета).
/// Методы не обращаются к БД и ничего не нормализуют: пустое значение считается «не указано», а не ошибкой.
/// </summary>
public static class RussianRequisitesValidator
{
    private static readonly int[] Inn10Weights = [2, 4, 10, 3, 5, 9, 4, 6, 8];
    private static readonly int[] Inn12FirstWeights = [7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
    private static readonly int[] Inn12SecondWeights = [3, 7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
    private static readonly int[] AccountWeights = [7, 1, 3];

    /// <summary>ИНН юрлица (10 цифр) или физлица/ИП (12 цифр) с проверкой контрольных разрядов.</summary>
    public static bool IsValidInn(string? value)
    {
        if (!TryGetDigits(value, out var digits))
        {
            return false;
        }

        return digits.Length switch
        {
            10 => digits[9] == Checksum(digits, Inn10Weights),
            12 => digits[10] == Checksum(digits, Inn12FirstWeights) && digits[11] == Checksum(digits, Inn12SecondWeights),
            _ => false,
        };
    }

    /// <summary>КПП: 9 знаков, пятый и шестой — цифра или заглавная латинская буква.</summary>
    public static bool IsValidKpp(string? value)
    {
        var text = value?.Trim();
        if (text is null || text.Length != 9)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isDigit = c is >= '0' and <= '9';
            var isUpperLatin = c is >= 'A' and <= 'Z';
            if (i is 4 or 5 ? !(isDigit || isUpperLatin) : !isDigit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ОГРН (13 цифр) или ОГРНИП (15 цифр) с проверкой контрольной цифры.</summary>
    public static bool IsValidOgrn(string? value)
    {
        var text = value?.Trim();
        if (text is null || (text.Length != 13 && text.Length != 15) || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        var divisor = text.Length == 13 ? 11L : 13L;
        var body = long.Parse(text[..^1]);
        var control = (int)(body % divisor % 10);
        return control == text[^1] - '0';
    }

    /// <summary>БИК: 9 цифр, начинается с «04» (код страны).</summary>
    public static bool IsValidBik(string? value)
    {
        var text = value?.Trim();
        return text is { Length: 9 } && text.All(char.IsAsciiDigit) && text.StartsWith("04", StringComparison.Ordinal);
    }

    /// <summary>Формат номера счёта: ровно 20 цифр.</summary>
    public static bool IsValidAccountFormat(string? value)
    {
        var text = value?.Trim();
        return text is { Length: 20 } && text.All(char.IsAsciiDigit);
    }

    /// <summary>Контрольный разряд расчётного счёта относительно БИК банка.</summary>
    public static bool IsValidSettlementAccount(string? account, string? bik)
    {
        if (!IsValidAccountFormat(account) || !IsValidBik(bik))
        {
            return false;
        }

        return AccountKeyIsValid(bik!.Trim()[^3..] + account!.Trim());
    }

    /// <summary>Контрольный разряд корреспондентского счёта относительно БИК банка.</summary>
    public static bool IsValidCorrespondentAccount(string? account, string? bik)
    {
        if (!IsValidAccountFormat(account) || !IsValidBik(bik))
        {
            return false;
        }

        return AccountKeyIsValid("0" + bik!.Trim().Substring(4, 2) + account!.Trim());
    }

    private static bool AccountKeyIsValid(string key)
    {
        var sum = 0;
        for (var i = 0; i < key.Length; i++)
        {
            sum += (key[i] - '0') * AccountWeights[i % 3] % 10;
        }

        return sum % 10 == 0;
    }

    private static int Checksum(int[] digits, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            sum += digits[i] * weights[i];
        }

        return sum % 11 % 10;
    }

    private static bool TryGetDigits(string? value, out int[] digits)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiDigit))
        {
            digits = [];
            return false;
        }

        digits = text.Select(c => c - '0').ToArray();
        return true;
    }
}

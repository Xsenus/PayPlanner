namespace PayPlanner.Api.Services;

/// <summary>
/// Converts phone numbers to the digits-only representation expected by
/// Russian telephony providers while leaving unrecognized international
/// numbers intact.
/// </summary>
public static class RussianPhoneNumberCanonicalizer
{
    public static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(character => character is >= '0' and <= '9').ToArray());
        if (digits.Length == 10)
        {
            return $"7{digits}";
        }

        if (digits.Length == 11 && digits[0] == '8')
        {
            return $"7{digits[1..]}";
        }

        return digits.Length == 0 ? null : digits;
    }
}

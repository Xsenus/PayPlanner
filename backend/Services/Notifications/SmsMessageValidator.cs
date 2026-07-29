namespace PayPlanner.Api.Services.Notifications;

public static class SmsMessageValidationErrorCodes
{
    public const string EmptyBody = "sms-body-empty";
    public const string UnresolvedTemplate = "sms-template-unresolved";
    public const string TooLong = "sms-body-too-long";
}

public sealed record SmsMessageValidationResult(
    bool IsValid,
    string? ErrorCode,
    SmsSegmentCalculation Calculation);

public static class SmsMessageValidator
{
    public const int DefaultMaxUnitCount = 480;

    public static SmsMessageValidationResult Validate(
        string? text,
        int maxUnitCount = DefaultMaxUnitCount)
    {
        if (maxUnitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUnitCount), "SMS length limit must be positive.");
        }

        var calculation = SmsSegmentCalculator.Calculate(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Invalid(SmsMessageValidationErrorCodes.EmptyBody, calculation);
        }

        if (ContainsTemplateMarker(text))
        {
            return Invalid(SmsMessageValidationErrorCodes.UnresolvedTemplate, calculation);
        }

        return calculation.UnitCount > maxUnitCount
            ? Invalid(SmsMessageValidationErrorCodes.TooLong, calculation)
            : new SmsMessageValidationResult(true, null, calculation);
    }

    private static bool ContainsTemplateMarker(string text)
        => text.Contains("{{", StringComparison.Ordinal)
            || text.Contains("}}", StringComparison.Ordinal);

    private static SmsMessageValidationResult Invalid(
        string errorCode,
        SmsSegmentCalculation calculation)
        => new(false, errorCode, calculation);
}

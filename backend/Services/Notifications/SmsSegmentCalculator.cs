using System.Collections.Frozen;

namespace PayPlanner.Api.Services.Notifications;

public enum SmsTextEncoding
{
    Gsm7,
    Unicode,
}

public sealed record SmsSegmentCalculation(
    SmsTextEncoding Encoding,
    int UnitCount,
    int SegmentCount,
    int SingleSegmentLimit,
    int ConcatenatedSegmentLimit,
    int UnitsInLastSegment)
{
    public bool IsMultipart => SegmentCount > 1;
}

public static class SmsSegmentCalculator
{
    private const int Gsm7SingleSegmentLimit = 160;
    private const int Gsm7ConcatenatedSegmentLimit = 153;
    private const int UnicodeSingleSegmentLimit = 70;
    private const int UnicodeConcatenatedSegmentLimit = 67;

    private static readonly FrozenSet<char> Gsm7BasicCharacters =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà"
            .ToFrozenSet();

    private static readonly FrozenSet<char> Gsm7ExtensionCharacters =
        "\f^{}\\[~]|€".ToFrozenSet();

    public static SmsSegmentCalculation Calculate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return CreateResult(SmsTextEncoding.Gsm7, 0);
        }

        var gsm7Units = 0;
        foreach (var character in text)
        {
            if (Gsm7BasicCharacters.Contains(character))
            {
                gsm7Units++;
                continue;
            }

            if (Gsm7ExtensionCharacters.Contains(character))
            {
                gsm7Units += 2;
                continue;
            }

            return CreateResult(SmsTextEncoding.Unicode, text.Length);
        }

        return CreateResult(SmsTextEncoding.Gsm7, gsm7Units);
    }

    private static SmsSegmentCalculation CreateResult(SmsTextEncoding encoding, int unitCount)
    {
        var singleSegmentLimit = encoding == SmsTextEncoding.Gsm7
            ? Gsm7SingleSegmentLimit
            : UnicodeSingleSegmentLimit;
        var concatenatedSegmentLimit = encoding == SmsTextEncoding.Gsm7
            ? Gsm7ConcatenatedSegmentLimit
            : UnicodeConcatenatedSegmentLimit;

        var segmentCount = unitCount == 0
            ? 0
            : unitCount <= singleSegmentLimit
                ? 1
                : (unitCount + concatenatedSegmentLimit - 1) / concatenatedSegmentLimit;

        var unitsInLastSegment = segmentCount switch
        {
            0 => 0,
            1 => unitCount,
            _ => unitCount - ((segmentCount - 1) * concatenatedSegmentLimit),
        };

        return new SmsSegmentCalculation(
            encoding,
            unitCount,
            segmentCount,
            singleSegmentLimit,
            concatenatedSegmentLimit,
            unitsInLastSegment);
    }
}

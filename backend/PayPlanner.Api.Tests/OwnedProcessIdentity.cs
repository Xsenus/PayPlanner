using System.Diagnostics;
using System.Globalization;

namespace PayPlanner.Api.Tests;

// Shared only by the tests and their child probe. Linux Process.StartTime adds
// a boot-time estimate cached independently in each process; that UTC value is
// not an exact cross-process identity. Keep the kernel start ticks unchanged.
internal static class OwnedProcessIdentity
{
    internal static string Capture(Process process)
    {
        var pid = process.Id;
        if (!OperatingSystem.IsLinux())
            return FormattableString.Invariant($"start-utc:{pid}:{process.StartTime.ToUniversalTime().Ticks}");

        var bootId = ReadBoundedText("/proc/sys/kernel/random/boot_id", 128);
        var stat = ReadBoundedText(FormattableString.Invariant($"/proc/{pid}/stat"), 4096);
        return ParseLinux(pid, bootId, stat);
    }

    internal static string ParseLinux(int expectedPid, string bootId, string stat)
    {
        if (expectedPid <= 0 || !Guid.TryParseExact(bootId.Trim(), "D", out var boot) || boot == Guid.Empty)
            throw new InvalidDataException("Owned process boot identity is invalid.");

        // comm is enclosed in parentheses and can itself contain spaces or ')'.
        // The numeric fields after its final ')' cannot contain parentheses.
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 2 || close <= open || close + 2 >= stat.Length || stat[open - 1] != ' ' || stat[close + 1] != ' '
            || !int.TryParse(stat.AsSpan(0, open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var actualPid)
            || actualPid != expectedPid)
            throw new InvalidDataException("Owned process stat identity is invalid.");

        var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The first remaining field is state (field 3); starttime is field 22.
        if (fields.Length < 20 || fields[0].Length != 1
            || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks))
            throw new InvalidDataException("Owned process kernel start time is invalid.");

        return FormattableString.Invariant($"linux:{boot:N}:{actualPid}:{startTicks}");
    }

    private static string ReadBoundedText(string path, int maximumCharacters)
    {
        using var reader = File.OpenText(path);
        var buffer = new char[maximumCharacters + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        if (read > maximumCharacters)
            throw new InvalidDataException("Owned process identity exceeds its read bound.");
        return new string(buffer, 0, read);
    }
}

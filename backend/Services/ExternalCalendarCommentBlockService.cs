using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static partial class ExternalCalendarCommentBlockService
{
    public const int DefaultMaximumDescriptionLength = 7000;
    public const string StartMarker = "--- PAYPLANNER-COMMENTS-V1 START";
    public const string EndMarker = "--- PAYPLANNER-COMMENTS-V1 END ---";

    public static ExternalCalendarCommentBlockResult AppendToDescription(
        string baseDescription,
        IReadOnlyCollection<TaskJournalComment> comments,
        int maximumDescriptionLength = DefaultMaximumDescriptionLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDescriptionLength, 512);

        var cleanBase = StripBlock(baseDescription).TrimEnd();
        var visibleComments = comments
            .Where(item =>
                item.IsExternalCalendarVisible
                && item.ExternalDeletedAt is null
                && string.Equals(item.Kind, "comment", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToList();

        if (visibleComments.Count == 0)
        {
            return new ExternalCalendarCommentBlockResult(
                cleanBase,
                ComputeSemanticHash(Array.Empty<TaskJournalComment>()),
                0,
                false);
        }

        var available = maximumDescriptionLength - cleanBase.Length - (cleanBase.Length == 0 ? 0 : 2);
        if (available < 256)
        {
            return new ExternalCalendarCommentBlockResult(
                cleanBase.Length <= maximumDescriptionLength
                    ? cleanBase
                    : cleanBase[..maximumDescriptionLength],
                ComputeSemanticHash(Array.Empty<TaskJournalComment>()),
                0,
                true);
        }

        var entries = new List<string>(visibleComments.Count);
        var exportedCount = 0;
        var entriesLength = 0;
        foreach (var comment in visibleComments)
        {
            var entry = SerializeComment(comment);
            var candidateCount = exportedCount + 1;
            var candidateEntriesLength = entriesLength + entry.Length;
            if (GetBlockLength(
                    candidateEntriesLength,
                    candidateCount,
                    truncated: candidateCount < visibleComments.Count) > available)
            {
                break;
            }

            entries.Add(entry);
            entriesLength = candidateEntriesLength;
            exportedCount = candidateCount;
        }

        var truncated = exportedCount < visibleComments.Count;
        var semanticHash = ComputeSemanticHash(visibleComments.Take(exportedCount));
        var block = BuildBlock(entries, semanticHash, truncated);

        if (block.Length > available)
        {
            return new ExternalCalendarCommentBlockResult(
                cleanBase,
                ComputeSemanticHash(Array.Empty<TaskJournalComment>()),
                0,
                true);
        }

        var description = cleanBase.Length == 0 ? block : $"{cleanBase}\n\n{block}";
        return new ExternalCalendarCommentBlockResult(description, semanticHash, exportedCount, truncated);
    }

    public static string StripBlock(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return string.Empty;
        }

        var start = description.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return description;
        }

        var end = description.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return description;
        }

        end += EndMarker.Length;
        var prefix = description[..start].TrimEnd();
        var suffix = description[end..].TrimStart('\r', '\n');
        return suffix.Length == 0 ? prefix : $"{prefix}\n{suffix}".Trim();
    }

    public static ExternalCalendarParsedCommentBlock? TryParse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var start = description.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = start < 0 ? -1 : description.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return null;
        }

        var block = description[start..(end + EndMarker.Length)];
        var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var header = lines.FirstOrDefault();
        var hashMatch = header is null ? null : HeaderHashRegex().Match(header);
        if (hashMatch?.Success != true)
        {
            return null;
        }

        var comments = new List<ExternalCalendarParsedComment>();
        for (var index = 1; index < lines.Length - 1; index++)
        {
            var match = CommentHeaderRegex().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var textLines = new List<string>();
            index++;
            while (index < lines.Length - 1 && !CommentHeaderRegex().IsMatch(lines[index]))
            {
                if (!string.Equals(lines[index], "Показана часть комментариев. Полная история доступна в CRM.", StringComparison.Ordinal))
                {
                    textLines.Add(lines[index]);
                }
                index++;
            }
            index--;

            if (!int.TryParse(match.Groups["id"].Value, out var id)
                || !DateTime.TryParse(match.Groups["at"].Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt))
            {
                return null;
            }

            comments.Add(new ExternalCalendarParsedComment(
                id,
                match.Groups["author"].Value.Trim(),
                string.Join('\n', textLines).TrimEnd(),
                createdAt.ToUniversalTime()));
        }

        var expectedHash = hashMatch.Groups["hash"].Value;
        var actualHash = ComputeSemanticHash(comments);
        return new ExternalCalendarParsedCommentBlock(
            comments,
            expectedHash,
            string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase),
            block.Contains("Показана часть комментариев.", StringComparison.Ordinal));
    }

    public static string ComputeSemanticHash(IEnumerable<TaskJournalComment> comments)
        => ComputeHash(comments
            .Where(item => item.IsExternalCalendarVisible && item.ExternalDeletedAt is null)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => $"{item.Id}|{Normalize(item.AuthorName)}|{item.CreatedAt.ToUniversalTime():O}|{Normalize(item.Text)}"));

    private static string ComputeSemanticHash(IEnumerable<ExternalCalendarParsedComment> comments)
        => ComputeHash(comments
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => $"{item.Id}|{Normalize(item.AuthorName)}|{item.CreatedAt.ToUniversalTime():O}|{Normalize(item.Text)}"));

    private static string ComputeHash(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));

    private static string SerializeComment(TaskJournalComment comment)
    {
        var author = Sanitize(comment.AuthorName).Replace('\n', ' ');
        var text = Sanitize(comment.Text);
        return $"[Комментарий #{comment.Id}; {comment.CreatedAt.ToUniversalTime():O}; {author}]\n{text}";
    }

    private static string BuildBlock(IReadOnlyCollection<string> entries, string semanticHash, bool truncated)
    {
        var builder = new StringBuilder();
        builder.Append(StartMarker).Append(" hash=").Append(semanticHash).AppendLine(" ---");
        builder.AppendLine("Комментарии, разрешённые для внешнего календаря:");
        if (entries.Count > 0)
        {
            builder.Append(string.Join("\n\n", entries)).AppendLine();
        }
        if (truncated)
        {
            builder.AppendLine("Показана часть комментариев. Полная история доступна в CRM.");
        }
        builder.Append(EndMarker);
        return builder.ToString();
    }

    private static int GetBlockLength(int entriesLength, int entryCount, bool truncated)
    {
        const int sha256HexLength = 64;
        const string hashPrefix = " hash=";
        const string headerSuffix = " ---";
        const string intro = "Комментарии, разрешённые для внешнего календаря:";
        const string truncatedNotice = "Показана часть комментариев. Полная история доступна в CRM.";
        var newlineLength = Environment.NewLine.Length;

        return StartMarker.Length
            + hashPrefix.Length
            + sha256HexLength
            + headerSuffix.Length
            + newlineLength
            + intro.Length
            + newlineLength
            + (entryCount > 0 ? entriesLength + ((entryCount - 1) * 2) + newlineLength : 0)
            + (truncated ? truncatedNotice.Length + newlineLength : 0)
            + EndMarker.Length;
    }

    private static string Sanitize(string value)
        => Normalize(value)
            .Replace(StartMarker, "PAYPLANNER COMMENTS", StringComparison.Ordinal)
            .Replace(EndMarker, "PAYPLANNER COMMENTS END", StringComparison.Ordinal);

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    [GeneratedRegex(@"^--- PAYPLANNER-COMMENTS-V1 START hash=(?<hash>[A-Fa-f0-9]{64}) ---$")]
    private static partial Regex HeaderHashRegex();

    [GeneratedRegex(@"^\[Комментарий #(?<id>\d+); (?<at>[^;]+); (?<author>.*)\]$")]
    private static partial Regex CommentHeaderRegex();
}

public sealed record ExternalCalendarCommentBlockResult(
    string Description,
    string SemanticHash,
    int ExportedCount,
    bool Truncated);

public sealed record ExternalCalendarParsedComment(
    int Id,
    string AuthorName,
    string Text,
    DateTime CreatedAt);

public sealed record ExternalCalendarParsedCommentBlock(
    IReadOnlyList<ExternalCalendarParsedComment> Comments,
    string SemanticHash,
    bool HashIsValid,
    bool Truncated);

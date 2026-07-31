using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using System.Diagnostics;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ExternalCalendarCommentBlockServiceTests
{
    [Fact]
    public void AppendToDescription_ExportsOnlyExplicitlyVisibleComments()
    {
        var comments = new[]
        {
            Comment(1, "Внутренний", visible: false),
            Comment(2, "Разрешённый", visible: true),
            Comment(3, "Системный", visible: true, kind: "system"),
        };

        var result = ExternalCalendarCommentBlockService.AppendToDescription("Описание задачи", comments);

        Assert.Contains("Описание задачи", result.Description);
        Assert.Contains("Разрешённый", result.Description);
        Assert.DoesNotContain("Внутренний", result.Description);
        Assert.DoesNotContain("Системный", result.Description);
        Assert.Equal(1, result.ExportedCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void AppendToDescription_ReplacesOwnBlockAndPreservesUserText()
    {
        var first = ExternalCalendarCommentBlockService.AppendToDescription(
            "Текст пользователя",
            [Comment(1, "Первый", visible: true)]);

        var second = ExternalCalendarCommentBlockService.AppendToDescription(
            first.Description,
            [Comment(2, "Второй", visible: true)]);

        Assert.StartsWith("Текст пользователя", second.Description);
        Assert.DoesNotContain("Первый", second.Description);
        Assert.Contains("Второй", second.Description);
        Assert.Equal(1, Count(second.Description, ExternalCalendarCommentBlockService.StartMarker));
    }

    [Fact]
    public void TryParse_ValidatesSemanticHash()
    {
        var result = ExternalCalendarCommentBlockService.AppendToDescription(
            "Описание",
            [Comment(7, "Публичный текст", visible: true)]);

        var parsed = ExternalCalendarCommentBlockService.TryParse(result.Description);
        var tampered = ExternalCalendarCommentBlockService.TryParse(
            result.Description.Replace("Публичный текст", "Подменённый текст", StringComparison.Ordinal));

        Assert.NotNull(parsed);
        Assert.True(parsed.HashIsValid);
        Assert.Single(parsed.Comments);
        Assert.Equal(7, parsed.Comments[0].Id);
        Assert.False(tampered!.HashIsValid);
    }

    [Fact]
    public void AppendToDescription_TruncatesOnlyAtCommentBoundary()
    {
        var comments = Enumerable.Range(1, 20)
            .Select(id => Comment(id, new string((char)('а' + id % 20), 300), visible: true))
            .ToArray();

        var result = ExternalCalendarCommentBlockService.AppendToDescription("Описание", comments, 1200);
        var parsed = ExternalCalendarCommentBlockService.TryParse(result.Description);

        Assert.True(result.Truncated);
        Assert.InRange(result.Description.Length, 1, 1200);
        Assert.NotNull(parsed);
        Assert.True(parsed.Truncated);
        Assert.True(parsed.HashIsValid);
        Assert.Equal(result.ExportedCount, parsed.Comments.Count);
    }

    [Fact]
    public void AppendToDescription_RemovesDeletedExternalComment()
    {
        var deleted = Comment(1, "Удалённый", visible: true);
        deleted.ExternalDeletedAt = DateTime.UtcNow;

        var result = ExternalCalendarCommentBlockService.AppendToDescription(
            "Описание",
            [deleted, Comment(2, "Активный", visible: true)]);

        Assert.DoesNotContain("Удалённый", result.Description);
        Assert.Contains("Активный", result.Description);
    }

    [Fact]
    public void AppendToDescription_LongFeedBenchmark_RemainsBounded()
    {
        var comments = Enumerable.Range(1, 10_000)
            .Select(id => Comment(id, $"Длинный комментарий {id}: {new string('я', 500)}", visible: true))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        var result = ExternalCalendarCommentBlockService.AppendToDescription("Описание задачи", comments);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Формирование заняло {stopwatch.Elapsed}.");
        Assert.True(result.Truncated);
        Assert.InRange(result.Description.Length, 1, ExternalCalendarCommentBlockService.DefaultMaximumDescriptionLength);
        Assert.True(result.ExportedCount < comments.Length);
        Assert.True(ExternalCalendarCommentBlockService.TryParse(result.Description)?.HashIsValid);
    }

    private static TaskJournalComment Comment(int id, string text, bool visible, string kind = "comment")
        => new()
        {
            Id = id,
            TaskJournalItemId = 10,
            AuthorName = "Проверенный пользователь",
            Kind = kind,
            Text = text,
            IsExternalCalendarVisible = visible,
            CreatedAt = new DateTime(2026, 7, 31, 9, id % 60, 0, DateTimeKind.Utc),
        };

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}

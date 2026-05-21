using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class AppReleaseSeedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> AllowedItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "new",
        "improved",
        "fixed",
        "important",
    };

    public static async Task SyncAsync(PaymentContext db, string contentRootPath, CancellationToken ct = default)
    {
        var path = Path.Combine(contentRootPath, "AppReleases", "releases.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var releases = JsonSerializer.Deserialize<List<AppReleaseSeedDto>>(json, JsonOptions) ?? [];
        var normalized = releases
            .Where(release => !string.IsNullOrWhiteSpace(release.ReleaseId))
            .GroupBy(release => release.ReleaseId.Trim(), StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();

        foreach (var seed in normalized)
        {
            var releaseId = seed.ReleaseId.Trim();
            var existing = await db.AppReleases
                .Include(release => release.Items)
                .FirstOrDefaultAsync(release => release.ReleaseId == releaseId, ct);

            if (existing?.Source == "manual")
            {
                continue;
            }

            var now = DateTime.UtcNow;
            var target = existing ?? new AppRelease
            {
                ReleaseId = releaseId,
                CreatedAt = now,
                Source = "agent",
            };

            target.Version = seed.Version?.Trim() ?? string.Empty;
            target.ReleasedAt = seed.ReleasedAt;
            target.Title = string.IsNullOrWhiteSpace(seed.Title) ? "Обновление приложения" : seed.Title.Trim();
            target.Summary = seed.Summary?.Trim() ?? string.Empty;
            target.IsActive = seed.IsActive;
            target.UpdatedAt = now;
            target.Source = string.IsNullOrWhiteSpace(seed.Source) ? "agent" : seed.Source.Trim();

            if (existing is not null)
            {
                db.AppReleaseItems.RemoveRange(existing.Items);
            }

            target.Items = seed.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select((item, index) => new AppReleaseItem
                {
                    Type = NormalizeType(item.Type),
                    Text = item.Text.Trim(),
                    SortOrder = index,
                })
                .ToList();

            if (existing is null)
            {
                db.AppReleases.Add(target);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeType(string? type)
        => !string.IsNullOrWhiteSpace(type) && AllowedItemTypes.Contains(type)
            ? type.Trim().ToLowerInvariant()
            : "improved";

    private sealed class AppReleaseSeedDto
    {
        public string ReleaseId { get; set; } = string.Empty;
        public string? Version { get; set; }
        public DateTime ReleasedAt { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Source { get; set; }
        public List<AppReleaseSeedItemDto> Items { get; set; } = [];
    }

    private sealed class AppReleaseSeedItemDto
    {
        public string? Type { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}

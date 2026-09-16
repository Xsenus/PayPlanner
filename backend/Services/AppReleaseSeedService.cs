using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class AppReleaseSeedService
{
    private const int MaxReleaseItemTextLength = 1000;

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
        ValidateSeedReleases(releases);

        var normalized = releases
            .Select(release =>
            {
                release.ReleaseId = release.ReleaseId.Trim();
                release.Version = release.Version?.Trim();
                return release;
            })
            .ToArray();
        var seedReleaseIds = normalized
            .Select(release => release.ReleaseId.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var existingReleases = await db.AppReleases
            .Include(release => release.Items)
            .Where(release => release.Source == "agent" || seedReleaseIds.Contains(release.ReleaseId))
            .AsSingleQuery()
            .ToListAsync(ct);
        var existingReleasesById = existingReleases
            .ToDictionary(release => release.ReleaseId, StringComparer.Ordinal);

        foreach (var seed in normalized)
        {
            var releaseId = seed.ReleaseId.Trim();
            existingReleasesById.TryGetValue(releaseId, out var existing);

            if (existing?.Source == "manual")
            {
                continue;
            }

            var version = seed.Version?.Trim() ?? string.Empty;
            var title = string.IsNullOrWhiteSpace(seed.Title) ? "Обновление приложения" : seed.Title.Trim();
            var summary = seed.Summary?.Trim() ?? string.Empty;
            var source = string.IsNullOrWhiteSpace(seed.Source) ? "agent" : seed.Source.Trim();
            var items = seed.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select((item, index) => new AppReleaseItem
                {
                    Type = NormalizeType(item.Type),
                    Text = item.Text.Trim(),
                    SortOrder = index,
                })
                .ToList();
            if (existing is not null
                && MatchesSeed(existing, seed, version, title, summary, source, items))
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

            target.Version = version;
            target.ReleasedAt = seed.ReleasedAt;
            target.Title = title;
            target.Summary = summary;
            target.IsActive = seed.IsActive;
            target.UpdatedAt = now;
            target.Source = source;

            if (existing is not null)
            {
                db.AppReleaseItems.RemoveRange(existing.Items);
            }

            target.Items = items;

            if (existing is null)
            {
                db.AppReleases.Add(target);
            }
        }

        var staleAgentReleases = existingReleases
            .Where(release => release.Source == "agent" && !seedReleaseIds.Contains(release.ReleaseId))
            .ToArray();
        if (staleAgentReleases.Length > 0)
        {
            db.AppReleases.RemoveRange(staleAgentReleases);
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool MatchesSeed(
        AppRelease existing,
        AppReleaseSeedDto seed,
        string version,
        string title,
        string summary,
        string source,
        IReadOnlyList<AppReleaseItem> items)
    {
        if (!string.Equals(existing.Version, version, StringComparison.Ordinal)
            || existing.ReleasedAt != seed.ReleasedAt
            || !string.Equals(existing.Title, title, StringComparison.Ordinal)
            || !string.Equals(existing.Summary, summary, StringComparison.Ordinal)
            || existing.IsActive != seed.IsActive
            || !string.Equals(existing.Source, source, StringComparison.Ordinal))
        {
            return false;
        }

        var existingItems = existing.Items
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToArray();
        return existingItems.Length == items.Count
            && existingItems.Zip(items).All(pair =>
                pair.First.SortOrder == pair.Second.SortOrder
                && string.Equals(pair.First.Type, pair.Second.Type, StringComparison.Ordinal)
                && string.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal));
    }

    private static string NormalizeType(string? type)
        => !string.IsNullOrWhiteSpace(type) && AllowedItemTypes.Contains(type)
            ? type.Trim().ToLowerInvariant()
            : "improved";

    private static void ValidateSeedReleases(IReadOnlyCollection<AppReleaseSeedDto> releases)
    {
        if (releases.Any(release => string.IsNullOrWhiteSpace(release.ReleaseId)))
        {
            throw new InvalidDataException("App release seed contains release without releaseId.");
        }

        var duplicateReleaseId = releases
            .GroupBy(release => release.ReleaseId.Trim(), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateReleaseId is not null)
        {
            throw new InvalidDataException($"App release seed contains duplicate releaseId '{duplicateReleaseId}'.");
        }

        if (releases.Any(release => string.IsNullOrWhiteSpace(release.Version)))
        {
            throw new InvalidDataException("App release seed contains release without version.");
        }

        var duplicateVersion = releases
            .GroupBy(release => release.Version!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateVersion is not null)
        {
            throw new InvalidDataException($"App release seed contains duplicate version '{duplicateVersion}'.");
        }

        var corruptedRelease = releases.FirstOrDefault(ContainsCorruptedTextMarker);
        if (corruptedRelease is not null)
        {
            throw new InvalidDataException(
                $"App release seed contains suspicious replacement markers in release '{corruptedRelease.ReleaseId}'. Check release note encoding.");
        }

        var oversizedItem = releases
            .SelectMany(release => release.Items.Select((item, index) => new { release.ReleaseId, Item = item, Index = index }))
            .FirstOrDefault(entry => (entry.Item.Text?.Trim().Length ?? 0) > MaxReleaseItemTextLength);
        if (oversizedItem is not null)
        {
            throw new InvalidDataException(
                $"App release seed item {oversizedItem.Index + 1} in release '{oversizedItem.ReleaseId}' is longer than {MaxReleaseItemTextLength} characters.");
        }

        var ordered = releases
            .Select(release =>
            {
                var version = ReleaseVersion.Parse(release.Version);
                if (version is null)
                {
                    throw new InvalidDataException($"App release seed contains invalid version '{release.Version}'.");
                }

                return new SeedReleaseWithVersion(release, version.Value);
            })
            .OrderBy(item => item.Version)
            .ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (!current.Version.IsNextAfter(previous.Version))
            {
                throw new InvalidDataException(
                    $"App release versions must be continuous. Expected next version after '{previous.Release.Version}', but found '{current.Release.Version}'.");
            }
        }
    }

    private static bool ContainsCorruptedTextMarker(AppReleaseSeedDto release)
    {
        return HasSuspiciousQuestionMarkRun(release.Title)
            || HasSuspiciousQuestionMarkRun(release.Summary)
            || release.Items.Any(item => HasSuspiciousQuestionMarkRun(item.Text));
    }

    private static bool HasSuspiciousQuestionMarkRun(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var runLength = 0;
        foreach (var ch in value)
        {
            if (ch == '?')
            {
                runLength++;
                if (runLength >= 4)
                {
                    return true;
                }
            }
            else
            {
                runLength = 0;
            }
        }

        return false;
    }

    private readonly record struct SeedReleaseWithVersion(AppReleaseSeedDto Release, ReleaseVersion Version);

    private readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
    {
        public int CompareTo(ReleaseVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;

            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;

            return Patch.CompareTo(other.Patch);
        }

        public bool IsNextAfter(ReleaseVersion previous)
        {
            if (Major == previous.Major && Minor == previous.Minor)
            {
                return Patch == previous.Patch + 1;
            }

            if (Major == previous.Major && Minor == previous.Minor + 1)
            {
                return Patch == 0;
            }

            return Major == previous.Major + 1 && Minor == 0 && Patch == 0;
        }

        public static ReleaseVersion? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var parts = value.Trim().Split('.');
            if (parts.Length != 3) return null;

            return int.TryParse(parts[0], out var major)
                && int.TryParse(parts[1], out var minor)
                && int.TryParse(parts[2], out var patch)
                && major >= 0
                && minor >= 0
                && patch >= 0
                    ? new ReleaseVersion(major, minor, patch)
                    : null;
        }
    }

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

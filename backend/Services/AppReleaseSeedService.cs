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

        var staleAgentReleases = await db.AppReleases
            .Where(release => release.Source == "agent" && !seedReleaseIds.Contains(release.ReleaseId))
            .ToArrayAsync(ct);
        if (staleAgentReleases.Length > 0)
        {
            db.AppReleases.RemoveRange(staleAgentReleases);
        }

        await db.SaveChangesAsync(ct);
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

using System.Text.Json;

namespace Aep.PlatformServices.Governance;

/// <summary>
/// Persists the last successfully-loaded <see cref="ExecutiveBriefingDto"/> to
/// disk so the Command Center has something real to show on a cold start when
/// the governance API/Docker stack happens to be down - e.g. Docker Desktop
/// itself failing to start, not just the containers being stopped. Without
/// this, a cold start during an outage renders a totally blank console (see
/// the 2026-08-18 Docker Desktop outage that prompted this) even though the
/// user has a perfectly good briefing from the last time it worked.
///
/// Deliberately a dumb single-slot cache - one file, overwritten on every
/// successful load, no history. This is a "better than nothing" fallback for
/// display purposes only, never treated as authoritative (the governance API
/// remains the source of truth per ADR 0003).
/// </summary>
public static class BriefingCache
{
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AepCommandCenter",
        "last-briefing.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public sealed record CachedBriefing(ExecutiveBriefingDto Briefing, DateTimeOffset FetchedAt);

    public static async Task SaveAsync(ExecutiveBriefingDto briefing, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFilePath)!;
            Directory.CreateDirectory(directory);
            var cached = new CachedBriefing(briefing, DateTimeOffset.Now);
            var json = JsonSerializer.Serialize(cached, JsonOptions);
            await File.WriteAllTextAsync(CacheFilePath, json, cancellationToken);
        }
        catch
        {
            // Caching is a nice-to-have, not a requirement - a failure to write
            // the cache (disk full, permissions, etc.) should never surface as
            // an error to the user or block a live load that already succeeded.
        }
    }

    public static CachedBriefing? TryLoad()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(CacheFilePath);
            return JsonSerializer.Deserialize<CachedBriefing>(json, JsonOptions);
        }
        catch
        {
            // A corrupt or unreadable cache file is equivalent to no cache -
            // fall through to the normal "nothing to show yet" behavior
            // rather than crashing the app over a stale local file.
            return null;
        }
    }
}

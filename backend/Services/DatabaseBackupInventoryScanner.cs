using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

// Depth-first cursor: only active ancestors are retained, not an inventory of the whole tree.
// Each directory is enumerated locally to choose a bounded, ordinally ordered window.
public sealed class DatabaseBackupInventoryScanner(string rootDirectory, IBackupDirectoryReader reader)
{
    private const int MaxDepth = 32;
    private const int MaxSteps = 64;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".sqlite", ".backup", ".dump", ".sql" };
    private readonly string _root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);

    public DatabaseBackupDiscoveryPage Read(string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 20) throw new ArgumentException("Размер страницы поиска должен быть от 1 до 20.");
        var state = Decode(cursor);
        var items = new List<DatabaseBackupDiscoveryItem>();
        var issues = new List<DatabaseBackupDiscoveryIssue>();
        var steps = 0;
        while (state.Frames.Count > 0 && items.Count < pageSize && steps++ < MaxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = state.Frames[^1];
            var path = Resolve(frame.Directory);
            try
            {
                EnsureNoLinks(path);
                var count = frame.Directories ? 1 : pageSize - items.Count + 1;
                var entries = SelectEntries(path, frame, count, cancellationToken);
                if (frame.Directories)
                {
                    if (entries.Count == 0) { state.Frames.RemoveAt(state.Frames.Count - 1); continue; }
                    frame.After = Path.GetFileName(entries[0]);
                    var relative = Path.GetRelativePath(_root, entries[0]).Replace('\\', '/');
                    if (state.Frames.Count >= MaxDepth || relative.Length > 2048)
                        issues.Add(new(relative, "Превышена допустимая глубина поиска; каталог не просмотрен."));
                    else state.Frames.Add(new() { Directory = relative });
                }
                else
                {
                    var remaining = pageSize - items.Count;
                    foreach (var entry in entries.Take(remaining))
                    {
                        frame.After = Path.GetFileName(entry);
                        items.Add(new(Path.GetRelativePath(_root, entry).Replace('\\', '/'), File.Exists(entry + ".manifest.json")));
                    }
                    if (entries.Count <= remaining) { frame.Directories = true; frame.After = string.Empty; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(frame.Directory, "Каталог недоступен или содержит символическую ссылку; поиск здесь не выполнен."));
                state.Frames.RemoveAt(state.Frames.Count - 1);
            }
        }
        return new(items, issues, state.Frames.Count == 0 ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));
    }

    private List<string> SelectEntries(string directory, Frame frame, int limit, CancellationToken cancellationToken)
    {
        var selected = new List<string>(limit + 1);
        foreach (var path in reader.EnumerateEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (StringComparer.Ordinal.Compare(name, frame.After) <= 0) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory != frame.Directories || (!isDirectory && !Extensions.Contains(Path.GetExtension(path)))) continue;
            var index = selected.BinarySearch(path, StringComparer.Ordinal);
            selected.Insert(index < 0 ? ~index : index, path);
            if (selected.Count > limit) selected.RemoveAt(limit);
        }
        return selected;
    }

    private State Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return new() { Frames = [new()] };
        try
        {
            if (cursor.Length > 600_000) throw new ArgumentException();
            var state = JsonSerializer.Deserialize<State>(Convert.FromBase64String(cursor));
            if (state is null || state.Version != 1 || state.Frames is null || state.Frames.Count is < 1 or > MaxDepth)
                throw new ArgumentException();
            for (var index = 0; index < state.Frames.Count; index++)
            {
                var frame = state.Frames[index];
                if (frame is null || frame.Directory is null || frame.After is null || frame.Directory.Length > 2048
                    || frame.After.Length > 255 || frame.After.IndexOfAny(['/', '\\']) >= 0 || frame.After is "." or "..")
                    throw new ArgumentException();
                Resolve(frame.Directory);
                if (index == 0)
                {
                    if (frame.Directory != string.Empty) throw new ArgumentException();
                }
                else
                {
                    var separator = frame.Directory.LastIndexOf('/');
                    var parent = separator < 0 ? string.Empty : frame.Directory[..separator];
                    if (frame.Directory.Length == 0 || parent != state.Frames[index - 1].Directory
                        || !state.Frames[index - 1].Directories) throw new ArgumentException();
                }
            }
            return state;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("Курсор поиска недействителен. Начните поиск заново.", ex);
        }
    }

    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Путь поиска выходит из каталога резервных копий.");
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path != _root && !path.StartsWith(_root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Путь поиска выходит из каталога резервных копий.");
        return path;
    }

    private void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            if (string.Equals(current, _root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        }
        throw new IOException();
    }

    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<Frame> Frames { get; set; } = [];
    }
    private sealed class Frame
    {
        public string Directory { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public bool Directories { get; set; }
    }
}

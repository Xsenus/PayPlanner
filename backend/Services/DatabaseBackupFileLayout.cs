namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupFileRoot(string Name, string Path, bool Required = false);

public sealed record DatabaseBackupFileRootMapping(string Name, string ArchivePrefix);

/// <summary>Freezes the physical-to-archive layout for one backup operation.</summary>
public sealed class DatabaseBackupFileLayout
{
    private readonly string excludedDirectory;
    private readonly IReadOnlyDictionary<string, DatabaseBackupFileRoot> archiveRootsByName;
    private readonly IReadOnlyDictionary<string, string> logicalRootPaths;
    public IReadOnlyList<DatabaseBackupFileRoot> ArchiveRoots { get; }
    public IReadOnlyList<DatabaseBackupFileRootMapping> RootMappings { get; }

    private DatabaseBackupFileLayout(string excludedDirectory,
        IReadOnlyList<DatabaseBackupFileRoot> roots, IReadOnlyList<DatabaseBackupFileRootMapping> mappings,
        IReadOnlyDictionary<string, string> logicalRootPaths)
    {
        this.excludedDirectory = excludedDirectory;
        ArchiveRoots = roots;
        RootMappings = mappings;
        this.logicalRootPaths = logicalRootPaths;
        archiveRootsByName = roots.ToDictionary(root => root.Name, StringComparer.Ordinal);
    }

    public static DatabaseBackupFileLayout Create(IEnumerable<DatabaseBackupFileRoot> candidates,
        string excludedDirectory)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var excludedPath = NormalizeAbsolutePath(excludedDirectory);
        var items = candidates.Take(33).ToArray();
        if (items.Length > 32) throw new ArgumentException("Too many backup file roots.", nameof(candidates));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var present = new List<DatabaseBackupFileRoot>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrEmpty(item.Name) || item.Name.Length > 64
                || item.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
                || !names.Add(item.Name))
                throw new ArgumentException("Backup root names must be unique safe archive prefixes.", nameof(candidates));
            var path = NormalizeAbsolutePath(item.Path);
            if (IsWithin(path, excludedPath)
                || string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException($"Файловый корень резервной копии не может быть целым диском или каталогом резервных копий: {item.Name}.");
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"Путь файлов для резервного копирования не является каталогом: {item.Name}.");
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Символическая ссылка не может быть файловым корнем резервной копии: {item.Name}.");
                present.Add(item with { Path = path });
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (item.Required)
                    throw new DirectoryNotFoundException($"Каталог файлов для резервного копирования недоступен: {item.Name}.", ex);
            }
        }
        var selected = new List<DatabaseBackupFileRoot>();
        foreach (var item in present.OrderBy(item => item.Path.Length))
            if (!selected.Any(parent => IsWithin(item.Path, parent.Path))) selected.Add(item);
        var mappings = present.Select(item =>
        {
            var parent = selected.Single(root => IsWithin(item.Path, root.Path));
            var relative = ToArchiveRelativePath(Path.GetRelativePath(parent.Path, item.Path));
            return new DatabaseBackupFileRootMapping(item.Name,
                relative == "." ? parent.Name : $"{parent.Name}/{relative}");
        }).ToArray();
        ValidateRootMappings(mappings);
        return new DatabaseBackupFileLayout(excludedPath, selected.AsReadOnly(), Array.AsReadOnly(mappings),
            present.ToDictionary(item => item.Name, item => item.Path, StringComparer.Ordinal));
    }

    public string GetPhysicalFilePath(string logicalRootName, string relativePath)
    {
        if (!logicalRootPaths.TryGetValue(logicalRootName, out var root))
            throw new InvalidDataException("Файловый корень ссылки не включён в снимок.");
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 4096)
            throw new InvalidDataException("Некорректный относительный путь файловой ссылки.");
        // Stored relative paths can originate from either OS; normalize before the containment check.
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
            throw new InvalidDataException("Файловая ссылка содержит небезопасные сегменты пути.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        _ = GetArchiveEntryPath(path); // Also rejects references into the excluded backup directory.
        return path;
    }

    public string GetArchiveEntryPath(string physicalFilePath, string? archiveRootName = null)
    {
        var path = NormalizeAbsolutePath(physicalFilePath);
        if (IsWithin(path, excludedDirectory))
            throw new InvalidDataException("Обязательный файл находится в исключённом каталоге резервных копий.");
        var root = archiveRootName is null
            ? ArchiveRoots.SingleOrDefault(root => IsWithin(path, root.Path))
            : archiveRootsByName.GetValueOrDefault(archiveRootName);
        if (root is null || !IsWithin(path, root.Path))
            throw new InvalidDataException("Файл не принадлежит указанному файловому корню.");
        var relative = ToArchiveRelativePath(Path.GetRelativePath(root.Path, path));
        if (relative == ".") throw new InvalidDataException("Вместо файла указан файловый корень.");
        return $"{root.Name}/{relative}";
    }

    public static void ValidateRootMappings(IReadOnlyList<DatabaseBackupFileRootMapping> mappings)
    {
        if (mappings.Count > 32) throw new InvalidDataException("Слишком много файловых корней в manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is null || string.IsNullOrEmpty(mapping.Name) || mapping.Name.Length > 64
                || mapping.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
                || !names.Add(mapping.Name) || string.IsNullOrEmpty(mapping.ArchivePrefix)
                || mapping.ArchivePrefix.Length > 4096 || mapping.ArchivePrefix.Contains('\\'))
                throw new InvalidDataException("Некорректное сопоставление файлового корня в manifest.");
            var segments = mapping.ArchivePrefix.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")
                || segments[0].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new InvalidDataException("Небезопасный архивный префикс в manifest.");
            if (mapping.ArchivePrefix == mapping.Name) archiveRoots.Add(mapping.Name);
        }
        if (mappings.Any(mapping => !archiveRoots.Contains(mapping.ArchivePrefix.Split('/')[0])))
            throw new InvalidDataException("Сопоставление ссылается на неописанный архивный корень.");
    }

    private static string ToArchiveRelativePath(string relative)
    {
        if (!OperatingSystem.IsWindows() && relative.Contains('\\'))
            throw new InvalidDataException("Имя файла содержит неоднозначный разделитель архивного пути.");
        return relative.Replace('\\', '/');
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified file path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, comparison) || path.StartsWith(prefix, comparison);
    }
}

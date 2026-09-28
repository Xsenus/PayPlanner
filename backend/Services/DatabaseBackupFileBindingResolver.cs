using System.Globalization;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupResolvedFile(string? PhysicalPath, string? ArchiveEntryPath, bool ExternalReference);

/// <summary>
/// Resolves snapshot references against the pinned source layout, never the restore server's paths.
/// It does not prove that resolved bytes exist in the archive, and never fetches external URLs.
/// </summary>
public sealed class DatabaseBackupFileBindingResolver(DatabaseBackupFileLayout layout)
{
    public DatabaseBackupResolvedFile Resolve(DatabaseBackupStoredFileReference reference)
        => reference.SourceKind switch
        {
            "task-file" => Local("file-storage", "task-files/" + reference.StoredPath),
            "case-document" => Local("file-storage", "case-documents/" + reference.StoredPath),
            "chat-attachment" => reference.ParentId is > 0 && !reference.StoredPath.Contains('/') && !reference.StoredPath.Contains('\\')
                ? Local("file-storage", $"internal-chat-files/{reference.ParentId.Value.ToString(CultureInfo.InvariantCulture)}/{reference.StoredPath}")
                : throw new InvalidDataException("Вложение чата не связано с перепиской."),
            "messenger-delivery" => Local("file-storage", "messenger-document-deliveries/" + reference.StoredPath),
            "esign-original" or "esign-artifact" => ElectronicSignature(reference.StoredPath),
            "profile-photo" => Photo(reference.StoredPath),
            _ => throw new InvalidDataException("Неизвестный класс файловой ссылки."),
        };

    public DatabaseBackupResolvedFile ResolveLocalReplica(DatabaseBackupStorageReplicaReference reference)
    {
        if (reference.ReplicaId is null || string.IsNullOrWhiteSpace(reference.Locator))
            throw new InvalidDataException("Поколение не имеет адреса локальной реплики.");
        if (reference.ProviderKey == "local") return Local("local-objects", reference.Locator);
        if (reference.ProviderKey != "legacy-local")
            throw new InvalidDataException("Внешнюю реплику необходимо читать через её файловый провайдер.");
        var locator = reference.Locator.Replace('\\', '/');
        var separator = locator.IndexOf('/');
        if (separator <= 0) throw new InvalidDataException("Некорректный адрес прежней локальной реплики.");
        var root = locator[..separator] switch
        {
            "files" => "file-storage",
            "profile-current" => "profile-photos",
            "profile-legacy" => "profile-photos-legacy",
            "esign-current" => "electronic-signatures",
            "esign-legacy" => "electronic-signatures-legacy",
            _ => throw new InvalidDataException("Неизвестный корень прежней локальной реплики."),
        };
        return Local(root, locator[(separator + 1)..]);
    }

    private DatabaseBackupResolvedFile ElectronicSignature(string path)
    {
        if (!Path.IsPathRooted(path)) return Local("electronic-signatures", path);
        if (layout.TryGetRelativeFilePath("electronic-signatures", path, out var current))
            return Local("electronic-signatures", current);
        if (!layout.TryGetRelativeFilePath("electronic-signatures-legacy", path, out var legacy))
            throw new InvalidDataException("Документ подписи находится вне разрешённых папок подписей.");
        if (layout.ContainsLogicalRoot("electronic-signatures"))
        {
            var migrated = Local("electronic-signatures", legacy);
            if (File.Exists(migrated.PhysicalPath)) return migrated;
        }
        return Local("electronic-signatures-legacy", legacy);
    }

    private DatabaseBackupResolvedFile Photo(string value)
    {
        var path = value;
        Uri? uri = null;
        var absolute = !value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out uri);
        if (absolute)
        {
            if (uri!.Scheme is not ("http" or "https"))
                throw new InvalidDataException("Неподдерживаемый адрес фотографии.");
            path = uri.AbsolutePath;
        }
        if (!path.StartsWith(UserPhotoStoragePaths.RequestPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            if (absolute) return new(null, null, true);
            throw new InvalidDataException("Фотография не имеет локального или внешнего адреса.");
        }
        var relative = path[(UserPhotoStoragePaths.RequestPath.Length + 1)..];
        DatabaseBackupResolvedFile? current = null;
        if (layout.ContainsLogicalRoot("profile-photos"))
        {
            current = Local("profile-photos", relative);
            if (File.Exists(current.PhysicalPath)) return current;
        }
        return layout.ContainsLogicalRoot("profile-photos-legacy")
            ? Local("profile-photos-legacy", relative)
            : current ?? throw new InvalidDataException("Папки локальной фотографии не включены в снимок.");
    }

    private DatabaseBackupResolvedFile Local(string root, string relative)
    {
        var physical = layout.GetPhysicalFilePath(root, relative);
        return new(physical, layout.GetArchiveEntryPath(physical), false);
    }
}

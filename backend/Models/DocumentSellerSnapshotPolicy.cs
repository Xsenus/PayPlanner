namespace PayPlanner.Api.Models;

/// <summary>Правило выбирается сервером при создании документа; null у старой записи означает FirstFile.</summary>
public static class DocumentSellerSnapshotPolicy
{
    public const string FirstFile = "first-file";
    public const string OnCreate = "on-create";
    public const string SettingKey = "documents.sellerSnapshotTiming";

    public static bool IsValid(string? value) => value is FirstFile or OnCreate;
    public static string Effective(string? value) => value is null ? FirstFile
        : IsValid(value) ? value : throw new InvalidOperationException("Неизвестное правило фиксации реквизитов документа.");
}

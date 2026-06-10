using Xunit;

namespace PayPlanner.Api.Tests;

public class FileUploadLimitsDocumentationTests
{
    private static readonly string[] RequiredUploadTypes =
    [
        "profile photo",
        "task attachment",
        "case document",
        "database import",
        "spreadsheet import",
        "S3 upload",
        "Yandex Disk upload",
    ];

    [Fact]
    public void FileUploadLimitsDocumentation_CoversRequiredUploadTypesAndFields()
    {
        var root = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(root, "docs", "file-upload-limits-2026-06-10.md"));

        Assert.Contains("Max size", document, StringComparison.Ordinal);
        Assert.Contains("Allowed extensions", document, StringComparison.Ordinal);
        Assert.Contains("Allowed content types", document, StringComparison.Ordinal);
        Assert.Contains("Error message", document, StringComparison.Ordinal);

        foreach (var uploadType in RequiredUploadTypes)
        {
            var row = document
                .Split('\n', StringSplitOptions.TrimEntries)
                .SingleOrDefault(line => line.StartsWith($"| {uploadType} |", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(row), $"Missing row for {uploadType}.");
            Assert.DoesNotContain("TODO", row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TBD", row, StringComparison.OrdinalIgnoreCase);

            var columns = row.Split('|', StringSplitOptions.None);
            Assert.True(columns.Length >= 8, $"Row for {uploadType} must document every required field.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

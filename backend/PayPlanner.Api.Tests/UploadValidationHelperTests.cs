using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UploadValidationHelperTests
{
    [Fact]
    public void ValidateRequiredFile_ReturnsEmptyMessageForMissingFile()
    {
        var error = UploadValidationHelper.ValidateRequiredFile(
            file: null,
            emptyMessage: "Выберите файл для загрузки.");

        Assert.NotNull(error);
        Assert.Equal("Выберите файл для загрузки.", error.Message);
    }

    [Fact]
    public void ValidateRequiredFile_ReturnsEmptyMessageForZeroLengthFile()
    {
        using var stream = new MemoryStream();
        var file = new FormFile(stream, 0, 0, "file", "empty.txt");

        var error = UploadValidationHelper.ValidateRequiredFile(
            file,
            "Выберите файл для загрузки.");

        Assert.NotNull(error);
        Assert.Equal("Выберите файл для загрузки.", error.Message);
    }

    [Fact]
    public void ValidateRequiredFile_ReturnsTooLargeMessageWhenDeclaredLengthExceedsLimit()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var file = new FormFile(stream, 0, 26L * 1024L * 1024L, "file", "large.txt");

        var error = UploadValidationHelper.ValidateRequiredFile(
            file,
            "Выберите файл для загрузки.",
            maxSizeBytes: 25L * 1024L * 1024L,
            tooLargeMessage: "Файл не должен превышать 25 МБ.");

        Assert.NotNull(error);
        Assert.Equal("Файл не должен превышать 25 МБ.", error.Message);
    }

    [Fact]
    public void ValidateRequiredFile_ReturnsNullForValidFile()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var file = new FormFile(stream, 0, stream.Length, "file", "valid.txt");

        var error = UploadValidationHelper.ValidateRequiredFile(
            file,
            "Выберите файл для загрузки.",
            maxSizeBytes: 25L * 1024L * 1024L,
            tooLargeMessage: "Файл не должен превышать 25 МБ.");

        Assert.Null(error);
    }
}

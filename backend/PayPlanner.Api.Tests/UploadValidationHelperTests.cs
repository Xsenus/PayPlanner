using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UploadValidationHelperTests
{
    [Theory]
    [InlineData(4, 4, 4, true)]
    [InlineData(5, 1, 4, false)]
    [InlineData(3, 4, 4, false)]
    [InlineData(4, 3, 4, false)]
    [InlineData(4, 1, 4, false)]
    public async Task CopyValidatedAsync_EnforcesActualSizeAndNeverExceedsLimit(int actual, int declared, int max, bool valid)
    {
        var file = new UntrustedFile(new byte[actual], declared);
        using var output = new MemoryStream();
        if (valid)
        {
            await UploadValidationHelper.CopyValidatedAsync(file, output, max, CancellationToken.None);
            Assert.Equal(actual, output.Length);
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => UploadValidationHelper.CopyValidatedAsync(file, output, max, CancellationToken.None));
        Assert.InRange(output.Length, 0, Math.Min(max, declared));
        Assert.True(output.CanWrite);
    }

    private sealed class UntrustedFile(byte[] bytes, long declared) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => declared;
        public string Name => "file";
        public string FileName => "file.bin";
        public Stream OpenReadStream() => new MemoryStream(bytes);
        public void CopyTo(Stream target) => throw new InvalidOperationException("Unsafe copy must not be called.");
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => throw new InvalidOperationException("Unsafe copy must not be called.");
    }

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

using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ProfileControllerTests
{
    [Theory]
    [InlineData("uploaded", 200)]
    [InlineData("pending", 503)]
    [InlineData("conflict", 409)]
    [InlineData("invalid", 400)]
    [InlineData("notFound", 404)]
    public async Task PhotoUpload_UsesCurrentUserAndStableKey(string status, int expected)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "photo@example.test", "Фото");
        var uploader = new PhotoUploader(status);
        var controller = CreateController(db, user, uploader);
        var key = Guid.NewGuid();
        controller.Request.Headers["X-Upload-Key"] = key.ToString();
        var result = await controller.UploadProfilePhoto(PhotoFile(), CancellationToken.None);
        Assert.Equal(expected, result is ObjectResult body ? body.StatusCode : Assert.IsType<NotFoundResult>(result).StatusCode);
        Assert.Equal(user.Id, uploader.UserId);
        Assert.Equal(key, uploader.Key);
    }

    [Theory]
    [InlineData("bad-key")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task PhotoUpload_InvalidKeyNeverCallsStorage(string key)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "photo@example.test", "Фото");
        var uploader = new PhotoUploader("uploaded");
        var controller = CreateController(db, user, uploader);
        controller.Request.Headers["X-Upload-Key"] = key;
        Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.UploadProfilePhoto(PhotoFile(), CancellationToken.None)).StatusCode);
        Assert.Equal(0, uploader.UserId);
    }

    [Fact]
    public async Task PhotoUpload_AnonymousNeverCallsStorage()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "photo@example.test", "Фото");
        var uploader = new PhotoUploader("uploaded");
        var controller = CreateController(db, user, uploader);
        controller.HttpContext.User = new ClaimsPrincipal();
        Assert.IsType<UnauthorizedResult>(await controller.UploadProfilePhoto(PhotoFile(), CancellationToken.None));
        Assert.Equal(0, uploader.UserId);
    }

    [Fact]
    public async Task PhotoUpload_DisabledWriterDoesNotReplaceManagedBinding()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "photo@example.test", "Фото");
        user.PhotoUrl = "https://example.test/user-photos/old.png";
        var storageObject = new StorageObject();
        db.StorageObjects.Add(storageObject);
        db.StorageProductBindings.Add(new StorageProductBinding
        {
            Source = LegacyStorageSources.ProfilePhotos,
            SourceId = user.Id,
            StorageObjectId = storageObject.Id,
            Version = 1,
        });
        await db.SaveChangesAsync();
        var result = await CreateController(db, user).UploadProfilePhoto(PhotoFile(), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("https://example.test/user-photos/old.png", user.PhotoUrl);
        Assert.Equal(storageObject.Id, (await db.StorageProductBindings.SingleAsync()).StorageObjectId);
    }

    private static IFormFile PhotoFile() => new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };

    [Fact]
    public async Task PhotoUpload_DefaultModeStillWritesLegacyPhoto()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-profile-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = await CreateContextAsync();
            var user = await CreateUserAsync(db, "legacy@example.test", "Фото");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ProfilePhotos:StorageRoot"] = root }).Build();
            var controller = CreateController(db, user, configurationOverride: config);
            controller.Request.Scheme = "https";
            controller.Request.Host = new HostString("crm.example.test");
            Assert.IsType<OkObjectResult>(await controller.UploadProfilePhoto(PhotoFile(), CancellationToken.None));
            var photo = (await db.Users.AsNoTracking().SingleAsync()).PhotoUrl;
            Assert.StartsWith("https://crm.example.test/user-photos/", photo);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(UserPhotoStoragePaths.ResolvePhotoPath(root, photo)!));
            Assert.Empty(await db.StorageProductBindings.ToListAsync());
        }
        finally
        {
            Assert.StartsWith("pp-profile-legacy-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PhotoUpload_LostDatabaseAcknowledgementKeepsReferencedPhotoBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-profile-ack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var interceptor = new RejectPhotoAcknowledgement();
            await using var db = await CreateContextAsync(interceptor);
            var user = await CreateUserAsync(db, "photo-ack@example.test", "Фото");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ProfilePhotos:StorageRoot"] = root }).Build();
            var controller = CreateController(db, user, configurationOverride: config);
            controller.Request.Scheme = "https";
            controller.Request.Host = new HostString("crm.example.test");
            interceptor.Enabled = true;

            await Assert.ThrowsAsync<IOException>(() => controller.UploadProfilePhoto(PhotoFile(), CancellationToken.None));

            var photo = (await db.Users.AsNoTracking().SingleAsync()).PhotoUrl;
            Assert.NotNull(photo);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(UserPhotoStoragePaths.ResolvePhotoPath(root, photo)!));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            Assert.StartsWith("pp-profile-ack-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RejectPhotoAcknowledgement : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<User>()
                .Any(entry => entry.Entity.PhotoUrl?.Contains("/user-photos/", StringComparison.Ordinal) == true) == true)
                throw new IOException("Owned synthetic acknowledgement failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PhotoUploader(string status) : IManagedProfilePhotoUploadService
    {
        public bool Enabled => true;
        public int UserId { get; private set; }
        public Guid Key { get; private set; }
        public Task<ManagedPhotoUploadResult> UploadAsync(int userId, Guid uploadId, IFormFile file, string baseUrl, CancellationToken ct)
        {
            UserId = userId;
            Key = uploadId;
            return Task.FromResult(new ManagedPhotoUploadResult(status));
        }
    }
    [Fact]
    public async Task GetContractorBalance_ShowsOnlyCurrentUserExecutorPayments()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author@example.com", "Постановщик");
        var contractor = await CreateUserAsync(db, "contractor@example.com", "Исполнитель", isExternalContractor: true);
        var task = new TaskJournalItem
        {
            Title = "Подготовить документы",
            Status = "done",
            AssigneeUserId = contractor.Id,
            AssigneeName = contractor.FullName,
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 3_500m,
            ContractorPaymentDueDate = new DateTime(2026, 6, 10),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var authorResult = await CreateController(db, author).GetContractorBalance(CancellationToken.None);
        var contractorResult = await CreateController(db, contractor).GetContractorBalance(CancellationToken.None);

        var authorBalance = AssertBalance(authorResult);
        Assert.Equal(0m, authorBalance.OwedToMe);
        Assert.Equal(0m, authorBalance.OwedByMe);
        Assert.Empty(authorBalance.Queue);

        var contractorBalance = AssertBalance(contractorResult);
        Assert.Equal(3_500m, contractorBalance.OwedToMe);
        Assert.Equal(0m, contractorBalance.OwedByMe);
        var queued = Assert.Single(contractorBalance.Queue);
        Assert.Equal(task.Id, queued.TaskId);
        Assert.Equal("toMe", queued.Direction);
        Assert.Equal("Постановщик", queued.AuthorName);
    }

    private static ContractorBalanceDto AssertBalance(ActionResult<ContractorBalanceDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<ContractorBalanceDto>(ok.Value);
    }

    private static ProfileController CreateController(PaymentContext db, User user, IManagedProfilePhotoUploadService? uploader = null, IConfiguration? configurationOverride = null)
    {
        var configuration = configurationOverride ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var passwordHasher = new PasswordHasher<User>();
        var authService = new AuthService(
            db,
            configuration,
            new NoopEmailService(),
            new FakeWebHostEnvironment(),
            passwordHasher);
        var externalAuthService = new ExternalAuthService(
            db,
            configuration,
            new FakeHttpClientFactory(),
            passwordHasher,
            authService,
            NullLogger<ExternalAuthService>.Instance);

        return new ProfileController(authService, externalAuthService, db, new FakeWebHostEnvironment(), configuration, uploader)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.FullName),
                        new Claim(ClaimTypes.Email, user.Email),
                    ], "TestAuth")),
                },
            },
        };
    }

    private static async Task<PaymentContext> CreateContextAsync(SaveChangesInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        var options = builder.Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string fullName, bool isExternalContractor = false)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "profile-test")
            ?? new Role { Name = "profile-test", Description = "Profile test" };
        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            IsActive = true,
            IsApproved = true,
            IsExternalContractor = isExternalContractor,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

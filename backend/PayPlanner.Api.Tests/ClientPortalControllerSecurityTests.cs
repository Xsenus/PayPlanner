using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPortalControllerSecurityTests
{
    [Fact]
    public async Task PreviewAppliesPrivateResponseAndBrowserIsolationHeaders()
    {
        var controller = CreateController(out _);

        var result = await controller.Preview(
            Guid.NewGuid(),
            new ClientPortalTokenRequest { Token = "opaque-token" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("no-store, max-age=0", controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
        Assert.Equal("noindex, nofollow, noarchive", controller.Response.Headers["X-Robots-Tag"]);
        Assert.Contains("frame-ancestors 'none'", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains("form-action 'self'", controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1", "cross-site")]
    public async Task StateChangingRequestRejectsMissingOrCrossSiteBrowserProof(string? portalHeader, string? fetchSite)
    {
        var controller = CreateController(out var service);
        if (portalHeader is not null) controller.Request.Headers["X-Portal-Request"] = portalHeader;
        if (fetchSite is not null) controller.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        var result = await controller.CreateDocumentGrant(Guid.NewGuid(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("csrf_rejected", badRequest.Value!.ToString(), StringComparison.Ordinal);
        Assert.False(service.DocumentGrantRequested);
    }

    [Fact]
    public async Task VerifyCreatesHttpOnlySecureStrictCookieInProduction()
    {
        var controller = CreateController(out _);

        var result = await controller.Verify(
            Guid.NewGuid(),
            new ClientPortalVerifyRequest { Token = "opaque-token", Otp = "123456" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var cookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("pp_client_portal=opaque-session", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static ClientPortalController CreateController(out PortalServiceStub service)
    {
        service = new PortalServiceStub();
        return new ClientPortalController(service, new MaxWebhookServiceStub(), new ProductionEnvironment())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class PortalServiceStub : IClientPortalService
    {
        public bool DocumentGrantRequested { get; private set; }

        public Task<ClientPortalLoginPreviewDto> PreviewLoginAsync(Guid challengeId, string token, CancellationToken ct)
            => Task.FromResult(new ClientPortalLoginPreviewDto
            {
                DisplayName = "Клиент",
                EmailHint = "k***t@example.test",
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });

        public Task<ClientPortalSessionCreatedDto> VerifyLoginAsync(Guid challengeId, string token, string otp, string? ip, string? userAgent, CancellationToken ct)
            => Task.FromResult(new ClientPortalSessionCreatedDto
            {
                SessionToken = "opaque-session",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });

        public Task<ClientPortalDocumentGrantDto?> CreateDocumentGrantAsync(ClientPortalSessionContext session, Guid documentPublicId, CancellationToken ct)
        {
            DocumentGrantRequested = true;
            return Task.FromResult<ClientPortalDocumentGrantDto?>(new());
        }

        public Task<ClientPortalSessionContext?> ResolveSessionAsync(string? token, string? ip, string? userAgent, CancellationToken ct)
            => Task.FromResult<ClientPortalSessionContext?>(new(1, Guid.NewGuid(), "Клиент", "client@example.test"));

        public Task<ClientPortalAdminAccountDto> CreateOrUpdateAccessAsync(ClientPortalAccessRequest request, int actorId, string actorName, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPortalAdminAccountDto?> GetAccountAsync(int accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<ClientPortalCaseAccessDto>> GetCaseAccountsAsync(int caseId, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<ClientPortalAuditDto>> GetCaseAuditAsync(int caseId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPortalChallengeCreatedDto> SendLoginAsync(int accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAccountAsync(int accountId, int actorId, string actorName, string? reason, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeCaseAccessAsync(int accountId, int caseId, int actorId, string? reason, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeSessionAsync(string? token, bool all, CancellationToken ct) => Task.CompletedTask;
        public Task<ClientPortalHomeDto> GetHomeAsync(ClientPortalSessionContext session, int page, int pageSize, CancellationToken ct, string? search = null, string? status = null) => throw new NotSupportedException();
        public Task<ClientPortalProfileDto> GetProfileAsync(ClientPortalSessionContext session, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPortalCaseDto?> GetCaseAsync(ClientPortalSessionContext session, Guid casePublicId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPortalDocumentDownloadDto?> GetDocumentAsync(ClientPortalSessionContext session, Guid documentPublicId, string grantToken, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MaxWebhookServiceStub : IMaxWebhookService
    {
        public Task<MaxEmployeeLinkStatus> GetEmployeeLinkStatusAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MaxLinkTokenResult> CreateEmployeeLinkAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MaxLinkTokenResult> CreatePortalLinkAsync(int portalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OptOutEmployeeAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OptOutPortalAsync(int portalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MaxWebhookResult> ProcessAsync(string rawJson, string? suppliedSecret, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ProductionEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

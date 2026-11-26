using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public interface IClientPortalService
{
    Task<ClientPortalAdminAccountDto> CreateOrUpdateAccessAsync(ClientPortalAccessRequest request, int actorId, string actorName, CancellationToken ct);
    Task<ClientPortalAdminAccountDto?> GetAccountAsync(int accountId, CancellationToken ct);
    Task<List<ClientPortalCaseAccessDto>> GetCaseAccountsAsync(int caseId, CancellationToken ct);
    Task<List<ClientPortalAuditDto>> GetCaseAuditAsync(int caseId, CancellationToken ct);
    Task<ClientPortalCaseDto?> PreviewCaseAsync(int caseId, ClientPortalCasePreviewRequest request, int actorId, string actorName, CancellationToken ct);
    Task<ClientPortalChallengeCreatedDto> SendLoginAsync(int accountId, CancellationToken ct, int? caseId = null);
    Task RevokeAccountAsync(int accountId, int actorId, string actorName, string? reason, CancellationToken ct);
    Task RevokeCaseAccessAsync(int accountId, int caseId, int actorId, string? reason, CancellationToken ct);
    Task<ClientPortalLoginPreviewDto> PreviewLoginAsync(Guid challengeId, string token, CancellationToken ct);
    Task<ClientPortalSessionCreatedDto> VerifyLoginAsync(Guid challengeId, string token, string otp, string? ip, string? userAgent, CancellationToken ct);
    Task<ClientPortalSessionContext?> ResolveSessionAsync(string? token, string? ip, string? userAgent, CancellationToken ct);
    Task RevokeSessionAsync(string? token, bool all, CancellationToken ct);
    Task<ClientPortalHomeDto> GetHomeAsync(ClientPortalSessionContext session, int page, int pageSize, CancellationToken ct, string? search = null, string? status = null);
    Task<ClientPortalProfileDto> GetProfileAsync(ClientPortalSessionContext session, CancellationToken ct);
    Task<ClientPortalCaseDto?> GetCaseAsync(ClientPortalSessionContext session, Guid casePublicId, CancellationToken ct);
    Task<ClientPortalDocumentGrantDto?> CreateDocumentGrantAsync(ClientPortalSessionContext session, Guid documentPublicId, CancellationToken ct);
    Task<ClientPortalDocumentDownloadDto?> GetDocumentAsync(ClientPortalSessionContext session, Guid documentPublicId, string grantToken, CancellationToken ct);
}

public sealed class ClientPortalService : IClientPortalService
{
    private const int ChallengeHours = 2;
    private const int SessionHours = 12;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan DocumentGrantLifetime = TimeSpan.FromMinutes(5);
    private readonly PaymentContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _flags;
    private readonly ICaseDocumentStorage _documentStorage;
    private readonly ITimeLimitedDataProtector _documentGrantProtector;

    public ClientPortalService(
        PaymentContext db,
        IEmailService email,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> flags,
        ICaseDocumentStorage documentStorage,
        IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _flags = flags;
        _documentStorage = documentStorage;
        _documentGrantProtector = dataProtectionProvider
            .CreateProtector("PayPlanner.ClientPortal.DocumentGrant.v1")
            .ToTimeLimitedDataProtector();
    }

    public async Task<ClientPortalAdminAccountDto> CreateOrUpdateAccessAsync(
        ClientPortalAccessRequest request,
        int actorId,
        string actorName,
        CancellationToken ct)
    {
        EnsureEnabled();
        var email = NormalizeEmail(request.Email);
        var name = Require(request.DisplayName, 256, "Укажите имя внешнего пользователя.");
        var requestedDocumentIds = GetRequestedDocumentIds(request.CanViewDocuments ? request.DocumentIds : null);
        var type = request.AccountType?.Trim().ToLowerInvariant() switch
        {
            "client" or null or "" => "client",
            "representative" => "representative",
            _ => throw Error("validation_error", "Тип доступа должен быть «клиент» или «представитель»."),
        };

        var clientCase = await _db.ClientCases.AsNoTracking()
            .Where(item => item.Id == request.ClientCaseId)
            .Select(item => new { item.Id, item.ClientId, item.LegalEntityId })
            .FirstOrDefaultAsync(ct)
            ?? throw Error("case_not_found", "Дело не найдено.");
        var matchesOwner = (clientCase.ClientId.HasValue && request.ClientId == clientCase.ClientId)
            || (clientCase.LegalEntityId.HasValue && request.LegalEntityId == clientCase.LegalEntityId);
        if (type == "client" && !matchesOwner)
        {
            throw Error("access_mismatch", "Клиентский аккаунт можно связать только с владельцем выбранного дела.");
        }

        var account = await _db.ClientPortalAccounts
            .Include(item => item.CaseGrants)
            .FirstOrDefaultAsync(item => item.Email == email
                && item.ClientId == request.ClientId
                && item.LegalEntityId == request.LegalEntityId
                && item.AccountType == type, ct);
        if (account is null)
        {
            account = new ClientPortalAccount
            {
                Email = email,
                DisplayName = name,
                AccountType = type,
                ClientId = request.ClientId,
                LegalEntityId = request.LegalEntityId,
                CreatedByUserId = actorId,
                CreatedByUserName = Truncate(actorName, 256),
            };
            _db.ClientPortalAccounts.Add(account);
        }
        else
        {
            account.DisplayName = name;
            account.IsActive = true;
            account.RevokedAt = null;
            account.RevocationReason = string.Empty;
        }

        var grant = account.CaseGrants.FirstOrDefault(item => item.ClientCaseId == clientCase.Id);
        if (grant is null)
        {
            grant = new ClientPortalCaseGrant
            {
                ClientCaseId = clientCase.Id,
                CreatedByUserId = actorId,
            };
            account.CaseGrants.Add(grant);
        }
        grant.CanViewCaseSummary = request.CanViewCaseSummary;
        grant.CanViewDeadlines = request.CanViewDeadlines;
        grant.CanViewDocuments = request.CanViewDocuments;
        grant.CanViewFinance = request.CanViewFinance;
        grant.CanViewApprovals = request.CanViewApprovals;
        grant.CanViewMessages = request.CanViewMessages;
        grant.RevokedAt = null;
        grant.RevocationReason = string.Empty;

        await _db.SaveChangesAsync(ct);

        {
            var allowedIds = await _db.ClientCaseDocuments.AsNoTracking()
                .Where(item => item.ClientCaseId == clientCase.Id && requestedDocumentIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToListAsync(ct);
            var existing = await _db.ClientPortalDocumentGrants
                .Where(item => item.ClientPortalCaseGrantId == grant.Id)
                .ToListAsync(ct);
            foreach (var item in existing)
            {
                item.RevokedAt = allowedIds.Contains(item.ClientCaseDocumentId) ? null : DateTime.UtcNow;
            }
            foreach (var id in allowedIds.Where(id => existing.All(item => item.ClientCaseDocumentId != id)))
            {
                _db.ClientPortalDocumentGrants.Add(new ClientPortalDocumentGrant
                {
                    ClientPortalCaseGrantId = grant.Id,
                    ClientCaseDocumentId = id,
                    CreatedByUserId = actorId,
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        Audit(account.Id, grant.Id, "grant_saved", "case", clientCase.Id.ToString(CultureInfo.InvariantCulture), null, null);
        await _db.SaveChangesAsync(ct);
        return (await GetAccountAsync(account.Id, ct))!;
    }

    public async Task<ClientPortalAdminAccountDto?> GetAccountAsync(int accountId, CancellationToken ct)
    {
        EnsureEnabled();
        return await _db.ClientPortalAccounts.AsNoTracking()
            .Where(item => item.Id == accountId)
            .Select(item => new ClientPortalAdminAccountDto
            {
                Id = item.Id,
                PublicId = item.PublicId,
                Email = item.Email,
                DisplayName = item.DisplayName,
                AccountType = item.AccountType,
                ClientId = item.ClientId,
                LegalEntityId = item.LegalEntityId,
                IsActive = item.IsActive,
                CreatedAt = item.CreatedAt,
                RevokedAt = item.RevokedAt,
                Grants = item.CaseGrants.OrderBy(grant => grant.Id).Select(grant => new ClientPortalGrantDto
                {
                    Id = grant.Id,
                    CasePublicId = grant.PublicId,
                    CaseTitle = grant.ClientCase.Title,
                    RevokedAt = grant.RevokedAt,
                    CanViewCaseSummary = grant.CanViewCaseSummary,
                    CanViewDeadlines = grant.CanViewDeadlines,
                    CanViewDocuments = grant.CanViewDocuments,
                    CanViewFinance = grant.CanViewFinance,
                    CanViewApprovals = grant.CanViewApprovals,
                    CanViewMessages = grant.CanViewMessages,
                }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ClientPortalCaseAccessDto>> GetCaseAccountsAsync(int caseId, CancellationToken ct)
    {
        EnsureEnabled();
        return await _db.ClientPortalCaseGrants.AsNoTracking()
            .Where(grant => grant.ClientCaseId == caseId)
            .OrderBy(grant => grant.Account.DisplayName)
            .ThenBy(grant => grant.Id)
            .Select(grant => new ClientPortalCaseAccessDto
            {
                AccountId = grant.ClientPortalAccountId,
                DisplayName = grant.Account.DisplayName,
                Email = grant.Account.Email,
                AccountType = grant.Account.AccountType,
                IsActive = grant.Account.IsActive && grant.Account.RevokedAt == null && grant.RevokedAt == null,
                CanViewCaseSummary = grant.CanViewCaseSummary,
                CanViewDeadlines = grant.CanViewDeadlines,
                CanViewDocuments = grant.CanViewDocuments,
                CanViewFinance = grant.CanViewFinance,
                CanViewApprovals = grant.CanViewApprovals,
                CanViewMessages = grant.CanViewMessages,
                DocumentIds = _db.ClientPortalDocumentGrants
                    .Where(document => document.ClientPortalCaseGrantId == grant.Id && document.RevokedAt == null)
                    .Select(document => document.ClientCaseDocumentId)
                    .ToList(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<ClientPortalAuditDto>> GetCaseAuditAsync(int caseId, CancellationToken ct)
    {
        EnsureEnabled();
        return await _db.ClientPortalAuditEvents.AsNoTracking()
            .Where(item => item.ClientPortalCaseGrantId.HasValue
                && _db.ClientPortalCaseGrants.Any(grant =>
                    grant.Id == item.ClientPortalCaseGrantId.Value && grant.ClientCaseId == caseId))
            .OrderByDescending(item => item.OccurredAt)
            .Take(50)
            .Select(item => new ClientPortalAuditDto
            {
                EventType = item.EventType,
                TargetType = item.TargetType,
                TargetKey = item.TargetKey,
                OccurredAt = item.OccurredAt,
                DisplayName = item.Account != null ? item.Account.DisplayName : string.Empty,
                Email = item.Account != null ? item.Account.Email : string.Empty,
            })
            .ToListAsync(ct);
    }

    public async Task<ClientPortalChallengeCreatedDto> SendLoginAsync(int accountId, CancellationToken ct, int? caseId = null)
    {
        EnsureEnabled();

        ClientPortalAccount account;
        ClientPortalLoginChallenge challenge;
        int? grantId = null;
        var token = GenerateToken(32);
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            if (_db.Database.IsNpgsql())
            {
                await _db.Database.SqlQuery<int>(
                    $"SELECT 1 FROM \"ClientPortalAccounts\" WHERE \"Id\" = {accountId} FOR UPDATE").ToListAsync(ct);
            }
            account = await _db.ClientPortalAccounts.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == accountId && item.IsActive && item.RevokedAt == null, ct)
                ?? throw Error("account_unavailable", "Аккаунт не найден или отключён.");
            if (caseId is int requestedCaseId)
            {
                grantId = await _db.ClientPortalCaseGrants.AsNoTracking()
                    .Where(item => item.ClientPortalAccountId == accountId && item.ClientCaseId == requestedCaseId
                        && item.RevokedAt == null && item.CanViewCaseSummary)
                    .Select(item => (int?)item.Id).SingleOrDefaultAsync(ct)
                    ?? throw Error("grant_not_found", "Доступ к выбранному делу не найден.");
            }

            var now = DateTime.UtcNow;
            var active = await _db.ClientPortalLoginChallenges
                .Where(item => item.ClientPortalAccountId == accountId && item.ConsumedAt == null && item.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var item in active) item.RevokedAt = now;

            challenge = new ClientPortalLoginChallenge
            {
                ClientPortalAccountId = accountId,
                TokenHash = Hash(token),
                OtpHash = Hash($"{token}:{otp}"),
                CreatedAt = now,
                ExpiresAt = now.AddHours(ChallengeHours),
                MaxAttempts = MaxAttempts,
            };
            _db.ClientPortalLoginChallenges.Add(challenge);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var baseUrl = (_configuration["App:FrontendUrl"]
            ?? _configuration["Frontend:BaseUrl"]
            ?? _configuration["Frontend:PublicUrl"]
            ?? "http://localhost:5173").TrimEnd('/');
        var link = $"{baseUrl}/portal/login/{challenge.PublicId:D}#token={Uri.EscapeDataString(token)}";
        try
        {
            await _email.SendEmailAsync(
                account.Email,
                "Вход в клиентский кабинет",
                $"<h2>Клиентский кабинет</h2><p>{WebUtility.HtmlEncode(account.DisplayName)}, откройте защищённую ссылку:</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Войти в кабинет</a></p><p>Код: <strong>{otp}</strong></p><p>Ссылка действует {ChallengeHours} часа.</p>",
                true,
                ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            challenge.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw Error("delivery_failed", "Не удалось отправить письмо; ссылка отключена.");
        }

        Audit(account.Id, grantId, "login_sent", "account", account.PublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
        return new ClientPortalChallengeCreatedDto { ChallengeId = challenge.PublicId, ExpiresAt = challenge.ExpiresAt, EmailHint = MaskEmail(account.Email) };
    }

    public async Task RevokeAccountAsync(int accountId, int actorId, string actorName, string? reason, CancellationToken ct)
    {
        EnsureEnabled();
        var account = await _db.ClientPortalAccounts
            .Include(item => item.Sessions)
            .Include(item => item.CaseGrants)
            .FirstOrDefaultAsync(item => item.Id == accountId, ct)
            ?? throw Error("account_not_found", "Аккаунт не найден.");
        var now = DateTime.UtcNow;
        account.IsActive = false;
        account.RevokedAt = now;
        account.RevokedByUserId = actorId;
        account.RevokedByUserName = Truncate(actorName, 256);
        account.RevocationReason = Truncate(reason?.Trim() ?? "Доступ отозван администратором.", 500);
        foreach (var grant in account.CaseGrants.Where(item => item.RevokedAt == null)) grant.RevokedAt = now;
        foreach (var session in account.Sessions.Where(item => item.RevokedAt == null)) session.RevokedAt = now;
        Audit(account.Id, null, "account_revoked", "account", account.PublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeCaseAccessAsync(
        int accountId,
        int caseId,
        int actorId,
        string? reason,
        CancellationToken ct)
    {
        EnsureEnabled();
        var grant = await _db.ClientPortalCaseGrants
            .FirstOrDefaultAsync(item => item.ClientPortalAccountId == accountId
                && item.ClientCaseId == caseId
                && item.RevokedAt == null, ct)
            ?? throw Error("grant_not_found", "Доступ к делу уже отозван или не найден.");
        grant.RevokedAt = DateTime.UtcNow;
        grant.RevokedByUserId = actorId;
        grant.RevocationReason = Truncate(reason?.Trim() ?? "Доступ к делу отозван администратором.", 500);
        Audit(accountId, grant.Id, "grant_revoked", "case", caseId.ToString(CultureInfo.InvariantCulture), null, null);

        var hasOtherGrant = await _db.ClientPortalCaseGrants.AsNoTracking()
            .AnyAsync(item => item.ClientPortalAccountId == accountId
                && item.Id != grant.Id
                && item.RevokedAt == null
                && item.CanViewCaseSummary, ct);
        if (!hasOtherGrant)
        {
            var account = await _db.ClientPortalAccounts
                .Include(item => item.Sessions)
                .FirstAsync(item => item.Id == accountId, ct);
            account.IsActive = false;
            account.RevokedAt = DateTime.UtcNow;
            account.RevokedByUserId = actorId;
            account.RevocationReason = "Отозван последний доступ к делу.";
            foreach (var session in account.Sessions.Where(item => item.RevokedAt == null))
            {
                session.RevokedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<ClientPortalLoginPreviewDto> PreviewLoginAsync(Guid challengeId, string token, CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        var challenge = await LoadChallengeAsync(challengeId, token, ct);
        EnsureChallengeUsable(challenge);
        return new ClientPortalLoginPreviewDto
        {
            DisplayName = challenge.Account.DisplayName,
            EmailHint = MaskEmail(challenge.Account.Email),
            ExpiresAt = challenge.ExpiresAt,
        };
    }

    public async Task<ClientPortalSessionCreatedDto> VerifyLoginAsync(
        Guid challengeId,
        string token,
        string otp,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        var challenge = await LoadChallengeAsync(challengeId, token, ct);
        EnsureChallengeUsable(challenge);
        if (!FixedTimeEquals(challenge.OtpHash, Hash($"{token}:{otp?.Trim()}")))
        {
            challenge.FailedAttempts++;
            if (challenge.FailedAttempts >= challenge.MaxAttempts) challenge.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw Error("invalid_confirmation", challenge.RevokedAt.HasValue ? "Ссылка заблокирована." : "Неверный код.");
        }

        challenge.ConsumedAt = DateTime.UtcNow;
        var sessionToken = GenerateToken(48);
        var session = new ClientPortalSession
        {
            ClientPortalAccountId = challenge.ClientPortalAccountId,
            TokenHash = Hash(sessionToken),
            ExpiresAt = DateTime.UtcNow.AddHours(SessionHours),
            IpHash = Hash($"{challenge.Account.PublicId:N}:{ip}"),
            UserAgent = Truncate(userAgent?.Trim() ?? string.Empty, 512),
        };
        _db.ClientPortalSessions.Add(session);
        Audit(challenge.Account.Id, null, "login_succeeded", "session", session.PublicId.ToString("D"), ip, userAgent);
        await _db.SaveChangesAsync(ct);
        try
        {
            var loginAt = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var device = string.IsNullOrWhiteSpace(userAgent) ? "не определено" : Truncate(userAgent.Trim(), 160);
            await _email.SendEmailAsync(
                challenge.Account.Email,
                "Новый вход в клиентский кабинет",
                $"<h2>Новый вход</h2><p>{WebUtility.HtmlEncode(challenge.Account.DisplayName)}, выполнен вход в клиентский кабинет.</p><p>Время: {loginAt}<br>Устройство: {WebUtility.HtmlEncode(device)}</p><p>Если это были не вы, завершите все сессии в разделе «Профиль» и свяжитесь с компанией.</p>",
                true,
                ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Сбой дополнительного security-письма не должен отменять уже подтверждённый вход.
        }
        return new ClientPortalSessionCreatedDto { SessionToken = sessionToken, ExpiresAt = session.ExpiresAt };
    }

    public async Task<ClientPortalSessionContext?> ResolveSessionAsync(string? token, string? ip, string? userAgent, CancellationToken ct)
    {
        EnsureEnabled(publicSurface: true);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token.Trim());
        var session = await _db.ClientPortalSessions
            .Include(item => item.Account)
            .FirstOrDefaultAsync(item => item.TokenHash == hash, ct);
        if (session is null || session.RevokedAt.HasValue || session.ExpiresAt <= DateTime.UtcNow
            || !session.Account.IsActive || session.Account.RevokedAt.HasValue)
        {
            return null;
        }
        if (DateTime.UtcNow - session.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            session.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return new ClientPortalSessionContext(session.Account.Id, session.Account.PublicId, session.Account.DisplayName, session.Account.Email);
    }

    public async Task RevokeSessionAsync(string? token, bool all, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(token, null, null, ct);
        if (session is null) return;
        var sessions = await _db.ClientPortalSessions
            .Where(item => item.ClientPortalAccountId == session.AccountId && item.RevokedAt == null
                && (all || item.TokenHash == Hash(token!.Trim())))
            .ToListAsync(ct);
        foreach (var item in sessions) item.RevokedAt = DateTime.UtcNow;
        Audit(session.AccountId, null, all ? "logout_all" : "logout", "account", session.AccountPublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ClientPortalHomeDto> GetHomeAsync(
        ClientPortalSessionContext session,
        int page,
        int pageSize,
        CancellationToken ct,
        string? search = null,
        string? status = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _db.ClientPortalCaseGrants.AsNoTracking()
            .Where(grant => grant.ClientPortalAccountId == session.AccountId
                && grant.RevokedAt == null
                && grant.CanViewCaseSummary);
        var normalizedSearch = Truncate(search?.Trim() ?? string.Empty, 100);
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            var pattern = $"%{normalizedSearch}%";
            query = query.Where(grant => EF.Functions.Like(grant.ClientCase.Title, pattern)
                || EF.Functions.Like(grant.ClientCase.CaseNumber, pattern)
                || EF.Functions.Like(grant.ClientCase.CaseType, pattern));
        }
        var normalizedStatus = Truncate(status?.Trim() ?? string.Empty, 64);
        if (!string.IsNullOrWhiteSpace(normalizedStatus))
        {
            if (!Enum.TryParse<ClientCaseStatus>(normalizedStatus, true, out var parsedStatus))
            {
                query = query.Where(_ => false);
            }
            else
            {
                query = query.Where(grant => grant.ClientCase.Status == parsedStatus);
            }
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(grant => grant.ClientCase.CreatedAt).ThenByDescending(grant => grant.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(grant => new ClientPortalCaseListItemDto
            {
                PublicId = grant.PublicId,
                Title = grant.ClientCase.Title,
                CaseNumber = grant.ClientCase.CaseNumber,
                CaseType = grant.ClientCase.CaseType,
                Status = grant.ClientCase.Status.ToString(),
                Stage = grant.ClientCase.Stage,
                DueDate = grant.ClientCase.DueDate,
                CanViewDeadlines = grant.CanViewDeadlines,
                CanViewDocuments = grant.CanViewDocuments,
                CanViewFinance = grant.CanViewFinance,
                CanViewApprovals = grant.CanViewApprovals,
                CanViewMessages = grant.CanViewMessages,
            }).ToListAsync(ct);
        Audit(session.AccountId, null, "home_viewed", "case_list", page.ToString(CultureInfo.InvariantCulture), null, null);
        await _db.SaveChangesAsync(ct);
        var upcoming = await (
            from grant in _db.ClientPortalCaseGrants.AsNoTracking()
            join deadline in _db.ClientCaseProceduralDeadlines.AsNoTracking()
                on grant.ClientCaseId equals deadline.ClientCaseId
            where grant.ClientPortalAccountId == session.AccountId
                && grant.RevokedAt == null
                && grant.CanViewCaseSummary
                && grant.CanViewDeadlines
                && deadline.Status != "cancelled"
                && deadline.DueAt >= DateTime.UtcNow
            orderby deadline.DueAt, deadline.Id
            select new ClientPortalUpcomingActionDto
            {
                CasePublicId = grant.PublicId,
                CaseTitle = deadline.ClientCase.Title,
                Title = deadline.Title,
                DueAt = deadline.DueAt,
                Status = deadline.Status,
            })
            .Take(10)
            .ToListAsync(ct);
        return new ClientPortalHomeDto
        {
            DisplayName = session.DisplayName,
            Items = items,
            UpcomingActions = upcoming,
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<ClientPortalProfileDto> GetProfileAsync(ClientPortalSessionContext session, CancellationToken ct)
    {
        var profile = await _db.ClientPortalAccounts.AsNoTracking()
            .Where(account => account.Id == session.AccountId && account.IsActive && account.RevokedAt == null)
            .Select(account => new ClientPortalProfileDto
            {
                DisplayName = account.DisplayName,
                Email = account.Email,
                AccountType = account.AccountType,
                SecurityNotificationsEnabled = true,
                ActiveSessionCount = account.Sessions.Count(item => item.RevokedAt == null && item.ExpiresAt > DateTime.UtcNow),
                ActiveCaseCount = account.CaseGrants.Count(item => item.RevokedAt == null && item.CanViewCaseSummary),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Error("account_unavailable", "Аккаунт недоступен.");
        Audit(session.AccountId, null, "profile_viewed", "account", session.AccountPublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<ClientPortalCaseDto?> GetCaseAsync(ClientPortalSessionContext session, Guid casePublicId, CancellationToken ct)
    {
        var view = await ReadGrantedCaseAsync(
            _db.ClientPortalCaseGrants.Where(item => item.ClientPortalAccountId == session.AccountId
                && item.PublicId == casePublicId), ct);
        if (view is null) return null;

        await LoadCaseContentsAsync(view, [], ct);
        Audit(session.AccountId, view.GrantId, "case_viewed", "case", casePublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
        return view.Case;
    }

    public async Task<ClientPortalCaseDto?> PreviewCaseAsync(
        int caseId, ClientPortalCasePreviewRequest request, int actorId, string actorName, CancellationToken ct)
    {
        var view = request.AccountId is int accountId
            ? await ReadGrantedCaseAsync(_db.ClientPortalCaseGrants.Where(item =>
                item.ClientCaseId == caseId && item.ClientPortalAccountId == accountId), ct)
            : await ReadDraftCaseAsync(caseId, request, ct);
        if (view is null) return null;

        List<int> documentIds = request.AccountId.HasValue || !request.CanViewDocuments
            ? []
            : GetRequestedDocumentIds(request.DocumentIds);
        await LoadCaseContentsAsync(view, documentIds, ct);
        _db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case",
            ObjectId = caseId,
            Action = "ClientPortalPreview",
            Description = request.AccountId.HasValue
                ? "Просмотр дела с правами клиента."
                : "Предпросмотр настроек клиентского доступа к делу.",
            ActorUserId = actorId,
            ActorUserName = Truncate(actorName, 256),
            MetadataJson = JsonSerializer.Serialize(new { accountId = request.AccountId }),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return view.Case;
    }

    private static Task<CaseView?> ReadGrantedCaseAsync(IQueryable<ClientPortalCaseGrant> grants, CancellationToken ct)
    {
        return grants.AsNoTracking()
            .Where(item => item.RevokedAt == null && item.CanViewCaseSummary
                && item.Account.IsActive && item.Account.RevokedAt == null)
            .Select(item => new CaseView(
                item.Id, item.ClientCaseId, item.Account.Email, item.CanViewDeadlines,
                item.CanViewDocuments, item.CanViewFinance, item.CanViewApprovals,
                new ClientPortalCaseDto
                {
                    PublicId = item.PublicId,
                    Title = item.ClientCase.Title,
                    CaseNumber = item.ClientCase.CaseNumber,
                    CaseType = item.ClientCase.CaseType,
                    Status = item.ClientCase.Status.ToString(),
                    Stage = item.ClientCase.Stage,
                    StartDate = item.ClientCase.StartDate,
                    DueDate = item.ClientCase.DueDate,
                })).SingleOrDefaultAsync(ct);
    }

    private async Task<CaseView?> ReadDraftCaseAsync(int caseId, ClientPortalCasePreviewRequest request, CancellationToken ct)
    {
        var preview = await _db.ClientCases.AsNoTracking()
            .Where(item => item.Id == caseId)
            .Select(item => new ClientPortalCaseDto
            {
                Title = item.Title,
                CaseNumber = item.CaseNumber,
                CaseType = item.CaseType,
                Status = item.Status.ToString(),
                Stage = item.Stage,
                StartDate = item.StartDate,
                DueDate = item.DueDate,
            }).SingleOrDefaultAsync(ct);
        if (preview is null) return null;

        var email = request.CanViewApprovals && !string.IsNullOrWhiteSpace(request.Email)
            ? NormalizeEmail(request.Email)
            : null;
        return new CaseView(null, caseId, email, request.CanViewDeadlines, request.CanViewDocuments,
            request.CanViewFinance, request.CanViewApprovals, preview);
    }

    private async Task LoadCaseContentsAsync(CaseView grant, List<int> draftDocumentIds, CancellationToken ct)
    {
        if (grant.CanViewDeadlines)
        {
            grant.Case.Deadlines = await _db.ClientCaseProceduralDeadlines.AsNoTracking()
                .Where(item => item.ClientCaseId == grant.ClientCaseId && item.Status != "cancelled")
                .OrderBy(item => item.DueAt).Take(100)
                .Select(item => new ClientPortalDeadlineDto { Title = item.Title, DueAt = item.DueAt, Status = item.Status })
                .ToListAsync(ct);
            grant.Case.Events = await _db.ClientCaseCourtEvents.AsNoTracking()
                .Where(item => item.ClientCaseId == grant.ClientCaseId)
                .OrderByDescending(item => item.ScheduledAt)
                .Take(50)
                .Select(item => new ClientPortalEventDto
                {
                    Title = item.Title,
                    OccurredAt = item.ScheduledAt,
                    EventType = item.EventType,
                    Status = item.MovementStatus,
                })
                .ToListAsync(ct);
        }
        if (grant.CanViewDocuments)
        {
            grant.Case.Documents = grant.GrantId is int grantId
                ? await _db.ClientPortalDocumentGrants.AsNoTracking()
                    .Where(item => item.ClientPortalCaseGrantId == grantId && item.RevokedAt == null
                        && item.Document.ClientCaseId == grant.ClientCaseId)
                    .OrderByDescending(item => item.Document.UploadedAt).Take(100)
                    .Select(item => new ClientPortalDocumentDto
                    {
                        PublicId = item.PublicId,
                        Name = item.Document.OriginalFileName,
                        DocumentType = item.Document.DocumentType,
                        Status = item.Document.Status,
                        Version = item.Document.Version,
                        SizeBytes = item.Document.SizeBytes,
                        UploadedAt = item.Document.UploadedAt,
                    }).ToListAsync(ct)
                : await _db.ClientCaseDocuments.AsNoTracking()
                    .Where(item => item.ClientCaseId == grant.ClientCaseId && draftDocumentIds.Contains(item.Id))
                    .OrderByDescending(item => item.UploadedAt).Take(100)
                    .Select(item => new ClientPortalDocumentDto
                    {
                        Name = item.OriginalFileName,
                        DocumentType = item.DocumentType,
                        Status = item.Status,
                        Version = item.Version,
                        SizeBytes = item.SizeBytes,
                        UploadedAt = item.UploadedAt,
                    }).ToListAsync(ct);
        }
        if (grant.CanViewFinance)
        {
            grant.Case.Invoices = await _db.Payments.AsNoTracking()
                .Where(item => (item.ClientCaseId == grant.ClientCaseId
                    || item.InvoiceClientCases.Any(link => link.ClientCaseId == grant.ClientCaseId))
                    && item.Type == PaymentType.Income)
                .OrderByDescending(item => item.Date).Take(100)
                .Select(item => new ClientPortalInvoiceDto
                {
                    Number = item.Account ?? item.LegacyAccountNumber ?? string.Empty,
                    Date = item.AccountDate ?? item.Date,
                    Amount = item.Amount,
                    Status = item.Status.ToString(),
                    WorkflowStatus = item.InvoiceWorkflowStatus,
                }).ToListAsync(ct);
            grant.Case.Acts = await _db.Acts.AsNoTracking()
                .Where(item => item.InvoiceId.HasValue
                    && (item.Invoice!.ClientCaseId == grant.ClientCaseId
                        || item.Invoice.InvoiceClientCases.Any(link => link.ClientCaseId == grant.ClientCaseId)))
                .OrderByDescending(item => item.Date)
                .Take(100)
                .Select(item => new ClientPortalActDto
                {
                    Number = item.Number,
                    Date = item.Date,
                    Amount = item.Amount,
                    Status = item.Status.ToString(),
                    WorkflowStatus = item.WorkflowStatus,
                })
                .ToListAsync(ct);
            grant.Case.Contracts = await _db.Contracts.AsNoTracking()
                .Where(item => item.ContractInvoices.Any(link => link.Invoice.ClientCaseId == grant.ClientCaseId
                    || link.Invoice.InvoiceClientCases.Any(caseLink => caseLink.ClientCaseId == grant.ClientCaseId)))
                .OrderByDescending(item => item.Date)
                .Take(100)
                .Select(item => new ClientPortalContractDto
                {
                    Number = item.Number,
                    Date = item.Date,
                    Amount = item.Amount,
                    Status = item.Status,
                    ValidUntil = item.ValidUntil,
                })
                .ToListAsync(ct);
        }
        if (grant.CanViewApprovals && grant.RecipientEmail is not null)
        {
            grant.Case.Approvals = await _db.ExternalWorkOrderApprovalRequests.AsNoTracking()
                .Where(item => item.WorkOrder.ClientCaseId == grant.ClientCaseId
                    && item.RecipientEmail == grant.RecipientEmail)
                .OrderByDescending(item => item.Version).Take(20)
                .Select(item => new ClientPortalApprovalDto
                {
                    Version = item.Version,
                    Status = item.Status,
                    CreatedAt = item.CreatedAt,
                    ExpiresAt = item.ExpiresAt,
                    DecisionAt = item.DecisionAt,
                }).ToListAsync(ct);
        }
    }

    private sealed record CaseView(int? GrantId, int ClientCaseId, string? RecipientEmail,
        bool CanViewDeadlines, bool CanViewDocuments, bool CanViewFinance, bool CanViewApprovals, ClientPortalCaseDto Case);

    public async Task<ClientPortalDocumentGrantDto?> CreateDocumentGrantAsync(
        ClientPortalSessionContext session,
        Guid documentPublicId,
        CancellationToken ct)
    {
        var allowed = await _db.ClientPortalDocumentGrants.AsNoTracking()
            .AnyAsync(item => item.PublicId == documentPublicId
                && item.RevokedAt == null
                && item.CaseGrant.ClientPortalAccountId == session.AccountId
                && item.CaseGrant.RevokedAt == null
                && item.CaseGrant.CanViewCaseSummary
                && item.CaseGrant.CanViewDocuments, ct);
        if (!allowed)
        {
            return null;
        }
        var payload = JsonSerializer.Serialize(new ClientPortalDocumentGrantPayload(session.AccountId, documentPublicId));
        Audit(session.AccountId, null, "document_grant_created", "document", documentPublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
        return new ClientPortalDocumentGrantDto
        {
            GrantToken = _documentGrantProtector.Protect(payload, DocumentGrantLifetime),
            ExpiresAt = DateTime.UtcNow.Add(DocumentGrantLifetime),
        };
    }

    public async Task<ClientPortalDocumentDownloadDto?> GetDocumentAsync(
        ClientPortalSessionContext session,
        Guid documentPublicId,
        string grantToken,
        CancellationToken ct)
    {
        ClientPortalDocumentGrantPayload payload;
        try
        {
            var json = _documentGrantProtector.Unprotect(grantToken.Trim(), out _);
            payload = JsonSerializer.Deserialize<ClientPortalDocumentGrantPayload>(json)
                ?? throw new CryptographicException("Empty portal document grant.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
        if (payload.AccountId != session.AccountId || payload.DocumentPublicId != documentPublicId)
        {
            return null;
        }

        var document = await _db.ClientPortalDocumentGrants.AsNoTracking()
            .Where(item => item.PublicId == documentPublicId
                && item.RevokedAt == null
                && item.CaseGrant.ClientPortalAccountId == session.AccountId
                && item.CaseGrant.RevokedAt == null
                && item.CaseGrant.CanViewCaseSummary
                && item.CaseGrant.CanViewDocuments)
            .Select(item => new
            {
                item.ClientPortalCaseGrantId,
                item.Document.StoragePath,
                item.Document.ContentType,
                item.Document.OriginalFileName,
            })
            .FirstOrDefaultAsync(ct);
        if (document is null || !_documentStorage.Exists(document.StoragePath))
        {
            return null;
        }

        Audit(session.AccountId, document.ClientPortalCaseGrantId, "document_downloaded", "document", documentPublicId.ToString("D"), null, null);
        await _db.SaveChangesAsync(ct);
        return new ClientPortalDocumentDownloadDto(
            _documentStorage.ResolvePath(document.StoragePath),
            document.ContentType,
            document.OriginalFileName);
    }

    private async Task<ClientPortalLoginChallenge> LoadChallengeAsync(Guid publicId, string token, CancellationToken ct)
    {
        var challenge = await _db.ClientPortalLoginChallenges.Include(item => item.Account)
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (challenge is null || string.IsNullOrWhiteSpace(token)
            || !FixedTimeEquals(challenge.TokenHash, Hash(token.Trim())))
            throw Error("link_unavailable", "Ссылка недействительна или больше недоступна.");
        return challenge;
    }

    private static void EnsureChallengeUsable(ClientPortalLoginChallenge challenge)
    {
        if (!challenge.Account.IsActive || challenge.Account.RevokedAt.HasValue
            || challenge.RevokedAt.HasValue || challenge.ConsumedAt.HasValue
            || challenge.ExpiresAt <= DateTime.UtcNow)
            throw Error("link_unavailable", "Ссылка недействительна или больше недоступна.");
    }

    private void EnsureEnabled(bool publicSurface = false)
    {
        if (!_flags.CurrentValue.Legal.ClientPortal)
            throw Error("feature_disabled", publicSurface ? "Клиентский кабинет недоступен." : "Клиентский кабинет выключен.");
    }

    private void Audit(int? accountId, int? grantId, string eventType, string targetType, string targetKey, string? ip, string? userAgent)
        => _db.ClientPortalAuditEvents.Add(new ClientPortalAuditEvent
        {
            ClientPortalAccountId = accountId,
            ClientPortalCaseGrantId = grantId,
            EventType = eventType,
            TargetType = targetType,
            TargetKey = Truncate(targetKey, 128),
            IpHash = Hash($"{accountId}:{ip}"),
            UserAgent = Truncate(userAgent?.Trim() ?? string.Empty, 512),
        });

    private static ClientPortalException Error(string code, string message) => new(code, message);
    private static List<int> GetRequestedDocumentIds(List<int>? ids)
    {
        var requestedIds = (ids ?? []).Where(id => id > 0).Distinct().Take(201).ToList();
        if (requestedIds.Count > 200)
            throw Error("validation_error", "За один раз можно открыть не более 200 документов.");
        return requestedIds;
    }

    private static string GenerateToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedTimeEquals(string a, string b)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
        catch (FormatException) { return false; }
    }
    private static string Require(string? value, int max, string error)
        => string.IsNullOrWhiteSpace(value) ? throw Error("validation_error", error) : Truncate(value.Trim(), max);
    private static string NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        try { return new MailAddress(email).Address == email ? Truncate(email, 320) : throw new FormatException(); }
        catch (FormatException) { throw Error("validation_error", "Укажите корректный email."); }
    }
    private static string MaskEmail(string email) => email.IndexOf('@') is var at && at > 1 ? $"{email[0]}***{email[(at - 1)..]}" : "***";
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed class ClientPortalException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public sealed record ClientPortalSessionContext(int AccountId, Guid AccountPublicId, string DisplayName, string Email);
public sealed record ClientPortalDocumentDownloadDto(string FullPath, string ContentType, string FileName);
public sealed record ClientPortalDocumentGrantPayload(int AccountId, Guid DocumentPublicId);
public sealed class ClientPortalCasePreviewRequest
{
    public int? AccountId { get; set; }
    public string? Email { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public List<int> DocumentIds { get; set; } = [];
}

public sealed class ClientPortalAccessRequest
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? AccountType { get; set; }
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public int ClientCaseId { get; set; }
    public bool CanViewCaseSummary { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public bool CanViewMessages { get; set; }
    public List<int> DocumentIds { get; set; } = [];
}
public sealed class ClientPortalAdminAccountDto
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Email { get; set; } = ""; public string DisplayName { get; set; } = "";
    public string AccountType { get; set; } = ""; public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public List<ClientPortalGrantDto> Grants { get; set; } = [];
}
public sealed class ClientPortalGrantDto
{
    public int Id { get; set; }
    public Guid CasePublicId { get; set; }
    public string CaseTitle { get; set; } = ""; public DateTime? RevokedAt { get; set; }
    public bool CanViewCaseSummary { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public bool CanViewMessages { get; set; }
}
public sealed class ClientPortalChallengeCreatedDto { public Guid ChallengeId { get; set; } public DateTime ExpiresAt { get; set; } public string EmailHint { get; set; } = ""; }
public sealed class ClientPortalCaseAccessDto
{
    public int AccountId { get; set; }
    public string DisplayName { get; set; } = ""; public string Email { get; set; } = ""; public string AccountType { get; set; } = "";
    public bool IsActive { get; set; }
    public bool CanViewCaseSummary { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public bool CanViewMessages { get; set; }
    public List<int> DocumentIds { get; set; } = [];
}
public sealed class ClientPortalAuditDto
{
    public string EventType { get; set; } = ""; public string TargetType { get; set; } = ""; public string TargetKey { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string DisplayName { get; set; } = ""; public string Email { get; set; } = "";
}
public sealed class ClientPortalLoginPreviewDto { public string DisplayName { get; set; } = ""; public string EmailHint { get; set; } = ""; public DateTime ExpiresAt { get; set; } }
public sealed class ClientPortalSessionCreatedDto { public string SessionToken { get; set; } = ""; public DateTime ExpiresAt { get; set; } }
public sealed class ClientPortalHomeDto { public string DisplayName { get; set; } = ""; public List<ClientPortalCaseListItemDto> Items { get; set; } = []; public List<ClientPortalUpcomingActionDto> UpcomingActions { get; set; } = []; public int Total { get; set; } public int Page { get; set; } public int PageSize { get; set; } }
public sealed class ClientPortalUpcomingActionDto { public Guid CasePublicId { get; set; } public string CaseTitle { get; set; } = ""; public string Title { get; set; } = ""; public DateTime DueAt { get; set; } public string Status { get; set; } = ""; }
public sealed class ClientPortalProfileDto { public string DisplayName { get; set; } = ""; public string Email { get; set; } = ""; public string AccountType { get; set; } = ""; public bool SecurityNotificationsEnabled { get; set; } public int ActiveSessionCount { get; set; } public int ActiveCaseCount { get; set; } }
public sealed class ClientPortalDocumentGrantDto { public string GrantToken { get; set; } = ""; public DateTime ExpiresAt { get; set; } }
public sealed class ClientPortalCaseListItemDto
{
    public Guid PublicId { get; set; }
    public string Title { get; set; } = ""; public string CaseNumber { get; set; } = ""; public string CaseType { get; set; } = "";
    public string Status { get; set; } = ""; public string Stage { get; set; } = ""; public DateTime? DueDate { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public bool CanViewMessages { get; set; }
}
public sealed class ClientPortalCaseDto
{
    public Guid PublicId { get; set; }
    public string Title { get; set; } = ""; public string CaseNumber { get; set; } = ""; public string CaseType { get; set; } = "";
    public string Status { get; set; } = ""; public string Stage { get; set; } = ""; public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public List<ClientPortalDeadlineDto> Deadlines { get; set; } = []; public List<ClientPortalDocumentDto> Documents { get; set; } = [];
    public List<ClientPortalEventDto> Events { get; set; } = []; public List<ClientPortalInvoiceDto> Invoices { get; set; } = []; public List<ClientPortalActDto> Acts { get; set; } = []; public List<ClientPortalContractDto> Contracts { get; set; } = []; public List<ClientPortalApprovalDto> Approvals { get; set; } = [];
}
public sealed class ClientPortalDeadlineDto { public string Title { get; set; } = ""; public DateTime DueAt { get; set; } public string Status { get; set; } = ""; }
public sealed class ClientPortalEventDto { public string Title { get; set; } = ""; public DateTime OccurredAt { get; set; } public string EventType { get; set; } = ""; public string Status { get; set; } = ""; }
public sealed class ClientPortalDocumentDto { public Guid PublicId { get; set; } public string Name { get; set; } = ""; public string DocumentType { get; set; } = ""; public string Status { get; set; } = ""; public int Version { get; set; } public long SizeBytes { get; set; } public DateTime UploadedAt { get; set; } }
public sealed class ClientPortalInvoiceDto { public string Number { get; set; } = ""; public DateTime Date { get; set; } public decimal Amount { get; set; } public string Status { get; set; } = ""; public string WorkflowStatus { get; set; } = ""; }
public sealed class ClientPortalActDto { public string Number { get; set; } = ""; public DateTime Date { get; set; } public decimal Amount { get; set; } public string Status { get; set; } = ""; public string WorkflowStatus { get; set; } = ""; }
public sealed class ClientPortalContractDto { public string Number { get; set; } = ""; public DateTime Date { get; set; } public decimal? Amount { get; set; } public string Status { get; set; } = ""; public DateTime? ValidUntil { get; set; } }
public sealed class ClientPortalApprovalDto { public int Version { get; set; } public string Status { get; set; } = ""; public DateTime CreatedAt { get; set; } public DateTime ExpiresAt { get; set; } public DateTime? DecisionAt { get; set; } }

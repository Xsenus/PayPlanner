using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class ExternalAuthService
{
    private static string NormalizeExternalAuthIntent(string? intent)
        => string.Equals(intent?.Trim(), "link", StringComparison.OrdinalIgnoreCase)
            ? "link"
            : "signin";

    public async Task<IReadOnlyList<UserExternalIdentity>> GetExternalIdentitiesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.UserExternalIdentities
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Provider)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DetachExternalIdentityAsync(
        int userId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = TechnicalEmailHelper.NormalizeProvider(provider);
        if (string.IsNullOrWhiteSpace(normalizedProvider))
        {
            throw new InvalidOperationException("External auth provider is invalid.");
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("Пользователь не найден.");

        var identity = await _context.UserExternalIdentities
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.Provider == normalizedProvider,
                cancellationToken);
        if (identity is null)
        {
            return false;
        }

        var linkedProvidersCount = await _context.UserExternalIdentities
            .CountAsync(x => x.UserId == userId, cancellationToken);

        var hasRealEmail = TechnicalEmailHelper.IsValidRealEmail(user.Email);
        if (linkedProvidersCount <= 1 && !hasRealEmail)
        {
            throw new InvalidOperationException("Нельзя отвязать последний способ входа без основного email.");
        }

        _context.UserExternalIdentities.Remove(identity);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<UserExternalIdentity> AttachExternalIdentityAsync(
        int userId,
        ExternalIdentityProfile profile,
        long? chatId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = TechnicalEmailHelper.NormalizeProvider(profile.Provider);
        var providerUserId = NormalizeRequired(profile.ProviderUserId, "External provider user id is invalid.");
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException("External auth provider is invalid.");
        }

        var user = await _context.Users
            .Include(x => x.Role)
            .Include(x => x.ExternalIdentities)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("Пользователь не найден.");

        var conflictingIdentity = await _context.UserExternalIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Provider == provider && x.ProviderUserId == providerUserId && x.UserId != userId,
                cancellationToken);
        if (conflictingIdentity is not null)
        {
            throw new InvalidOperationException("Этот способ входа уже привязан к другому профилю.");
        }

        var identity = await _context.UserExternalIdentities
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == provider, cancellationToken);

        var now = DateTime.UtcNow;
        var confirmedEmail = profile.EmailVerified
            ? TechnicalEmailHelper.NormalizeRealEmail(profile.Email)
            : null;

        if (identity is null)
        {
            identity = new UserExternalIdentity
            {
                UserId = userId,
                Provider = provider,
                ProviderUserId = providerUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.UserExternalIdentities.Add(identity);
        }

        identity.UserId = userId;
        identity.Provider = provider;
        identity.ProviderUserId = providerUserId;
        identity.ProviderEmail = confirmedEmail ?? identity.ProviderEmail;
        identity.ProviderUsername = NormalizeOptional(profile.Username);
        identity.DisplayName = NormalizeOptional(profile.DisplayName);
        identity.AvatarUrl = NormalizeOptional(profile.AvatarUrl);
        identity.TelegramUserId = provider == "telegram" ? providerUserId : identity.TelegramUserId;
        identity.ChatId = provider == "telegram" ? chatId ?? identity.ChatId : identity.ChatId;
        identity.VerifiedAt ??= now;
        identity.LastUsedAt = now;
        identity.UpdatedAt = now;

        if (!string.IsNullOrWhiteSpace(confirmedEmail)
            && (TechnicalEmailHelper.IsTechnicalEmail(user.Email)
                || string.Equals(user.Email, confirmedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            user.Email = confirmedEmail;
        }

        if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            user.FullName = profile.DisplayName.Trim();
        }

        if (string.IsNullOrWhiteSpace(user.PhotoUrl) && !string.IsNullOrWhiteSpace(profile.AvatarUrl))
        {
            user.PhotoUrl = profile.AvatarUrl.Trim();
        }

        if (string.IsNullOrWhiteSpace(user.PhoneNumber) && !string.IsNullOrWhiteSpace(profile.Phone))
        {
            user.PhoneNumber = profile.Phone.Trim();
        }

        if (provider == "telegram"
            && string.IsNullOrWhiteSpace(user.Telegram)
            && !string.IsNullOrWhiteSpace(profile.Username))
        {
            user.Telegram = FormatTelegramUsername(profile.Username);
        }

        user.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        return identity;
    }
}

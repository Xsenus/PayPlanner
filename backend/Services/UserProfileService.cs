using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Auth;

namespace PayPlanner.Api.Services;

public class UserProfileService
{
    private readonly PaymentContext _context;

    public UserProfileService(PaymentContext context)
    {
        _context = context;
    }

    public async Task<UserProfileDto?> GetProfileByUserIdAsync(string userId)
    {
        var profile = await _context.UserProfiles
            .Include(p => p.Contacts)
            .Include(p => p.SocialProfiles)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        return profile != null ? MapToDto(profile) : null;
    }

    public async Task<UserProfileDto> CreateOrUpdateProfileAsync(string userId, CreateUserProfileRequest request)
    {
        var profile = await _context.UserProfiles
            .Include(p => p.Contacts)
            .Include(p => p.SocialProfiles)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            profile = new UserProfile
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };
            _context.UserProfiles.Add(profile);
        }

        profile.FirstName = request.FirstName;
        profile.LastName = request.LastName;
        profile.Patronymic = request.Patronymic;
        profile.DateOfBirth = request.DateOfBirth;
        profile.HireDate = request.HireDate;
        profile.TerminationDate = request.TerminationDate;
        profile.UpdatedAt = DateTime.UtcNow;

        // Remove old contacts and social profiles
        _context.UserContacts.RemoveRange(profile.Contacts);
        _context.UserSocialProfiles.RemoveRange(profile.SocialProfiles);

        await _context.SaveChangesAsync();

        // Add new contacts
        foreach (var contactDto in request.Contacts)
        {
            var contact = new UserContact
            {
                ProfileId = profile.Id,
                ContactType = contactDto.ContactType,
                ContactValue = contactDto.ContactValue,
                IsPrimary = contactDto.IsPrimary,
                CreatedAt = DateTime.UtcNow
            };
            _context.UserContacts.Add(contact);
        }

        // Add new social profiles
        foreach (var socialDto in request.SocialProfiles)
        {
            var social = new UserSocialProfile
            {
                ProfileId = profile.Id,
                Platform = socialDto.Platform,
                ProfileUrl = socialDto.ProfileUrl,
                CreatedAt = DateTime.UtcNow
            };
            _context.UserSocialProfiles.Add(social);
        }

        await _context.SaveChangesAsync();

        // Reload to get the updated profile
        await _context.Entry(profile).ReloadAsync();
        await _context.Entry(profile).Collection(p => p.Contacts).LoadAsync();
        await _context.Entry(profile).Collection(p => p.SocialProfiles).LoadAsync();

        return MapToDto(profile);
    }

    public async Task<bool> DeleteProfileAsync(string userId)
    {
        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            return false;
        }

        _context.UserProfiles.Remove(profile);
        await _context.SaveChangesAsync();

        return true;
    }

    private static UserProfileDto MapToDto(UserProfile profile)
    {
        return new UserProfileDto
        {
            Id = profile.Id,
            UserId = profile.UserId,
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            Patronymic = profile.Patronymic,
            DateOfBirth = profile.DateOfBirth,
            HireDate = profile.HireDate,
            TerminationDate = profile.TerminationDate,
            Contacts = profile.Contacts.Select(c => new UserContactDto
            {
                Id = c.Id,
                ContactType = c.ContactType,
                ContactValue = c.ContactValue,
                IsPrimary = c.IsPrimary
            }).ToList(),
            SocialProfiles = profile.SocialProfiles.Select(s => new UserSocialProfileDto
            {
                Id = s.Id,
                Platform = s.Platform,
                ProfileUrl = s.ProfileUrl
            }).ToList()
        };
    }
}

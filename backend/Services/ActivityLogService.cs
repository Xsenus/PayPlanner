using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Auth;
using System.Text.Json;

namespace PayPlanner.Api.Services;

public class ActivityLogService
{
    private readonly PaymentContext _context;
    private bool _loggingEnabled = true;

    public ActivityLogService(PaymentContext context)
    {
        _context = context;
    }

    public void EnableLogging() => _loggingEnabled = true;
    public void DisableLogging() => _loggingEnabled = false;
    public bool IsLoggingEnabled() => _loggingEnabled;

    public async Task LogActivityAsync(
        string userId,
        string actionType,
        string section,
        object? details = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        if (!_loggingEnabled) return;

        var log = new ActivityLog
        {
            UserId = userId,
            ActionType = actionType,
            Section = section,
            Details = details != null ? JsonSerializer.Serialize(details) : null,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Timestamp = DateTime.UtcNow
        };

        _context.ActivityLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    public async Task<(List<ActivityLogDto> Logs, int Total)> GetLogsAsync(ActivityLogFilterRequest filter)
    {
        var query = _context.ActivityLogs
            .Include(al => al.User)
            .ThenInclude(u => u.Profile)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filter.UserId))
        {
            query = query.Where(al => al.UserId == filter.UserId);
        }

        if (!string.IsNullOrEmpty(filter.ActionType))
        {
            query = query.Where(al => al.ActionType == filter.ActionType);
        }

        if (!string.IsNullOrEmpty(filter.Section))
        {
            query = query.Where(al => al.Section == filter.Section);
        }

        if (filter.FromDate.HasValue)
        {
            query = query.Where(al => al.Timestamp >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(al => al.Timestamp <= filter.ToDate.Value);
        }

        var total = await query.CountAsync();

        var logs = await query
            .OrderByDescending(al => al.Timestamp)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var dtos = logs.Select(log => new ActivityLogDto
        {
            Id = log.Id,
            UserId = log.UserId,
            UserFullName = log.User.Profile != null
                ? $"{log.User.Profile.LastName} {log.User.Profile.FirstName} {log.User.Profile.Patronymic ?? ""}".Trim()
                : log.User.FullName,
            ActionType = log.ActionType,
            Section = log.Section,
            Details = log.Details,
            IpAddress = log.IpAddress,
            Timestamp = log.Timestamp
        }).ToList();

        return (dtos, total);
    }

    public async Task<List<ActivityLogDto>> GetUserLogsAsync(string userId, int limit = 100)
    {
        var logs = await _context.ActivityLogs
            .Include(al => al.User)
            .ThenInclude(u => u.Profile)
            .Where(al => al.UserId == userId)
            .OrderByDescending(al => al.Timestamp)
            .Take(limit)
            .ToListAsync();

        return logs.Select(log => new ActivityLogDto
        {
            Id = log.Id,
            UserId = log.UserId,
            UserFullName = log.User.Profile != null
                ? $"{log.User.Profile.LastName} {log.User.Profile.FirstName} {log.User.Profile.Patronymic ?? ""}".Trim()
                : log.User.FullName,
            ActionType = log.ActionType,
            Section = log.Section,
            Details = log.Details,
            IpAddress = log.IpAddress,
            Timestamp = log.Timestamp
        }).ToList();
    }

    public async Task ClearLogsAsync(string? userId = null, DateTime? beforeDate = null)
    {
        var query = _context.ActivityLogs.AsQueryable();

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(al => al.UserId == userId);
        }

        if (beforeDate.HasValue)
        {
            query = query.Where(al => al.Timestamp < beforeDate.Value);
        }

        _context.ActivityLogs.RemoveRange(query);
        await _context.SaveChangesAsync();
    }
}

// Action type constants for consistency
public static class ActivityActionTypes
{
    public const string Login = "login";
    public const string Logout = "logout";
    public const string View = "view";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Click = "click";
    public const string Navigate = "navigate";
    public const string Export = "export";
    public const string Import = "import";
}

// Section constants
public static class ActivitySections
{
    public const string Auth = "auth";
    public const string Calendar = "calendar";
    public const string Clients = "clients";
    public const string Users = "users";
    public const string Reports = "reports";
    public const string Calculator = "calculator";
    public const string Dictionaries = "dictionaries";
    public const string Settings = "settings";
}

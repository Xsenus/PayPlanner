using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

public partial class NotificationsController
{
    [HttpGet("contract-expiration")]
    public async Task<ActionResult<ContractExpirationRecipientSettingsDto>> GetContractExpirationSettings(CancellationToken ct)
    {
        if (!await CanViewNotificationSettingsAsync(ct)) return Forbid();
        var settings = await ContractExpirationRecipientSettings.LoadAsync(_context, ct);
        return Ok(await MapContractExpirationSettingsAsync(settings, ct));
    }

    [HttpPut("contract-expiration")]
    public async Task<ActionResult<ContractExpirationRecipientSettingsDto>> UpdateContractExpirationSettings(
        [FromBody] ContractExpirationRecipientSettings request, CancellationToken ct)
    {
        if (!await CanEditNotificationSettingsAsync(ct)) return Forbid();
        if (!ContractExpirationRecipientSettings.TryNormalize(request, out var settings, out var error))
            return BadRequest(new { message = error });
        if (settings.RoleIds.Length > 0 && await _context.Roles.CountAsync(role => settings.RoleIds.Contains(role.Id), ct) != settings.RoleIds.Length)
            return BadRequest(new { message = "Одна из выбранных ролей больше не существует." });
        var userIds = settings.UserIds.Concat(settings.DepartmentManagers.Select(item => item.ManagerUserId)).Distinct().ToArray();
        if (userIds.Length > 0 && await _context.Users.CountAsync(user => userIds.Contains(user.Id)
            && user.IsActive && user.IsApproved && user.Role != null, ct) != userIds.Length)
            return BadRequest(new { message = "Один из выбранных сотрудников больше недоступен." });
        var departmentIds = settings.DepartmentManagers.Select(item => item.DepartmentId).ToArray();
        if (departmentIds.Length > 0 && await _context.Departments.CountAsync(department => departmentIds.Contains(department.Id)
            && department.IsActive, ct) != departmentIds.Length)
            return BadRequest(new { message = "Одно из выбранных подразделений больше недоступно." });
        var stored = await _context.SystemSettings.FirstOrDefaultAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey, ct);
        if (!await CanEditNotificationSettingsAsync(ct)) return Forbid();
        var now = DateTime.UtcNow;
        var raw = ContractExpirationRecipientSettings.Serialize(settings);
        if (stored is null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = ContractExpirationRecipientSettings.SettingKey,
                Value = raw,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            stored.Value = raw;
            stored.UpdatedAt = now;
        }
        await _context.SaveChangesAsync(ct);
        return Ok(await MapContractExpirationSettingsAsync(settings, ct));
    }

    [HttpGet("contract-expiration/options")]
    public async Task<ActionResult<ContractExpirationRecipientOptionsPageDto>> GetContractExpirationOptions(
        [FromQuery] string kind, [FromQuery] string? search = null, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (!await CanViewNotificationSettingsAsync(ct)) return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var term = search?.Trim();
        IQueryable<ContractExpirationRecipientOptionDto> query;
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "roles":
                query = _context.Roles.AsNoTracking().Select(role => new ContractExpirationRecipientOptionDto
                {
                    Id = role.Id,
                    Name = role.Name,
                    IsAvailable = true
                });
                break;
            case "users":
                query = _context.Users.AsNoTracking().Where(user => user.IsActive && user.IsApproved && user.Role != null)
                    .Select(user => new ContractExpirationRecipientOptionDto
                    {
                        Id = user.Id,
                        Name = user.FullName == string.Empty ? "Сотрудник №" + user.Id : user.FullName,
                        IsAvailable = true,
                        DepartmentId = user.DepartmentId,
                        DepartmentName = user.DepartmentDictionary != null ? user.DepartmentDictionary.Name : user.Department,
                        RoleId = user.RoleId,
                        RoleName = user.Role!.Name
                    });
                break;
            case "departments":
                query = _context.Departments.AsNoTracking().Where(department => department.IsActive)
                    .Select(department => new ContractExpirationRecipientOptionDto
                    {
                        Id = department.Id,
                        Name = department.Name,
                        IsAvailable = true
                    });
                break;
            default:
                return BadRequest(new { message = "Выберите справочник roles, users или departments." });
        }
        if (!string.IsNullOrEmpty(term))
        {
            if (_context.Database.IsNpgsql())
            {
                var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                query = query.Where(item => EF.Functions.ILike(item.Name, pattern, "\\"));
            }
            else
            {
                var matching = 0;
                var offset = (long)(page - 1) * pageSize;
                var pageItems = new List<ContractExpirationRecipientOptionDto>(pageSize);
                // Match Unicode in managed code, retaining only one output page.
                await foreach (var option in query.OrderBy(item => item.Name).ThenBy(item => item.Id)
                    .AsAsyncEnumerable().WithCancellation(ct))
                {
                    if (!option.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                    if (matching >= offset && pageItems.Count < pageSize) pageItems.Add(option);
                    matching++;
                }
                return Ok(new ContractExpirationRecipientOptionsPageDto
                {
                    Items = pageItems.ToArray(),
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = matching
                });
            }
        }
        var total = await query.CountAsync(ct);
        var skip = (long)(page - 1) * pageSize;
        ContractExpirationRecipientOptionDto[] items = skip > int.MaxValue ? [] : await query.OrderBy(item => item.Name).ThenBy(item => item.Id)
            .Skip((int)skip).Take(pageSize).ToArrayAsync(ct);
        return Ok(new ContractExpirationRecipientOptionsPageDto
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        });
    }

    private async Task<ContractExpirationRecipientSettingsDto> MapContractExpirationSettingsAsync(
        ContractExpirationRecipientSettings settings, CancellationToken ct)
    {
        var roles = settings.RoleIds.Length == 0 ? new Dictionary<int, ContractExpirationRecipientOptionDto>()
            : await _context.Roles.AsNoTracking().Where(role => settings.RoleIds.Contains(role.Id))
                .Select(role => new ContractExpirationRecipientOptionDto { Id = role.Id, Name = role.Name, IsAvailable = true })
                .ToDictionaryAsync(item => item.Id, ct);
        var selectedUserIds = settings.UserIds.Concat(settings.DepartmentManagers.Select(item => item.ManagerUserId)).Distinct().OrderBy(id => id).ToArray();
        var users = selectedUserIds.Length == 0 ? new Dictionary<int, ContractExpirationRecipientOptionDto>()
            : await _context.Users.AsNoTracking().Where(user => selectedUserIds.Contains(user.Id))
                .Select(user => new ContractExpirationRecipientOptionDto
                {
                    Id = user.Id,
                    Name = user.FullName == string.Empty ? "Сотрудник №" + user.Id : user.FullName,
                    IsAvailable = user.IsActive && user.IsApproved && user.Role != null,
                    DepartmentId = user.DepartmentId,
                    DepartmentName = user.DepartmentDictionary != null ? user.DepartmentDictionary.Name : user.Department,
                    RoleId = user.Role == null ? null : user.RoleId,
                    RoleName = user.Role == null ? null : user.Role.Name
                }).ToDictionaryAsync(item => item.Id, ct);
        var selectedDepartmentIds = settings.DepartmentManagers.Select(item => item.DepartmentId).OrderBy(id => id).ToArray();
        var departments = selectedDepartmentIds.Length == 0 ? new Dictionary<int, ContractExpirationRecipientOptionDto>()
            : await _context.Departments.AsNoTracking().Where(department => selectedDepartmentIds.Contains(department.Id))
                .Select(department => new ContractExpirationRecipientOptionDto
                {
                    Id = department.Id,
                    Name = department.Name,
                    IsAvailable = department.IsActive
                }).ToDictionaryAsync(item => item.Id, ct);
        return new ContractExpirationRecipientSettingsDto
        {
            DepartmentManagersEnabled = settings.DepartmentManagersEnabled,
            RoleIds = settings.RoleIds,
            UserIds = settings.UserIds,
            DepartmentManagers = settings.DepartmentManagers,
            SelectedRoles = settings.RoleIds.Select(id => roles.GetValueOrDefault(id)
                ?? new ContractExpirationRecipientOptionDto { Id = id, Name = $"Роль №{id}" }).ToArray(),
            SelectedUsers = selectedUserIds.Select(id => users.GetValueOrDefault(id)
                ?? new ContractExpirationRecipientOptionDto { Id = id, Name = $"Сотрудник №{id}" }).ToArray(),
            SelectedDepartments = selectedDepartmentIds.Select(id => departments.GetValueOrDefault(id)
                ?? new ContractExpirationRecipientOptionDto { Id = id, Name = $"Подразделение №{id}" }).ToArray()
        };
    }
}

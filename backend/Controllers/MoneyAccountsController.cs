using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-accounts")]
[Authorize]
public class MoneyAccountsController(PaymentContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MoneyAccount>>> Get(
        [FromQuery] MoneyOperationSource? source = null,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default,
        [FromQuery] int? companyId = null)
    {
        var access = await BankStatementImportAccess.ResolveAsync(db, User, ct);
        if (access is null || !access.Permission.CanView) return Forbid();
        var query = ReadScope(access);
        if (source.HasValue) query = query.Where(account => account.Source == source.Value);
        if (activeOnly)
        {
            var available = UsableAccounts(db.MoneyAccounts.AsNoTracking());
            query = query.Where(account => available.Any(item => item.Id == account.Id));
        }
        if (companyId.HasValue) query = query.Where(account => account.CompanyId == companyId);
        return Ok(await Project(query).OrderBy(account => account.CompanyId).ThenBy(account => account.Source)
            .ThenByDescending(account => account.IsDefault).ThenBy(account => account.Name).ThenBy(account => account.Id).ToListAsync(ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<MoneyAccount>> GetById(int id, CancellationToken ct)
    {
        var access = await BankStatementImportAccess.ResolveAsync(db, User, ct);
        if (access is null || !access.Permission.CanView) return Forbid();
        var account = await Project(ReadScope(access).Where(item => item.Id == id)).SingleOrDefaultAsync(ct);
        return account is null ? NotFound() : Ok(account);
    }

    private IQueryable<MoneyAccount> ReadScope(BankStatementImportAccessContext access)
    {
        var query = BankStatementImportAccess.ScopeAccounts(db.MoneyAccounts.AsNoTracking(), access, includeUnknownForAdmin: true);
        if (access.Visibility.IsAdmin) return query;
        return BankStatementImportAccess.CoherentAccounts(db, query);
    }

    private IQueryable<MoneyAccount> Project(IQueryable<MoneyAccount> query)
    {
        var available = UsableAccounts(db.MoneyAccounts.AsNoTracking());
        var editableHistory = BankStatementImportAccess.ActiveAccountState(
            BankStatementImportAccess.CoherentAccounts(db, db.MoneyAccounts.AsNoTracking()));
        return query.Select(account => new MoneyAccount
        {
            Id = account.Id,
            CompanyId = account.CompanyId,
            CompanyName = account.Company == null ? null : account.Company.ShortName,
            CanUseForNewOperations = available.Any(item => item.Id == account.Id),
            CanEditExistingOperations = editableHistory.Any(item => item.Id == account.Id),
            Name = account.Name,
            Source = account.Source,
            Currency = account.Currency,
            BankName = account.BankName,
            BankAccountNumber = account.BankAccountNumber,
            BankBik = account.BankBik,
            BankConnectionAccountId = account.BankConnectionAccountId,
            IsActive = account.IsActive,
            IsDefault = account.IsDefault,
            CreatedAt = account.CreatedAt,
            UpdatedAt = account.UpdatedAt,
        });
    }

    [HttpPost]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MoneyAccount>> Create([FromBody] MoneyAccount model, CancellationToken ct)
    {
        if (await AdminAccessAsync(ct) is not { } access) return Forbid();
        var account = Normalize(model);
        if (BasicError(account) is { } basic) return BadRequest(basic);
        if (await ValidateOriginAsync(account, access, null, ct) is { } invalid) return invalid;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await LockCompaniesAsync([account.CompanyId!.Value], ct)
                || !await LockBanksAsync([account.BankConnectionAccountId], access, ct)) return BadRequest("Компания или банковский счёт изменены; обновите список.");
            if (await AdminAccessAsync(ct) is not { } fresh) return Forbid();
            if (await ValidateOriginAsync(account, fresh, null, ct) is { } changed) return changed;
            if (await HasDuplicateNameAsync(account, null, ct)) return Conflict("Счёт с таким названием уже существует у выбранной компании и источника.");
            account.IsDefault = account.IsActive && (account.IsDefault || !await UsableAccounts(db.MoneyAccounts)
                .AnyAsync(item => item.CompanyId == account.CompanyId && item.Source == account.Source, ct));
            account.CreatedAt = DateTime.UtcNow;
            db.MoneyAccounts.Add(account);
            await EnsureSingleDefaultAsync(account, ct);
            await db.SaveChangesAsync(ct);
            var response = await Project(db.MoneyAccounts.AsNoTracking().Where(item => item.Id == account.Id)).SingleAsync(ct);
            await transaction.CommitAsync(ct);
            return CreatedAtAction(nameof(GetById), new { id = account.Id }, response);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            db.ChangeTracker.Clear();
            return Conflict("Счёт с таким названием или банковской привязкой уже существует. Обновите список.");
        }
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MoneyAccount>> Update(int id, [FromBody] MoneyAccount model, CancellationToken ct)
    {
        if (await AdminAccessAsync(ct) is not { } access) return Forbid();
        var previous = await BankStatementImportAccess.ScopeAccounts(db.MoneyAccounts.AsNoTracking(), access, includeUnknownForAdmin: true).SingleOrDefaultAsync(item => item.Id == id, ct);
        if (previous is null) return NotFound();
        if (!previous.CompanyId.HasValue) return Conflict("Компания исторического счёта не указана. До уточнения принадлежности доступен только просмотр.");
        var account = Normalize(model);
        if (BasicError(account) is { } basic) return BadRequest(basic);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!access.OwnCompanyIds.Contains(account.CompanyId!.Value)
                || !await LockCompaniesAsync([previous.CompanyId.Value, account.CompanyId.Value], ct)) return BadRequest("Выберите собственную компанию.");
            if (!await LockBanksAsync([previous.BankConnectionAccountId, account.BankConnectionAccountId], access, ct)) return BadRequest("Банковская привязка счёта требует уточнения.");
            if (await AdminAccessAsync(ct) is not { } fresh) return Forbid();
            if (!fresh.OwnCompanyIds.Contains(previous.CompanyId.Value)) return NotFound();
            if (!await LockAccountAsync(previous, ct)) return Conflict("Счёт изменён другим запросом. Обновите список.");
            var tracked = db.MoneyAccounts.Local.FirstOrDefault(item => item.Id == id);
            if (tracked is not null) await db.Entry(tracked).ReloadAsync(ct);
            var entity = await db.MoneyAccounts.SingleAsync(item => item.Id == id, ct);
            if (IdentityChanged(entity, account) && await IsReferencedAsync(entity, ct))
                return Conflict("У используемого счёта нельзя менять компанию, источник, валюту или банковские реквизиты. Создайте отдельный счёт; прежний можно перенести в архив.");
            if (await ValidateOriginAsync(account, fresh, id, ct) is { } invalid) return invalid;
            if (await HasDuplicateNameAsync(account, id, ct)) return Conflict("Счёт с таким названием уже существует у выбранной компании и источника.");
            var oldCompany = entity.CompanyId!.Value;
            var oldSource = entity.Source;
            entity.Name = account.Name;
            entity.CompanyId = account.CompanyId;
            entity.CompanyName = account.CompanyName;
            entity.Source = account.Source;
            entity.Currency = account.Currency;
            entity.BankName = account.BankName;
            entity.BankAccountNumber = account.BankAccountNumber;
            entity.BankBik = account.BankBik;
            entity.BankConnectionAccountId = account.BankConnectionAccountId;
            entity.IsActive = account.IsActive;
            entity.IsDefault = account.IsActive && account.IsDefault;
            entity.UpdatedAt = DateTime.UtcNow;
            await EnsureSingleDefaultAsync(entity, ct);
            await db.SaveChangesAsync(ct);
            foreach (var scope in new[] { (Company: oldCompany, Source: oldSource), (Company: entity.CompanyId!.Value, Source: entity.Source) }.Distinct())
                await EnsureDefaultExistsAsync(scope.Company, scope.Source, ct);
            var response = await Project(db.MoneyAccounts.AsNoTracking().Where(item => item.Id == entity.Id)).SingleAsync(ct);
            await transaction.CommitAsync(ct);
            return Ok(response);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            db.ChangeTracker.Clear();
            return Conflict("Счёт с таким названием или банковской привязкой уже существует. Обновите список.");
        }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await AdminAccessAsync(ct) is not { } access) return Forbid();
        var previous = await BankStatementImportAccess.ScopeAccounts(db.MoneyAccounts.AsNoTracking(), access, includeUnknownForAdmin: true).SingleOrDefaultAsync(item => item.Id == id, ct);
        if (previous is null) return NotFound();
        if (!previous.CompanyId.HasValue) return Conflict("Компания исторического счёта не указана. До уточнения принадлежности доступен только просмотр.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockCompaniesAsync([previous.CompanyId.Value], ct)
            || !await LockBanksAsync([previous.BankConnectionAccountId], access, ct)) return BadRequest("Компания или банковский счёт изменены; обновите список.");
        if (await AdminAccessAsync(ct) is not { } fresh) return Forbid();
        if (!fresh.OwnCompanyIds.Contains(previous.CompanyId.Value)) return NotFound();
        if (!await LockAccountAsync(previous, ct)) return Conflict("Счёт изменён другим запросом. Обновите список.");
        var tracked = db.MoneyAccounts.Local.FirstOrDefault(item => item.Id == id);
        if (tracked is not null) await db.Entry(tracked).ReloadAsync(ct);
        var entity = await db.MoneyAccounts.SingleAsync(item => item.Id == id, ct);
        if (await IsReferencedAsync(entity, ct))
        {
            entity.IsActive = false;
            entity.IsDefault = false;
            entity.UpdatedAt = DateTime.UtcNow;
        }
        else db.MoneyAccounts.Remove(entity);
        await db.SaveChangesAsync(ct);
        await EnsureDefaultExistsAsync(previous.CompanyId.Value, previous.Source, ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    private async Task<BankStatementImportAccessContext?> AdminAccessAsync(CancellationToken ct)
        => await BankStatementImportAccess.ResolveAsync(db, User, ct) is { Visibility.IsAdmin: true } access ? access : null;

    private static string? BasicError(MoneyAccount account)
    {
        if (!account.CompanyId.HasValue || account.CompanyId <= 0) return "Выберите собственную компанию для денежного счёта.";
        if (string.IsNullOrWhiteSpace(account.Name) || account.Name.Length > 200) return "Укажите название счёта длиной до 200 символов.";
        if (!Enum.IsDefined(account.Source)) return "Выберите допустимый источник счёта.";
        if (account.Currency.Length > 16 || account.BankName?.Length > 200 || account.BankAccountNumber?.Length > 64 || account.BankBik?.Length > 16) return "Проверьте длину банковских реквизитов и валюты.";
        if (account.BankConnectionAccountId is <= 0) return "Укажите допустимую банковскую привязку.";
        return null;
    }

    private async Task<ActionResult?> ValidateOriginAsync(MoneyAccount account, BankStatementImportAccessContext access, int? excludeId, CancellationToken ct)
    {
        if (!account.CompanyId.HasValue || !access.OwnCompanyIds.Contains(account.CompanyId.Value)) return BadRequest("Компания не входит в реестр собственных.");
        var company = await db.LegalEntities.AsNoTracking().Where(item => item.Id == account.CompanyId).Select(item => new { item.ShortName, item.ProfileStatus }).SingleOrDefaultAsync(ct);
        if (company is null) return BadRequest("Собственная компания недоступна.");
        if (account.IsActive && company.ProfileStatus != CompanyProfileStatuses.Active) return BadRequest("Для активного счёта нужен действующий профиль компании.");
        account.CompanyName = company.ShortName;
        if (account.BankConnectionAccountId.HasValue)
        {
            var bank = await db.BankConnectionAccounts.AsNoTracking().Where(item => item.Id == account.BankConnectionAccountId && item.BankConnection.LegalEntityId == account.CompanyId)
                .Select(item => new { item.AccountNumber, item.Bik, item.Currency, item.Status, item.IsHidden }).SingleOrDefaultAsync(ct);
            if (bank is null || account.Source != MoneyOperationSource.Bank || bank.AccountNumber != account.BankAccountNumber
                || (bank.Bik ?? "") != (account.BankBik ?? "") || bank.Currency != account.Currency)
                return BadRequest("Банковская привязка не соответствует компании и реквизитам счёта.");
            if (account.IsActive && (bank.IsHidden || !BankAccountStatuses.AllowsNewDocuments(bank.Status))) return BadRequest("Банковский счёт недоступен для новых операций.");
            if (await db.MoneyAccounts.AnyAsync(item => item.Id != excludeId && item.BankConnectionAccountId == account.BankConnectionAccountId, ct))
                return Conflict("Этот банковский счёт уже связан с денежным счётом.");
        }
        if (account.Source == MoneyOperationSource.Bank && account.BankAccountNumber is not null
            && await db.MoneyAccounts.AnyAsync(item => item.Id != excludeId && item.CompanyId == account.CompanyId
                && item.Source == MoneyOperationSource.Bank && item.BankAccountNumber == account.BankAccountNumber
                && item.Currency == account.Currency && ((item.BankBik ?? "") == (account.BankBik ?? "") || item.BankBik == null || account.BankBik == null), ct))
            return Conflict("Счёт с этими банковскими реквизитами уже есть у компании. Уточните привязку вместо создания дубликата.");
        return null;
    }

    private async Task<bool> LockCompaniesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        foreach (var id in ids.Distinct().Order())
            if (await db.LegalEntities.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ShortName, item => item.ShortName), ct) != 1) return false;
        return true;
    }

    private async Task<bool> LockBanksAsync(IEnumerable<int?> ids, BankStatementImportAccessContext access, CancellationToken ct)
    {
        foreach (var id in ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().Order())
            if (await db.BankConnectionAccounts.Where(item => item.Id == id && access.OwnCompanyIds.Contains(item.BankConnection.LegalEntityId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExternalAccountId, item => item.ExternalAccountId), ct) != 1) return false;
        return true;
    }

    private async Task<bool> LockAccountAsync(MoneyAccount previous, CancellationToken ct)
        => await db.MoneyAccounts.Where(item => item.Id == previous.Id && item.CompanyId == previous.CompanyId && item.Source == previous.Source
            && item.BankConnectionAccountId == previous.BankConnectionAccountId && item.BankAccountNumber == previous.BankAccountNumber && item.Currency == previous.Currency)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Name, item => item.Name), ct) == 1;

    private async Task<bool> IsReferencedAsync(MoneyAccount account, CancellationToken ct)
        => await db.MoneyOperations.IgnoreQueryFilters().AnyAsync(item => item.MoneyAccountId == account.Id, ct)
            || await db.MoneyBalanceReconciliationResults.IgnoreQueryFilters().AnyAsync(item => item.MoneyAccountId == account.Id, ct)
            || await db.BankStatementImportBatches.IgnoreQueryFilters().AnyAsync(item => item.MoneyAccountId == account.Id, ct)
            || account.BankConnectionAccountId.HasValue && await db.BankStatementOperations.AnyAsync(item => item.BankConnectionAccountId == account.BankConnectionAccountId, ct);

    private static bool IdentityChanged(MoneyAccount old, MoneyAccount next)
        => old.CompanyId != next.CompanyId || old.Source != next.Source || old.Currency != next.Currency
            || old.BankAccountNumber != next.BankAccountNumber || old.BankBik != next.BankBik || old.BankConnectionAccountId != next.BankConnectionAccountId;

    private async Task<bool> HasDuplicateNameAsync(MoneyAccount account, int? excludeId, CancellationToken ct)
    {
        // Ordinal comparison also works for Cyrillic on SQLite; stream only one indexed company/source scope.
        await foreach (var name in db.MoneyAccounts.AsNoTracking().Where(item => item.Id != excludeId && item.CompanyId == account.CompanyId && item.Source == account.Source)
            .Select(item => item.Name).AsAsyncEnumerable().WithCancellation(ct))
            if (string.Equals(name, account.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task EnsureSingleDefaultAsync(MoneyAccount account, CancellationToken ct)
    {
        if (!account.IsDefault) return;
        var now = DateTime.UtcNow;
        await db.MoneyAccounts.Where(item => item.Id != account.Id && item.CompanyId == account.CompanyId && item.Source == account.Source && item.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false).SetProperty(item => item.UpdatedAt, now), ct);
        foreach (var item in db.MoneyAccounts.Local.Where(item => item.Id != account.Id && item.CompanyId == account.CompanyId && item.Source == account.Source && item.IsDefault))
        { item.IsDefault = false; item.UpdatedAt = now; }
    }

    private async Task EnsureDefaultExistsAsync(int companyId, MoneyOperationSource source, CancellationToken ct)
    {
        var scope = db.MoneyAccounts.Where(item => item.CompanyId == companyId && item.Source == source);
        var id = await UsableAccounts(scope).OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name)
            .ThenBy(item => item.Id).Select(item => (int?)item.Id).FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        await scope.Where(item => item.IsDefault && item.Id != id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.IsDefault, false).SetProperty(item => item.UpdatedAt, now), ct);
        foreach (var other in db.MoneyAccounts.Local.Where(item => item.CompanyId == companyId && item.Source == source && item.IsDefault && item.Id != id))
        { other.IsDefault = false; other.UpdatedAt = now; }
        if (!id.HasValue) return;
        await scope.Where(item => item.Id == id && !item.IsDefault).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.IsDefault, true).SetProperty(item => item.UpdatedAt, now), ct);
        var tracked = db.MoneyAccounts.Local.FirstOrDefault(item => item.Id == id);
        if (tracked is not null) { tracked.IsDefault = true; tracked.UpdatedAt = now; }
    }

    private IQueryable<MoneyAccount> UsableAccounts(IQueryable<MoneyAccount> query)
        => BankStatementImportAccess.CoherentAccounts(db, query, forNewOperations: true);

    private static bool IsUniqueViolation(DbUpdateException error) => error.GetBaseException() is
        Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 }
        or Npgsql.PostgresException { SqlState: "23505" };

    private static MoneyAccount Normalize(MoneyAccount model) => new()
    {
        CompanyId = model.CompanyId,
        BankConnectionAccountId = model.BankConnectionAccountId,
        Name = (model.Name ?? string.Empty).Trim(),
        Source = model.Source,
        Currency = string.IsNullOrWhiteSpace(model.Currency) ? "RUB" : model.Currency.Trim().ToUpperInvariant(),
        BankName = string.IsNullOrWhiteSpace(model.BankName) ? null : model.BankName.Trim(),
        BankAccountNumber = string.IsNullOrWhiteSpace(model.BankAccountNumber) ? null : model.BankAccountNumber.Trim(),
        BankBik = string.IsNullOrWhiteSpace(model.BankBik) ? null : model.BankBik.Trim(),
        IsActive = model.IsActive,
        IsDefault = model.IsActive && model.IsDefault,
    };
}

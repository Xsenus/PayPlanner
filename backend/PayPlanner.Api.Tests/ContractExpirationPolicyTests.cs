using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ContractExpirationPolicyTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 7)]
    [InlineData(7, 7)]
    [InlineData(8, 14)]
    [InlineData(14, 14)]
    [InlineData(15, 30)]
    [InlineData(30, 30)]
    [InlineData(31, null)]
    public void ContractExpiration_LatestWindowAndOldSevenDayKeyRemainCanonical(int days, int? expected)
    {
        Assert.Equal(expected, ContractExpirationNotificationPolicy.StageForDays(days));
        var date = new DateTime(2030, 2, 3);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal("contract:17:expiring:23:2030-02-03", ContractExpirationNotificationPolicy.EventKey(17, 23, date, 7));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.NotEqual(ContractExpirationNotificationPolicy.EventKey(17, 23, date, 30), ContractExpirationNotificationPolicy.EventKey(17, 23, date, 14));
        Assert.True(ContractExpirationNotificationPolicy.TryReadSource(ContractExpirationNotificationPolicy.EventKey(17, 23, date, 14), out var source));
        Assert.Equal(14, source!.Stage);
    }

    [Theory]
    [InlineData("contract:017:expiring:23:2030-02-03")]
    [InlineData("contract:17:expiring:23:2030-02-03:stage:7")]
    [InlineData("contract:17:expiring:23:2030-02-30")]
    [InlineData("contract:17:expiring:23:2030-02-03:stage:1")]
    public void ContractExpiration_InvalidSourceCannotBecomeATrustedStage(string key)
        => Assert.False(ContractExpirationNotificationPolicy.TryReadSource(key, out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_ProducerCombinesThreeManagerSourcesAndRetainsResponsibleWithoutDuplicates(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = "Юристы" };
        scope.Db.Departments.Add(department);
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentId = department.Id;
        var rolePeer = AddUser(scope.Db, scope.Source.Other.Role!, "role-peer");
        var direct = AddUser(scope.Db, AllowedRole("direct"), "direct");
        var deniedRole = new Role { Name = "denied", Permissions = { new RolePermission { Section = RolePermissionCatalog.Contracts, CanView = false } } };
        var denied = AddUser(scope.Db, deniedRole, "denied");
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new()
        {
            DepartmentManagersEnabled = true,
            DepartmentManagers = [new() { DepartmentId = department.Id, ManagerUserId = scope.Source.Other.Id }],
            RoleIds = [scope.Source.Other.RoleId],
            UserIds = [scope.Source.Other.Id, direct.Id, denied.Id]
        });
        var recorder = DispatchProxy.Create<INotificationService, ExpiryRecorder>();
        var calls = (ExpiryRecorder)(object)recorder;
        using var provider = Provider(scope.Db, recorder);
        var service = new ContractExpirationNotificationService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContractExpirationNotificationService>.Instance, clock);

        Assert.Equal(1, await service.RunOnceAsync());

        var call = Assert.Single(calls.Calls);
        Assert.Equal(contract.Id, call.ContractId);
        Assert.Equal(20, call.Days);
        Assert.Equal(new[] { scope.Source.Actor.Id, scope.Source.Other.Id, rolePeer.Id, direct.Id }.Order(), call.Recipients.Order());
        Assert.Equal(call.Recipients.Count, call.Recipients.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_ProducerUsesEachRecipientDateAndOnlyItsCurrentWindow(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock(new DateTimeOffset(2030, 1, 1, 23, 30, 0, TimeSpan.Zero));
        var contract = await PrepareAsync(scope, clock);
        contract.ValidUntil = new DateTime(2030, 1, 16);
        (await scope.Db.UserSettings.SingleAsync(item => item.UserId == scope.Source.Other.Id && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)).Value = "Europe/Moscow";
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new() { UserIds = [scope.Source.Other.Id] });
        var recorder = DispatchProxy.Create<INotificationService, ExpiryRecorder>();
        var calls = (ExpiryRecorder)(object)recorder;
        using var provider = Provider(scope.Db, recorder);
        var service = new ContractExpirationNotificationService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContractExpirationNotificationService>.Instance, clock);

        Assert.Equal(1, await service.RunOnceAsync());

        Assert.Equal(2, calls.Calls.Count);
        Assert.Equal(scope.Source.Actor.Id, Assert.Single(Assert.Single(calls.Calls, call => call.Days == 15).Recipients));
        Assert.Equal(scope.Source.Other.Id, Assert.Single(Assert.Single(calls.Calls, call => call.Days == 14).Recipients));
    }

    [Theory]
    [InlineData(false, "role")]
    [InlineData(true, "role")]
    [InlineData(false, "user")]
    [InlineData(true, "user")]
    [InlineData(false, "department")]
    [InlineData(true, "department")]
    [InlineData(false, "mapping")]
    [InlineData(true, "mapping")]
    [InlineData(false, "department-inactive")]
    [InlineData(true, "department-inactive")]
    [InlineData(false, "expiry")]
    [InlineData(true, "expiry")]
    [InlineData(false, "status")]
    [InlineData(true, "status")]
    [InlineData(false, "acl")]
    [InlineData(true, "acl")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "stage")]
    [InlineData(true, "stage")]
    public async Task ContractExpiration_DeliveryRechecksCurrentSelectionSourceAndAccess(bool postgres, string change)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = "Юристы" };
        scope.Db.Departments.Add(department);
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentId = department.Id;
        await scope.Db.SaveChangesAsync();
        var byDepartment = change is "department" or "mapping" or "department-inactive";
        var settings = new ContractExpirationRecipientSettings
        {
            DepartmentManagersEnabled = byDepartment,
            DepartmentManagers = byDepartment ? [new() { DepartmentId = department.Id, ManagerUserId = scope.Source.Other.Id }] : [],
            RoleIds = change == "role" ? [scope.Source.Other.RoleId] : [],
            UserIds = byDepartment || change == "role" ? [] : [scope.Source.Other.Id]
        };
        await StoreAsync(scope.Db, settings);
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, scope.Source.Other.Id, contract.ValidUntil!.Value, 30);
        var policy = new ContractExpirationNotificationPolicy(scope.Db, clock);
        Assert.True(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        if (change == "role") scope.Source.Other.Role = AllowedRole("unselected");
        if (change == "user") await StoreAsync(scope.Db, new());
        if (change == "mapping") { settings.DepartmentManagers.Clear(); await StoreAsync(scope.Db, settings); }
        if (change == "department") scope.Source.Actor.DepartmentDictionary = new DepartmentDictionaryItem { Name = "Другой отдел" };
        if (change == "department-inactive") department.IsActive = false;
        if (change == "expiry") contract.ValidUntil = contract.ValidUntil!.Value.AddDays(1);
        if (change == "status") contract.Status = ContractStatus.Terminated;
        if (change == "acl") (await scope.Db.RolePermissions.SingleAsync(item => item.RoleId == scope.Source.Other.RoleId && item.Section == RolePermissionCatalog.Contracts)).CanView = false;
        if (change == "inactive") scope.Source.Other.IsActive = false;
        if (change == "stage") clock.Advance(TimeSpan.FromDays(6));
        await scope.Db.SaveChangesAsync();

        Assert.False(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        Assert.Empty(await scope.Db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "casefold")]
    [InlineData(true, "casefold")]
    [InlineData(false, "ambiguous")]
    [InlineData(true, "ambiguous")]
    [InlineData(false, "blank")]
    [InlineData(true, "blank")]
    public async Task ContractExpiration_LegacyDepartmentNamesHandleUnicodeAmbiguityAndBlankAssignments(bool postgres, string scenario)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = scenario == "blank" ? "" : "Юристы" };
        scope.Db.Departments.Add(department);
        if (scenario == "ambiguous") scope.Db.Departments.Add(new DepartmentDictionaryItem { Name = "ЮРИСТЫ" });
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentDictionary = null;
        scope.Source.Actor.DepartmentId = null;
        scope.Source.Actor.Department = scenario == "blank" ? null : "юРиСтЫ";
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new()
        {
            DepartmentManagersEnabled = true,
            DepartmentManagers = [new() { DepartmentId = department.Id, ManagerUserId = scope.Source.Other.Id }]
        });
        var policy = new ContractExpirationNotificationPolicy(scope.Db, clock);
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, scope.Source.Other.Id, contract.ValidUntil!.Value, 30);

        Assert.Equal(scenario == "casefold", await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        // The stored department ID remains authoritative even when its label is blank or ambiguous.
        scope.Source.Actor.DepartmentId = department.Id;
        await scope.Db.SaveChangesAsync();
        Assert.True(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_ProducerPagesContractsAndRoleRecipientsWithBoundedQueries(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        for (var index = 0; index < 128; index++) scope.Db.Contracts.Add(new Contract
        {
            Number = "PAGE-" + index,
            Date = contract.Date,
            ValidUntil = contract.ValidUntil,
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { ClientId = scope.Source.OwnClient.Id } }
        });
        for (var index = 0; index < 513; index++) AddUser(scope.Db, scope.Source.Other.Role!, "paged-" + index);
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new() { RoleIds = [scope.Source.Other.RoleId] });
        var recorder = DispatchProxy.Create<INotificationService, ExpiryRecorder>();
        var calls = (ExpiryRecorder)(object)recorder;
        using var provider = Provider(scope.Db, recorder);
        var service = new ContractExpirationNotificationService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContractExpirationNotificationService>.Instance, clock);
        scope.Source.Commands.Clear();

        Assert.Equal(129, await service.RunOnceAsync());

        Assert.Equal(13, scope.Source.Commands.Count);
        Assert.All(calls.Calls, call => Assert.InRange(call.Recipients.Count, 1, 512));
        Assert.Equal(129, calls.Calls.Select(call => call.ContractId).Distinct().Count());
        foreach (var group in calls.Calls.GroupBy(call => call.ContractId))
        {
            var recipients = group.SelectMany(call => call.Recipients).ToArray();
            Assert.Equal(515, recipients.Length);
            Assert.Equal(515, recipients.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_TypedDepartmentUsesOneSourceProofWithoutDictionaryReads(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = "Юристы" };
        scope.Db.Departments.AddRange(department, new DepartmentDictionaryItem { Name = "ЮРИСТЫ" });
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentId = department.Id;
        scope.Source.Actor.Department = "Другой отдел";
        await scope.Db.SaveChangesAsync();
        var settings = new ContractExpirationRecipientSettings
        {
            DepartmentManagersEnabled = true,
            DepartmentManagers = [new() { DepartmentId = department.Id, ManagerUserId = scope.Source.Other.Id }]
        };
        await StoreAsync(scope.Db, settings);
        var policy = new ContractExpirationNotificationPolicy(scope.Db, clock);
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, scope.Source.Other.Id, contract.ValidUntil!.Value, 30);
        var departmentTable = "FROM \"" + scope.Db.Model.FindEntityType(typeof(DepartmentDictionaryItem))!.GetTableName() + "\"";
        scope.Source.Commands.Clear();

        Assert.True(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));

        var commands = scope.Source.Commands.ToArray();
        var proof = Assert.Single(commands, command => command.Contains("FROM \"Contracts\"", StringComparison.Ordinal)
            && command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains("\"ConfigurationCurrent\"", StringComparison.Ordinal));
        Assert.Contains(departmentTable, proof, StringComparison.Ordinal);
        Assert.DoesNotContain(commands.Where(command => command != proof), command => command.Contains(departmentTable, StringComparison.Ordinal));
        department.IsActive = false;
        await scope.Db.SaveChangesAsync();
        Assert.False(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        department.IsActive = true;
        await scope.Db.SaveChangesAsync();
        Assert.True(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        settings.DepartmentManagers.Clear();
        await StoreAsync(scope.Db, settings);
        Assert.False(await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));
        Assert.Empty(await scope.Db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "legacy")]
    [InlineData(true, "legacy")]
    [InlineData(false, "mixed")]
    [InlineData(true, "mixed")]
    [InlineData(false, "typed-name-only")]
    [InlineData(true, "typed-name-only")]
    public async Task ContractExpiration_DepartmentFallbackUsesOnlyNullIdResponsibles(bool postgres, string scenario)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = "Юристы" };
        var unrelated = new DepartmentDictionaryItem { Name = "Архив" };
        scope.Db.Departments.AddRange(department, unrelated);
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentId = scenario == "legacy" ? null : unrelated.Id;
        scope.Source.Actor.Department = "юРиСтЫ";
        if (scenario == "mixed")
        {
            var legacyUser = AddUser(scope.Db, scope.Source.Other.Role!, "legacy-department-responsible");
            legacyUser.Department = "юРиСтЫ";
            await scope.Db.SaveChangesAsync();
            scope.Source.ForeignClient.ResponsibleUserId = legacyUser.Id;
            contract.ClientContracts.Add(new ClientContract { ClientId = scope.Source.ForeignClient.Id });
        }
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new()
        {
            DepartmentManagersEnabled = true,
            DepartmentManagers = [new() { DepartmentId = department.Id, ManagerUserId = scope.Source.Other.Id }]
        });
        var policy = new ContractExpirationNotificationPolicy(scope.Db, clock);
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, scope.Source.Other.Id, contract.ValidUntil!.Value, 30);
        var departmentTable = "FROM \"" + scope.Db.Model.FindEntityType(typeof(DepartmentDictionaryItem))!.GetTableName() + "\"";
        scope.Source.Commands.Clear();

        Assert.Equal(scenario != "typed-name-only", await policy.CanSendAsync(key, scope.Source.Other.Id, default, contract.Id, true));

        var commands = scope.Source.Commands.ToArray();
        Assert.Single(commands, command => command.Contains("FROM \"Contracts\"", StringComparison.Ordinal)
            && command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains("\"ConfigurationCurrent\"", StringComparison.Ordinal));
        Assert.Equal(scenario == "typed-name-only" ? 1 : 2, commands.Count(command =>
            command.Contains("FROM \"Contracts\"", StringComparison.Ordinal)
            && command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)));
        var dictionaryReads = commands.Where(command => command.Contains(departmentTable, StringComparison.Ordinal)
            && !command.Contains("FROM \"Contracts\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(scenario == "typed-name-only" ? 0 : 2, dictionaryReads.Length);
        Assert.Empty(await scope.Db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "responsible")]
    [InlineData(true, "responsible")]
    [InlineData(false, "role")]
    [InlineData(true, "role")]
    [InlineData(false, "user")]
    [InlineData(true, "user")]
    public async Task ContractExpiration_DepartmentFastPathRetainsOtherRecipientSources(bool postgres, string source)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var clock = new ExpiryClock();
        var contract = await PrepareAsync(scope, clock);
        var department = new DepartmentDictionaryItem { Name = "Юристы" };
        var unrelated = new DepartmentDictionaryItem { Name = "Архив" };
        scope.Db.Departments.AddRange(department, unrelated);
        await scope.Db.SaveChangesAsync();
        scope.Source.Actor.DepartmentId = unrelated.Id;
        var recipientId = source == "responsible" ? scope.Source.Actor.Id : scope.Source.Other.Id;
        await scope.Db.SaveChangesAsync();
        await StoreAsync(scope.Db, new()
        {
            DepartmentManagersEnabled = true,
            DepartmentManagers = [new() { DepartmentId = department.Id, ManagerUserId = recipientId }],
            RoleIds = source == "role" ? [scope.Source.Other.RoleId] : [],
            UserIds = source == "user" ? [scope.Source.Other.Id] : []
        });
        var policy = new ContractExpirationNotificationPolicy(scope.Db, clock);
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, recipientId, contract.ValidUntil!.Value, 30);
        var departmentTable = "FROM \"" + scope.Db.Model.FindEntityType(typeof(DepartmentDictionaryItem))!.GetTableName() + "\"";
        scope.Source.Commands.Clear();

        Assert.True(await policy.CanSendAsync(key, recipientId, default, contract.Id, true));

        var commands = scope.Source.Commands.ToArray();
        var proof = Assert.Single(commands, command => command.Contains("FROM \"Contracts\"", StringComparison.Ordinal)
            && command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains("\"ConfigurationCurrent\"", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Where(command => command != proof), command => command.Contains(departmentTable, StringComparison.Ordinal));
        Assert.Empty(await scope.Db.UserNotifications.ToArrayAsync());
    }

    private static async Task<Contract> PrepareAsync(ActAnnualTestScope scope, ExpiryClock clock)
    {
        scope.Source.Other.Role!.Permissions.Add(new RolePermission
        { Section = RolePermissionCatalog.Contracts, CanView = true, ViewScope = DataVisibilityScope.All });
        scope.Db.UserSettings.AddRange(new UserSetting { UserId = scope.Source.Actor.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "UTC" },
            new UserSetting { UserId = scope.Source.Other.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "UTC" });
        var contract = new Contract
        {
            Number = "EXPIRY",
            Date = clock.GetUtcNow().UtcDateTime.Date,
            ValidUntil = clock.GetUtcNow().UtcDateTime.Date.AddDays(20),
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { ClientId = scope.Source.OwnClient.Id } }
        };
        scope.Db.Contracts.Add(contract);
        await scope.Db.SaveChangesAsync();
        return contract;
    }

    private static Role AllowedRole(string name) => new()
    {
        Name = name,
        Permissions = { new RolePermission { Section = RolePermissionCatalog.Contracts, CanView = true, ViewScope = DataVisibilityScope.All } }
    };

    private static User AddUser(PaymentContext db, Role role, string name)
    {
        var user = new User { Role = role, FullName = name, Email = name + "@example.test", PasswordHash = "synthetic", IsActive = true, IsApproved = true };
        db.Users.Add(user);
        return user;
    }

    private static async Task StoreAsync(PaymentContext db, ContractExpirationRecipientSettings settings)
    {
        var row = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey);
        if (row == null) db.SystemSettings.Add(new SystemSetting { Key = ContractExpirationRecipientSettings.SettingKey, Value = ContractExpirationRecipientSettings.Serialize(settings) });
        else row.Value = ContractExpirationRecipientSettings.Serialize(settings);
        await db.SaveChangesAsync();
    }

    private static ServiceProvider Provider(PaymentContext db, INotificationService recorder)
        => new ServiceCollection().AddSingleton(db).AddSingleton(recorder).BuildServiceProvider();

    private sealed class ExpiryClock(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    public class ExpiryRecorder : DispatchProxy
    {
        public List<ExpiryCall> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(INotificationService.NotifyContractExpiringAsync)) throw new NotSupportedException(targetMethod?.Name);
            Calls.Add(new(((Contract)args![0]!).Id, (int)args[1]!, ((IReadOnlyCollection<int>)args[2]!).ToArray()));
            return Task.CompletedTask;
        }
    }
    public sealed record ExpiryCall(int ContractId, int Days, IReadOnlyList<int> Recipients);
}

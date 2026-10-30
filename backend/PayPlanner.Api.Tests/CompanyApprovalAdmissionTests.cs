using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("promote")]
    [InlineData("settings")]
    [InlineData("default")]
    public async Task CompanyApproval_PostgreSqlRechecksApprovalAfterTransactionAdmission(string operation)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var writer = CompanyCreatePgContext(database.ConnectionString);
        var actor = await CreateUserAsync(writer, "admin");
        var company = await CreateLegalEntityAsync(writer, "Candidate");
        var contact = await SeedCompanyCreationContact(writer, actor);
        var before = await CompanyApprovalMutationSnapshot(writer);
        var settingsVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(CreateController(writer, actor));
        var hook = new CompanyApprovalAdmissionHook(writer, actor);
        await using var db = CompanyCreatePgContext(database.ConnectionString, hook);
        var controller = CreateController(db, actor);
        IActionResult? result = operation switch
        {
            "create" => (await controller.Create(new LegalEntityRequest { ShortName = "Must require approval", Inn = "7700000113" }, default)).Result,
            "promote" => (await CompanyCreationContactsController(db, actor).PromoteExternalContact(contact, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default)).Result,
            "settings" => (await controller.UpdateMainCompanySettings(new UpdateMainCompanySettingsRequest { ExpectedVersion = settingsVersion, CompanyIds = [company], DefaultCompanyId = company }, default)).Result,
            "default" => (await controller.SetCompanyProfileMain(company, default)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        Assert.IsType<ForbidResult>(result); Assert.Equal(1, hook.Revocations);
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(writer));
        Assert.False(await writer.Users.AsNoTracking().Where(user => user.Id == actor).Select(user => user.IsApproved).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("default")]
    public async Task CompanyApproval_OwnSettingsRollBackMetadataAndAssignmentsAfterSaveFailure(string operation)
    {
        var failure = new CompanyCreateSecondSaveFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var actor = await CreateUserAsync(db, "admin");
        var first = await CreateLegalEntityAsync(db, "Current"); var second = await CreateLegalEntityAsync(db, "Candidate");
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{first}]" },
            new SystemSetting { Key = "legalEntities.mainCompanyId", Value = first.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.FirstFile });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor, LegalEntityId = first }); await db.SaveChangesAsync();
        var before = await CompanyApprovalMutationSnapshot(db);
        var controller = CreateController(db, actor);
        var settingsVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(controller);
        failure.Remaining = 1;
        if (operation == "settings")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UpdateMainCompanySettings(new UpdateMainCompanySettingsRequest { ExpectedVersion = settingsVersion, CompanyIds = [second], DefaultCompanyId = second, SellerSnapshotTiming = DocumentSellerSnapshotPolicy.OnCreate }, default));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetCompanyProfileMain(second, default));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
    }

    [Theory]
    [InlineData(CompanyProfileStatuses.Draft)]
    [InlineData(CompanyProfileStatuses.Archived)]
    public async Task CompanyApproval_LegacyDefaultSetterRejectsInactiveCompany(string status)
    {
        await using var db = await CreateContextAsync(); var actor = await CreateUserAsync(db, "admin");
        var company = await CreateLegalEntityAsync(db, "Inactive");
        (await db.LegalEntities.SingleAsync()).ProfileStatus = status; await db.SaveChangesAsync();
        var before = await CompanyApprovalMutationSnapshot(db);
        var response = await CreateController(db, actor).SetCompanyProfileMain(company, default);
        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class CompanyApprovalAdmissionHook(PaymentContext writer, int actor) : DbCommandInterceptor
    {
        public int Revocations { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Revocations == 0 && eventData.Context?.Database.CurrentTransaction is not null && command.CommandText.Contains("\"Users\"", StringComparison.Ordinal))
            {
                Revocations++;
                await writer.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsApproved, false), cancellationToken);
            }
            return result;
        }
    }
}

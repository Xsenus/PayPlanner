using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    private static ContactsController CompanyCreationContactsController(PaymentContext db, int userId)
        => new(db, new NoopFormRequirementsService(), new ConfigurationBuilder().Build())
        { ControllerContext = CreateController(db, userId).ControllerContext };

    private static async Task<int> SeedCompanyCreationContact(PaymentContext db, int userId)
    {
        var contact = new ExternalContact { FullName = "Organization contact", ContactType = "Organization", Inn = " 7700000108 ", RolesJson = "[\"Contact\"]", ResponsibleUserId = userId, IsActive = true };
        db.ExternalContacts.Add(contact); await db.SaveChangesAsync();
        db.ExternalContacts.Add(new ExternalContact { FullName = "Employee", ParentContactId = contact.Id, ResponsibleUserId = userId });
        await db.SaveChangesAsync(); return contact.Id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_PostgreSqlContactPromotionAndCardCreationCannotBothClaimOneInn(bool promoteFirst)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var userId = await CreateUserAsync(seed, "admin");
        var contactId = await SeedCompanyCreationContact(seed, userId);
        var barrier = new CompanyCreateSaveBarrier();
        await using var first = CompanyCreatePgContext(database.ConnectionString, barrier);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        async Task<IActionResult?> Run(PaymentContext db, bool promote)
            => promote ? (await CompanyCreationContactsController(db, userId).PromoteExternalContact(contactId, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default)).Result
                : (await CreateController(db, userId).Create(new LegalEntityRequest { ShortName = "Card company", Inn = "7700000108" }, default)).Result;
        var pending = Run(first, promoteFirst);
        IActionResult? winner = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            winner = await Run(second, !promoteFirst).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { barrier.Release.TrySetResult(true); }
        Assert.IsType<ConflictObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        if (promoteFirst) Assert.IsType<CreatedResult>(winner); else Assert.IsType<OkObjectResult>(winner);
        var company = Assert.Single(await second.LegalEntities.AsNoTracking().ToArrayAsync());
        Assert.Equal("7700000108", company.Inn);
        var contact = await second.ExternalContacts.AsNoTracking().SingleAsync(row => row.Id == contactId);
        var employee = await second.ExternalContacts.AsNoTracking().SingleAsync(row => row.ParentContactId == contactId);
        Assert.Equal(promoteFirst ? (int?)null : company.Id, contact.LinkedLegalEntityId);
        Assert.Equal(promoteFirst ? (int?)null : company.Id, employee.LegalEntityId);
        Assert.Equal(" 7700000108 ", contact.Inn);
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CompanyCreate_ContactPromotionFailureRollsBackCompanyContactAndEmployees()
    {
        var failure = new CompanyCreateSecondSaveFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var userId = await CreateUserAsync(db, "admin");
        var contactId = await SeedCompanyCreationContact(db, userId);
        failure.Remaining = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompanyCreationContactsController(db, userId)
            .PromoteExternalContact(contactId, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync());
        var contact = await db.ExternalContacts.AsNoTracking().SingleAsync(row => row.Id == contactId);
        Assert.Null(contact.LinkedLegalEntityId); Assert.Equal("[\"Contact\"]", contact.RolesJson);
        Assert.Null((await db.ExternalContacts.AsNoTracking().SingleAsync(row => row.ParentContactId == contactId)).LegalEntityId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_ContactPromotionRejectsDirtyOrExternalUnitOfWork(bool ambient)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin"); var contactId = await SeedCompanyCreationContact(db, userId);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Foreign pending change" }; db.Clients.Add(pending);
        var response = await CompanyCreationContactsController(db, userId).PromoteExternalContact(contactId, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default);
        Assert.IsType<ConflictObjectResult>(response.Result); Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(EntityState.Added, db.Entry(pending).State); Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CompanyCreate_ContactPromotionKeepsHistoricalDuplicateAndContactUntouched()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin"); var contactId = await SeedCompanyCreationContact(db, userId);
        db.LegalEntities.Add(new LegalEntity { ShortName = "Historical", Inn = " 7700000108  ", Notes = "Keep history" }); await db.SaveChangesAsync();
        var response = await CompanyCreationContactsController(db, userId).PromoteExternalContact(contactId, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default);
        Assert.IsType<ConflictObjectResult>(response.Result);
        var company = Assert.Single(await db.LegalEntities.AsNoTracking().ToArrayAsync()); Assert.Equal("Keep history", company.Notes); Assert.Equal(" 7700000108  ", company.Inn);
        Assert.Null((await db.ExternalContacts.AsNoTracking().SingleAsync(row => row.Id == contactId)).LinkedLegalEntityId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyCreate_ContactPromotionRequiresVisibleContactAndLegalCreatePermission(bool foreign)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db); var otherId = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, userId, canCreate: false, canEdit: false, canDelete: false);
        var roleId = (await db.Users.SingleAsync(row => row.Id == userId)).RoleId;
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = RolePermissionCatalog.Clients, CanView = true, ViewScope = DataVisibilityScope.Own });
        await db.SaveChangesAsync();
        var contactId = await SeedCompanyCreationContact(db, foreign ? otherId : userId);
        var response = await CompanyCreationContactsController(db, userId).PromoteExternalContact(contactId, new PromoteContactRequest { Target = "legalEntity", Role = "Client" }, default);
        if (foreign) Assert.IsType<NotFoundResult>(response.Result); else Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
        Assert.Null((await db.ExternalContacts.AsNoTracking().SingleAsync(row => row.Id == contactId)).LinkedLegalEntityId);
    }

    [Fact]
    public async Task CompanyCreate_PostgreSqlSameContactWithoutInnIsPromotedOnce()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var userId = await CreateUserAsync(seed, "admin"); var contactId = await SeedCompanyCreationContact(seed, userId);
        (await seed.ExternalContacts.SingleAsync(row => row.Id == contactId)).Inn = null; await seed.SaveChangesAsync();
        var barrier = new CompanyCreateSaveBarrier();
        await using var first = CompanyCreatePgContext(database.ConnectionString, barrier);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var request = new PromoteContactRequest { Target = "legalEntity", Role = "Client" };
        var pending = CompanyCreationContactsController(first, userId).PromoteExternalContact(contactId, request, default);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<OkObjectResult>((await CompanyCreationContactsController(second, userId).PromoteExternalContact(contactId, request, default).WaitAsync(TimeSpan.FromSeconds(15))).Result);
        }
        finally { barrier.Release.TrySetResult(true); }
        Assert.IsType<ConflictObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        var company = Assert.Single(await second.LegalEntities.AsNoTracking().ToArrayAsync()); Assert.Null(company.Inn);
        Assert.Equal(company.Id, (await second.ExternalContacts.AsNoTracking().SingleAsync(row => row.Id == contactId)).LinkedLegalEntityId);
        Assert.Equal(company.Id, (await second.ExternalContacts.AsNoTracking().SingleAsync(row => row.ParentContactId == contactId)).LegalEntityId);
    }
}

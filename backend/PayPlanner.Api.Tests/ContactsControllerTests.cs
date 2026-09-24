using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ContactsControllerTests
{
    [Fact]
    public async Task MergeContacts_TransfersClientCaseAndPayment_ArchivesDuplicate()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-clients", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id, "Мария Манаева", "89198106343");
        var duplicate = await CreateClientAsync(db, user.Id, "Мария Манаева", "79198106343");
        db.ClientCases.Add(new ClientCase { ClientId = duplicate.Id, Title = "Дело дубля" });
        db.Payments.Add(new Payment { ClientId = duplicate.Id, Date = DateTime.UtcNow, Amount = 100 });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        };

        var preview = await controller.PreviewMerge(request, CancellationToken.None);
        var previewResult = Assert.IsType<OkObjectResult>(preview.Result);
        var details = Assert.IsType<ContactsController.ContactMergePreview>(previewResult.Value);
        Assert.True(details.CanMerge);
        Assert.True(details.AffectedLinks >= 2);

        var merged = await controller.MergeContacts(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(merged.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(primary.Id, (await db.ClientCases.SingleAsync()).ClientId);
        Assert.Equal(primary.Id, (await db.Payments.SingleAsync()).ClientId);
        Assert.Null(await db.Clients.FirstOrDefaultAsync(x => x.Id == duplicate.Id));
        Assert.NotNull(await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == duplicate.Id));
        Assert.Contains(db.ObjectAuditEvents, x => x.ObjectType == "contact-merge" && x.ObjectId == primary.Id);
    }

    [Fact]
    public async Task MergeContacts_RejectsDifferentBirthDatesWithoutChangingLinks()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-conflict", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id, "Мария Манаева");
        var duplicate = await CreateClientAsync(db, user.Id, "Мария Манаева");
        primary.BirthDate = new DateTime(1980, 1, 1);
        duplicate.BirthDate = new DateTime(1990, 1, 1);
        db.ClientCases.Add(new ClientCase { ClientId = duplicate.Id, Title = "Не переносить" });
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        };

        var response = await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(duplicate.Id, (await db.ClientCases.SingleAsync()).ClientId);
        Assert.NotNull(await db.Clients.FindAsync(duplicate.Id));
    }

    [Fact]
    public async Task MergeContacts_MovesExternalContactChildren_ArchivesDuplicate()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-external", true, true, true, true, true);
        var primary = await CreateExternalContactAsync(db, user.Id, "Дубль", "89198106343");
        var duplicate = await CreateExternalContactAsync(db, user.Id, "Дубль", "79198106343");
        var child = await CreateExternalContactAsync(db, user.Id, "Сотрудник", "79990000001");
        child.ParentContactId = duplicate.Id;
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = primary.Id,
            DuplicateSourceType = "external", DuplicateSourceId = duplicate.Id,
        };

        var response = await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(primary.Id, (await db.ExternalContacts.SingleAsync(x => x.Id == child.Id)).ParentContactId);
        Assert.Null(await db.ExternalContacts.FirstOrDefaultAsync(x => x.Id == duplicate.Id));
        var mergedPhones = (await db.ExternalContacts.SingleAsync(x => x.Id == primary.Id)).PhonesJson;
        Assert.Contains("79198106343", mergedPhones);
    }

    [Fact]
    public async Task MergeContacts_QuickPersonIntoFullClient_PreservesQuickLinksAndArchivesOriginal()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-client", true, true, true, true, true);
        var client = await CreateClientAsync(db, user.Id, "Мария Манаева", "89198106343");
        var quick = await CreateExternalContactAsync(db, user.Id, "Мария Манаева", "79991234567");
        var child = await CreateExternalContactAsync(db, user.Id, "Связанный контакт", "79990000001");
        child.ParentContactId = quick.Id;
        var clientCase = new ClientCase { ClientId = client.Id, Title = "Дело" };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.ClientCaseRelatedParties.Add(new ClientCaseRelatedParty
        {
            ClientCaseId = clientCase.Id, SourceType = "external", ExternalContactId = quick.Id,
            DisplayName = quick.FullName,
        });
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = client.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        };

        var preview = await CreateController(db, user.Id).PreviewMerge(request, CancellationToken.None);
        Assert.True(Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value).CanMerge);
        var result = await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        var alias = await db.ExternalContacts.SingleAsync(x => x.LinkedClientId == client.Id);
        Assert.NotEqual(quick.Id, alias.Id);
        Assert.Equal(alias.Id, (await db.ClientCaseRelatedParties.SingleAsync()).ExternalContactId);
        Assert.Equal(alias.Id, (await db.ExternalContacts.SingleAsync(x => x.Id == child.Id)).ParentContactId);
        Assert.Null(await db.ExternalContacts.FirstOrDefaultAsync(x => x.Id == quick.Id));
        Assert.NotNull(await db.ExternalContacts.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == quick.Id));
        Assert.Contains("79991234567", (await db.Clients.SingleAsync(x => x.Id == client.Id)).Notes);
    }

    [Fact]
    public async Task MergeContacts_QuickOrganizationIntoFullLegal_UsesExistingAlias()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-legal", true, true, true, true, true,
            legalEntityViewScope: DataVisibilityScope.All);
        var legal = await CreateLegalEntityAsync(db, user.Id, "ООО Мастер");
        var alias = await CreateExternalContactAsync(db, user.Id, "ООО Мастер", "79990001111");
        alias.ContactType = "Organization";
        alias.LinkedLegalEntityId = legal.Id;
        var quick = await CreateExternalContactAsync(db, user.Id, "ООО Мастер", "79990002222");
        quick.ContactType = "Organization";
        await db.SaveChangesAsync();

        var result = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "legal", PrimarySourceId = legal.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(alias.Id, (await db.ExternalContacts.SingleAsync(x => x.LinkedLegalEntityId == legal.Id)).Id);
        Assert.Contains("79990002222", (await db.LegalEntities.SingleAsync(x => x.Id == legal.Id)).ContactPhonesJson);
        Assert.Null(await db.ExternalContacts.FirstOrDefaultAsync(x => x.Id == quick.Id));
    }

    [Fact]
    public async Task MergeContacts_QuickPersonAsPrimary_CreatesNewFullClientAndMovesCaseAndPayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-reverse-client", true, true, true, true, true);
        var oldClient = await CreateClientAsync(db, user.Id, "Старая полная карточка", "79991110000");
        oldClient.Address = "Адрес из старой карточки";
        var quick = await CreateExternalContactAsync(db, user.Id, "Выбранный основной", "79992220000");
        db.ClientCases.Add(new ClientCase { ClientId = oldClient.Id, Title = "Судебное дело" });
        db.Payments.Add(new Payment { ClientId = oldClient.Id, Date = DateTime.UtcNow, Amount = 100m });
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = quick.Id,
            DuplicateSourceType = "client", DuplicateSourceId = oldClient.Id,
        };

        var preview = await CreateController(db, user.Id).PreviewMerge(request, CancellationToken.None);
        var previewValue = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value);
        Assert.True(previewValue.CanMerge);
        Assert.True(previewValue.AffectedLinks >= 2);
        Assert.IsType<OkObjectResult>((await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None)).Result);

        db.ChangeTracker.Clear();
        var survivingQuick = await db.ExternalContacts.SingleAsync(x => x.Id == quick.Id);
        Assert.NotNull(survivingQuick.LinkedClientId);
        Assert.NotEqual(oldClient.Id, survivingQuick.LinkedClientId);
        var replacement = await db.Clients.SingleAsync(x => x.Id == survivingQuick.LinkedClientId);
        Assert.Equal(quick.FullName, replacement.Name);
        Assert.Equal(oldClient.Address, replacement.Address);
        Assert.Contains("79992220000", replacement.Notes);
        Assert.Contains("79991110000", replacement.Notes);
        Assert.Equal(replacement.Id, (await db.ClientCases.SingleAsync()).ClientId);
        Assert.Equal(replacement.Id, (await db.Payments.SingleAsync()).ClientId);
        Assert.Null(await db.Clients.FirstOrDefaultAsync(x => x.Id == oldClient.Id));
        Assert.NotNull(await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == oldClient.Id));
        Assert.Contains(await db.ObjectAuditEvents.ToListAsync(), x => x.ObjectType == "contact-merge" && x.ObjectId == quick.Id);
    }

    [Fact]
    public async Task MergeContacts_QuickPrimaryAlreadyLinkedToDuplicate_ReplacesLinkWithoutLosingHistory()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-reverse-linked", true, true, true, true, true);
        var oldClient = await CreateClientAsync(db, user.Id);
        var quick = await CreateExternalContactAsync(db, user.Id);
        quick.LinkedClientId = oldClient.Id;
        db.ClientCases.Add(new ClientCase { ClientId = oldClient.Id, Title = "История дела" });
        await db.SaveChangesAsync();

        var response = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = quick.Id,
            DuplicateSourceType = "client", DuplicateSourceId = oldClient.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        var replacementId = (await db.ExternalContacts.SingleAsync(x => x.Id == quick.Id)).LinkedClientId;
        Assert.NotNull(replacementId);
        Assert.NotEqual(oldClient.Id, replacementId);
        Assert.Equal(replacementId, (await db.ClientCases.SingleAsync()).ClientId);
    }

    [Fact]
    public async Task MergeContacts_QuickOrganizationAsPrimary_PreservesFullDetailsAndMovesClient()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-reverse-legal", true, true, true, true, true,
            legalEntityViewScope: DataVisibilityScope.All);
        var oldLegal = await CreateLegalEntityAsync(db, user.Id, "Старая организация");
        oldLegal.Inn = "5401000000";
        oldLegal.BankAccountsJson = "[{\"number\":\"123\"}]";
        var quick = await CreateExternalContactAsync(db, user.Id, "Новая организация", "79993330000");
        quick.ContactType = "Organization";
        quick.Inn = oldLegal.Inn;
        var linkedClient = await CreateClientAsync(db, user.Id);
        linkedClient.LegalEntityId = oldLegal.Id;
        await db.SaveChangesAsync();

        var response = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = quick.Id,
            DuplicateSourceType = "legal", DuplicateSourceId = oldLegal.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        var replacementId = (await db.ExternalContacts.SingleAsync(x => x.Id == quick.Id)).LinkedLegalEntityId;
        Assert.NotNull(replacementId);
        Assert.NotEqual(oldLegal.Id, replacementId);
        var replacement = await db.LegalEntities.SingleAsync(x => x.Id == replacementId);
        Assert.Equal(quick.FullName, replacement.ShortName);
        Assert.Equal(oldLegal.BankAccountsJson, replacement.BankAccountsJson);
        Assert.Equal(oldLegal.Inn, replacement.Inn);
        Assert.Equal(replacement.Id, (await db.Clients.SingleAsync(x => x.Id == linkedClient.Id)).LegalEntityId);
        Assert.Null(await db.LegalEntities.FirstOrDefaultAsync(x => x.Id == oldLegal.Id));
        Assert.NotNull(await db.LegalEntities.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == oldLegal.Id));
    }

    [Fact]
    public async Task PreviewMerge_QuickPrimaryRejectsOtherAliasAndDifferentFullProfile()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-reverse-conflict", true, true, true, true, true);
        var oldClient = await CreateClientAsync(db, user.Id);
        var anotherClient = await CreateClientAsync(db, user.Id);
        var quick = await CreateExternalContactAsync(db, user.Id);
        quick.LinkedClientId = anotherClient.Id;
        var otherAlias = await CreateExternalContactAsync(db, user.Id);
        otherAlias.LinkedClientId = oldClient.Id;
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = quick.Id,
            DuplicateSourceType = "client", DuplicateSourceId = oldClient.Id,
        };

        var preview = await CreateController(db, user.Id).PreviewMerge(request, CancellationToken.None);
        var details = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value);
        Assert.False(details.CanMerge);
        Assert.Contains(details.Conflicts, x => x.Contains("другой полной карточкой", StringComparison.Ordinal));
        Assert.Contains(details.Conflicts, x => x.Contains("другим быстрым контактом", StringComparison.Ordinal));
        Assert.IsType<ConflictObjectResult>((await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None)).Result);
        Assert.Equal(oldClient.Id, (await db.ExternalContacts.SingleAsync(x => x.Id == otherAlias.Id)).LinkedClientId);
    }

    [Fact]
    public async Task MergeContacts_QuickPrimaryRequiresPermissionToCreateFullProfile()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateClientUserAsync(db, "merge-reverse-owner", true, true, true, true, true);
        var noCreate = await CreateClientUserAsync(db, "merge-reverse-no-create", true, false, true, true, true,
            DataVisibilityScope.All);
        var oldClient = await CreateClientAsync(db, owner.Id);
        var quick = await CreateExternalContactAsync(db, owner.Id);

        var response = await CreateController(db, noCreate.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "external", PrimarySourceId = quick.Id,
            DuplicateSourceType = "client", DuplicateSourceId = oldClient.Id,
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Fact]
    public async Task MergeContacts_RejectsQuickContactLinkedToDifferentFullProfile()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-conflict", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id);
        var another = await CreateClientAsync(db, user.Id);
        var quick = await CreateExternalContactAsync(db, user.Id);
        quick.LinkedClientId = another.Id;
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        };

        var preview = await CreateController(db, user.Id).PreviewMerge(request, CancellationToken.None);
        Assert.False(Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value).CanMerge);
        Assert.IsType<ConflictObjectResult>((await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None)).Result);
        Assert.Equal(another.Id, (await db.ExternalContacts.SingleAsync(x => x.Id == quick.Id)).LinkedClientId);
    }

    [Fact]
    public async Task MergeContacts_QuickAlreadyLinkedToPrimary_ReplacesAliasWithoutUniqueConflict()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-linked", true, true, true, true, true);
        var client = await CreateClientAsync(db, user.Id);
        var quick = await CreateExternalContactAsync(db, user.Id);
        quick.LinkedClientId = client.Id;
        await db.SaveChangesAsync();

        var result = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = client.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        }, CancellationToken.None);

        Assert.True(result.Result is OkObjectResult,
            result.Result is ObjectResult failure ? System.Text.Json.JsonSerializer.Serialize(failure.Value) : result.Result?.GetType().Name);
        db.ChangeTracker.Clear();
        Assert.Single(await db.ExternalContacts.Where(x => x.LinkedClientId == client.Id).ToListAsync());
        Assert.Null(await db.ExternalContacts.FirstOrDefaultAsync(x => x.Id == quick.Id));
    }

    [Fact]
    public async Task MergeContacts_MixedPairRequiresBothSectionsPermissions()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateClientUserAsync(db, "merge-mixed-owner", true, true, true, true, true,
            legalEntityViewScope: DataVisibilityScope.All);
        var noLegalAccess = await CreateClientUserAsync(db, "merge-mixed-no-legal", true, true, true, true, true,
            DataVisibilityScope.All);
        var legal = await CreateLegalEntityAsync(db, owner.Id, "ООО Мастер");
        var quick = await CreateExternalContactAsync(db, owner.Id);
        quick.ContactType = "Organization";
        await db.SaveChangesAsync();

        var result = await CreateController(db, noLegalAccess.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "legal", PrimarySourceId = legal.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.NotNull(await db.ExternalContacts.FindAsync(quick.Id));
    }

    [Fact]
    public async Task PreviewMerge_RejectsArchivedAliasThatStillOccupiesFullProfile()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-archived-alias", true, true, true, true, true);
        var client = await CreateClientAsync(db, user.Id);
        var alias = await CreateExternalContactAsync(db, user.Id);
        alias.LinkedClientId = client.Id;
        await db.SaveChangesAsync();
        db.ExternalContacts.Remove(alias);
        await db.SaveChangesAsync();
        var quick = await CreateExternalContactAsync(db, user.Id);

        var preview = await CreateController(db, user.Id).PreviewMerge(new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = client.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        }, CancellationToken.None);

        var details = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value);
        Assert.False(details.CanMerge);
        Assert.Contains(details.Conflicts, x => x.Contains("архивным", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewMerge_RejectsPersonLinkedToDifferentOrganization()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-mixed-organization", true, true, true, true, true);
        var firstOrganization = await CreateLegalEntityAsync(db, user.Id, "ООО Первое");
        var secondOrganization = await CreateLegalEntityAsync(db, user.Id, "ООО Второе");
        var client = await CreateClientAsync(db, user.Id);
        client.LegalEntityId = firstOrganization.Id;
        var quick = await CreateExternalContactAsync(db, user.Id, legalEntityId: secondOrganization.Id);
        await db.SaveChangesAsync();

        var preview = await CreateController(db, user.Id).PreviewMerge(new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = client.Id,
            DuplicateSourceType = "external", DuplicateSourceId = quick.Id,
        }, CancellationToken.None);

        var details = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value);
        Assert.False(details.CanMerge);
        Assert.Contains(details.Conflicts, x => x.Contains("разным организациям", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeContacts_DoesNotExposeAnotherUsersCard()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-visible", true, true, true, true, true);
        var another = await CreateClientUserAsync(db, "merge-hidden", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id);
        var duplicate = await CreateClientAsync(db, another.Id);
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        };

        var response = await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(response.Result);
        Assert.NotNull(await db.Clients.FindAsync(duplicate.Id));
    }

    [Fact]
    public async Task MergeContacts_RequiresEditAndDeletePermissions()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateClientUserAsync(db, "merge-owner", true, true, true, true, true);
        var noDelete = await CreateClientUserAsync(db, "merge-no-delete", true, true, true, false, true, DataVisibilityScope.All);
        var noEdit = await CreateClientUserAsync(db, "merge-no-edit", true, true, false, true, true, DataVisibilityScope.All);
        var primary = await CreateClientAsync(db, owner.Id);
        var duplicate = await CreateClientAsync(db, owner.Id);
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        };

        Assert.IsType<ForbidResult>((await CreateController(db, noDelete.Id).MergeContacts(request, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await CreateController(db, noEdit.Id).MergeContacts(request, CancellationToken.None)).Result);
        Assert.NotNull(await db.Clients.FindAsync(duplicate.Id));
    }

    [Fact]
    public async Task MergeContacts_TransfersOrganizationClient_AndKeepsTheArchivedOrganization()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-legal", true, true, true, true, true, legalEntityViewScope: DataVisibilityScope.All);
        var primary = await CreateLegalEntityAsync(db, user.Id, "ООО Мастер");
        var duplicate = await CreateLegalEntityAsync(db, user.Id, "ООО Мастер");
        var client = await CreateClientAsync(db, user.Id);
        client.LegalEntityId = duplicate.Id;
        await db.SaveChangesAsync();

        var response = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "legal", PrimarySourceId = primary.Id,
            DuplicateSourceType = "legal", DuplicateSourceId = duplicate.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(primary.Id, (await db.Clients.SingleAsync(x => x.Id == client.Id)).LegalEntityId);
        Assert.Null(await db.LegalEntities.FirstOrDefaultAsync(x => x.Id == duplicate.Id));
        Assert.NotNull(await db.LegalEntities.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == duplicate.Id));
    }

    [Fact]
    public async Task MergeContacts_RefusesOverlappingContractLinksWithoutLosingComments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-contracts", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id);
        var duplicate = await CreateClientAsync(db, user.Id);
        var contract = new Contract { Number = "MERGE-1", Date = DateTime.UtcNow };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.ClientContracts.AddRange(
            new ClientContract { ClientId = primary.Id, ContractId = contract.Id, Comment = "основной" },
            new ClientContract { ClientId = duplicate.Id, ContractId = contract.Id, Comment = "дубль" });
        await db.SaveChangesAsync();
        var request = new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        };

        var preview = await CreateController(db, user.Id).PreviewMerge(request, CancellationToken.None);
        Assert.False(Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(preview.Result).Value).CanMerge);
        var response = await CreateController(db, user.Id).MergeContacts(request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(response.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ClientContracts.CountAsync());
        Assert.Contains(await db.ClientContracts.ToListAsync(), x => x.ClientId == duplicate.Id && x.Comment == "дубль");
    }

    [Fact]
    public async Task MergeContacts_KeepsSelectedClientsPrimaryPassportAndAddress()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "merge-primary", true, true, true, true, true);
        var primary = await CreateClientAsync(db, user.Id);
        var duplicate = await CreateClientAsync(db, user.Id);
        db.ClientPassports.AddRange(
            new ClientPassport { ClientId = primary.Id, IsPrimary = true, Number = "111" },
            new ClientPassport { ClientId = duplicate.Id, IsPrimary = true, Number = "222" });
        db.AddressRecords.AddRange(
            new AddressRecord { ClientId = primary.Id, IsPrimary = true, Value = "Первый адрес" },
            new AddressRecord { ClientId = duplicate.Id, IsPrimary = true, Value = "Второй адрес" });
        await db.SaveChangesAsync();

        var result = await CreateController(db, user.Id).MergeContacts(new ContactMergeRequest
        {
            PrimarySourceType = "client", PrimarySourceId = primary.Id,
            DuplicateSourceType = "client", DuplicateSourceId = duplicate.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ClientPassports.CountAsync(x => x.ClientId == primary.Id));
        Assert.Equal("111", (await db.ClientPassports.SingleAsync(x => x.ClientId == primary.Id && x.IsPrimary)).Number);
        Assert.Equal(2, await db.AddressRecords.CountAsync(x => x.ClientId == primary.Id));
        Assert.Equal("Первый адрес", (await db.AddressRecords.SingleAsync(x => x.ClientId == primary.Id && x.IsPrimary)).Value);
    }

    [Fact]
    public async Task Endpoints_RequireClientSectionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noViewUser = await CreateClientUserAsync(db, "contacts-no-view", canView: false, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var noCreateUser = await CreateClientUserAsync(db, "contacts-no-create", canView: true, canCreate: false, canEdit: true, canDelete: true, canExport: true);
        var noEditUser = await CreateClientUserAsync(db, "contacts-no-edit", canView: true, canCreate: true, canEdit: false, canDelete: true, canExport: true);
        var noDeleteUser = await CreateClientUserAsync(db, "contacts-no-delete", canView: true, canCreate: true, canEdit: true, canDelete: false, canExport: true);
        var noExportUser = await CreateClientUserAsync(db, "contacts-no-export", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: false);
        var client = await CreateClientAsync(db, noEditUser.Id);
        var externalForEdit = await CreateExternalContactAsync(db, noEditUser.Id);
        var externalForDelete = await CreateExternalContactAsync(db, noDeleteUser.Id);

        var listResult = await CreateController(db, noViewUser.Id).GetAll(
            scope: null,
            search: null,
            legalEntityId: null,
            includeInactive: false,
            syncOnly: null,
            ct: CancellationToken.None);
        var createResult = await CreateController(db, noCreateUser.Id).CreateExternalContact(
            BuildExternalContactRequest("Forbidden create"),
            CancellationToken.None);
        var updateClientResult = await CreateController(db, noEditUser.Id).UpdateClientContact(
            client.Id,
            BuildClientContactRequest("Forbidden client update"),
            CancellationToken.None);
        var updateExternalResult = await CreateController(db, noEditUser.Id).UpdateExternalContact(
            externalForEdit.Id,
            BuildExternalContactRequest("Forbidden external update"),
            CancellationToken.None);
        var deleteResult = await CreateController(db, noDeleteUser.Id).DeleteExternalContact(
            externalForDelete.Id,
            CancellationToken.None);
        var exportResult = await CreateController(db, noExportUser.Id).DownloadYealinkXml(CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult.Result);
        Assert.IsType<ForbidResult>(createResult.Result);
        Assert.IsType<ForbidResult>(updateClientResult.Result);
        Assert.IsType<ForbidResult>(updateExternalResult.Result);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.IsType<ForbidResult>(exportResult);
    }

    [Fact]
    public async Task DownloadYealinkXml_ExportsOnlyVisibleContacts_ForUserWithExportPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "contacts-export", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var otherUser = await CreateClientUserAsync(db, "contacts-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        await CreateClientAsync(db, user.Id, "Visible Client", "111");
        await CreateClientAsync(db, otherUser.Id, "Hidden Client", "222");
        await CreateExternalContactAsync(db, user.Id, "Visible External", "333");
        await CreateExternalContactAsync(db, otherUser.Id, "Hidden External", "444");
        var controller = CreateController(db, user.Id);

        var action = await controller.DownloadYealinkXml(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        var xml = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Visible Client", xml);
        Assert.Contains("Visible External", xml);
        Assert.DoesNotContain("Hidden Client", xml);
        Assert.DoesNotContain("Hidden External", xml);
    }

    [Fact]
    public async Task RemoteYealinkPhonebook_RequiresTokenAndExportsPeopleOnly()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "contacts-remote-phonebook", true, true, true, true, true);
        await CreateClientAsync(db, user.Id, "Телефонный клиент", "8 (999) 000-00-01");
        await CreateExternalContactAsync(db, user.Id, "Телефонный контакт", "+7 999 000-00-02");
        var organization = await CreateExternalContactAsync(db, user.Id, "ООО Не звонить", "79990000003");
        organization.ContactType = "Organization";
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            user.Id,
            new Dictionary<string, string?> { ["Phonebook:Token"] = "test-phonebook-token" });

        var denied = await controller.GetYealinkRemotePhonebook("wrong-token", CancellationToken.None);
        var allowed = await controller.GetYealinkRemotePhonebook("test-phonebook-token", CancellationToken.None);

        Assert.IsType<NotFoundResult>(denied);
        var content = Assert.IsType<ContentResult>(allowed);
        Assert.Contains("Телефонный клиент", content.Content);
        Assert.Contains("Телефонный контакт", content.Content);
        Assert.Contains("Phone1=\"79990000001\"", content.Content);
        Assert.Contains("Phone1=\"79990000002\"", content.Content);
        Assert.DoesNotContain("Phone1=\"89990000001\"", content.Content);
        Assert.DoesNotContain("ООО Не звонить", content.Content);
        Assert.Equal("private, max-age=300", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task GetAll_WithAllScope_ReturnsContactsAcrossResponsibles()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        await CreateClientAsync(db, otherUser.Id, "Other Responsible Client", "222");
        await CreateExternalContactAsync(db, otherUser.Id, "Other Responsible External", "333");

        var action = await CreateController(db, user.Id).GetAll(
            scope: null,
            search: null,
            legalEntityId: null,
            includeInactive: true,
            syncOnly: null,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsType<List<ContactDirectoryItemDto>>(ok.Value);
        Assert.Contains(items, item => item.FullName == "Other Responsible Client");
        Assert.Contains(items, item => item.FullName == "Other Responsible External");
    }

    [Fact]
    public async Task UpdateClientContact_WithAllScope_AllowsOtherResponsibleClient()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-edit-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-edit-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var client = await CreateClientAsync(db, otherUser.Id, "Other Client Before Update", "222");

        var action = await CreateController(db, user.Id).UpdateClientContact(
            client.Id,
            BuildClientContactRequest("Other Client After Update"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<ContactDirectoryItemDto>(ok.Value);
        Assert.Equal("Other Client After Update", item.FullName);

        var stored = await db.Clients.AsNoTracking().SingleAsync(entity => entity.Id == client.Id);
        Assert.Equal("Other Client After Update", stored.Name);
        Assert.Equal(otherUser.Id, stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateExternalContact_WithAllScope_AllowsOtherResponsibleContact()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var contact = await CreateExternalContactAsync(db, otherUser.Id, "Other External Before Update", "333");

        var action = await CreateController(db, user.Id).UpdateExternalContact(
            contact.Id,
            BuildExternalContactRequest("Other External After Update"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<ContactDirectoryItemDto>(ok.Value);
        Assert.Equal("Other External After Update", item.FullName);

        var stored = await db.ExternalContacts.AsNoTracking().SingleAsync(entity => entity.Id == contact.Id);
        Assert.Equal("Other External After Update", stored.FullName);
        Assert.Equal(otherUser.Id, stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateClientContact_WithHiddenLegalEntity_ReturnsBadRequestAndKeepsOriginalLink()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-client-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-client-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var client = await CreateClientAsync(db, user.Id, "Client before hidden legal", "222");
        var visibleLegal = await CreateLegalEntityAsync(db, user.Id, "Visible legal");
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden legal");
        client.LegalEntityId = visibleLegal.Id;
        await db.SaveChangesAsync();

        var request = BuildClientContactRequest("Client after hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).UpdateClientContact(client.Id, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.Clients.AsNoTracking().SingleAsync(entity => entity.Id == client.Id);
        Assert.Equal("Client before hidden legal", stored.Name);
        Assert.Equal(visibleLegal.Id, stored.LegalEntityId);
    }

    [Fact]
    public async Task CreateExternalContact_WithHiddenLegalEntity_ReturnsBadRequestAndDoesNotCreateContact()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden external legal");

        var request = BuildExternalContactRequest("External with hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).CreateExternalContact(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.ExternalContacts.AnyAsync(contact => contact.FullName == "External with hidden legal"));
    }

    [Fact]
    public async Task UpdateExternalContact_WithHiddenLegalEntity_ReturnsBadRequestAndKeepsOriginalLink()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-update-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-update-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var visibleLegal = await CreateLegalEntityAsync(db, user.Id, "Visible external legal");
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden external update legal");
        var contact = await CreateExternalContactAsync(db, user.Id, "External before hidden legal", legalEntityId: visibleLegal.Id);

        var request = BuildExternalContactRequest("External after hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).UpdateExternalContact(contact.Id, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.ExternalContacts.AsNoTracking().SingleAsync(entity => entity.Id == contact.Id);
        Assert.Equal("External before hidden legal", stored.FullName);
        Assert.Equal(visibleLegal.Id, stored.LegalEntityId);
    }

    [Fact]
    public async Task GetAll_ReturnsPeopleAndOrganizations_InOneDirectory()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-unified",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.All);
        await CreateClientAsync(db, user.Id, "Иван Иванов", "79990000001");
        await CreateLegalEntityAsync(db, user.Id, "ООО Ромашка");
        var quickOrganization = await CreateExternalContactAsync(db, user.Id, "ООО Василёк", "79990000002");
        quickOrganization.ContactType = "Organization";
        quickOrganization.Inn = "5400000000";
        quickOrganization.RolesJson = "[\"Supplier\"]";
        await db.SaveChangesAsync();

        var action = await CreateController(db, user.Id).GetAll(
            scope: "all",
            search: null,
            legalEntityId: null,
            includeInactive: false,
            syncOnly: null,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsType<List<ContactDirectoryItemDto>>(ok.Value);
        Assert.Contains(items, item => item.FullName == "Иван Иванов" && item.ContactType == "Person");
        Assert.Contains(items, item => item.FullName == "ООО Ромашка" && item.SourceType == "legal");
        Assert.Contains(items, item => item.FullName == "ООО Василёк" && item.Roles.Contains("Supplier"));
    }

    [Fact]
    public async Task GetAll_SearchesAllSourcesBeforeMaterialization_AndHidesPromotedDuplicates()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-search",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.All);
        var client = await CreateClientAsync(db, user.Id, "Клиент Альфа", "79991110001");
        client.Email = "alpha-client@example.test";
        await CreateClientAsync(db, user.Id, "Посторонний клиент", "79992220002");
        var legal = await CreateLegalEntityAsync(db, user.Id, "ООО Альфа");
        legal.Email = "alpha-legal@example.test";
        await CreateLegalEntityAsync(db, user.Id, "ООО Постороннее");
        db.ExternalContacts.AddRange(
            new ExternalContact
            {
                FullName = "Быстрый клиент Альфа",
                LinkedClientId = client.Id,
                Phone = "79993330003",
                Email = "alpha-client@example.test",
                IsActive = true,
            },
            new ExternalContact
            {
                FullName = "Быстрая организация Альфа",
                ContactType = "Organization",
                LinkedLegalEntityId = legal.Id,
                Inn = "5400000001",
                Email = "alpha-legal@example.test",
                IsActive = true,
            },
            new ExternalContact
            {
                FullName = "Ненужный быстрый контакт",
                Phone = "79994440004",
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var action = await CreateController(db, user.Id).GetAll(
            scope: "all",
            search: "alpha",
            legalEntityId: null,
            includeInactive: false,
            syncOnly: null,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsType<List<ContactDirectoryItemDto>>(ok.Value);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.FullName == "Быстрый клиент Альфа" && item.LinkedClientId == client.Id);
        Assert.Contains(items, item => item.FullName == "Быстрая организация Альфа" && item.LinkedLegalEntityId == legal.Id);
        Assert.DoesNotContain(items, item => item.SourceType == "client" && item.SourceId == client.Id);
        Assert.DoesNotContain(items, item => item.SourceType == "legal" && item.SourceId == legal.Id);

        var mergeDirectory = await CreateController(db, user.Id).GetAll(
            scope: "all",
            search: "alpha",
            legalEntityId: null,
            includeInactive: false,
            syncOnly: null,
            includeLinkedProfiles: true,
            ct: CancellationToken.None);
        var mergeItems = Assert.IsType<List<ContactDirectoryItemDto>>(Assert.IsType<OkObjectResult>(mergeDirectory.Result).Value);
        Assert.Contains(mergeItems, item => item.SourceType == "client" && item.SourceId == client.Id);
        Assert.Contains(mergeItems, item => item.SourceType == "legal" && item.SourceId == legal.Id);
    }

    [Fact]
    public async Task PromoteOrganization_CreatesLegalEntityAndMovesEmployeesToIt()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-promote-organization",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.All);
        var organization = await CreateExternalContactAsync(db, user.Id, "ООО Ромашка", "73830000000");
        organization.ContactType = "Organization";
        organization.Inn = "5401000000";
        var employee = await CreateExternalContactAsync(db, user.Id, "Иван Иванов", "79990000000");
        employee.ParentContactId = organization.Id;
        await db.SaveChangesAsync();

        var action = await CreateController(db, user.Id).PromoteExternalContact(
            organization.Id,
            new PromoteContactRequest { Target = "legalEntity", Role = "Client" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<ContactDirectoryItemDto>(ok.Value);
        Assert.True(item.HasFullProfile);
        Assert.NotNull(item.LinkedLegalEntityId);
        var storedEmployee = await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == employee.Id);
        Assert.Equal(item.LinkedLegalEntityId, storedEmployee.LegalEntityId);
    }

    private static UpsertExternalContactRequest BuildExternalContactRequest(string name)
        => new()
        {
            FullName = name,
            Phone = "79990000001",
            Phones = ["79990000001"],
            IsActive = true,
            SyncToPhonebook = true,
        };

    private static UpdateClientContactRequest BuildClientContactRequest(string name)
        => new()
        {
            FullName = name,
            Phone = "79990000002",
            Phones = ["79990000002"],
            SyncToPhonebook = true,
        };

    private static async Task<Client> CreateClientAsync(
        PaymentContext db,
        int responsibleUserId,
        string name = "Client Contact",
        string phone = "79991112233")
    {
        var client = new Client
        {
            Name = name,
            Phone = phone,
            Company = "Test Company",
            ResponsibleUserId = responsibleUserId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ExternalContact> CreateExternalContactAsync(
        PaymentContext db,
        int responsibleUserId,
        string name = "External Contact",
        string phone = "79994445566",
        int? legalEntityId = null)
    {
        var contact = new ExternalContact
        {
            FullName = name,
            Phone = phone,
            PhonesJson = System.Text.Json.JsonSerializer.Serialize(new[] { phone }),
            ResponsibleUserId = responsibleUserId,
            LegalEntityId = legalEntityId,
            IsActive = true,
            SyncToPhonebook = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExternalContacts.Add(contact);
        await db.SaveChangesAsync();
        return contact;
    }

    private static async Task<LegalEntity> CreateLegalEntityAsync(
        PaymentContext db,
        int responsibleUserId,
        string shortName)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            ResponsibleUserId = responsibleUserId,
            CreatedAt = DateTime.UtcNow,
        };
        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static async Task<User> CreateClientUserAsync(
        PaymentContext db,
        string name,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canExport,
        string viewScope = DataVisibilityScope.Own,
        string? legalEntityViewScope = null)
    {
        var permissions = new List<RolePermission>
        {
            new()
            {
                Section = RolePermissionCatalog.Clients,
                CanView = canView,
                CanCreate = canCreate,
                CanEdit = canEdit,
                CanDelete = canDelete,
                CanExport = canExport,
                ViewScope = viewScope,
            },
        };
        if (!string.IsNullOrWhiteSpace(legalEntityViewScope))
        {
            permissions.Add(new RolePermission
            {
                Section = RolePermissionCatalog.LegalEntities,
                CanView = true,
                CanCreate = true,
                CanEdit = true,
                CanDelete = true,
                ViewScope = legalEntityViewScope,
            });
        }

        var role = new Role
        {
            Name = $"{name}-{Guid.NewGuid():N}",
            Description = name,
            Permissions = permissions,
        };
        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ContactsController CreateController(
        PaymentContext db,
        int userId,
        IReadOnlyDictionary<string, string?>? configurationValues = null)
        => new(
            db,
            new NoopFormRequirementsService(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "Test user"),
                    ], "Test")),
                },
            },
        };

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }
}

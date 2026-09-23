using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class NotificationInboxControllerTests
{
    [Fact]
    public void UnreadSnoozePredicate_IsTranslatableForPostgreSql()
    {
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_query_translation;Username=payplanner")
            .Options;
        using var db = new PaymentContext(options);
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        var sql = db.UserNotifications
            .Where(item => !item.SnoozedUntil.HasValue || item.SnoozedUntil <= now)
            .ToQueryString();

        Assert.Contains("SnoozedUntil", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTypes_IncludesRoutingMetadata()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "types@example.com");
        var controller = CreateController(db, user.Id);

        var result = controller.GetTypes();

        var types = Assert.IsAssignableFrom<IReadOnlyList<UserNotificationTypeDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var approval = Assert.Single(types, item => item.Key == UserNotificationTypes.TaskApprovalRequested);
        Assert.Equal(NotificationPriorities.High, approval.Priority);
        Assert.Contains(NotificationChannels.InApp, approval.Channels);
        Assert.Contains(NotificationChannels.Toast, approval.Channels);
        Assert.Contains(NotificationChannels.ForcedModal, approval.Channels);
        Assert.Equal(UserNotificationDomains.Task, approval.Domain);
        Assert.Equal("Задачи", approval.DomainLabel);
        Assert.True(approval.Deduplicate);
        Assert.False(approval.Digestible);
        Assert.True(approval.ShowToastByDefault);
        Assert.True(approval.PushEligible);
        Assert.Equal(NotificationPriorities.High, approval.ToastPriority);
        Assert.Equal(NotificationPriorities.High, approval.PushPriority);
        Assert.Equal(60, approval.GroupingWindowSeconds);
        Assert.True(approval.RequiresAction);

        var taskChanged = Assert.Single(types, item => item.Key == UserNotificationTypes.TaskChanged);
        Assert.True(taskChanged.ShowToastByDefault);
        Assert.True(taskChanged.PushEligible);
        Assert.Equal(NotificationPriorities.Normal, taskChanged.ToastPriority);
        Assert.Equal(NotificationPriorities.Normal, taskChanged.PushPriority);
        Assert.Equal(300, taskChanged.GroupingWindowSeconds);
        Assert.False(taskChanged.RequiresAction);

        var advanceReview = Assert.Single(types, item => item.Key == UserNotificationTypes.PaymentAdvanceReviewRequired);
        Assert.Equal(NotificationPriorities.Critical, advanceReview.Priority);
        Assert.True(advanceReview.ShowToastByDefault);
        Assert.True(advanceReview.PushEligible);
        Assert.Equal(NotificationPriorities.Critical, advanceReview.ToastPriority);
        Assert.Equal(NotificationPriorities.Critical, advanceReview.PushPriority);
        Assert.Equal(0, advanceReview.GroupingWindowSeconds);
        Assert.True(advanceReview.RequiresAction);

        var completedTask = Assert.Single(types, item => item.Key == UserNotificationTypes.TaskCompleted);
        Assert.Equal(NotificationPriorities.Normal, completedTask.Priority);
        Assert.True(completedTask.ShowToastByDefault);
        Assert.False(completedTask.PushEligible);
        Assert.Equal(NotificationPriorities.Normal, completedTask.ToastPriority);
        Assert.Equal(NotificationPriorities.Normal, completedTask.PushPriority);
        Assert.Equal(60, completedTask.GroupingWindowSeconds);
        Assert.Equal(new[] { NotificationChannels.InApp, NotificationChannels.Toast }, completedTask.Channels);

        Assert.Contains(types, item => item.Key == UserNotificationTypes.PaymentAssigned && item.Domain == UserNotificationDomains.Payment && item.DomainLabel == "Платежи");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.InvoiceChanged && item.Domain == UserNotificationDomains.Invoice && item.DomainLabel == "Счета");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.ActChanged && item.Domain == UserNotificationDomains.Act && item.DomainLabel == "Акты");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.ContractExpiring && item.Domain == UserNotificationDomains.Contract && item.DomainLabel == "Договоры");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtHearing && item.Priority == NotificationPriorities.High && item.Domain == UserNotificationDomains.Court);
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtStatusChanged && item.Priority == NotificationPriorities.High);
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtDecisionPublished && item.Priority == NotificationPriorities.High);
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtSyncFailed && item.Priority == NotificationPriorities.Critical && item.Tone == "rose");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CaseInactivityReminder && item.Priority == NotificationPriorities.High && item.Domain == UserNotificationDomains.Case);
        Assert.Contains(types, item => item.Key == UserNotificationTypes.SystemAdminWarning && item.Tone == "rose");
    }

    [Fact]
    public async Task GetNotifications_KeepsNotificationVisibleWhenForcedModalChannelDisabled()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "forced-modal-disabled@example.com");
        var notification = CreateNotification(
            user.Id,
            "task:1:approval-request",
            UserNotificationTypes.TaskApprovalRequested,
            title: "Ожидает согласования");
        db.UserNotifications.Add(notification);
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledChannels":{"taskApprovalRequested":["forcedModal"]}}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);

        Assert.Equal(notification.Id, item.Id);
        Assert.False(item.ForcedModalEnabled);
        Assert.True(item.ToastEnabled);
        Assert.False(item.IsRead);
    }

    [Fact]
    public async Task InboxActions_UpdateOnlyCurrentUserNotification()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current@example.com");
        var otherUser = await CreateUserAsync(db, "other@example.com");
        var currentNotification = CreateNotification(currentUser.Id, "task:1:changed");
        currentNotification.MetadataJson = "{\"clientCaseDocumentId\":991,\"action\":\"uploaded\"}";
        var otherNotification = CreateNotification(otherUser.Id, "task:2:changed");
        db.UserNotifications.AddRange(currentNotification, otherNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(page.Items);
        Assert.Equal(currentNotification.Id, page.Items[0].Id);
        Assert.Equal(currentNotification.MetadataJson, page.Items[0].MetadataJson);

        var readResult = await controller.MarkRead(currentNotification.Id, CancellationToken.None);
        var readDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(readResult.Result).Value);
        Assert.True(readDto.IsRead);
        Assert.Equal(currentNotification.MetadataJson, readDto.MetadataJson);

        var archiveResult = await controller.Archive(currentNotification.Id, CancellationToken.None);
        var archivedDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        Assert.True(archivedDto.IsArchived);

        var unarchiveResult = await controller.Unarchive(currentNotification.Id, CancellationToken.None);
        var unarchivedDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(unarchiveResult.Result).Value);
        Assert.False(unarchivedDto.IsArchived);
        Assert.Null(unarchivedDto.ArchivedAt);

        var deleteResult = await controller.Delete(currentNotification.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);

        await db.Entry(currentNotification).ReloadAsync();
        await db.Entry(otherNotification).ReloadAsync();
        Assert.NotNull(currentNotification.DeletedAt);
        Assert.Null(otherNotification.DeletedAt);
    }

    [Fact]
    public async Task Snooze_HidesUnreadNotificationUntilLaterAndReadClearsSnooze()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "snooze@example.com");
        var notification = CreateNotification(
            user.Id,
            "case:1:inactivity-control:10",
            UserNotificationTypes.CaseInactivityControl);
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var snoozeResult = await controller.Snooze(
            notification.Id,
            new SnoozeUserNotificationRequest { Minutes = 30 },
            CancellationToken.None);
        var snoozed = Assert.IsType<UserNotificationDto>(
            Assert.IsType<OkObjectResult>(snoozeResult.Result).Value);
        Assert.False(snoozed.IsRead);
        Assert.True(snoozed.SnoozedUntil > DateTime.UtcNow.AddMinutes(29));

        var activeResult = await controller.GetNotifications("active", null, null);
        var activePage = Assert.IsType<PagedResponse<UserNotificationDto>>(
            Assert.IsType<OkObjectResult>(activeResult.Result).Value);
        Assert.Empty(activePage.Items);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        Assert.Equal(0, Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value));

        var allResult = await controller.GetNotifications("all", null, null);
        var allPage = Assert.IsType<PagedResponse<UserNotificationDto>>(
            Assert.IsType<OkObjectResult>(allResult.Result).Value);
        Assert.Single(allPage.Items);

        var readResult = await controller.MarkRead(notification.Id, CancellationToken.None);
        var read = Assert.IsType<UserNotificationDto>(
            Assert.IsType<OkObjectResult>(readResult.Result).Value);
        Assert.True(read.IsRead);
        Assert.Null(read.SnoozedUntil);

        await controller.Snooze(
            notification.Id,
            new SnoozeUserNotificationRequest { Minutes = 30 },
            CancellationToken.None);
        var markUnreadResult = await controller.MarkUnread(notification.Id, CancellationToken.None);
        var markedUnread = Assert.IsType<UserNotificationDto>(
            Assert.IsType<OkObjectResult>(markUnreadResult.Result).Value);
        Assert.False(markedUnread.IsRead);
        Assert.Null(markedUnread.SnoozedUntil);

        unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        Assert.Equal(1, Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value));
    }

    [Fact]
    public async Task GetUnreadCount_IgnoresArchivedReadAndDeletedNotifications()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "unread@example.com");
        var unread = CreateNotification(user.Id, "task:1:changed");
        var read = CreateNotification(user.Id, "task:2:changed");
        read.IsRead = true;
        read.ReadAt = DateTime.UtcNow;
        var archived = CreateNotification(user.Id, "task:3:changed");
        archived.IsArchived = true;
        archived.ArchivedAt = DateTime.UtcNow;
        var deleted = CreateNotification(user.Id, "task:4:changed");
        deleted.DeletedAt = DateTime.UtcNow;
        db.UserNotifications.AddRange(
            unread,
            read,
            archived,
            deleted);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var result = await controller.GetUnreadCount(CancellationToken.None);
        var count = Assert.IsType<int>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetNotifications_DisablesToastForSilentNotificationTypes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "silent-type@example.com");
        var notification = CreateNotification(
            user.Id,
            "case:1:document",
            UserNotificationTypes.CaseDocument,
            title: "Документ дела");
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);

        Assert.Equal(notification.Id, item.Id);
        Assert.False(item.ToastEnabled);
    }

    [Fact]
    public async Task GetNotificationsAndUnreadCount_IgnoreDisabledUserNotificationTypes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "disabled-types@example.com");
        var visible = CreateNotification(
            user.Id,
            "task:1:assigned",
            UserNotificationTypes.TaskAssigned,
            title: "Новая задача");
        var disabled = CreateNotification(
            user.Id,
            "task:2:changed",
            UserNotificationTypes.TaskChanged,
            title: "Изменение задачи");
        db.UserNotifications.AddRange(visible, disabled);
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskChanged"]}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);

        Assert.Single(page.Items);
        Assert.Equal(visible.Id, page.Items[0].Id);
        Assert.Equal(1, unreadCount);
    }

    [Fact]
    public async Task GetNotifications_HidesTaskNotificationWhenTaskIsNotVisible()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "visible-notifications@example.com");
        var otherUser = await CreateUserAsync(db, "hidden-task-owner@example.com");
        var visibleTask = CreateTask(currentUser.Id, "Видимая задача");
        var hiddenTask = CreateTask(otherUser.Id, "Чужая задача");
        db.TaskJournalItems.AddRange(visibleTask, hiddenTask);
        await db.SaveChangesAsync();
        var visible = CreateNotification(
            currentUser.Id,
            "task:visible:changed",
            UserNotificationTypes.TaskChanged,
            title: "Видимое уведомление");
        visible.TaskJournalItemId = visibleTask.Id;
        visible.ObjectId = visibleTask.Id;
        var hidden = CreateNotification(
            currentUser.Id,
            "task:hidden:changed",
            UserNotificationTypes.TaskChanged,
            title: "Скрытое уведомление");
        hidden.TaskJournalItemId = hiddenTask.Id;
        hidden.ObjectId = hiddenTask.Id;
        db.UserNotifications.AddRange(visible, hidden);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);

        Assert.Single(page.Items);
        Assert.Equal(visible.Id, page.Items[0].Id);
        Assert.Equal(1, unreadCount);
    }

    [Fact]
    public async Task GetNotifications_HidesObjectNotificationsWhenTargetObjectIsNotVisible()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "visible-object-notifications@example.com");
        var otherUser = await CreateUserAsync(db, "hidden-object-owner@example.com");

        var visibleClient = CreateClient("Visible client");
        visibleClient.ResponsibleUserId = currentUser.Id;
        var hiddenClient = CreateClient("Hidden client");
        hiddenClient.ResponsibleUserId = otherUser.Id;
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = CreateCase(visibleClient, "Visible case");
        var hiddenCase = CreateCase(hiddenClient, "Hidden case");
        var visiblePayment = CreatePayment(visibleClient, DocumentKind.Payment, "visible-payment");
        var hiddenPayment = CreatePayment(hiddenClient, DocumentKind.Payment, "hidden-payment");
        var visibleInvoice = CreatePayment(visibleClient, DocumentKind.Invoice, "visible-invoice");
        var hiddenInvoice = CreatePayment(hiddenClient, DocumentKind.Invoice, "hidden-invoice");
        var visibleAct = CreateAct(visibleClient, "visible-act");
        var hiddenAct = CreateAct(hiddenClient, "hidden-act");
        var visibleContract = CreateContract(visibleClient, "visible-contract");
        var hiddenContract = CreateContract(hiddenClient, "hidden-contract");
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        db.Payments.AddRange(visiblePayment, hiddenPayment, visibleInvoice, hiddenInvoice);
        db.Acts.AddRange(visibleAct, hiddenAct);
        db.Contracts.AddRange(visibleContract, hiddenContract);
        await db.SaveChangesAsync();

        var visibleDocument = CreateCaseDocument(visibleCase, "visible-document");
        var hiddenDocument = CreateCaseDocument(hiddenCase, "hidden-document");
        db.ClientCaseDocuments.AddRange(visibleDocument, hiddenDocument);
        await db.SaveChangesAsync();

        var visibleNotifications = new[]
        {
            CreateObjectNotification(currentUser.Id, "case", visibleCase.Id, "visible-case"),
            CreateObjectNotification(currentUser.Id, "client-case-document", visibleDocument.Id, "visible-document"),
            CreateObjectNotification(currentUser.Id, "payment", visiblePayment.Id, "visible-payment"),
            CreateObjectNotification(currentUser.Id, "invoice", visibleInvoice.Id, "visible-invoice"),
            CreateObjectNotification(currentUser.Id, "act", visibleAct.Id, "visible-act"),
            CreateObjectNotification(currentUser.Id, "contract", visibleContract.Id, "visible-contract"),
        };
        var hiddenNotifications = new[]
        {
            CreateObjectNotification(currentUser.Id, "case", hiddenCase.Id, "hidden-case"),
            CreateObjectNotification(currentUser.Id, "client-case-document", hiddenDocument.Id, "hidden-document"),
            CreateObjectNotification(currentUser.Id, "payment", hiddenPayment.Id, "hidden-payment"),
            CreateObjectNotification(currentUser.Id, "invoice", hiddenInvoice.Id, "hidden-invoice"),
            CreateObjectNotification(currentUser.Id, "act", hiddenAct.Id, "hidden-act"),
            CreateObjectNotification(currentUser.Id, "contract", hiddenContract.Id, "hidden-contract"),
        };
        db.UserNotifications.AddRange(visibleNotifications);
        db.UserNotifications.AddRange(hiddenNotifications);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);
        var hiddenReadResult = await controller.MarkRead(hiddenNotifications[0].Id, CancellationToken.None);
        var archiveResult = await controller.ArchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [visibleNotifications[0].Id, hiddenNotifications[1].Id],
        }, CancellationToken.None);
        var archiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        var unarchiveResult = await controller.UnarchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [visibleNotifications[0].Id, hiddenNotifications[1].Id],
        }, CancellationToken.None);
        var unarchiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(unarchiveResult.Result).Value);

        Assert.Equal(visibleNotifications.Length, page.Items.Count);
        Assert.Equal(visibleNotifications.Length, unreadCount);
        Assert.All(visibleNotifications, item => Assert.Contains(page.Items, dto => dto.Id == item.Id));
        Assert.All(hiddenNotifications, item => Assert.DoesNotContain(page.Items, dto => dto.Id == item.Id));
        Assert.IsType<NotFoundResult>(hiddenReadResult.Result);
        Assert.Equal(1, archiveDto.UpdatedCount);
        Assert.Equal(1, unarchiveDto.UpdatedCount);
        await db.Entry(visibleNotifications[0]).ReloadAsync();
        Assert.False(visibleNotifications[0].IsArchived);
        Assert.Null(visibleNotifications[0].ArchivedAt);
    }

    [Fact]
    public async Task GetNotifications_FiltersNoteNotificationsByBoardAccess()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "visible-note-notifications@example.com");
        var otherUser = await CreateUserAsync(db, "hidden-note-owner@example.com");
        await SetNotesPermissionAsync(db, currentUser, canView: true);

        var ownBoard = new StickyNoteBoard
        {
            Title = "Мои заметки",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = currentUser.Id,
            OwnerUserName = currentUser.FullName,
            Background = "gray",
        };
        var sharedBoard = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var hiddenBoard = new StickyNoteBoard
        {
            Title = "Чужая личная доска",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = otherUser.Id,
            OwnerUserName = otherUser.FullName,
            Background = "gray",
        };
        var archivedBoard = new StickyNoteBoard
        {
            Title = "Архивная доска",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
            IsArchived = true,
        };
        var ownNote = CreateNote(ownBoard, "Своя заметка");
        var sharedNote = CreateNote(sharedBoard, "Общая заметка");
        var hiddenNote = CreateNote(hiddenBoard, "Чужая заметка");
        var archivedNote = CreateNote(archivedBoard, "Архивная заметка");
        db.StickyNotes.AddRange(ownNote, sharedNote, hiddenNote, archivedNote);
        await db.SaveChangesAsync();

        var ownNotification = CreateNoteNotification(currentUser.Id, ownNote.Id, "own-note");
        var sharedNotification = CreateNoteNotification(currentUser.Id, sharedNote.Id, "shared-note");
        var hiddenNotification = CreateNoteNotification(currentUser.Id, hiddenNote.Id, "hidden-note");
        var archivedNotification = CreateNoteNotification(currentUser.Id, archivedNote.Id, "archived-note");
        db.UserNotifications.AddRange(ownNotification, sharedNotification, hiddenNotification, archivedNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", domain: "note", type: null, search: null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);
        var hiddenReadResult = await controller.MarkRead(hiddenNotification.Id, CancellationToken.None);
        var archiveResult = await controller.ArchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [ownNotification.Id, hiddenNotification.Id, archivedNotification.Id],
        }, CancellationToken.None);
        var archiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.Id == ownNotification.Id);
        Assert.Contains(page.Items, item => item.Id == sharedNotification.Id);
        Assert.DoesNotContain(page.Items, item => item.Id == hiddenNotification.Id);
        Assert.DoesNotContain(page.Items, item => item.Id == archivedNotification.Id);
        Assert.Equal(2, unreadCount);
        Assert.IsType<NotFoundResult>(hiddenReadResult.Result);
        Assert.Equal(1, archiveDto.UpdatedCount);
    }

    [Fact]
    public async Task GetNotifications_HidesNoteNotificationsWhenUserCannotViewNotes()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "no-note-access@example.com");
        await SetNotesPermissionAsync(db, currentUser, canView: false);

        var sharedBoard = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var sharedNote = CreateNote(sharedBoard, "Общая заметка без доступа");
        db.StickyNotes.Add(sharedNote);
        await db.SaveChangesAsync();

        var notification = CreateNoteNotification(currentUser.Id, sharedNote.Id, "shared-note-without-access");
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", domain: "note", type: null, search: null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);
        var readResult = await controller.MarkRead(notification.Id, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, unreadCount);
        Assert.IsType<NotFoundResult>(readResult.Result);
    }

    [Fact]
    public async Task GetNotifications_FiltersTaskNotificationsByDateEmployeeClientAndRole()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "notification-filter-owner@example.com");
        var assignee = await CreateUserAsync(db, "notification-filter-assignee@example.com");
        var otherAssignee = await CreateUserAsync(db, "notification-filter-other@example.com");
        var targetClient = CreateClient("Target client");
        var otherClient = CreateClient("Other client");
        db.Clients.AddRange(targetClient, otherClient);
        await db.SaveChangesAsync();

        var targetTask = CreateTask(currentUser.Id, "Target task");
        targetTask.ClientId = targetClient.Id;
        targetTask.AssigneeUserId = assignee.Id;
        targetTask.AssigneeName = assignee.FullName;
        targetTask.Status = "done";
        var otherTask = CreateTask(currentUser.Id, "Other task");
        otherTask.ClientId = otherClient.Id;
        otherTask.AssigneeUserId = otherAssignee.Id;
        otherTask.AssigneeName = otherAssignee.FullName;
        db.TaskJournalItems.AddRange(targetTask, otherTask);
        await db.SaveChangesAsync();
        db.Entry(targetTask).Property<bool>("IsDeleted").CurrentValue = true;

        var target = CreateNotification(currentUser.Id, "task:target:changed", title: "Target");
        target.TaskJournalItemId = targetTask.Id;
        target.ObjectId = targetTask.Id;
        target.CreatedAt = new DateTime(2026, 5, 20, 8, 30, 0, DateTimeKind.Utc);
        var wrongDate = CreateNotification(currentUser.Id, "task:target:old", title: "Old");
        wrongDate.TaskJournalItemId = targetTask.Id;
        wrongDate.ObjectId = targetTask.Id;
        wrongDate.CreatedAt = new DateTime(2026, 5, 19, 8, 30, 0, DateTimeKind.Utc);
        var wrongClientAndEmployee = CreateNotification(currentUser.Id, "task:other:changed", title: "Other");
        wrongClientAndEmployee.TaskJournalItemId = otherTask.Id;
        wrongClientAndEmployee.ObjectId = otherTask.Id;
        wrongClientAndEmployee.CreatedAt = new DateTime(2026, 5, 20, 8, 30, 0, DateTimeKind.Utc);
        db.UserNotifications.AddRange(target, wrongDate, wrongClientAndEmployee);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications(
            "active",
            null,
            null,
            dateFrom: "2026-05-20",
            dateTo: "2026-05-20",
            employeeId: assignee.Id,
            clientId: targetClient.Id,
            taskRole: "assignee");

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(target.Id, item.Id);
    }

    [Fact]
    public async Task MarkAllRead_UpdatesOnlyFilteredCurrentUserNotifications()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current-bulk@example.com");
        var otherUser = await CreateUserAsync(db, "other-bulk@example.com");
        var target = CreateNotification(
            currentUser.Id,
            "task:1:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок по задаче",
            body: "Нужно проверить срок");
        var otherType = CreateNotification(
            currentUser.Id,
            "task:2:changed",
            UserNotificationTypes.TaskChanged,
            title: "Срок изменен",
            body: "Нужно проверить срок");
        var otherSearch = CreateNotification(
            currentUser.Id,
            "task:3:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Другое событие",
            body: "Без совпадения");
        var archived = CreateNotification(
            currentUser.Id,
            "task:4:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок в архиве",
            body: "Нужно проверить срок");
        archived.IsArchived = true;
        archived.ArchivedAt = DateTime.UtcNow;
        var otherUserNotification = CreateNotification(
            otherUser.Id,
            "task:5:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок по чужой задаче",
            body: "Нужно проверить срок");
        db.UserNotifications.AddRange(target, otherType, otherSearch, archived, otherUserNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var result = await controller.MarkAllRead(
            "active",
            UserNotificationTypes.TaskReminder,
            "срок",
            ct: CancellationToken.None);

        var dto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, dto.UpdatedCount);

        await db.Entry(target).ReloadAsync();
        await db.Entry(otherType).ReloadAsync();
        await db.Entry(otherSearch).ReloadAsync();
        await db.Entry(archived).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.True(target.IsRead);
        Assert.False(otherType.IsRead);
        Assert.False(otherSearch.IsRead);
        Assert.False(archived.IsRead);
        Assert.False(otherUserNotification.IsRead);
    }

    [Fact]
    public async Task DomainAndActionFilters_LimitInboxAndBulkRead()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "domain-action@example.com");
        var client = CreateClient("Domain action client");
        client.ResponsibleUserId = user.Id;
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(client, DocumentKind.Payment, "domain-action-payment");
        var act = CreateAct(client, "domain-action-act");
        db.Payments.Add(payment);
        db.Acts.Add(act);
        await db.SaveChangesAsync();

        var financeAction = CreateObjectNotification(user.Id, "payment", payment.Id, "Аванс требует решения");
        financeAction.Type = UserNotificationTypes.PaymentAdvanceReviewRequired;
        var financeInfo = CreateObjectNotification(user.Id, "act", act.Id, "Акт обновлен");
        financeInfo.Type = UserNotificationTypes.TaskChanged;
        var taskAction = CreateNotification(
            user.Id,
            "task:12:assigned",
            UserNotificationTypes.TaskAssigned,
            title: "Новая задача");
        db.UserNotifications.AddRange(financeAction, financeInfo, taskAction);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications(
            "active",
            null,
            null,
            domain: "finance",
            requiresAction: true);

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(financeAction.Id, item.Id);

        var markResult = await controller.MarkAllRead(
            "active",
            null,
            null,
            domain: "finance",
            requiresAction: true,
            ct: CancellationToken.None);

        var dto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(markResult.Result).Value);
        Assert.Equal(1, dto.UpdatedCount);

        await db.Entry(financeAction).ReloadAsync();
        await db.Entry(financeInfo).ReloadAsync();
        await db.Entry(taskAction).ReloadAsync();
        Assert.True(financeAction.IsRead);
        Assert.False(financeInfo.IsRead);
        Assert.False(taskAction.IsRead);
    }

    [Fact]
    public async Task GetNotifications_RequiresActionShowsApprovalRequestOnlyForCurrentApprover()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-author@example.com");
        var assignee = await CreateUserAsync(db, "approval-assignee@example.com");
        var firstApprover = await CreateUserAsync(db, "approval-first@example.com");
        var nextApprover = await CreateUserAsync(db, "approval-next@example.com");
        var task = CreateTask(author.Id, "Approval action task");
        task.AssigneeUserId = assignee.Id;
        task.AssigneeName = assignee.FullName;
        task.Status = "onApproval";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            SortOrder = 0,
            ApprovalStatus = "pending"
        });
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = nextApprover.Id,
            Role = "approver",
            DisplayName = nextApprover.FullName,
            SortOrder = 1,
            ApprovalStatus = "pending"
        });
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var assigneeNotification = CreateNotification(
            assignee.Id,
            $"task:{task.Id}:approval-request:{task.Participants.First().Id}",
            UserNotificationTypes.TaskApprovalRequested,
            title: "Task waits for approval");
        assigneeNotification.TaskJournalItemId = task.Id;
        assigneeNotification.ObjectType = "task";
        assigneeNotification.ObjectId = task.Id;
        var firstApproverNotification = CreateNotification(
            firstApprover.Id,
            $"task:{task.Id}:approval-request:{task.Participants.First().Id}",
            UserNotificationTypes.TaskApprovalRequested,
            title: "Task waits for approval");
        firstApproverNotification.TaskJournalItemId = task.Id;
        firstApproverNotification.ObjectType = "task";
        firstApproverNotification.ObjectId = task.Id;
        var nextApproverNotification = CreateNotification(
            nextApprover.Id,
            $"task:{task.Id}:approval-request:{task.Participants.First().Id}",
            UserNotificationTypes.TaskApprovalRequested,
            title: "Task waits for approval");
        nextApproverNotification.TaskJournalItemId = task.Id;
        nextApproverNotification.ObjectType = "task";
        nextApproverNotification.ObjectId = task.Id;
        db.UserNotifications.AddRange(assigneeNotification, firstApproverNotification, nextApproverNotification);
        await db.SaveChangesAsync();

        var assigneeController = CreateController(db, assignee.Id);
        var assigneeActiveResult = await assigneeController.GetNotifications("active", null, null);
        var assigneeActive = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(assigneeActiveResult.Result).Value);
        var assigneeActionResult = await assigneeController.GetNotifications("active", null, null, requiresAction: true);
        var assigneeAction = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(assigneeActionResult.Result).Value);

        var firstApproverController = CreateController(db, firstApprover.Id);
        var firstApproverActionResult = await firstApproverController.GetNotifications("active", null, null, requiresAction: true);
        var firstApproverAction = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(firstApproverActionResult.Result).Value);

        var nextApproverController = CreateController(db, nextApprover.Id);
        var nextApproverActionResult = await nextApproverController.GetNotifications("active", null, null, requiresAction: true);
        var nextApproverAction = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(nextApproverActionResult.Result).Value);

        Assert.Single(assigneeActive.Items);
        Assert.Empty(assigneeAction.Items);
        var approverItem = Assert.Single(firstApproverAction.Items);
        Assert.Equal(firstApproverNotification.Id, approverItem.Id);
        Assert.Empty(nextApproverAction.Items);
    }

    [Fact]
    public async Task BulkArchiveAndDelete_UpdateOnlySelectedCurrentUserNotifications()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current-selected-bulk@example.com");
        var otherUser = await CreateUserAsync(db, "other-selected-bulk@example.com");
        var first = CreateNotification(currentUser.Id, "task:1:changed");
        var second = CreateNotification(currentUser.Id, "task:2:changed");
        var third = CreateNotification(currentUser.Id, "task:3:changed");
        var otherUserNotification = CreateNotification(otherUser.Id, "task:4:changed");
        db.UserNotifications.AddRange(first, second, third, otherUserNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var archiveResult = await controller.ArchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [first.Id, otherUserNotification.Id, 0, first.Id],
        }, CancellationToken.None);

        var archiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        Assert.Equal(1, archiveDto.UpdatedCount);

        await db.Entry(first).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.True(first.IsArchived);
        Assert.True(first.IsRead);
        Assert.False(otherUserNotification.IsArchived);

        var deleteResult = await controller.DeleteSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [second.Id, third.Id, otherUserNotification.Id],
        }, CancellationToken.None);

        var deleteDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(deleteResult.Result).Value);
        Assert.Equal(2, deleteDto.UpdatedCount);

        await db.Entry(second).ReloadAsync();
        await db.Entry(third).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.NotNull(second.DeletedAt);
        Assert.NotNull(third.DeletedAt);
        Assert.Null(otherUserNotification.DeletedAt);
    }

    private static UserNotification CreateNotification(
        int userId,
        string eventKey,
        string type = UserNotificationTypes.TaskChanged,
        string title = "Изменение по задаче",
        string body = "Описание события")
        => new()
        {
            RecipientUserId = userId,
            Type = type,
            EventKey = eventKey,
            Title = title,
            BodyPreview = body,
            Tone = "blue",
            ObjectType = string.Empty,
            ObjectId = null,
            TaskJournalItemId = null,
            MetadataJson = "{}",
            CreatedAt = DateTime.UtcNow
        };

    private static UserNotification CreateObjectNotification(
        int userId,
        string objectType,
        int objectId,
        string title)
    {
        var notification = CreateNotification(
            userId,
            $"{objectType}:{objectId}:changed",
            title: title,
            body: $"{title} body");
        notification.ObjectType = objectType;
        notification.ObjectId = objectId;
        return notification;
    }

    private static UserNotification CreateNoteNotification(int userId, int noteId, string title)
    {
        var notification = CreateNotification(
            userId,
            $"note:{noteId}:{title}",
            UserNotificationTypes.NoteImportantChanged,
            title,
            $"{title} body");
        notification.ObjectType = "note";
        notification.ObjectId = noteId;
        return notification;
    }

    private static StickyNote CreateNote(StickyNoteBoard board, string text)
        => new()
        {
            Board = board,
            Text = text,
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            CreatedAt = DateTime.UtcNow,
        };

    private static async Task SetNotesPermissionAsync(PaymentContext db, User user, bool canView)
    {
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Notes,
            CanView = canView,
            CanCreate = canView,
            CanEdit = canView,
            CanDelete = canView,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();
    }

    private static TaskJournalItem CreateTask(int userId, string title)
        => new()
        {
            ObjectName = "Тестовое дело",
            Title = title,
            AuthorUserId = userId,
            AuthorName = $"user-{userId}",
            AssigneeUserId = userId,
            AssigneeName = $"user-{userId}",
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 60,
            Type = "task",
            Priority = "normal",
            Status = "new",
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static ClientCase CreateCase(Client client, string title)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            CaseNumber = title,
            Title = title,
            CaseType = "test",
            Stage = "active",
            Priority = "normal",
            CreatedAt = DateTime.UtcNow,
        };

    private static ClientCaseDocument CreateCaseDocument(ClientCase clientCase, string key)
        => new()
        {
            ClientCase = clientCase,
            ClientCaseId = clientCase.Id,
            DocumentKey = key,
            DocumentType = "test",
            Status = "created",
            OriginalFileName = $"{key}.txt",
            StoredFileName = $"{key}.txt",
            StoragePath = $"cases/{clientCase.Id}/{key}.txt",
            ContentType = "text/plain",
            UploadedByName = "Test",
            UploadedAt = DateTime.UtcNow,
        };

    private static Payment CreatePayment(Client client, DocumentKind documentKind, string account)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            Account = account,
            AccountDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Description = account,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = documentKind,
            CreatedAt = DateTime.UtcNow,
        };

    private static Act CreateAct(Client client, string number)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            Number = number,
            Date = DateTime.UtcNow.Date,
            Amount = 1000m,
            CreatedAt = DateTime.UtcNow,
        };

    private static Contract CreateContract(Client client, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Date = DateTime.UtcNow.Date,
            Amount = 1000m,
            CreatedAt = DateTime.UtcNow,
        };
        contract.ClientContracts.Add(new ClientContract
        {
            Client = client,
            ClientId = client.Id,
            Contract = contract,
        });
        return contract;
    }

    private static Client CreateClient(string name)
        => new()
        {
            Name = name,
            Email = string.Empty,
            Phone = string.Empty,
            Company = string.Empty,
            Address = string.Empty,
            Notes = string.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

    private static NotificationInboxController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Test User")
        };

        return new NotificationInboxController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email)
    {
        var role = new Role
        {
            Name = $"role-{Guid.NewGuid():N}",
            Description = "test",
            CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}

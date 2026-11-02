using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateServerFieldsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpdatingAnExistingRecordDoesNotResetItsServerHistory(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var row = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        var originalDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        row.CreatedAt = originalDate; row.SellerSnapshotJson = "{\"trusted\":\"keep\"}";
        row.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile; row.InvoiceWorkflowStatus = InvoiceWorkflowStatus.SignedByClient; row.InvoiceSignedAt = originalDate;
        await db.SaveChangesAsync(); var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture);
        SetServerFields(model, fixture.ActorId, "snapshot"); SetServerFields(model, fixture.ActorId, "workflow"); SetServerFields(model, fixture.ActorId, "audit");
        model.Notes = "Permitted existing record note";
        var result = api == 0 ? await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, default)
            : await VersionedPaymentWriterTests.WriteAsync(db, fixture, api, "update", model);
        Assert.IsType<OkObjectResult>(result);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(originalDate, stored.CreatedAt); Assert.Equal("{\"trusted\":\"keep\"}", stored.SellerSnapshotJson);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, stored.SellerSnapshotPolicy); Assert.Equal(InvoiceWorkflowStatus.SignedByClient, stored.InvoiceWorkflowStatus);
        Assert.Equal(originalDate, stored.InvoiceSignedAt); Assert.Equal(model.Notes, stored.Notes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ANewAdvanceDecisionGetsItsActualReviewerFromTheServer(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        var model = PaymentFor(fixture); model.PaidAmount = 100m; model.IsPaid = true; model.Status = PaymentStatus.Completed;
        model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance; SetServerFields(model, fixture.ActorId, "review"); var before = DateTime.UtcNow;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(fixture.ActorId, stored.AdvanceReviewedByUserId); Assert.NotEqual("Forged reviewer", stored.AdvanceReviewedByUserName);
        Assert.InRange(stored.AdvanceReviewedAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(100m, (await db.ClientBalanceTransactions.SingleAsync(t => t.PaymentId == saved.Id && t.Type == ClientBalanceTransactionType.UnallocatedAdvance)).Amount);
    }
    public static IEnumerable<object[]> Cases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var group in new[] { "snapshot", "workflow", "review", "audit", "display" }) yield return [api, group];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CallerCannotChooseServerOwnedCreationFields(int api, string group)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = PaymentFor(fixture); SetServerFields(model, fixture.ActorId, group); var before = DateTime.UtcNow;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Null(stored.SellerSnapshotJson); Assert.Null(stored.SellerSnapshotPolicy);
        Assert.Equal(InvoiceWorkflowStatus.Draft, stored.InvoiceWorkflowStatus);
        Assert.Null(stored.InvoiceSentForSigningAt); Assert.Null(stored.InvoiceSignedAt); Assert.Null(stored.InvoiceCancelledAt);
        Assert.Null(stored.InvoiceCancelledByUserId); Assert.Null(stored.InvoiceCancelledReason);
        Assert.Null(stored.AdvanceReviewedAt); Assert.Null(stored.AdvanceReviewedByUserId); Assert.Null(stored.AdvanceReviewedByUserName);
        Assert.NotEqual("Forged audit actor", saved.AuditCreatedByUserName); Assert.False(saved.AuditIsDeleted);
        Assert.NotEqual(fixture.ActorId + 10000, saved.AuditCreatedByUserId);
        Assert.NotEqual("Forged updater", saved.AuditUpdatedByUserName); Assert.NotEqual("Forged deletion", saved.AuditDeletedByUserName);
        Assert.Null(saved.AuditDeletedAt); Assert.Null(saved.AuditDeletedByUserId);
        Assert.NotEqual("Forged contractor", saved.ContractorUserName);
        Assert.InRange(stored.CreatedAt, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(2, await db.Payments.CountAsync()); Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LogicalInputAndLegacyBusinessFieldsRemainAvailable(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        var model = PaymentFor(fixture);
        model.SystemNotes = "Ordinary requested note"; model.RescheduleCount = 3;
        model.Account = "input-ref-31"; model.AccountDate = model.Date;
        model.LegacyAccountNumber = "legacy-ref-31"; model.LegacyAccountDate = model.Date;
        model.IsNotForAccounting = true; model.CounterpartyName = "Legacy free text"; model.ClientCaseReference = "Legacy case reference";
        model.InvoiceDocumentTemplateKey = "invoice"; model.PaymentOrderNumber = "PO-31"; model.PaymentDocumentDate = model.Date;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(model.ClientId, stored.ClientId); Assert.Equal(model.LegalEntityId, stored.LegalEntityId);
        Assert.Contains("Ordinary requested note", stored.SystemNotes); Assert.Equal(3, stored.RescheduleCount);
        Assert.Equal("legacy-ref-31", stored.LegacyAccountNumber); Assert.Equal(model.LegacyAccountDate, stored.LegacyAccountDate);
        Assert.True(stored.IsNotForAccounting); Assert.Equal("Legacy free text", stored.CounterpartyName); Assert.Equal("Legacy case reference", stored.ClientCaseReference);
        Assert.Equal("invoice", stored.InvoiceDocumentTemplateKey); Assert.Equal("PO-31", stored.PaymentOrderNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WireReadonlyValuesDoNotBecomeStoredOrReturnedServerState(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = PaymentFor(fixture);
        foreach (var group in new[] { "snapshot", "workflow", "review", "audit", "display" }) SetServerFields(model, fixture.ActorId, group);
        using var server = PaymentCreateGraphTests.Server(db, fixture); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", (await LegacyPaymentOperationKeyTests.RegisterAsync(db, fixture, model)).ToString("D"));
        using var response = await client.PostAsJsonAsync(api == 0 ? "/api/payments" : $"/api/v{api}/payments", model);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); var id = json.GetProperty("id").GetInt32();
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Null(stored.SellerSnapshotJson); Assert.Null(stored.SellerSnapshotPolicy); Assert.Equal(InvoiceWorkflowStatus.Draft, stored.InvoiceWorkflowStatus);
        Assert.Null(stored.AdvanceReviewedAt); Assert.NotEqual(2000, stored.CreatedAt.Year); Assert.False(json.GetProperty("auditIsDeleted").GetBoolean());
        Assert.NotEqual("Forged audit actor", json.GetProperty("auditCreatedByUserName").GetString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task DeniedActorDoesNotReachServerFieldInitialization(int api, bool inactive)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        if (inactive) (await db.Users.SingleAsync()).IsActive = false;
        else (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanCreate = false;
        await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        var model = PaymentFor(fixture); SetServerFields(model, fixture.ActorId, "snapshot");
        var result = await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model);
        if (inactive) Assert.IsType<UnauthorizedResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Contains("Forged company", model.SellerSnapshotJson); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData("sellerSnapshotJson")]
    [InlineData("invoiceWorkflowStatus")]
    [InlineData("createdAt")]
    [InlineData("advanceReviewedByUserName")]
    [InlineData("auditIsDeleted")]
    public async Task DedicatedCommandStillRejectsReadonlyPropertiesAndRetainsItsDraft(string property)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var model = await PaymentCreateFinancialBoundaryTests.NewPaymentAsync(db, seed.Receipt); var controller = PaymentCreateFinancialBoundaryTests.Controller(db, seed.Receipt);
        var attempt = await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller);
        var command = System.Text.Json.Nodes.JsonNode.Parse(PaymentCreateFinancialBoundaryTests.Command(model).GetRawText())!.AsObject();
        command[property] = property == "auditIsDeleted" ? System.Text.Json.Nodes.JsonValue.Create(true) : System.Text.Json.Nodes.JsonValue.Create("Forged readonly value");
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await controller.CommitCreateAttempt(attempt, System.Text.Json.JsonSerializer.SerializeToElement(command), default));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.SingleAsync()).State);
    }

    internal static void SetServerFields(Payment model, int actorId, string group)
    {
        var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (group == "snapshot") { model.SellerSnapshotJson = "{\"CompanyId\":987,\"FullName\":\"Forged company\"}"; model.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate; }
        if (group == "workflow")
        {
            model.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
            model.InvoiceSentForSigningAt = old; model.InvoiceSignedAt = old; model.InvoiceCancelledAt = old;
            model.InvoiceCancelledByUserId = actorId; model.InvoiceCancelledReason = "Forged cancellation";
        }
        if (group == "review") { model.AdvanceReviewedAt = old; model.AdvanceReviewedByUserId = actorId; model.AdvanceReviewedByUserName = "Forged reviewer"; }
        if (group == "audit")
        {
            model.CreatedAt = old; model.AuditCreatedByUserId = actorId + 10000; model.AuditCreatedByUserName = "Forged audit actor";
            model.AuditUpdatedAt = old; model.AuditUpdatedByUserId = actorId + 10000; model.AuditUpdatedByUserName = "Forged updater";
            model.AuditIsDeleted = true; model.AuditDeletedAt = old; model.AuditDeletedByUserId = actorId + 10000; model.AuditDeletedByUserName = "Forged deletion";
        }
        if (group == "display") model.ContractorUserName = "Forged contractor";
    }
}

public sealed class PaymentCreateServerFieldsPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [MemberData(nameof(PaymentCreateServerFieldsTests.Cases), MemberType = typeof(PaymentCreateServerFieldsTests))]
    public async Task NativeCreationStoresOnlyServerOwnedMetadata(int api, string group)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var model = PaymentFor(fixture); PaymentCreateServerFieldsTests.SetServerFields(model, fixture.ActorId, group); var before = DateTime.UtcNow;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Null(stored.SellerSnapshotJson); Assert.Null(stored.SellerSnapshotPolicy); Assert.Equal(InvoiceWorkflowStatus.Draft, stored.InvoiceWorkflowStatus);
        Assert.Null(stored.InvoiceSignedAt); Assert.Null(stored.InvoiceCancelledAt); Assert.Null(stored.InvoiceCancelledByUserId);
        Assert.Null(stored.AdvanceReviewedAt); Assert.Null(stored.AdvanceReviewedByUserName); Assert.InRange(stored.CreatedAt, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        Assert.False(saved.AuditIsDeleted); Assert.NotEqual("Forged audit actor", saved.AuditCreatedByUserName); Assert.NotEqual("Forged contractor", saved.ContractorUserName);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task NativeLogicalAndLegacyFieldsAreNotDropped(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legal); var model = PaymentFor(fixture);
        model.IsNotForAccounting = true; model.CounterpartyName = "Legacy name"; model.ClientCaseReference = "Legacy case";
        model.Account = "input-ref-31"; model.AccountDate = model.Date;
        model.LegacyAccountNumber = "old-ref-31"; model.LegacyAccountDate = model.Date; model.InvoiceDocumentTemplateKey = "invoice";
        model.RescheduleCount = 3; model.SystemNotes = "Ordinary note";
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(model.ClientId, stored.ClientId); Assert.Equal(model.LegalEntityId, stored.LegalEntityId); Assert.Equal("old-ref-31", stored.LegacyAccountNumber);
        Assert.True(stored.IsNotForAccounting); Assert.Equal("Legacy name", stored.CounterpartyName); Assert.Equal("Legacy case", stored.ClientCaseReference);
        Assert.Equal("invoice", stored.InvoiceDocumentTemplateKey); Assert.Equal(3, stored.RescheduleCount); Assert.Contains("Ordinary note", stored.SystemNotes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task NativeAdvanceGetsARealReviewStamp(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legal); var model = PaymentFor(fixture);
        model.PaidAmount = 100m; model.IsPaid = true; model.Status = PaymentStatus.Completed; model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        PaymentCreateServerFieldsTests.SetServerFields(model, fixture.ActorId, "review");
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(fixture.ActorId, stored.AdvanceReviewedByUserId); Assert.NotEqual("Forged reviewer", stored.AdvanceReviewedByUserName); Assert.NotEqual(2000, stored.AdvanceReviewedAt!.Value.Year);
        Assert.Equal(100m, (await db.ClientBalanceTransactions.SingleAsync(t => t.PaymentId == saved.Id && t.Type == ClientBalanceTransactionType.UnallocatedAdvance)).Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeExistingHistoryIsNotReinitialized(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var row = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        var originalDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        row.CreatedAt = originalDate; row.SellerSnapshotJson = "{\"trusted\":\"keep\"}"; row.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile;
        row.InvoiceWorkflowStatus = InvoiceWorkflowStatus.SignedByClient; row.InvoiceSignedAt = originalDate; await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Notes = "Permitted note";
        PaymentCreateServerFieldsTests.SetServerFields(model, fixture.ActorId, "snapshot"); PaymentCreateServerFieldsTests.SetServerFields(model, fixture.ActorId, "workflow");
        var result = api == 0 ? await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, default)
            : await VersionedPaymentWriterTests.WriteAsync(db, fixture, api, "update", model);
        Assert.IsType<OkObjectResult>(result); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(originalDate, stored.CreatedAt); Assert.Equal("{\"trusted\":\"keep\"}", stored.SellerSnapshotJson);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, stored.SellerSnapshotPolicy); Assert.Equal(InvoiceWorkflowStatus.SignedByClient, stored.InvoiceWorkflowStatus); Assert.Equal(originalDate, stored.InvoiceSignedAt);
    }

    private static PayPlanner.Api.Data.PostgreSqlPaymentContext Open(string connection)
        => new(new DbContextOptionsBuilder<PayPlanner.Api.Data.PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
}

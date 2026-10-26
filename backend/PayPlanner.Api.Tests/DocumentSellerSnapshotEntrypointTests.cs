using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class DocumentSellerSnapshotPolicyTests
{
    private const string IssuedEntrypointSnapshot = "{\"ShortName\":\"Ранее выданный продавец\",\"ImagesFrozen\":true}";

    [Theory]
    [InlineData(false, "first-file", false)]
    [InlineData(true, "first-file", false)]
    [InlineData(false, "on-create", false)]
    [InlineData(true, "on-create", false)]
    [InlineData(false, "on-create", true)]
    [InlineData(true, "on-create", true)]
    public async Task CorrectionEndpoint_CapturesNewDocumentPolicyAndKeepsIssuedOriginal(
        bool postgres, string timing, bool callerOwnsTransaction)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var (original, context) = await SeedCorrectionEntrypointAsync(db, timing);
        await using var caller = callerOwnsTransaction ? await db.Database.BeginTransactionAsync() : null;
        var controller = new ActsController(db, new SnapshotEntrypointRequirements()) { ControllerContext = context };

        var result = await controller.CreateCorrection(original.Id,
            new CreateActCorrectionRequest { Reason = "Изменился объём услуг" }, default);

        var dto = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        var correction = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == dto.Id);
        Assert.Equal(original.Id, correction.OriginalActId);
        Assert.Equal(original.Amount, correction.Amount);
        Assert.Equal(timing, correction.SellerSnapshotPolicy);
        var copiedLine = Assert.Single(await db.Set<ActLineItem>().AsNoTracking().Where(line => line.ActId == dto.Id).ToArrayAsync());
        Assert.Equal("Оказанная услуга", copiedLine.Name);
        Assert.Equal(100m, copiedLine.Price);
        Assert.Equal(1m, copiedLine.Quantity);
        var persistedOriginal = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == original.Id);
        Assert.Equal(IssuedEntrypointSnapshot, persistedOriginal.SellerSnapshotJson);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, persistedOriginal.SellerSnapshotPolicy);
        if (timing == DocumentSellerSnapshotPolicy.OnCreate)
        {
            Assert.Contains("Своя", correction.SellerSnapshotJson);
            Assert.NotEqual(IssuedEntrypointSnapshot, correction.SellerSnapshotJson);
            var metadata = Assert.IsType<SellerSnapshotMetadata>(SellerLegalEntityComposer.ReadMetadata(correction.SellerSnapshotJson));
            Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, metadata.FreezeReason);
            Assert.NotNull(metadata.FrozenAtUtc);
            Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
        }
        else
        {
            Assert.Null(correction.SellerSnapshotJson);
            Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
        }
        if (callerOwnsTransaction)
        {
            Assert.Same(caller, db.Database.CurrentTransaction);
            await caller!.RollbackAsync();
            db.ChangeTracker.Clear();
            Assert.Single(await db.Acts.ToArrayAsync());
            Assert.Single(await db.Set<ActLineItem>().ToArrayAsync());
            Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
            Assert.Equal(IssuedEntrypointSnapshot, (await db.Acts.SingleAsync()).SellerSnapshotJson);
        }
        else Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectionEndpoint_LateSaveFailureRollsBackNewActLinesAndImageVersions(bool postgres)
    {
        var fault = new CorrectionSnapshotSaveFault();
        var (db, lease) = await CreateAsync(postgres, fault);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var (original, context) = await SeedCorrectionEntrypointAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
        var controller = new ActsController(db, new SnapshotEntrypointRequirements()) { ControllerContext = context };
        fault.Enabled = true;

        await Assert.ThrowsAsync<SyntheticSnapshotSaveException>(() => controller.CreateCorrection(original.Id,
            new CreateActCorrectionRequest { Reason = "Синтетический отказ сохранения" }, default));

        Assert.True(fault.Triggered);
        Assert.Single(await db.Acts.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.Set<ActLineItem>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.AsNoTracking().ToArrayAsync());
        Assert.Equal(IssuedEntrypointSnapshot, (await db.Acts.AsNoTracking().SingleAsync()).SellerSnapshotJson);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "first-file")]
    [InlineData(true, "first-file")]
    public async Task InvoiceUpdateFork_CopiesIssuedSellerFieldsInsteadOfApplyingCurrentCreationPolicy(
        bool postgres, string? issuedPolicy)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var (client, context) = await SeedEntrypointActorAsync(db);
        var branch = new CompanyBranch { LegalEntityId = company.Id, Name = "Выданное подразделение", Status = CompanyProfileStatuses.Active };
        db.CompanyBranches.Add(branch);
        db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        await db.SaveChangesAsync();
        var date = new DateTime(2026, 4, 10);
        var original = new Payment
        {
            Invoice = new Invoice { Number = "ISSUED-FORK", Date = date },
            Account = "ISSUED-FORK",
            AccountDate = date,
            Date = date,
            PlannedDate = date.AddDays(5),
            Amount = 5000m,
            PaidAmount = 5000m,
            IsPaid = true,
            Status = PaymentStatus.Completed,
            PaidDate = date,
            LastPaymentDate = date,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            ClientId = client.Id,
            SellerCompanyId = company.Id,
            SellerBranchId = branch.Id,
            SellerSnapshotJson = IssuedEntrypointSnapshot,
            SellerSnapshotPolicy = issuedPolicy,
        };
        db.Payments.Add(original);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            InvoiceId = original.InvoiceId,
            Account = original.Account,
            AccountDate = date,
            Date = date.AddDays(35),
            PlannedDate = date.AddDays(35),
            Amount = 2000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            ClientId = client.Id,
        });
        company.ShortName = "Новый профиль не заменяет выданный снимок";
        await db.SaveChangesAsync();
        var originalId = original.Id;
        var branchId = branch.Id;
        db.ChangeTracker.Clear();
        var controller = new InvoicesController(db, null!, new SnapshotEntrypointRequirements()) { ControllerContext = context };

        var action = await controller.Update(originalId, new UpsertInvoiceRequest
        {
            Number = "ISSUED-FORK",
            Date = date,
            DueDate = date.AddDays(5),
            Amount = 25000m,
            ClientId = client.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
        }, default);

        var dto = Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.NotEqual(originalId, dto.Id);
        var rows = await db.Payments.AsNoTracking().Where(payment => payment.Id == originalId || payment.Id == dto.Id).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal(company.Id, row.SellerCompanyId);
            Assert.Equal(branchId, row.SellerBranchId);
            Assert.Equal(IssuedEntrypointSnapshot, row.SellerSnapshotJson);
            Assert.Equal(issuedPolicy, row.SellerSnapshotPolicy);
        }
        var preserved = Assert.Single(rows, payment => payment.Id == originalId);
        Assert.Equal(5000m, preserved.Amount);
        Assert.Equal(5000m, preserved.PaidAmount);
        Assert.True(preserved.IsPaid);
        Assert.Equal(DocumentKind.Payment, preserved.DocumentKind);
        Assert.Equal(25000m, Assert.Single(rows, payment => payment.Id == dto.Id).Amount);
        Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
    }

    private static async Task<(Act Original, ControllerContext Context)> SeedCorrectionEntrypointAsync(PaymentContext db, string timing)
    {
        var company = await SeedAsync(db);
        var (client, context) = await SeedEntrypointActorAsync(db);
        db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = timing });
        var original = new Act
        {
            Number = "ISSUED-CORRECTION",
            ClientId = client.Id,
            Date = new DateTime(2026, 10, 20),
            Amount = 100m,
            Status = ActStatus.Signed,
            SellerCompanyId = company.Id,
            SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile,
            SellerSnapshotJson = IssuedEntrypointSnapshot,
            LineItems = [new ActLineItem { Name = "Оказанная услуга", Quantity = 1m, Price = 100m, SourceKey = "manual-1" }],
        };
        db.Acts.Add(original);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (original, context);
    }

    private static async Task<(Client Client, ControllerContext Context)> SeedEntrypointActorAsync(PaymentContext db)
    {
        var role = new Role { Name = "admin" };
        var user = new User { Email = "seller-entrypoint@example.local", FullName = "Автор документа", PasswordHash = "synthetic", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Контрагент документа", IsActive = true, ResponsibleUser = user };
        db.Users.Add(user); db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (client, new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin"), new Claim(ClaimTypes.Name, user.FullName)], "Test"))
            }
        });
    }

    private sealed class SnapshotEntrypointRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class CorrectionSnapshotSaveFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<Act>()
                .Any(entry => entry.Entity.OriginalActId.HasValue && !string.IsNullOrWhiteSpace(entry.Entity.SellerSnapshotJson)))
            {
                Triggered = true;
                throw new SyntheticSnapshotSaveException();
            }
            return ValueTask.FromResult(result);
        }
    }
}

internal static class SellerSnapshotBoundaryTestData
{
    public static void MakeOversizedProfile(LegalEntity company)
    {
        // Both persisted JSON fields stay inside their actual PostgreSQL limits.
        // Unknown imported bank metadata is explicitly retained by the normalizer;
        // Compose copies this one selected account, rather than discarded accounts.
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var account = CompanyBankAccountList.Normalize(JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "snapshot-budget", accountNumber = "40702810900000000001", bik = "044525225",
                bankName = "Синтетический банк", isPrimary = true, isArchived = false,
                importedDetails = new string('B', 11000),
            },
        }, options));
        Assert.True(account.IsValid, string.Join("; ", account.Errors));
        company.BankAccountsJson = Assert.IsType<string>(account.Json);
        company.DocumentDirectorJson = JsonSerializer.Serialize(new
        {
            fullName = "Синтетический подписант",
            position = "Директор",
            documentBasis = new string('P', 5000),
        }, options);
        Assert.InRange(company.BankAccountsJson.Length, 1, CompanyBankAccountList.MaxJsonLength);
        Assert.InRange(company.DocumentDirectorJson.Length, 1, 8000);
        Assert.True(company.Address is null || company.Address.Length <= 500);
        var snapshot = SellerLegalEntityComposer.ToSnapshotJson(SellerLegalEntityComposer.Compose(company, null));
        Assert.True(snapshot.Length > DocumentSellerService.MaxSnapshotLength,
            $"The composed fixture must reach the snapshot guard; actual length {snapshot.Length}.");
    }
}

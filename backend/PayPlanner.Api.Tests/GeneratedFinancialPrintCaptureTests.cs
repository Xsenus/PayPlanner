using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, true)]
    public async Task FinancialPrintCaptureUsesActualRendererAndFreezesRequestDefaults(bool postgres, GeneratedFinancialDocumentKind kind, bool rounding)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var company = new LegalEntity
        {
            ShortName = "Продавец до принятия",
            Inn = "7707083893",
            Kpp = "770701001",
            ProfileStatus = CompanyProfileStatuses.Active,
            LogoDataUrl = "data:image/png;base64,TE9HTw==",
        };
        f.Db.LegalEntities.Add(company);
        await f.Db.SaveChangesAsync();
        f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var policy = new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.FirstFile };
        f.Db.SystemSettings.Add(policy);
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Items.Single().ServiceName = "Услуга <script>";
        order.Items.Single().SortOrder = 9;
        order.Items.Add(new ClientCaseWorkOrderItem
        {
            ServiceName = "Первая услуга",
            Quantity = 2m,
            UnitPrice = 3.25m,
            SortOrder = 1,
            ExecutorUserId = f.User.Id
        });
        if (rounding)
            foreach (var item in order.Items) { item.Quantity = 0.5m; item.UnitPrice = 0.01m; }
        order.TotalAmount = 99999m; // Stored cached total must not determine the financial graph.
        await f.Db.SaveChangesAsync();
        var controller = f.Controller();
        var method = typeof(CasesV2Controller).GetMethod("BuildFinancialCaptureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var key = Guid.NewGuid();
        var date = new DateTime(2026, 10, 1, 17, 30, 0);
        var invoice = new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest { Date = date, Number = " PRINT-42 ", Description = "  " };
        var act = new CasesV2Controller.CreateClientCaseWorkOrderActRequest { Date = date, Number = " PRINT-42 ", Title = "  " };
        var contract = new CasesV2Controller.CreateClientCaseWorkOrderContractRequest { Date = date, Number = " PRINT-42 ", IsOpenEnded = false, ValidUntil = date.AddYears(1) };
        var calls = 0;
        async Task<GeneratedFinancialDocumentCapture> Capture(ClientCase c, ClientCaseWorkOrder o, GeneratedFinancialIdentity identity, CancellationToken token)
        {
            calls++;
            return await (Task<GeneratedFinancialDocumentCapture>)method.Invoke(controller,
                [c, o, identity, kind, invoice, act, contract, token])!;
        }
        var registry = new GeneratedFinancialDocumentRegistry(f.Db);
        var captured = await registry.CaptureAsync(f.Case.Id, order.Id, f.User.Id, f.User.FullName, key, kind, new string('b', 64), Capture, default);
        Assert.Equal("recorded", captured.Status);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(captured.Receipt);
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await registry.ReadFrozenAsync(receipt, default));
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, frozen.Domain.SellerSnapshotPolicy);
        Assert.Equal(company.Id, frozen.Domain.SellerCompanyId);
        var sellerInfo = SellerLegalEntityComposer.ReadMetadata(frozen.Domain.SellerSnapshotJson)!;
        Assert.False(sellerInfo.LegacyImages);
        Assert.Equal("Продавец до принятия", sellerInfo.CompanyName);
        Assert.NotNull(sellerInfo.FrozenAtUtc);
        var image = Assert.Single(await f.Db.CompanyBrandImageVersions.AsNoTracking().ToArrayAsync());
        Assert.Equal(company.LogoDataUrl, image.DataUrl);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Act ? "1" : "PRINT-42", frozen.Domain.Number); Assert.Equal(date.Date, frozen.Domain.Date);
        var expectedAmount = rounding ? kind == GeneratedFinancialDocumentKind.Act ? 0.01m : 0.02m : 41.5m;
        Assert.Equal(expectedAmount, frozen.Domain.Amount); Assert.Equal(2, frozen.Domain.Lines.Length);
        Assert.Equal("Первая услуга", frozen.Domain.Lines[0].Name);
        Assert.Equal(f.User.Id, frozen.Domain.Lines[0].ExecutorUserId);
        if (kind == GeneratedFinancialDocumentKind.Invoice)
        { Assert.Equal(date.Date, frozen.Domain.DueDate); Assert.StartsWith("Счет по заказ-наряду", frozen.Domain.Description); }
        if (kind == GeneratedFinancialDocumentKind.Act)
        { Assert.StartsWith("Акт по заказ-наряду", frozen.Domain.Title); Assert.StartsWith("Сформирован из заказ-наряда", frozen.Domain.Description); }
        if (kind == GeneratedFinancialDocumentKind.Contract)
        {
            Assert.Equal(date.Date, frozen.Domain.StartDate); Assert.Equal(date.AddYears(1).Date, frozen.Domain.ValidUntil);
            Assert.False(frozen.Domain.IsOpenEnded);
            Assert.EndsWith(rounding ? "Итого: 0,02 ₽." : "Итого: 41,50 ₽.", frozen.Domain.Description);
        }
        var html = Encoding.UTF8.GetString(frozen.Bytes);
        Assert.Contains("<!doctype html>", html); Assert.Contains(frozen.Domain.Number, html);
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("Услуга <script>", html);
        Assert.Contains("Продавец до принятия", html);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        order.Title = "После принятия"; order.Items.First().Quantity = 100;
        company.ShortName = "Позднее имя"; company.LogoDataUrl = null;
        policy.Value = DocumentSellerSnapshotPolicy.OnCreate;
        await f.Db.SaveChangesAsync();
        var replay = await registry.CaptureAsync(f.Case.Id, order.Id, f.User.Id, f.User.FullName, key, kind, new string('b', 64), Capture, default);
        Assert.True(replay.IsReplay); Assert.Equal(receipt, replay.Receipt); Assert.Equal(1, calls);
        Assert.Equal(frozen.Bytes, (await registry.ReadFrozenAsync(receipt, default))!.Bytes);
        var replayDomain = (await registry.ReadFrozenAsync(receipt, default))!.Domain;
        Assert.Equal(frozen.Domain.SellerSnapshotJson, replayDomain.SellerSnapshotJson);
        Assert.Equal(frozen.Domain.SellerSnapshotPolicy, replayDomain.SellerSnapshotPolicy);
        Assert.Equal(image.DataUrl, (await f.Db.CompanyBrandImageVersions.AsNoTracking().SingleAsync()).DataUrl);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        await using var observer = f.Observer();
        await AssertPublishedFinancialGraphAsync(observer, frozen.Domain);
        IDocumentSeller published = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => await observer.Payments.AsNoTracking().SingleAsync(),
            GeneratedFinancialDocumentKind.Act => await observer.Acts.AsNoTracking().SingleAsync(),
            _ => await observer.Contracts.AsNoTracking().SingleAsync(),
        };
        Assert.Equal(frozen.Domain.SellerCompanyId, published.SellerCompanyId);
        Assert.Equal(frozen.Domain.SellerSnapshotJson, published.SellerSnapshotJson);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, published.SellerSnapshotPolicy);
    }
}

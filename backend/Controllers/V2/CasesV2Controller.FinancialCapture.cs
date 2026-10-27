using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    // Called by the trusted registry callback under its case/order/number locks.
    // These detached print models never become ledger rows before the copy gate.
    private async Task<GeneratedFinancialDocumentCapture> BuildFinancialCaptureAsync(
        ClientCase clientCase, ClientCaseWorkOrder order, GeneratedFinancialIdentity identity,
        GeneratedFinancialDocumentKind kind, CreateClientCaseWorkOrderInvoiceRequest? invoiceRequest,
        CreateClientCaseWorkOrderActRequest? actRequest, CreateClientCaseWorkOrderContractRequest? contractRequest,
        CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Financial capture requires the registry transaction.");
        var items = order.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToArray();
        var claims = new GeneratedFinancialClaims(_db);
        if (kind == GeneratedFinancialDocumentKind.Act)
        {
            var used = (await claims.UsedActSourceKeysAsync(items.Select(i =>
                GeneratedFinancialClaims.SourceKey(order.Id, i.Id)).ToArray(), ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(i => !used.Contains(GeneratedFinancialClaims.SourceKey(order.Id, i.Id))).ToArray();
        }
        var amount = kind == GeneratedFinancialDocumentKind.Act
            ? decimal.Round(items.Sum(i => i.Quantity * i.UnitPrice), 2, MidpointRounding.AwayFromZero)
            : decimal.Round(items.Sum(i => decimal.Round(i.Quantity * i.UnitPrice, 2, MidpointRounding.AwayFromZero)), 2, MidpointRounding.AwayFromZero);
        if (items.Length == 0 || amount <= 0 || kind == GeneratedFinancialDocumentKind.Contract
            && !clientCase.ClientId.HasValue && !clientCase.LegalEntityId.HasValue)
            throw new FinancialCaptureRejectedException();
        var clock = await ResolveCaseStatusClockAsync(ct);
        var date = (kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => invoiceRequest?.Date,
            GeneratedFinancialDocumentKind.Act => actRequest?.Date,
            GeneratedFinancialDocumentKind.Contract => contractRequest?.Date,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        })?.Date ?? clock.TodayDate;
        var requestedNumber = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => invoiceRequest?.Number,
            GeneratedFinancialDocumentKind.Act => actRequest?.Number,
            _ => contractRequest?.Number
        };
        var number = await claims.NextNumberAsync(kind, clientCase.Id, order.Id, date, requestedNumber, ct);
        var instant = DateTime.UtcNow;
        // PostgreSQL timestamps retain microseconds. Freeze that same instant
        // for both providers so a financial receipt can compare it exactly.
        var now = new DateTime(instant.Ticks - instant.Ticks % 10, DateTimeKind.Utc);
        var dueDate = kind == GeneratedFinancialDocumentKind.Invoice ? invoiceRequest?.DueDate?.Date ?? date : (DateTime?)null;
        var startDate = kind == GeneratedFinancialDocumentKind.Contract ? contractRequest?.StartDate?.Date ?? date : (DateTime?)null;
        var validUntil = kind == GeneratedFinancialDocumentKind.Contract ? contractRequest?.ValidUntil?.Date : null;
        var openEnded = kind == GeneratedFinancialDocumentKind.Contract && (contractRequest?.IsOpenEnded ?? validUntil is null);
        var title = kind switch
        {
            GeneratedFinancialDocumentKind.Act => NormalizeOptional(actRequest?.Title) ?? $"Акт по заказ-наряду «{order.Title}»",
            GeneratedFinancialDocumentKind.Contract => NormalizeOptional(contractRequest?.Title) ?? $"Договор по заказ-наряду «{order.Title}»",
            _ => null
        };
        var description = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => NormalizeOptional(invoiceRequest?.Description) ?? $"Счет по заказ-наряду «{order.Title}»",
            GeneratedFinancialDocumentKind.Act => NormalizeOptional(actRequest?.Comment) ?? $"Сформирован из заказ-наряда «{order.Title}» по делу {clientCase.Title}.",
            _ => NormalizeOptional(contractRequest?.Description) ?? BuildWorkOrderContractDescription(clientCase, order, items, amount)
        };
        var domain = new GeneratedFinancialDomainSnapshot(1, clientCase.Id, order.Id,
            User.GetUserId() ?? throw new InvalidOperationException("Financial capture requires an actor."), User.GetDisplayName(),
            kind, identity, number, date, dueDate, startDate, validUntil, openEnded, title, description, amount,
            clientCase.ClientId, clientCase.LegalEntityId, NormalizeOptional(clientCase.CaseNumber) ?? clientCase.Title,
            clientCase.Title, order.Title, order.Currency, now,
            items.Select(i => new GeneratedFinancialLine(i.Id, i.ServiceName, i.Quantity, i.UnitPrice, i.SortOrder, i.ExecutorUserId)).ToArray());
        var sellerCapture = await CaptureNewFinancialSellerAsync(ct);
        domain = domain with
        {
            SellerCompanyId = sellerCapture.Snapshot.SellerCompanyId,
            SellerBranchId = sellerCapture.Snapshot.SellerBranchId,
            SellerSnapshotJson = sellerCapture.Snapshot.SellerSnapshotJson,
            SellerSnapshotPolicy = sellerCapture.Snapshot.SellerSnapshotPolicy,
        };
        string html;
        if (kind == GeneratedFinancialDocumentKind.Invoice)
        {
            var payment = new Payment
            {
                Id = identity.EntityId,
                InvoiceId = identity.InvoiceEntityId,
                Account = number,
                AccountDate = date,
                Date = dueDate!.Value,
                PlannedDate = dueDate.Value,
                Amount = amount,
                PaidAmount = 0,
                Status = PaymentStatus.Pending,
                InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
                Type = PaymentType.Income,
                DocumentKind = DocumentKind.Invoice,
                Description = description!,
                ClientId = clientCase.ClientId,
                LegalEntityId = clientCase.LegalEntityId,
                ClientCaseId = clientCase.Id,
                ClientCaseReference = domain.CaseReference,
                CreatedAt = now
            };
            var sort = 1;
            foreach (var item in items)
                payment.InvoiceLineItems.Add(new InvoiceLineItem
                {
                    Name = item.ServiceName,
                    Quantity = item.Quantity,
                    Unit = "шт",
                    Price = item.UnitPrice,
                    SortOrder = sort++,
                    CreatedAt = now
                });
            html = BuildWorkOrderInvoiceDocumentHtml(clientCase, order, payment, number, date, dueDate.Value, clock.NowLocal);
        }
        else if (kind == GeneratedFinancialDocumentKind.Act)
        {
            var act = new Act
            {
                Id = identity.EntityId,
                Number = number,
                Title = title!,
                Date = date,
                Amount = amount,
                CreationMode = ActCreationMode.Manual,
                Status = ActStatus.Created,
                WorkflowStatus = ActWorkflowStatus.Draft,
                ClientId = clientCase.ClientId,
                LegalEntityId = clientCase.LegalEntityId,
                ResponsibleId = items.FirstOrDefault(i => i.ExecutorUserId.HasValue)?.ExecutorUserId,
                Comment = description,
                CreatedAt = now
            };
            var sort = 1;
            foreach (var item in items)
                act.LineItems.Add(new ActLineItem
                {
                    SourceKey = GeneratedFinancialClaims.SourceKey(order.Id, item.Id),
                    SourceSortOrder = item.SortOrder,
                    Name = item.ServiceName,
                    Quantity = item.Quantity,
                    Unit = "шт",
                    Price = item.UnitPrice,
                    SortOrder = sort++,
                    CreatedAt = now
                });
            html = BuildWorkOrderActDocumentHtml(clientCase, order, act, clock.NowLocal);
        }
        else
        {
            var contract = new Contract
            {
                Id = identity.EntityId,
                Number = number,
                Title = title!,
                Date = date,
                StartDate = startDate!.Value,
                ValidUntil = validUntil,
                Description = description,
                BillingMode = ContractBillingMode.Fixed,
                Status = ContractStatus.Draft,
                IsOpenEnded = openEnded,
                Amount = amount,
                CreatedAt = now
            };
            html = BuildWorkOrderContractDocumentHtml(clientCase, order, contract, items, clock.NowLocal);
        }
        return new(domain, AppendFinancialSellerHtml(html, sellerCapture.Seller));
    }

    private async Task<(Act Snapshot, LegalEntity? Seller)> CaptureNewFinancialSellerAsync(CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Фиксация реквизитов файла требует транзакцию создания.");
        // The detached seller carrier is never a ledger record. Both policy modes
        // freeze here because capture is already producing the first actual file.
        var snapshot = new Act();
        var service = new DocumentSellerService(_db);
        var defaultCompany = await OwnCompanyRegistry.LoadDefaultAsync(_db, ct);
        await service.InitializeForCreationAsync(snapshot, defaultCompany, ct);
        var seller = await service.ResolveAsync(snapshot, defaultCompany, freeze: true, ct);
        return (snapshot, seller);
    }

    private static void CopyCapturedSeller(IDocumentSeller source, IDocumentSeller target)
    {
        target.SellerCompanyId = source.SellerCompanyId;
        target.SellerBranchId = source.SellerBranchId;
        target.SellerSnapshotJson = source.SellerSnapshotJson;
        target.SellerSnapshotPolicy = source.SellerSnapshotPolicy;
    }

    private static string AppendFinancialSellerHtml(string html, LegalEntity? seller)
    {
        if (seller is null) return html;
        var requisites = CompanyRequisitesResolver.Resolve(seller, null, DateTime.UtcNow);
        var fields = new[]
        {
            requisites.FullName ?? requisites.Name,
            string.IsNullOrWhiteSpace(requisites.Inn) ? null : $"ИНН {requisites.Inn}",
            string.IsNullOrWhiteSpace(requisites.Kpp) ? null : $"КПП {requisites.Kpp}",
            requisites.Address, requisites.BankName,
            string.IsNullOrWhiteSpace(requisites.Bik) ? null : $"БИК {requisites.Bik}",
            string.IsNullOrWhiteSpace(requisites.AccountNumber) ? null : $"Расчётный счёт {requisites.AccountNumber}",
            string.IsNullOrWhiteSpace(requisites.CorrAccountNumber) ? null : $"Корр. счёт {requisites.CorrAccountNumber}",
        };
        var lines = string.Join("<br>", fields.Where(value => !string.IsNullOrWhiteSpace(value)).Select(System.Net.WebUtility.HtmlEncode));
        var images = new[] { (seller.LogoDataUrl, "Логотип"), (seller.SealDataUrl, "Печать"), (seller.DirectorSignatureDataUrl, "Подпись") }
            .Where(image => !string.IsNullOrWhiteSpace(image.Item1) && InlineImageDataUrlValidator.Validate(image.Item1) is null)
            .Select(image => $"<img src=\"{System.Net.WebUtility.HtmlEncode(image.Item1)}\" alt=\"{image.Item2}\" style=\"max-width:160px;max-height:100px;object-fit:contain\" />");
        var section = $"<section aria-label=\"Реквизиты исполнителя\" style=\"margin-top:18px;border-top:1px solid #e2e8f0;padding-top:12px\"><h2>Реквизиты исполнителя</h2><div>{lines}</div><div>{string.Join(" ", images)}</div></section>";
        var marker = html.LastIndexOf("</main>", StringComparison.Ordinal);
        if (marker < 0) throw new InvalidOperationException("Печатная форма документа не содержит область реквизитов.");
        return html.Insert(marker, section);
    }

    private sealed class FinancialCaptureRejectedException : Exception;
}

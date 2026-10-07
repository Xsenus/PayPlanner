using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
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
        return new(domain, html);
    }

    private sealed class FinancialCaptureRejectedException : Exception;
}

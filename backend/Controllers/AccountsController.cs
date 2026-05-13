using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly PaymentContext _db;
    private static readonly IComparer<string?> NaturalAccountComparer = Comparer<string?>.Create(CompareAccountNumbers);

    public AccountsController(PaymentContext db) => _db = db;

    [HttpGet("registry")]
    public async Task<ActionResult<AccountsPageResponse>> GetRegistry(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] PaymentType? type,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int? clientId = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? clientIds = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] int? contractId = null,
        [FromQuery] bool payablesOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        if (payablesOnly)
        {
            type = PaymentType.Expense;
        }

        var candidateQuery = ApplyRegistryFilters(
            BuildInvoiceCandidateQuery(visibility),
            from,
            to,
            search,
            type,
            clientId,
            legalEntityId,
            clientIds,
            clientRole,
            contractId);
        var candidateItems = await candidateQuery
            .Select(payment => new AccountInvoiceCandidate
            {
                Id = payment.Id,
                InvoiceEntityId = payment.InvoiceId,
                Number = payment.Invoice != null ? payment.Invoice.Number : (payment.Account ?? string.Empty),
                Date = payment.Invoice != null ? payment.Invoice.Date : (payment.AccountDate ?? payment.Date),
                DueDate = payment.Date,
                PlannedDate = payment.PlannedDate,
                Amount = payment.Amount,
                PaidAmount = payment.PaidAmount,
                Type = payment.Type,
                Status = payment.Status,
                WorkflowStatus = payment.InvoiceWorkflowStatus,
                SentForSigningAt = payment.InvoiceSentForSigningAt,
                SignedAt = payment.InvoiceSignedAt,
                CancelledAt = payment.InvoiceCancelledAt,
                CancelledByUserId = payment.InvoiceCancelledByUserId,
                CancelledReason = payment.InvoiceCancelledReason,
                IsPaid = payment.IsPaid,
                PaidDate = payment.PaidDate,
                SystemNotes = payment.SystemNotes,
                HasInstallmentMarker = payment.SystemNotes != null
                    && payment.SystemNotes.Contains(InvoiceManagedPaymentRules.InstallmentPaymentMarker),
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                LegalEntityName = payment.LegalEntity != null ? payment.LegalEntity.ShortName : null,
                LegalEntityInn = payment.LegalEntity != null ? payment.LegalEntity.Inn : null,
                ClientName = payment.Client != null ? payment.Client.Name : null,
                ClientCompany = payment.Client != null ? payment.Client.Company : null,
                ClientStatusId = payment.Client != null ? payment.Client.ClientStatusId : null,
                ClientStatus = payment.Client != null && payment.Client.ClientStatus != null
                    ? new ClientStatusDto
                    {
                        Id = payment.Client.ClientStatus.Id,
                        Name = payment.Client.ClientStatus.Name,
                        Description = payment.Client.ClientStatus.Description,
                        ColorHex = payment.Client.ClientStatus.ColorHex,
                        IsActive = payment.Client.ClientStatus.IsActive,
                        CreatedAt = payment.Client.ClientStatus.CreatedAt,
                        SystemKey = payment.Client.ClientStatus.SystemKey,
                    }
                    : null,
                ExpenseClientId = payment.ExpenseClientId,
                ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
                ExpenseLegalEntityName = payment.ExpenseLegalEntity != null ? payment.ExpenseLegalEntity.ShortName : null,
                ExpenseLegalEntityInn = payment.ExpenseLegalEntity != null ? payment.ExpenseLegalEntity.Inn : null,
                ExpenseClientName = payment.ExpenseClient != null ? payment.ExpenseClient.Name : null,
                ExpenseClientCompany = payment.ExpenseClient != null ? payment.ExpenseClient.Company : null,
                ExpenseClientStatusId = payment.ExpenseClient != null ? payment.ExpenseClient.ClientStatusId : null,
                ExpenseClientStatus = payment.ExpenseClient != null && payment.ExpenseClient.ClientStatus != null
                    ? new ClientStatusDto
                    {
                        Id = payment.ExpenseClient.ClientStatus.Id,
                        Name = payment.ExpenseClient.ClientStatus.Name,
                        Description = payment.ExpenseClient.ClientStatus.Description,
                        ColorHex = payment.ExpenseClient.ClientStatus.ColorHex,
                        IsActive = payment.ExpenseClient.ClientStatus.IsActive,
                        CreatedAt = payment.ExpenseClient.ClientStatus.CreatedAt,
                        SystemKey = payment.ExpenseClient.ClientStatus.SystemKey,
                    }
                    : null,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseTitle = payment.ClientCase != null ? payment.ClientCase.Title : null,
                ClientCaseReference = payment.ClientCaseReference,
                Description = payment.Description,
                IsNotForAccounting = payment.IsNotForAccounting,
                ActReference = payment.Notes,
                ResponsibleId = payment.Responsibles.Select(link => (int?)link.UserId).FirstOrDefault(),
                ResponsibleName = payment.Responsibles
                    .Select(link => link.User != null
                        ? (string.IsNullOrWhiteSpace(link.User.FullName) ? link.User.Email : link.User.FullName)
                        : null)
                    .FirstOrDefault(),
                CounterpartyName = payment.CounterpartyName,
                ServiceId = payment.ServiceId,
                ServiceName = payment.Service != null ? payment.Service.Name : null,
                PaymentStatusName = payment.PaymentStatusEntity != null ? payment.PaymentStatusEntity.Name : null,
                CreatedAt = payment.CreatedAt,
                DocumentKind = payment.DocumentKind,
            })
            .ToListAsync(ct);

        if (candidateItems.Count == 0)
        {
            return Ok(new AccountsPageResponse
            {
                Items = Array.Empty<AccountRegistryRowDto>(),
                Total = 0,
                Page = 1,
                PageSize = pageSize,
                Summary = new AccountsSummaryDto(),
            });
        }

        var groups = candidateItems
            .GroupBy(item => BuildGroupKey(item.InvoiceEntityId, item.Number, item.Date, item.ClientId, item.LegalEntityId, item.ClientCaseId, item.Type, item.Id))
            .Select(group =>
            {
                var orderedGroup = group
                    .OrderByDescending(item => item.DocumentKind == DocumentKind.Invoice)
                    .ThenByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToList();

                var editorPaymentId = group
                    .OrderByDescending(item => item.DocumentKind == DocumentKind.Invoice)
                    .ThenBy(item => item.Id)
                    .Select(item => item.Id)
                    .FirstOrDefault();
                var representative = orderedGroup.FirstOrDefault(item => item.Id == editorPaymentId) ?? orderedGroup[0];
                var invoiceEntityId = orderedGroup
                    .Select(item => item.InvoiceEntityId)
                    .FirstOrDefault(value => value.HasValue && value.Value > 0);

                return new WorkingAccountGroup
                {
                    GroupKey = group.Key,
                    Representative = representative,
                    EditorPaymentId = editorPaymentId > 0 ? editorPaymentId : representative.Id,
                    InvoiceEntityId = invoiceEntityId,
                    LegacyGroupKey = BuildLegacyGroupKey(representative.Number, representative.Date, representative.ClientId, representative.LegalEntityId, representative.ClientCaseId, representative.Type),
                    FallbackAmount = representative.Amount,
                    InvoicePaymentIds = orderedGroup.Select(item => item.Id).ToHashSet(),
                    CandidatePayments = orderedGroup.Select(CreateAggregateItem).ToList(),
                    Type = representative.Type,
                };
            })
            .ToList();

        var canonicalIds = groups
            .Where(group => group.InvoiceEntityId.HasValue)
            .Select(group => group.InvoiceEntityId!.Value)
            .Distinct()
            .ToList();

        var legacyGroupKeys = groups
            .Select(group => group.LegacyGroupKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var legacyAccountNumbers = groups
            .Select(group => group.Representative.Number)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToLower())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var paymentAggregateItems = await _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .Where(payment =>
                (payment.InvoiceId.HasValue && canonicalIds.Contains(payment.InvoiceId.Value))
                || (!payment.InvoiceId.HasValue
                    && payment.Account != null
                    && legacyAccountNumbers.Contains(payment.Account.Trim().ToLower())))
            .Select(payment => new PaymentAggregateItem
            {
                Id = payment.Id,
                InvoiceId = payment.InvoiceId,
                Account = payment.Account,
                AccountDate = payment.AccountDate,
                Date = payment.Date,
                PlannedDate = payment.PlannedDate,
                PaidDate = payment.PaidDate,
                Amount = payment.Amount,
                PaidAmount = payment.PaidAmount,
                Status = payment.Status,
                DocumentKind = payment.DocumentKind,
                SystemNotes = payment.SystemNotes,
                HasInstallmentMarker = payment.SystemNotes != null
                    && payment.SystemNotes.Contains(InvoiceManagedPaymentRules.InstallmentPaymentMarker),
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                ClientCaseId = payment.ClientCaseId,
                Type = payment.Type,
            })
            .ToListAsync(ct);

        var paymentsByLegacyKey = paymentAggregateItems
            .Where(item => item.DocumentKind != DocumentKind.Invoice && !item.InvoiceId.HasValue)
            .Select(item => new { Item = item, LegacyKey = BuildLegacyGroupKey(item.Account, item.AccountDate ?? item.Date, item.ClientId, item.LegalEntityId, item.ClientCaseId, item.Type) })
            .Where(entry => entry.LegacyKey is not null && legacyGroupKeys.Contains(entry.LegacyKey))
            .GroupBy(entry => entry.LegacyKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Item).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupPayments = group.CandidatePayments.ToList();

            if (!string.IsNullOrWhiteSpace(group.LegacyGroupKey)
                && paymentsByLegacyKey.TryGetValue(group.LegacyGroupKey, out var legacyPayments))
            {
                groupPayments.AddRange(legacyPayments.Where(item => !group.InvoicePaymentIds.Contains(item.Id)));
            }

            groupPayments = groupPayments
                .GroupBy(item => item.Id)
                .Select(grouped => grouped.First())
                .ToList();

            var statusSourcePayments = SelectStatusSourcePayments(
                groupPayments,
                group.EditorPaymentId,
                group.Representative.Amount);

            group.RelatedPaymentIds = groupPayments
                .Select(item => item.Id)
                .Distinct()
                .ToList();

            group.DueDate = statusSourcePayments
                .Select(item => EffectivePlannedDate(item))
                .DefaultIfEmpty(group.Representative.DueDate)
                .Max();

            group.PaidDate = statusSourcePayments
                .Where(item => item.PaidDate.HasValue)
                .Select(item => item.PaidDate)
                .DefaultIfEmpty(group.Representative.PaidDate)
                .Max();

            var totalAmount = statusSourcePayments.Sum(item => item.Amount);
            group.TotalAmount = Math.Max(group.FallbackAmount, totalAmount);
            if (statusSourcePayments.Count > 0)
            {
                group.PaidAmount = NormalizeMoneyAmount(statusSourcePayments.Sum(ResolvePaidAmount));
                group.OutstandingAmount = NormalizeMoneyAmount(statusSourcePayments.Sum(ResolveOutstandingAmount));
            }
            else
            {
                group.PaidAmount = ResolvePaidAmount(
                    group.Representative.Amount,
                    group.Representative.PaidAmount,
                    group.Representative.Status);
                group.OutstandingAmount = ResolveOutstandingAmount(
                    group.Representative.Amount,
                    group.Representative.PaidAmount,
                    group.Representative.Status);
            }

            group.Status = DeriveAccountStatus(
                statusSourcePayments.Select(CreateStatusSourcePayment),
                group.Representative.Status,
                group.Representative.PaidAmount);
            group.IsOverdue = statusSourcePayments.Any(item => item.Status == PaymentStatus.Overdue)
                || (statusSourcePayments.Count == 0 && group.Representative.Status == PaymentStatus.Overdue);
        }

        var normalizedStatus = NormalizeAccountStatus(status);
        var filteredGroups = groups
            .Where(group => normalizedStatus is null || string.Equals(group.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
            .Where(group => !payablesOnly || (group.Type == PaymentType.Expense && group.Status is not "paid" and not "cancelled"))
            .ToList();

        var summary = BuildSummary(filteredGroups);
        var orderedGroups = ApplyRegistrySort(filteredGroups, sortBy, sortDir).ToList();
        var total = orderedGroups.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        if (page > totalPages)
        {
            page = totalPages;
        }

        var pagedGroups = orderedGroups
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var representativeIds = pagedGroups
            .Select(group => group.EditorPaymentId)
            .Distinct()
            .ToList();

        var relatedPaymentIds = pagedGroups
            .SelectMany(group => group.RelatedPaymentIds)
            .Distinct()
            .ToList();

        var relatedPayments = relatedPaymentIds.Count == 0
            ? new List<Payment>()
            : await _db.Payments
                .WithPaymentIncludes()
                .AsNoTracking()
                .Where(payment => relatedPaymentIds.Contains(payment.Id))
                .ToListAsync(ct);

        var relatedPaymentsById = relatedPayments.ToDictionary(payment => payment.Id);
        var responsiblesMap = await LoadResponsiblesMapAsync(representativeIds, ct);
        var clientCasesMap = await LoadClientCasesMapAsync(representativeIds, ct);
        var invoiceLineItemsMap = await LoadInvoiceLineItemsMapAsync(representativeIds, ct);

        var candidateIdsForActs = pagedGroups
            .SelectMany(group => group.InvoicePaymentIds)
            .Distinct()
            .ToList();

        var accountNumbers = pagedGroups
            .Select(group => group.Representative.Number)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acts = candidateIdsForActs.Count == 0 && accountNumbers.Count == 0
            ? new List<ActDto>()
            : await _db.Acts
                .AsNoTracking()
                .Where(act =>
                    (act.InvoiceId.HasValue && candidateIdsForActs.Contains(act.InvoiceId.Value))
                    || (!act.InvoiceId.HasValue && act.InvoiceNumber != null && accountNumbers.Contains(act.InvoiceNumber)))
                .OrderByDescending(act => act.Date)
                .ThenByDescending(act => act.Id)
                .Select(act => new ActDto
                {
                    Id = act.Id,
                    Number = act.Number,
                    Title = act.Title,
                    Date = act.Date,
                    Amount = act.Amount,
                    CreationMode = act.CreationMode,
                    InvoiceNumber = act.InvoiceNumber,
                    InvoiceId = act.InvoiceId,
                    ContractId = act.Invoice != null ? act.Invoice.ContractId : null,
                    CounterpartyInn = act.CounterpartyInn,
                    Status = act.Status,
                    ClientId = act.ClientId,
                    ClientName = act.Client != null ? act.Client.Name : null,
                    ResponsibleId = act.ResponsibleId,
                    ResponsibleName = act.Responsible != null
                        ? (string.IsNullOrWhiteSpace(act.Responsible.FullName) ? act.Responsible.Email : act.Responsible.FullName)
                        : null,
                    Comment = act.Comment,
                    LineItems = act.LineItems
                        .OrderBy(item => item.SortOrder)
                        .ThenBy(item => item.Id)
                        .Select(item => new ActLineItemDto
                        {
                            Id = item.Id,
                            InvoiceLineItemId = item.InvoiceLineItemId,
                            SourceKey = item.SourceKey,
                            SourceSortOrder = item.SourceSortOrder,
                            Name = item.Name,
                            Quantity = item.Quantity,
                            Unit = item.Unit,
                            Price = item.Price,
                        })
                        .ToList(),
                    CreatedAt = act.CreatedAt,
                    UpdatedAt = act.UpdatedAt,
                })
                .ToListAsync(ct);

        var items = pagedGroups.Select(group =>
        {
            var groupActs = acts
                .Where(act =>
                    (act.InvoiceId.HasValue && group.InvoicePaymentIds.Contains(act.InvoiceId.Value))
                    || (!act.InvoiceId.HasValue && AreAccountsEqual(act.InvoiceNumber, group.Representative.Number)))
                .GroupBy(act => act.Id)
                .Select(bucket => bucket.First())
                .ToList();

            var invoice = BuildInvoiceDto(
                group,
                responsiblesMap.GetValueOrDefault(group.EditorPaymentId),
                clientCasesMap.GetValueOrDefault(group.EditorPaymentId),
                invoiceLineItemsMap.GetValueOrDefault(group.EditorPaymentId),
                groupActs);

            var payments = group.RelatedPaymentIds
                .Select(id => relatedPaymentsById.GetValueOrDefault(id))
                .Where(payment => payment is not null)
                .Cast<Payment>()
                .OrderBy(payment => payment.PlannedDate == default ? payment.Date : payment.PlannedDate)
                .ThenBy(payment => payment.Id)
                .ToList();

            var visiblePayments = InvoiceManagedPaymentRules.ShouldHideRepresentativeInvoice(payments)
                ? payments.Where(payment => payment.Id != group.EditorPaymentId).ToList()
                : payments;

            return new AccountRegistryRowDto
            {
                GroupKey = group.GroupKey,
                AccountNumber = group.Representative.Number,
                Invoice = invoice,
                Payments = visiblePayments,
                Acts = groupActs,
                TotalAmount = group.TotalAmount,
                DueDate = group.DueDate,
                PaidDate = group.PaidDate,
                Status = group.Status,
                Type = group.Type,
                IsOverdue = group.IsOverdue,
            };
        }).ToList();

        return Ok(new AccountsPageResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Summary = summary,
        });
    }

    [HttpGet("payables-dashboard")]
    public async Task<ActionResult<PayablesDashboardDto>> GetPayablesDashboard(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? search,
        [FromQuery] int? clientId = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? clientIds = null,
        [FromQuery] string? clientRole = null,
        CancellationToken ct = default)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var query = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .WithPaymentIncludes()
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment => payment.Type == PaymentType.Expense)
            .Where(payment => payment.Status != PaymentStatus.Completed && payment.Status != PaymentStatus.Cancelled)
            .Where(payment => payment.Amount > payment.PaidAmount)
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents: true);

        var parsedClientIds = ParseClientIds(clientIds);
        if (clientId.HasValue && clientId.Value > 0)
        {
            parsedClientIds.Add(clientId.Value);
        }

        if (parsedClientIds.Count > 0)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.ClientId.HasValue && parsedClientIds.Contains(payment.ClientId.Value)),
                "expenseClient" => query.Where(payment => payment.ExpenseClientId.HasValue && parsedClientIds.Contains(payment.ExpenseClientId.Value)),
                _ => query.Where(payment =>
                    (payment.ClientId.HasValue && parsedClientIds.Contains(payment.ClientId.Value)) ||
                    (payment.ExpenseClientId.HasValue && parsedClientIds.Contains(payment.ExpenseClientId.Value))),
            };
        }

        if (legalEntityId.HasValue && legalEntityId.Value > 0)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.LegalEntityId == legalEntityId.Value),
                "expenseClient" => query.Where(payment => payment.ExpenseLegalEntityId == legalEntityId.Value),
                _ => query.Where(payment =>
                    payment.LegalEntityId == legalEntityId.Value ||
                    payment.ExpenseLegalEntityId == legalEntityId.Value),
            };
        }

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => (payment.PlannedDate == default ? payment.Date : payment.PlannedDate) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => (payment.PlannedDate == default ? payment.Date : payment.PlannedDate) <= toDate);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(payment =>
                (payment.Account != null && payment.Account.ToLower().Contains(term)) ||
                (payment.Description != null && payment.Description.ToLower().Contains(term)) ||
                (payment.Notes != null && payment.Notes.ToLower().Contains(term)) ||
                (payment.CounterpartyName != null && payment.CounterpartyName.ToLower().Contains(term)) ||
                (payment.Client != null && payment.Client.Name != null && payment.Client.Name.ToLower().Contains(term)) ||
                (payment.Client != null && payment.Client.Company != null && payment.Client.Company.ToLower().Contains(term)) ||
                (payment.ExpenseClient != null && payment.ExpenseClient.Name != null && payment.ExpenseClient.Name.ToLower().Contains(term)) ||
                (payment.ExpenseClient != null && payment.ExpenseClient.Company != null && payment.ExpenseClient.Company.ToLower().Contains(term)) ||
                (payment.ClientCase != null && payment.ClientCase.Title != null && payment.ClientCase.Title.ToLower().Contains(term)));
        }

        var payments = await query.ToListAsync(ct);
        var visiblePayments = InvoiceManagedPaymentRules.FilterVisiblePayments(payments)
            .Where(payment => payment.Amount > payment.PaidAmount)
            .Where(payment => payment.Status is not PaymentStatus.Completed and not PaymentStatus.Cancelled)
            .ToList();

        var today = DateTime.UtcNow.Date;
        var upcomingLimit = today.AddDays(7);
        var rows = visiblePayments
            .Select(payment =>
            {
                var dueDate = (payment.PlannedDate == default ? payment.Date : payment.PlannedDate).Date;
                var outstanding = Math.Round(payment.Amount - payment.PaidAmount, 2);
                return new PayablesDashboardPaymentRow(
                    payment.ClientId,
                    payment.Client?.Name ?? payment.CounterpartyName ?? "Без поставщика",
                    dueDate,
                    outstanding,
                    payment.ClientCaseId,
                    payment.InvoiceClientCases.Select(link => link.ClientCaseId).ToList());
            })
            .Where(row => row.OutstandingAmount > 0m)
            .ToList();

        var response = new PayablesDashboardDto
        {
            TotalOutstanding = rows.Sum(row => row.OutstandingAmount),
            OverdueOutstanding = rows.Where(row => row.DueDate < today).Sum(row => row.OutstandingAmount),
            UpcomingOutstanding = rows.Where(row => row.DueDate >= today && row.DueDate <= upcomingLimit).Sum(row => row.OutstandingAmount),
            SupplierCount = rows.Select(row => row.SupplierId).Distinct().Count(),
            PaymentCount = rows.Count,
            DueBuckets = BuildPayablesDueBuckets(rows, today),
            Suppliers = rows
                .GroupBy(row => new { row.SupplierId, row.SupplierName })
                .Select(group => new PayablesSupplierGroupDto
                {
                    SupplierId = group.Key.SupplierId,
                    SupplierName = group.Key.SupplierName,
                    TotalOutstanding = group.Sum(row => row.OutstandingAmount),
                    OverdueOutstanding = group.Where(row => row.DueDate < today).Sum(row => row.OutstandingAmount),
                    NearestDueDate = group.Min(row => row.DueDate),
                    PaymentCount = group.Count(),
                    CaseCount = group
                        .SelectMany(row => row.LinkedCaseIds)
                        .Where(caseId => caseId > 0)
                        .Distinct()
                        .Count(),
                })
                .OrderByDescending(group => group.OverdueOutstanding > 0m)
                .ThenBy(group => group.NearestDueDate ?? DateTime.MaxValue)
                .ThenByDescending(group => group.TotalOutstanding)
                .ToList(),
        };

        return Ok(response);
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] string? clientRole,
        [FromQuery] string? q,
        [FromQuery] bool withDate = false,
        [FromQuery] bool dedupe = false,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var query = _db.Payments.AsNoTracking()
            .ApplyPaymentVisibility(visibility)
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.Account != null && payment.Account != string.Empty);

        if (clientId.HasValue)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.ClientId == clientId.Value),
                "expenseClient" => query.Where(payment => payment.ExpenseClientId == clientId.Value),
                _ => query.Where(payment => payment.ClientId == clientId.Value || payment.ExpenseClientId == clientId.Value),
            };
        }

        if (caseId.HasValue)
        {
            query = query.Where(payment =>
                payment.ClientCaseId == caseId.Value
                || payment.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(payment => payment.Account!.ToLower().Contains(term));
        }

        if (!withDate)
        {
            var accounts = await query
                .GroupBy(payment => payment.Account!)
                .Select(group => new { Account = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Account)
                .Select(item => item.Account)
                .Take(take)
                .ToListAsync(ct);

            return Ok(accounts);
        }

        var pairsQuery = query.Select(payment => new
        {
            Account = payment.Account!,
            AccountDate = payment.AccountDate,
            SortDate = payment.AccountDate ?? payment.Date,
        });

        if (dedupe)
        {
            pairsQuery = pairsQuery.Distinct();
        }

        var result = await pairsQuery
            .OrderByDescending(item => item.SortDate)
            .ThenBy(item => item.Account)
            .Select(item => new { account = item.Account, accountDate = item.AccountDate })
            .Take(take)
            .ToListAsync(ct);

        return Ok(result);
    }

    private IQueryable<Payment> BuildInvoiceCandidateQuery(DataVisibilityAccess? visibility = null)
    {
        var query = _db.Payments.AsNoTracking();
        if (visibility is not null)
        {
            query = query.ApplyPaymentVisibility(visibility);
        }

        return query
            .Where(payment =>
                (payment.InvoiceId != null && payment.Invoice != null)
                || (payment.DocumentKind == DocumentKind.Invoice && payment.Account != null && payment.Account != string.Empty));
    }

    private IQueryable<Payment> ApplyRegistryFilters(
        IQueryable<Payment> query,
        DateTime? from,
        DateTime? to,
        string? search,
        PaymentType? type,
        int? clientId,
        int? legalEntityId,
        string? clientIds,
        string? clientRole,
        int? contractId)
    {
        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
        }

        var parsedClientIds = ParseClientIds(clientIds);
        if (clientId.HasValue && clientId.Value > 0)
        {
            parsedClientIds.Add(clientId.Value);
        }

        if (parsedClientIds.Count > 0)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.ClientId.HasValue && parsedClientIds.Contains(payment.ClientId.Value)),
                "expenseClient" => query.Where(payment => payment.ExpenseClientId.HasValue && parsedClientIds.Contains(payment.ExpenseClientId.Value)),
                _ => query.Where(payment =>
                    (payment.ClientId.HasValue && parsedClientIds.Contains(payment.ClientId.Value)) ||
                    (payment.ExpenseClientId.HasValue && parsedClientIds.Contains(payment.ExpenseClientId.Value))),
            };
        }

        if (legalEntityId.HasValue && legalEntityId.Value > 0)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.LegalEntityId == legalEntityId.Value),
                "expenseClient" => query.Where(payment => payment.ExpenseLegalEntityId == legalEntityId.Value),
                _ => query.Where(payment =>
                    payment.LegalEntityId == legalEntityId.Value ||
                    payment.ExpenseLegalEntityId == legalEntityId.Value),
            };
        }

        if (contractId.HasValue && contractId.Value > 0)
        {
            query = query.Where(payment =>
                payment.ContractId == contractId.Value
                || _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == payment.Id));
        }

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => (payment.Invoice != null ? payment.Invoice.Date : (payment.AccountDate ?? payment.Date)) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => (payment.Invoice != null ? payment.Invoice.Date : (payment.AccountDate ?? payment.Date)) <= toDate);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(payment =>
                ((payment.Invoice != null && payment.Invoice.Number.ToLower().Contains(term)) || (payment.Account != null && payment.Account.ToLower().Contains(term)))
                || (payment.Description != null && payment.Description.ToLower().Contains(term))
                || (payment.Notes != null && payment.Notes.ToLower().Contains(term))
                || (payment.CounterpartyName != null && payment.CounterpartyName.ToLower().Contains(term))
                || (payment.Client != null && payment.Client.Name != null && payment.Client.Name.ToLower().Contains(term))
                || (payment.Client != null && payment.Client.Company != null && payment.Client.Company.ToLower().Contains(term))
                || (payment.LegalEntity != null && payment.LegalEntity.ShortName.ToLower().Contains(term))
                || (payment.LegalEntity != null && payment.LegalEntity.FullName != null && payment.LegalEntity.FullName.ToLower().Contains(term))
                || (payment.LegalEntity != null && payment.LegalEntity.Inn != null && payment.LegalEntity.Inn.ToLower().Contains(term))
                || (payment.ExpenseClient != null && payment.ExpenseClient.Name != null && payment.ExpenseClient.Name.ToLower().Contains(term))
                || (payment.ExpenseClient != null && payment.ExpenseClient.Company != null && payment.ExpenseClient.Company.ToLower().Contains(term))
                || (payment.ExpenseLegalEntity != null && payment.ExpenseLegalEntity.ShortName.ToLower().Contains(term))
                || (payment.ExpenseLegalEntity != null && payment.ExpenseLegalEntity.FullName != null && payment.ExpenseLegalEntity.FullName.ToLower().Contains(term))
                || (payment.ExpenseLegalEntity != null && payment.ExpenseLegalEntity.Inn != null && payment.ExpenseLegalEntity.Inn.ToLower().Contains(term))
                || (payment.ClientCase != null && payment.ClientCase.Title != null && payment.ClientCase.Title.ToLower().Contains(term))
                || payment.InvoiceClientCases.Any(link => link.ClientCase != null && link.ClientCase.Title != null && link.ClientCase.Title.ToLower().Contains(term))
                || payment.Acts.Any(act =>
                    act.Number.ToLower().Contains(term)
                    || (act.Title != null && act.Title.ToLower().Contains(term))
                    || (act.InvoiceNumber != null && act.InvoiceNumber.ToLower().Contains(term))
                    || (act.CounterpartyInn != null && act.CounterpartyInn.ToLower().Contains(term))
                    || (act.Comment != null && act.Comment.ToLower().Contains(term))
                    || (act.Client != null && act.Client.Name != null && act.Client.Name.ToLower().Contains(term))));
        }

        return query;
    }

    private static HashSet<int> ParseClientIds(string? clientIds)
    {
        if (string.IsNullOrWhiteSpace(clientIds))
        {
            return new HashSet<int>();
        }

        return clientIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    private static IEnumerable<WorkingAccountGroup> ApplyRegistrySort(
        IEnumerable<WorkingAccountGroup> groups,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy ?? "date").Trim().ToLowerInvariant() switch
        {
            "number" => desc
                ? groups.OrderByDescending(group => group.Representative.Number, NaturalAccountComparer)
                    .ThenByDescending(group => group.Representative.Date)
                    .ThenByDescending(group => group.GroupKey, StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(group => group.Representative.Number, NaturalAccountComparer)
                    .ThenBy(group => group.Representative.Date)
                    .ThenBy(group => group.GroupKey, StringComparer.OrdinalIgnoreCase),
            "amount" => desc
                ? groups.OrderByDescending(group => group.TotalAmount).ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.TotalAmount).ThenBy(group => group.GroupKey),
            "counterparty" or "client" => desc
                ? groups.OrderByDescending(group => group.Representative.ClientName ?? group.Representative.CounterpartyName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.Representative.ClientName ?? group.Representative.CounterpartyName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(group => group.GroupKey),
            "duedate" => desc
                ? groups.OrderByDescending(group => group.DueDate ?? DateTime.MinValue).ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.DueDate ?? DateTime.MaxValue).ThenBy(group => group.GroupKey),
            "responsible" => desc
                ? groups.OrderByDescending(group => group.Representative.ResponsibleName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.Representative.ResponsibleName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(group => group.GroupKey),
            _ => desc
                ? groups.OrderByDescending(group => group.Representative.Date)
                    .ThenByDescending(group => group.Representative.Number, NaturalAccountComparer)
                    .ThenByDescending(group => group.GroupKey, StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(group => group.Representative.Date)
                    .ThenBy(group => group.Representative.Number, NaturalAccountComparer)
                    .ThenBy(group => group.GroupKey, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static int CompareAccountNumbers(string? left, string? right)
    {
        var leftValue = (left ?? string.Empty).Trim();
        var rightValue = (right ?? string.Empty).Trim();
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < leftValue.Length && rightIndex < rightValue.Length)
        {
            var leftChar = leftValue[leftIndex];
            var rightChar = rightValue[rightIndex];

            if (char.IsDigit(leftChar) && char.IsDigit(rightChar))
            {
                var leftDigitsStart = leftIndex;
                while (leftIndex < leftValue.Length && char.IsDigit(leftValue[leftIndex]))
                {
                    leftIndex++;
                }

                var rightDigitsStart = rightIndex;
                while (rightIndex < rightValue.Length && char.IsDigit(rightValue[rightIndex]))
                {
                    rightIndex++;
                }

                var numberComparison = CompareNumericSegments(
                    leftValue.AsSpan(leftDigitsStart, leftIndex - leftDigitsStart),
                    rightValue.AsSpan(rightDigitsStart, rightIndex - rightDigitsStart));

                if (numberComparison != 0)
                {
                    return numberComparison;
                }

                continue;
            }

            var normalizedLeft = char.ToUpperInvariant(leftChar);
            var normalizedRight = char.ToUpperInvariant(rightChar);
            if (normalizedLeft != normalizedRight)
            {
                return normalizedLeft.CompareTo(normalizedRight);
            }

            leftIndex++;
            rightIndex++;
        }

        if (leftIndex < leftValue.Length)
        {
            return 1;
        }

        if (rightIndex < rightValue.Length)
        {
            return -1;
        }

        return 0;
    }

    private static int CompareNumericSegments(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var normalizedLeft = TrimLeadingZeros(left);
        var normalizedRight = TrimLeadingZeros(right);

        if (normalizedLeft.Length != normalizedRight.Length)
        {
            return normalizedLeft.Length.CompareTo(normalizedRight.Length);
        }

        for (var index = 0; index < normalizedLeft.Length; index++)
        {
            if (normalizedLeft[index] != normalizedRight[index])
            {
                return normalizedLeft[index].CompareTo(normalizedRight[index]);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length - 1 && value[index] == '0')
        {
            index++;
        }

        return value[index..];
    }

    private static AccountsSummaryDto BuildSummary(IEnumerable<WorkingAccountGroup> groups)
    {
        var summary = new MutableAccountsSummary();

        foreach (var group in groups)
        {
            summary.Total.Amount += group.TotalAmount;
            summary.Total.Count += 1;

            switch (group.Status)
            {
                case "paid":
                    summary.Paid.Amount += ResolvePaidAccountSummaryAmount(group);
                    summary.Paid.Count += 1;
                    break;
                case "cancelled":
                    summary.Cancelled.Amount += group.TotalAmount;
                    summary.Cancelled.Count += 1;
                    break;
                default:
                    summary.Pending.Amount += ResolveOutstandingAccountSummaryAmount(group);
                    summary.Pending.Count += 1;
                    break;
            }

            if (group.IsOverdue)
            {
                summary.Overdue.Amount += ResolveOutstandingAccountSummaryAmount(group);
                summary.Overdue.Count += 1;
            }
        }

        return new AccountsSummaryDto
        {
            Total = summary.Total.ToDto(),
            Pending = summary.Pending.ToDto(),
            Paid = summary.Paid.ToDto(),
            Overdue = summary.Overdue.ToDto(),
            Cancelled = summary.Cancelled.ToDto(),
        };
    }

    private static List<PayablesDueBucketDto> BuildPayablesDueBuckets(
        IReadOnlyCollection<PayablesDashboardPaymentRow> rows,
        DateTime today)
    {
        var buckets = new[]
        {
            new { Key = "overdue", Label = "Просрочено", Predicate = (Func<PayablesDashboardPaymentRow, bool>)(row => row.DueDate < today) },
            new { Key = "today", Label = "Сегодня", Predicate = (Func<PayablesDashboardPaymentRow, bool>)(row => row.DueDate == today) },
            new { Key = "week", Label = "7 дней", Predicate = (Func<PayablesDashboardPaymentRow, bool>)(row => row.DueDate > today && row.DueDate <= today.AddDays(7)) },
            new { Key = "later", Label = "Позже", Predicate = (Func<PayablesDashboardPaymentRow, bool>)(row => row.DueDate > today.AddDays(7)) },
        };

        return buckets
            .Select(bucket =>
            {
                var bucketRows = rows.Where(bucket.Predicate).ToList();
                return new PayablesDueBucketDto
                {
                    Key = bucket.Key,
                    Label = bucket.Label,
                    Amount = bucketRows.Sum(row => row.OutstandingAmount),
                    Count = bucketRows.Count,
                };
            })
            .ToList();
    }

    private static InvoiceDto BuildInvoiceDto(
        WorkingAccountGroup group,
        IReadOnlyList<InvoiceResponsibleDto>? responsibles,
        IReadOnlyList<InvoiceClientCaseDto>? clientCases,
        IReadOnlyList<InvoiceLineItemDto>? lineItems,
        IReadOnlyList<ActDto> acts)
    {
        var representative = group.Representative;
        var effectiveInvoiceStatus = MapAccountStatusToInvoiceStatus(group.Status, representative.Status);
        var invoice = new InvoiceDto
        {
            Id = group.EditorPaymentId,
            InvoiceEntityId = representative.InvoiceEntityId,
            Number = representative.Number,
            Date = representative.Date,
            DueDate = group.DueDate ?? representative.DueDate,
            Amount = representative.Amount,
            PaidAmount = group.PaidAmount,
            OutstandingAmount = group.OutstandingAmount,
            Type = representative.Type,
            Status = effectiveInvoiceStatus,
            WorkflowStatus = representative.WorkflowStatus,
            SentForSigningAt = representative.SentForSigningAt,
            SignedAt = representative.SignedAt,
            CancelledAt = representative.CancelledAt,
            CancelledByUserId = representative.CancelledByUserId,
            CancelledReason = representative.CancelledReason,
            IsPaid = effectiveInvoiceStatus == PaymentStatus.Completed,
            PaidDate = group.PaidDate ?? representative.PaidDate,
            ClientId = representative.ClientId,
            LegalEntityId = representative.LegalEntityId,
            LegalEntityName = representative.LegalEntityName,
            LegalEntityInn = representative.LegalEntityInn,
            ClientName = representative.ClientName,
            ClientCompany = representative.ClientCompany,
            ClientStatusId = representative.ClientStatusId,
            ClientStatus = representative.ClientStatus,
            ExpenseClientId = representative.ExpenseClientId,
            ExpenseLegalEntityId = representative.ExpenseLegalEntityId,
            ExpenseLegalEntityName = representative.ExpenseLegalEntityName,
            ExpenseLegalEntityInn = representative.ExpenseLegalEntityInn,
            ExpenseClientName = representative.ExpenseClientName,
            ExpenseClientCompany = representative.ExpenseClientCompany,
            ExpenseClientStatusId = representative.ExpenseClientStatusId,
            ExpenseClientStatus = representative.ExpenseClientStatus,
            ClientCaseId = representative.ClientCaseId,
            ClientCaseTitle = representative.ClientCaseTitle,
            ClientCaseReference = representative.ClientCaseReference,
            Description = representative.Description,
            IsNotForAccounting = representative.IsNotForAccounting,
            ActReference = representative.ActReference,
            ResponsibleId = representative.ResponsibleId,
            ResponsibleName = representative.ResponsibleName,
            CounterpartyName = representative.CounterpartyName,
            ServiceId = representative.ServiceId,
            ServiceName = representative.ServiceName,
            PaymentStatusName = representative.PaymentStatusName,
            CreatedAt = representative.CreatedAt,
            DocumentKind = representative.DocumentKind,
            Responsibles = responsibles?.ToList() ?? new List<InvoiceResponsibleDto>(),
            ClientCases = clientCases?.ToList() ?? new List<InvoiceClientCaseDto>(),
            LineItems = lineItems?.ToList() ?? new List<InvoiceLineItemDto>(),
            Acts = acts.Select(act => new InvoiceActDto
            {
                Id = act.Id,
                Number = act.Number,
                Title = act.Title,
                Date = act.Date,
                Amount = act.Amount,
                CreationMode = act.CreationMode ?? ActCreationMode.Manual,
                Status = act.Status,
                ClientId = act.ClientId,
                ClientName = act.ClientName,
                LegalEntityId = act.LegalEntityId,
                LegalEntityName = act.LegalEntityName,
                ResponsibleId = act.ResponsibleId,
                ResponsibleName = act.ResponsibleName,
                Comment = act.Comment,
                LineItems = act.LineItems ?? Array.Empty<ActLineItemDto>(),
            }).ToList(),
        };

        invoice.ClientCaseIds = invoice.ClientCases.Select(item => item.Id).ToList();
        invoice.ActIds = invoice.Acts.Select(item => item.Id).ToList();

        if (invoice.Responsibles.Count > 0)
        {
            invoice.ResponsibleId = invoice.Responsibles[0].Id;
            invoice.ResponsibleName = invoice.Responsibles[0].FullName;
        }

        if (invoice.ClientCases.Count > 0)
        {
            invoice.ClientCaseId = invoice.ClientCases[0].Id;
            invoice.ClientCaseTitle = invoice.ClientCases[0].Title;
        }

        if (invoice.Acts.Count > 0)
        {
            invoice.ActId = invoice.Acts[0].Id;
            invoice.ActNumber = invoice.Acts[0].Number;
            invoice.ActTitle = invoice.Acts[0].Title;
            invoice.ActStatus = invoice.Acts[0].Status;
        }

        return invoice;
    }

    private static PaymentStatus MapAccountStatusToInvoiceStatus(string status, PaymentStatus fallbackStatus)
    {
        return status.ToLowerInvariant() switch
        {
            "paid" => PaymentStatus.Completed,
            "partial" => PaymentStatus.Processing,
            "overdue" => PaymentStatus.Overdue,
            "cancelled" => PaymentStatus.Cancelled,
            "created" => PaymentStatus.Pending,
            _ => fallbackStatus,
        };
    }

    private async Task<Dictionary<int, List<InvoiceResponsibleDto>>> LoadResponsiblesMapAsync(
        IEnumerable<int> paymentIds,
        CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceResponsibleDto>>();
        }

        var rows = await _db.PaymentResponsibles
            .AsNoTracking()
            .Include(link => link.User)
            .Where(link => ids.Contains(link.PaymentId))
            .OrderBy(link => link.User!.LastName)
            .ThenBy(link => link.User!.FirstName)
            .ThenBy(link => link.User!.MiddleName)
            .ThenBy(link => link.User!.FullName)
            .Select(link => new
            {
                link.PaymentId,
                link.UserId,
                FullName = string.IsNullOrWhiteSpace(link.User!.LastName)
                    ? (string.IsNullOrWhiteSpace(link.User.FullName) ? link.User.Email : link.User.FullName)
                    : ($"{link.User.LastName} {((string.IsNullOrWhiteSpace(link.User.FirstName) ? string.Empty : link.User.FirstName[0] + "."))}{((string.IsNullOrWhiteSpace(link.User.MiddleName) ? string.Empty : link.User.MiddleName[0] + "."))}").Trim(),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.PaymentId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => new InvoiceResponsibleDto
                {
                    Id = row.UserId,
                    FullName = row.FullName,
                }).ToList());
    }

    private async Task<Dictionary<int, List<InvoiceClientCaseDto>>> LoadClientCasesMapAsync(
        IEnumerable<int> paymentIds,
        CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceClientCaseDto>>();
        }

        var paymentMeta = await _db.Payments
            .AsNoTracking()
            .Where(payment => ids.Contains(payment.Id))
            .Select(payment => new
            {
                payment.Id,
                payment.ClientCaseId,
            })
            .ToListAsync(ct);

        var legacyCaseIds = paymentMeta
            .Where(payment => payment.ClientCaseId.HasValue)
            .Select(payment => payment.ClientCaseId!.Value)
            .Distinct()
            .ToList();

        var legacyCases = legacyCaseIds.Count == 0
            ? new Dictionary<int, InvoiceClientCaseDto>()
            : await _db.ClientCases
                .AsNoTracking()
                .Where(clientCase => legacyCaseIds.Contains(clientCase.Id))
                .Select(clientCase => new InvoiceClientCaseDto
                {
                    Id = clientCase.Id,
                    ClientId = clientCase.ClientId,
                    LegalEntityId = clientCase.LegalEntityId,
                    Title = clientCase.Title,
                    Status = clientCase.Status,
                    CreatedAt = clientCase.CreatedAt,
                })
                .ToDictionaryAsync(clientCase => clientCase.Id, ct);

        var result = new Dictionary<int, List<InvoiceClientCaseDto>>();

        foreach (var payment in paymentMeta)
        {
            if (!payment.ClientCaseId.HasValue
                || !legacyCases.TryGetValue(payment.ClientCaseId.Value, out var clientCase))
            {
                continue;
            }

            result[payment.Id] = new List<InvoiceClientCaseDto> { clientCase };
        }

        var linkedRows = await _db.InvoiceClientCases
            .AsNoTracking()
            .Where(link => ids.Contains(link.PaymentId))
            .OrderByDescending(link => link.ClientCase.CreatedAt)
            .ThenByDescending(link => link.ClientCaseId)
            .Select(link => new
            {
                link.PaymentId,
                ClientCase = new InvoiceClientCaseDto
                {
                    Id = link.ClientCase.Id,
                    ClientId = link.ClientCase.ClientId,
                    LegalEntityId = link.ClientCase.LegalEntityId,
                    Title = link.ClientCase.Title,
                    Status = link.ClientCase.Status,
                    CreatedAt = link.ClientCase.CreatedAt,
                },
            })
            .ToListAsync(ct);

        foreach (var row in linkedRows)
        {
            if (!result.TryGetValue(row.PaymentId, out var bucket))
            {
                bucket = new List<InvoiceClientCaseDto>();
                result[row.PaymentId] = bucket;
            }

            if (bucket.Any(item => item.Id == row.ClientCase.Id))
            {
                continue;
            }

            bucket.Add(row.ClientCase);
        }

        return result;
    }

    private async Task<Dictionary<int, List<InvoiceLineItemDto>>> LoadInvoiceLineItemsMapAsync(
        IEnumerable<int> paymentIds,
        CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceLineItemDto>>();
        }

        var rows = await _db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.PaymentId))
            .OrderBy(item => item.PaymentId)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.PaymentId,
                LineItem = new InvoiceLineItemDto
                {
                    Id = item.Id,
                    SortOrder = item.SortOrder,
                    SourceKey = $"db:{item.Id}",
                    ServiceId = item.ServiceId,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    Price = item.Price,
                },
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.PaymentId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.LineItem).ToList());
    }

    private static PaymentAggregateItem CreateAggregateItem(AccountInvoiceCandidate payment)
        => new()
        {
            Id = payment.Id,
            InvoiceId = payment.InvoiceEntityId,
            Account = payment.Number,
            AccountDate = payment.Date,
            Date = payment.DueDate ?? payment.Date,
            PlannedDate = payment.PlannedDate ?? payment.DueDate ?? payment.Date,
            PaidDate = payment.PaidDate,
            Amount = payment.Amount,
            PaidAmount = payment.PaidAmount,
            Status = payment.Status,
            DocumentKind = payment.DocumentKind,
            SystemNotes = payment.SystemNotes,
            HasInstallmentMarker = payment.HasInstallmentMarker,
            ClientId = payment.ClientId,
            ClientCaseId = payment.ClientCaseId,
            Type = payment.Type,
        };

    private static StatusSourcePayment CreateStatusSourcePayment(PaymentAggregateItem payment)
        => new()
        {
            Id = payment.Id,
            Amount = payment.Amount,
            PaidAmount = payment.PaidAmount,
            Status = payment.Status,
        };

    private static List<PaymentAggregateItem> SelectStatusSourcePayments(
        IReadOnlyCollection<PaymentAggregateItem> payments,
        int representativePaymentId,
        decimal representativeAmount)
    {
        if (payments.Count == 0)
        {
            return new List<PaymentAggregateItem>();
        }

        var ordered = payments
            .OrderBy(payment => EffectivePlannedDate(payment) ?? DateTime.MaxValue)
            .ThenBy(payment => payment.Id)
            .ToList();

        if (IsConvertedLegacyInstallmentAggregate(ordered, representativePaymentId, representativeAmount))
        {
            return ordered;
        }

        if (ShouldHideRepresentativeInvoice(ordered, representativePaymentId, representativeAmount))
        {
            return ordered
                .Where(payment => payment.Id != representativePaymentId)
                .ToList();
        }

        var linkedPayments = ordered
            .Where(payment => payment.DocumentKind != DocumentKind.Invoice)
            .ToList();

        return linkedPayments.Count > 0 ? linkedPayments : ordered;
    }

    private static bool ShouldHideRepresentativeInvoice(
        IReadOnlyCollection<PaymentAggregateItem> payments,
        int representativePaymentId,
        decimal representativeAmount)
    {
        if (IsConvertedLegacyInstallmentAggregate(payments, representativePaymentId, representativeAmount))
        {
            return false;
        }

        return payments.Any(payment => payment.HasInstallmentMarker)
            || IsLegacyBrokenInstallmentAggregate(payments, representativePaymentId, representativeAmount);
    }

    private static bool IsConvertedLegacyInstallmentAggregate(
        IReadOnlyCollection<PaymentAggregateItem> payments,
        int representativePaymentId,
        decimal representativeAmount)
    {
        if (payments.Count < 3 || representativeAmount <= 0m)
        {
            return false;
        }

        var generatedInstallments = payments
            .Where(payment => payment.Id != representativePaymentId && payment.HasInstallmentMarker)
            .ToList();
        var generatedInstallmentTotal = generatedInstallments.Sum(payment => payment.Amount);

        return generatedInstallmentTotal > representativeAmount
               && !HasConsistentInstallmentToPay(generatedInstallments, generatedInstallmentTotal);
    }

    private static bool HasConsistentInstallmentToPay(IEnumerable<PaymentAggregateItem> payments, decimal expectedTotal)
        => payments.Any(payment => TryReadInstallmentToPay(payment.SystemNotes, out var toPay)
                                   && AreAmountsEqual(toPay, expectedTotal));

    private static bool TryReadInstallmentToPay(string? systemNotes, out decimal toPay)
    {
        toPay = 0m;
        if (string.IsNullOrWhiteSpace(systemNotes))
        {
            return false;
        }

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(InvoiceManagedPaymentRules.InstallmentPaymentMarker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine))
        {
            return false;
        }

        var json = markerLine[InvoiceManagedPaymentRules.InstallmentPaymentMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetDecimalProperty(document.RootElement, "ToPay", out toPay);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDecimalProperty(JsonElement element, string name, out decimal value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out value))
            {
                return true;
            }

            break;
        }

        value = 0m;
        return false;
    }

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.01m;

    private static bool IsLegacyBrokenInstallmentAggregate(
        IReadOnlyCollection<PaymentAggregateItem> payments,
        int representativePaymentId,
        decimal representativeAmount)
    {
        if (payments.Count < 3 || payments.Any(payment => payment.HasInstallmentMarker))
        {
            return false;
        }

        var invoiceRows = payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .ToList();
        if (invoiceRows.Count < 2)
        {
            return false;
        }

        var nonInvoiceCount = payments.Count - invoiceRows.Count;
        if (nonInvoiceCount > 1)
        {
            return false;
        }

        var maxSiblingAmount = payments
            .Where(payment => payment.Id != representativePaymentId)
            .Select(payment => payment.Amount)
            .DefaultIfEmpty(0m)
            .Max();

        return representativeAmount > maxSiblingAmount;
    }

    private static string DeriveAccountStatus(
        IEnumerable<StatusSourcePayment> payments,
        PaymentStatus fallbackStatus,
        decimal fallbackPaidAmount = 0m)
    {
        var paymentList = payments.ToList();
        if (paymentList.Count == 0)
        {
            if (fallbackPaidAmount > 0m)
            {
                return "partial";
            }

            return fallbackStatus switch
            {
                PaymentStatus.Completed => "paid",
                PaymentStatus.Cancelled => "cancelled",
                PaymentStatus.Processing => "partial",
                PaymentStatus.Overdue => "overdue",
                _ => "created",
            };
        }

        if (paymentList.All(payment => payment.Status == PaymentStatus.Cancelled))
        {
            return "cancelled";
        }

        if (paymentList.All(payment => payment.Status == PaymentStatus.Completed))
        {
            return "paid";
        }

        if (paymentList.Any(payment => payment.Status == PaymentStatus.Overdue))
        {
            return "overdue";
        }

        if (paymentList.Any(payment =>
                payment.Status == PaymentStatus.Completed
                || payment.Status == PaymentStatus.Processing
                || payment.PaidAmount > 0m))
        {
            return "partial";
        }

        return "created";
    }

    private static decimal ResolvePaidAmount(PaymentAggregateItem payment)
        => ResolvePaidAmount(payment.Amount, payment.PaidAmount, payment.Status);

    private static decimal ResolveOutstandingAmount(PaymentAggregateItem payment)
        => ResolveOutstandingAmount(payment.Amount, payment.PaidAmount, payment.Status);

    private static decimal ResolvePaidAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        var normalizedAmount = NormalizeMoneyAmount(amount);
        var normalizedPaidAmount = Math.Min(NormalizeMoneyAmount(paidAmount), normalizedAmount);
        if (normalizedPaidAmount > 0m)
        {
            return normalizedPaidAmount;
        }

        return status == PaymentStatus.Completed ? normalizedAmount : 0m;
    }

    private static decimal ResolveOutstandingAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        if (status is PaymentStatus.Completed or PaymentStatus.Cancelled)
        {
            return 0m;
        }

        var normalizedAmount = NormalizeMoneyAmount(amount);
        var resolvedPaidAmount = ResolvePaidAmount(normalizedAmount, paidAmount, status);
        return NormalizeMoneyAmount(Math.Max(normalizedAmount - resolvedPaidAmount, 0m));
    }

    private static decimal ResolveOutstandingAccountSummaryAmount(WorkingAccountGroup group)
        => group.OutstandingAmount > 0m
            ? group.OutstandingAmount
            : ResolveOutstandingAmount(group.TotalAmount, group.PaidAmount, MapAccountStatusToPaymentStatus(group.Status));

    private static decimal ResolvePaidAccountSummaryAmount(WorkingAccountGroup group)
        => group.PaidAmount > 0m
            ? group.PaidAmount
            : ResolvePaidAmount(group.TotalAmount, group.PaidAmount, MapAccountStatusToPaymentStatus(group.Status));

    private static PaymentStatus MapAccountStatusToPaymentStatus(string status)
        => status switch
        {
            "paid" => PaymentStatus.Completed,
            "cancelled" => PaymentStatus.Cancelled,
            "overdue" => PaymentStatus.Overdue,
            "partial" => PaymentStatus.Processing,
            _ => PaymentStatus.Pending,
        };

    private static decimal NormalizeMoneyAmount(decimal amount)
        => Math.Round(Math.Max(amount, 0m), 2, MidpointRounding.AwayFromZero);

    private static DateTime? EffectivePlannedDate(PaymentAggregateItem payment)
        => payment.PlannedDate == default ? payment.Date : payment.PlannedDate;

    private static bool AreAccountsEqual(string? left, string? right)
        => string.Equals(NormalizeAccount(left), NormalizeAccount(right), StringComparison.OrdinalIgnoreCase);

    private static string BuildGroupKey(
        int? invoiceEntityId,
        string? accountNumber,
        DateTime accountDate,
        int? clientId,
        int? legalEntityId,
        int? clientCaseId,
        PaymentType type,
        int fallbackId)
    {
        if (invoiceEntityId.HasValue && invoiceEntityId.Value > 0)
        {
            return $"entity:{invoiceEntityId.Value}";
        }

        var legacyKey = BuildLegacyGroupKey(accountNumber, accountDate, clientId, legalEntityId, clientCaseId, type);
        return legacyKey is not null ? $"legacy:{legacyKey}" : $"legacy:id:{fallbackId}";
    }

    private static string? BuildLegacyGroupKey(
        string? accountNumber,
        DateTime? accountDate,
        int? clientId,
        int? legalEntityId,
        int? clientCaseId,
        PaymentType type)
    {
        var normalizedAccount = NormalizeAccount(accountNumber);
        var ownerKey = clientId.HasValue && clientId.Value > 0
            ? $"client:{clientId.Value}"
            : legalEntityId.HasValue && legalEntityId.Value > 0
                ? $"legal:{legalEntityId.Value}"
                : null;
        if (normalizedAccount is null || ownerKey is null)
        {
            return null;
        }

        var year = (accountDate ?? DateTime.MinValue).Year;
        return $"{normalizedAccount}::{year}::{ownerKey}::case:{clientCaseId?.ToString() ?? "none"}::type:{type}";
    }

    private static string? NormalizeAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeAccountStatus(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "created" => "created",
            "partial" => "partial",
            "overdue" => "overdue",
            "paid" => "paid",
            "cancelled" => "cancelled",
            _ => null,
        };
    }

    private sealed class AccountInvoiceCandidate
    {
        public int Id { get; init; }
        public int? InvoiceEntityId { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateTime Date { get; init; }
        public DateTime? DueDate { get; init; }
        public DateTime? PlannedDate { get; init; }
        public decimal Amount { get; init; }
        public decimal PaidAmount { get; init; }
        public PaymentType Type { get; init; }
        public PaymentStatus Status { get; init; }
        public string WorkflowStatus { get; init; } = InvoiceWorkflowStatus.Draft;
        public DateTime? SentForSigningAt { get; init; }
        public DateTime? SignedAt { get; init; }
        public DateTime? CancelledAt { get; init; }
        public int? CancelledByUserId { get; init; }
        public string? CancelledReason { get; init; }
        public bool IsPaid { get; init; }
        public DateTime? PaidDate { get; init; }
        public string? SystemNotes { get; init; }
        public bool HasInstallmentMarker { get; init; }
        public int? ClientId { get; init; }
        public int? LegalEntityId { get; init; }
        public string? LegalEntityName { get; init; }
        public string? LegalEntityInn { get; init; }
        public string? ClientName { get; init; }
        public string? ClientCompany { get; init; }
        public int? ClientStatusId { get; init; }
        public ClientStatusDto? ClientStatus { get; init; }
        public int? ExpenseClientId { get; init; }
        public int? ExpenseLegalEntityId { get; init; }
        public string? ExpenseLegalEntityName { get; init; }
        public string? ExpenseLegalEntityInn { get; init; }
        public string? ExpenseClientName { get; init; }
        public string? ExpenseClientCompany { get; init; }
        public int? ExpenseClientStatusId { get; init; }
        public ClientStatusDto? ExpenseClientStatus { get; init; }
        public int? ClientCaseId { get; init; }
        public string? ClientCaseTitle { get; init; }
        public string? ClientCaseReference { get; init; }
        public string? Description { get; init; }
        public bool IsNotForAccounting { get; init; }
        public string? ActReference { get; init; }
        public int? ResponsibleId { get; init; }
        public string? ResponsibleName { get; init; }
        public string? CounterpartyName { get; init; }
        public int? ServiceId { get; init; }
        public string? ServiceName { get; init; }
        public string? PaymentStatusName { get; init; }
        public DateTime CreatedAt { get; init; }
        public DocumentKind DocumentKind { get; init; }
    }

    private sealed class PaymentAggregateItem
    {
        public int Id { get; init; }
        public int? InvoiceId { get; init; }
        public string? Account { get; init; }
        public DateTime? AccountDate { get; init; }
        public DateTime Date { get; init; }
        public DateTime PlannedDate { get; init; }
        public DateTime? PaidDate { get; init; }
        public decimal Amount { get; init; }
        public decimal PaidAmount { get; init; }
        public PaymentStatus Status { get; init; }
        public DocumentKind DocumentKind { get; init; }
        public string? SystemNotes { get; init; }
        public bool HasInstallmentMarker { get; init; }
        public int? ClientId { get; init; }
        public int? LegalEntityId { get; init; }
        public int? ClientCaseId { get; init; }
        public PaymentType Type { get; init; }
    }

    private sealed class StatusSourcePayment
    {
        public int Id { get; init; }
        public decimal Amount { get; init; }
        public decimal PaidAmount { get; init; }
        public PaymentStatus Status { get; init; }
    }

    private sealed record PayablesDashboardPaymentRow(
        int? SupplierId,
        string SupplierName,
        DateTime DueDate,
        decimal OutstandingAmount,
        int? LegacyCaseId,
        IReadOnlyCollection<int> InvoiceCaseIds)
    {
        public IEnumerable<int> LinkedCaseIds =>
            InvoiceCaseIds.Concat(LegacyCaseId.HasValue ? new[] { LegacyCaseId.Value } : Array.Empty<int>());
    }

    private sealed class WorkingAccountGroup
    {
        public string GroupKey { get; init; } = string.Empty;
        public AccountInvoiceCandidate Representative { get; init; } = null!;
        public int EditorPaymentId { get; init; }
        public int? InvoiceEntityId { get; init; }
        public string? LegacyGroupKey { get; init; }
        public HashSet<int> InvoicePaymentIds { get; init; } = new();
        public List<PaymentAggregateItem> CandidatePayments { get; init; } = new();
        public List<int> RelatedPaymentIds { get; set; } = new();
        public decimal FallbackAmount { get; init; }
        public decimal TotalAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? PaidDate { get; set; }
        public string Status { get; set; } = "created";
        public PaymentType Type { get; init; }
        public bool IsOverdue { get; set; }
    }

    private sealed class MutableAccountsSummary
    {
        public MutableBucket Total { get; } = new();
        public MutableBucket Pending { get; } = new();
        public MutableBucket Paid { get; } = new();
        public MutableBucket Overdue { get; } = new();
        public MutableBucket Cancelled { get; } = new();
    }

    private sealed class MutableBucket
    {
        public decimal Amount { get; set; }
        public int Count { get; set; }

        public AccountsSummaryBucketDto ToDto() => new()
        {
            Amount = Amount,
            Count = Count,
        };
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly PaymentContext _db;

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
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var candidateQuery = ApplyRegistryFilters(BuildInvoiceCandidateQuery(), from, to, search, type);
        var candidateItems = await candidateQuery
            .Select(payment => new AccountInvoiceCandidate
            {
                Id = payment.Id,
                InvoiceEntityId = payment.InvoiceId,
                Number = payment.Invoice != null ? payment.Invoice.Number : (payment.Account ?? string.Empty),
                Date = payment.Invoice != null ? payment.Invoice.Date : (payment.AccountDate ?? payment.Date),
                DueDate = payment.Date,
                Amount = payment.Amount,
                Type = payment.Type,
                Status = payment.Status,
                IsPaid = payment.IsPaid,
                PaidDate = payment.PaidDate,
                ClientId = payment.ClientId,
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
                    }
                    : null,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseTitle = payment.ClientCase != null ? payment.ClientCase.Title : null,
                ClientCaseReference = payment.ClientCaseReference,
                Description = payment.Description,
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
            .GroupBy(item => BuildGroupKey(item.InvoiceEntityId, item.Number, item.Date, item.ClientId, item.ClientCaseId, item.Type, item.Id))
            .Select(group =>
            {
                var orderedGroup = group
                    .OrderByDescending(item => item.DocumentKind == DocumentKind.Invoice)
                    .ThenByDescending(item => item.Date)
                    .ThenByDescending(item => item.Id)
                    .ToList();

                var representative = orderedGroup[0];
                var invoiceEntityId = orderedGroup
                    .Select(item => item.InvoiceEntityId)
                    .FirstOrDefault(value => value.HasValue && value.Value > 0);

                return new WorkingAccountGroup
                {
                    GroupKey = group.Key,
                    Representative = representative,
                    InvoiceEntityId = invoiceEntityId,
                    LegacyGroupKey = BuildLegacyGroupKey(representative.Number, representative.Date, representative.ClientId, representative.ClientCaseId, representative.Type),
                    FallbackAmount = orderedGroup.Sum(item => item.Amount),
                    InvoicePaymentIds = orderedGroup.Select(item => item.Id).ToHashSet(),
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

        var paymentAggregateItems = await _db.Payments
            .AsNoTracking()
            .ApplyPaymentFilters(from, to, null, null, null)
            .Where(payment =>
                (payment.InvoiceId.HasValue && canonicalIds.Contains(payment.InvoiceId.Value))
                || (!payment.InvoiceId.HasValue && payment.Account != null && payment.Account != string.Empty))
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
                Status = payment.Status,
                DocumentKind = payment.DocumentKind,
                ClientId = payment.ClientId,
                ClientCaseId = payment.ClientCaseId,
                Type = payment.Type,
            })
            .ToListAsync(ct);

        var paymentsByInvoiceEntityId = paymentAggregateItems
            .Where(item => item.DocumentKind != DocumentKind.Invoice && item.InvoiceId.HasValue)
            .GroupBy(item => item.InvoiceId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var paymentsByLegacyKey = paymentAggregateItems
            .Where(item => item.DocumentKind != DocumentKind.Invoice && !item.InvoiceId.HasValue)
            .Select(item => new { Item = item, LegacyKey = BuildLegacyGroupKey(item.Account, item.AccountDate ?? item.Date, item.ClientId, item.ClientCaseId, item.Type) })
            .Where(entry => entry.LegacyKey is not null && legacyGroupKeys.Contains(entry.LegacyKey))
            .GroupBy(entry => entry.LegacyKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Item).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupPayments = new List<PaymentAggregateItem>();

            if (group.InvoiceEntityId.HasValue
                && paymentsByInvoiceEntityId.TryGetValue(group.InvoiceEntityId.Value, out var canonicalPayments))
            {
                groupPayments.AddRange(canonicalPayments);
            }

            if (!string.IsNullOrWhiteSpace(group.LegacyGroupKey)
                && paymentsByLegacyKey.TryGetValue(group.LegacyGroupKey, out var legacyPayments))
            {
                groupPayments.AddRange(legacyPayments);
            }

            groupPayments = groupPayments
                .GroupBy(item => item.Id)
                .Select(grouped => grouped.First())
                .ToList();

            group.RelatedPaymentIds = group.InvoicePaymentIds
                .Concat(groupPayments.Select(item => item.Id))
                .Distinct()
                .ToList();

            group.DueDate = groupPayments
                .Select(item => EffectivePlannedDate(item))
                .DefaultIfEmpty(group.Representative.DueDate)
                .Max();

            group.PaidDate = groupPayments
                .Where(item => item.PaidDate.HasValue)
                .Select(item => item.PaidDate)
                .DefaultIfEmpty(group.Representative.PaidDate)
                .Max();

            var totalAmount = groupPayments.Sum(item => item.Amount);
            group.TotalAmount = totalAmount > 0m ? totalAmount : group.FallbackAmount;
            group.Status = DeriveAccountStatus(groupPayments, group.Representative.Status);
            group.IsOverdue = groupPayments.Any(item => item.Status == PaymentStatus.Overdue);
        }

        var normalizedStatus = NormalizeAccountStatus(status);
        var filteredGroups = groups
            .Where(group => normalizedStatus is null || string.Equals(group.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
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
            .Select(group => group.Representative.Id)
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
                responsiblesMap.GetValueOrDefault(group.Representative.Id),
                clientCasesMap.GetValueOrDefault(group.Representative.Id),
                groupActs);

            var payments = group.RelatedPaymentIds
                .Select(id => relatedPaymentsById.GetValueOrDefault(id))
                .Where(payment => payment is not null)
                .Cast<Payment>()
                .OrderBy(payment => payment.PlannedDate == default ? payment.Date : payment.PlannedDate)
                .ThenBy(payment => payment.Id)
                .ToList();

            return new AccountRegistryRowDto
            {
                GroupKey = group.GroupKey,
                AccountNumber = group.Representative.Number,
                Invoice = invoice,
                Payments = payments,
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

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] string? q,
        [FromQuery] bool withDate = false,
        [FromQuery] bool dedupe = false,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var query = _db.Payments.AsNoTracking()
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.Account != null && payment.Account != string.Empty);

        if (clientId.HasValue)
        {
            query = query.Where(payment => payment.ClientId == clientId.Value);
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

    private IQueryable<Payment> BuildInvoiceCandidateQuery()
    {
        return _db.Payments.AsNoTracking()
            .Where(payment =>
                (payment.InvoiceId != null && payment.Invoice != null)
                || (payment.DocumentKind == DocumentKind.Invoice && payment.Account != null && payment.Account != string.Empty));
    }

    private static IQueryable<Payment> ApplyRegistryFilters(
        IQueryable<Payment> query,
        DateTime? from,
        DateTime? to,
        string? search,
        PaymentType? type)
    {
        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
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

    private static IEnumerable<WorkingAccountGroup> ApplyRegistrySort(
        IEnumerable<WorkingAccountGroup> groups,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy ?? "date").Trim().ToLowerInvariant() switch
        {
            "number" => desc
                ? groups.OrderByDescending(group => group.Representative.Number, StringComparer.OrdinalIgnoreCase).ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.Representative.Number, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.GroupKey),
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
                ? groups.OrderByDescending(group => group.Representative.Date).ThenByDescending(group => group.GroupKey)
                : groups.OrderBy(group => group.Representative.Date).ThenBy(group => group.GroupKey),
        };
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
                    summary.Paid.Amount += group.TotalAmount;
                    summary.Paid.Count += 1;
                    break;
                case "cancelled":
                    summary.Cancelled.Amount += group.TotalAmount;
                    summary.Cancelled.Count += 1;
                    break;
                default:
                    summary.Pending.Amount += group.TotalAmount;
                    summary.Pending.Count += 1;
                    break;
            }

            if (group.IsOverdue)
            {
                summary.Overdue.Amount += group.TotalAmount;
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

    private static InvoiceDto BuildInvoiceDto(
        WorkingAccountGroup group,
        IReadOnlyList<InvoiceResponsibleDto>? responsibles,
        IReadOnlyList<InvoiceClientCaseDto>? clientCases,
        IReadOnlyList<ActDto> acts)
    {
        var representative = group.Representative;
        var invoice = new InvoiceDto
        {
            Id = representative.Id,
            InvoiceEntityId = representative.InvoiceEntityId,
            Number = representative.Number,
            Date = representative.Date,
            DueDate = group.DueDate ?? representative.DueDate,
            Amount = representative.Amount,
            Type = representative.Type,
            Status = representative.Status,
            IsPaid = representative.IsPaid,
            PaidDate = group.PaidDate ?? representative.PaidDate,
            ClientId = representative.ClientId,
            ClientName = representative.ClientName,
            ClientCompany = representative.ClientCompany,
            ClientStatusId = representative.ClientStatusId,
            ClientStatus = representative.ClientStatus,
            ClientCaseId = representative.ClientCaseId,
            ClientCaseTitle = representative.ClientCaseTitle,
            ClientCaseReference = representative.ClientCaseReference,
            Description = representative.Description,
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
            Acts = acts.Select(act => new InvoiceActDto
            {
                Id = act.Id,
                Number = act.Number,
                Title = act.Title,
                Date = act.Date,
                Amount = act.Amount,
                Status = act.Status,
                ClientId = act.ClientId,
                ClientName = act.ClientName,
                ResponsibleId = act.ResponsibleId,
                ResponsibleName = act.ResponsibleName,
                Comment = act.Comment,
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

    private static string DeriveAccountStatus(IEnumerable<PaymentAggregateItem> payments, PaymentStatus fallbackStatus)
    {
        var paymentList = payments.ToList();
        if (paymentList.Count == 0)
        {
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
                || payment.Status == PaymentStatus.Processing))
        {
            return "partial";
        }

        return "created";
    }

    private static DateTime? EffectivePlannedDate(PaymentAggregateItem payment)
        => payment.PlannedDate == default ? payment.Date : payment.PlannedDate;

    private static bool AreAccountsEqual(string? left, string? right)
        => string.Equals(NormalizeAccount(left), NormalizeAccount(right), StringComparison.OrdinalIgnoreCase);

    private static string BuildGroupKey(
        int? invoiceEntityId,
        string? accountNumber,
        DateTime accountDate,
        int? clientId,
        int? clientCaseId,
        PaymentType type,
        int fallbackId)
    {
        if (invoiceEntityId.HasValue && invoiceEntityId.Value > 0)
        {
            return $"entity:{invoiceEntityId.Value}";
        }

        var legacyKey = BuildLegacyGroupKey(accountNumber, accountDate, clientId, clientCaseId, type);
        return legacyKey is not null ? $"legacy:{legacyKey}" : $"legacy:id:{fallbackId}";
    }

    private static string? BuildLegacyGroupKey(
        string? accountNumber,
        DateTime? accountDate,
        int? clientId,
        int? clientCaseId,
        PaymentType type)
    {
        var normalizedAccount = NormalizeAccount(accountNumber);
        if (normalizedAccount is null || !clientId.HasValue || clientId.Value <= 0)
        {
            return null;
        }

        var year = (accountDate ?? DateTime.MinValue).Year;
        return $"{normalizedAccount}::{year}::client:{clientId.Value}::case:{clientCaseId?.ToString() ?? "none"}::type:{type}";
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
        public decimal Amount { get; init; }
        public PaymentType Type { get; init; }
        public PaymentStatus Status { get; init; }
        public bool IsPaid { get; init; }
        public DateTime? PaidDate { get; init; }
        public int? ClientId { get; init; }
        public string? ClientName { get; init; }
        public string? ClientCompany { get; init; }
        public int? ClientStatusId { get; init; }
        public ClientStatusDto? ClientStatus { get; init; }
        public int? ClientCaseId { get; init; }
        public string? ClientCaseTitle { get; init; }
        public string? ClientCaseReference { get; init; }
        public string? Description { get; init; }
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
        public PaymentStatus Status { get; init; }
        public DocumentKind DocumentKind { get; init; }
        public int? ClientId { get; init; }
        public int? ClientCaseId { get; init; }
        public PaymentType Type { get; init; }
    }

    private sealed class WorkingAccountGroup
    {
        public string GroupKey { get; init; } = string.Empty;
        public AccountInvoiceCandidate Representative { get; init; } = null!;
        public int? InvoiceEntityId { get; init; }
        public string? LegacyGroupKey { get; init; }
        public HashSet<int> InvoicePaymentIds { get; init; } = new();
        public List<int> RelatedPaymentIds { get; set; } = new();
        public decimal FallbackAmount { get; init; }
        public decimal TotalAmount { get; set; }
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

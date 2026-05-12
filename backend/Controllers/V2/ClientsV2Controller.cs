using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/clients")]
[Authorize]
public class ClientsV2Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV2Controller(PaymentContext db) => _db = db;

    private IQueryable<Client> BuildVisibleClientsQuery(int? currentUserId, bool isAdmin)
        => _db.Clients.ApplyClientVisibility(isAdmin, currentUserId);

    private async Task<(int? UserId, bool IsAdmin)> GetCurrentAccessAsync(CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return (null, false);
        }

        var isAdmin = await _db.IsUserAdminAsync(currentUserId.Value, ct);
        return (currentUserId, isAdmin);
    }

    private static string? FormatResponsibleUserName(User? user)
    {
        if (user is null) return null;
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
    }

    private static void PopulateResponsibleUserNames(IEnumerable<Client> clients)
    {
        foreach (var client in clients)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int? id,
        [FromQuery] string? name,
        [FromQuery] string? email,
        [FromQuery] string? phone,
        [FromQuery] string? company,
        [FromQuery] string? address,
        [FromQuery] string? notes,
        [FromQuery] int? legalEntityId,
        [FromQuery] string? legalEntityShortName,
        [FromQuery] string? legalEntityFullName,
        [FromQuery] string? legalEntityInn,
        [FromQuery] string? legalEntityOgrn,
        [FromQuery] string? legalEntityKpp,
        [FromQuery] int? clientStatusId,
        [FromQuery] string? clientStatus,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default,
        [FromQuery] string? counterpartyRole = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var q = BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim().ToLower();
            q = q.Where(c =>
                (c.Name != null && c.Name.ToLower().Contains(search)) ||
                (c.Email != null && c.Email.ToLower().Contains(search)) ||
                (c.Phone != null && c.Phone.ToLower().Contains(search)) ||
                (c.Company != null && c.Company.ToLower().Contains(search)) ||
                (c.Address != null && c.Address.ToLower().Contains(search)) ||
                (c.LegalEntity != null && c.LegalEntity.ShortName.ToLower().Contains(search)) ||
                (c.LegalEntity != null && c.LegalEntity.FullName != null && c.LegalEntity.FullName.ToLower().Contains(search)) ||
                (c.LegalEntity != null && c.LegalEntity.Inn != null && c.LegalEntity.Inn.ToLower().Contains(search)) ||
                (c.LegalEntity != null && c.LegalEntity.Kpp != null && c.LegalEntity.Kpp.ToLower().Contains(search)) ||
                (c.LegalEntity != null && c.LegalEntity.Ogrn != null && c.LegalEntity.Ogrn.ToLower().Contains(search)));
        }

        if (id.HasValue)
            q = q.Where(c => c.Id == id.Value);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var term = name.Trim().ToLower();
            q = q.Where(c => c.Name != null && c.Name.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(email))
        {
            var term = email.Trim().ToLower();
            q = q.Where(c => c.Email != null && c.Email.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var term = phone.Trim().ToLower();
            q = q.Where(c => c.Phone != null && c.Phone.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(company))
        {
            var term = company.Trim().ToLower();
            q = q.Where(c => c.Company != null && c.Company.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(address))
        {
            var term = address.Trim().ToLower();
            q = q.Where(c => c.Address != null && c.Address.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(notes))
        {
            var term = notes.Trim().ToLower();
            q = q.Where(c => c.Notes != null && c.Notes.ToLower().Contains(term));
        }
        if (legalEntityId.HasValue)
            q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
        if (!string.IsNullOrWhiteSpace(legalEntityShortName))
        {
            var term = legalEntityShortName.Trim().ToLower();
            q = q.Where(c => c.LegalEntity != null && c.LegalEntity.ShortName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(legalEntityFullName))
        {
            var term = legalEntityFullName.Trim().ToLower();
            q = q.Where(c => c.LegalEntity != null && c.LegalEntity.FullName != null && c.LegalEntity.FullName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(legalEntityInn))
        {
            var term = legalEntityInn.Trim().ToLower();
            q = q.Where(c => c.LegalEntity != null && c.LegalEntity.Inn != null && c.LegalEntity.Inn.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(legalEntityOgrn))
        {
            var term = legalEntityOgrn.Trim().ToLower();
            q = q.Where(c => c.LegalEntity != null && c.LegalEntity.Ogrn != null && c.LegalEntity.Ogrn.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(legalEntityKpp))
        {
            var term = legalEntityKpp.Trim().ToLower();
            q = q.Where(c => c.LegalEntity != null && c.LegalEntity.Kpp != null && c.LegalEntity.Kpp.ToLower().Contains(term));
        }
        if (clientStatusId.HasValue)
            q = q.Where(c => c.ClientStatusId == clientStatusId.Value);
        if (!string.IsNullOrWhiteSpace(clientStatus))
        {
            var statusValue = clientStatus.Trim();
            if (string.Equals(statusValue, "__none__", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(c => c.ClientStatusId == null);
            }
            else
            {
                statusValue = statusValue.ToLower();
                q = q.Where(c => c.ClientStatus != null && c.ClientStatus.Name != null && c.ClientStatus.Name.ToLower().Contains(statusValue));
            }
        }

        if (isActive.HasValue)
            q = q.Where(c => c.IsActive == isActive.Value);

        q = q.ApplyCounterpartyRoleFilter(counterpartyRole);

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        q = (sortBy?.ToLowerInvariant()) switch
        {
            "createdat" => (desc
                ? q.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.CreatedAt)
                : q.OrderByDescending(c => c.IsActive).ThenBy(c => c.CreatedAt)),
            _ => (desc
                ? q.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.Name)
                : q.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)),
        };

        var total = await q.CountAsync(ct);
        var items = await q.AsNoTracking().Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        PopulateResponsibleUserNames(items);

        var itemIds = items.Select(c => c.Id).ToList();
        if (itemIds.Count > 0)
        {
            var casesCountByClient = await _db.ClientCases
                .AsNoTracking()
                .Where(c => c.ClientId.HasValue && itemIds.Contains(c.ClientId.Value))
                .GroupBy(c => c.ClientId!.Value)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ClientId, x => x.Count, ct);

            var casesByClient = await _db.ClientCases
                .AsNoTracking()
                .Where(c => c.ClientId.HasValue && itemIds.Contains(c.ClientId.Value))
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync(ct);

            var caseLinksByClient = casesByClient
                .Where(c => c.ClientId.HasValue)
                .GroupBy(c => c.ClientId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => new RelatedDocumentLink
                    {
                        Id = c.Id,
                        Label = string.IsNullOrWhiteSpace(c.Title) ? $"Дело #{c.Id}" : c.Title,
                        SecondaryLabel = c.Status.ToString(),
                        ClientId = c.ClientId,
                        LegalEntityId = c.LegalEntityId,
                        Date = c.CreatedAt,
                    }).ToList());

            var contractsCountByClient = await _db.ClientContracts
                .AsNoTracking()
                .Where(c => itemIds.Contains(c.ClientId))
                .GroupBy(c => c.ClientId)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ClientId, x => x.Count, ct);

            var contractRows = await _db.ClientContracts
                .AsNoTracking()
                .Where(c => itemIds.Contains(c.ClientId))
                .Select(c => new
                {
                    c.ClientId,
                    c.ContractId,
                    c.Contract.Number,
                    c.Contract.Title,
                    c.Contract.Date,
                })
                .ToListAsync(ct);

            var contractLinksByClient = contractRows
                .GroupBy(row => row.ClientId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(row => row.Date)
                        .Select(row => new RelatedDocumentLink
                        {
                            Id = row.ContractId,
                            Label = !string.IsNullOrWhiteSpace(row.Number)
                                ? $"№{row.Number}"
                                : !string.IsNullOrWhiteSpace(row.Title)
                                    ? row.Title!
                                    : $"Договор #{row.ContractId}",
                            SecondaryLabel = row.Title,
                            ClientId = row.ClientId,
                            Date = row.Date,
                        }).ToList());

            var actsCountByClient = await _db.Acts
                .AsNoTracking()
                .Where(a => a.ClientId.HasValue && itemIds.Contains(a.ClientId.Value))
                .GroupBy(a => a.ClientId!.Value)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ClientId, x => x.Count, ct);

            var actRows = await _db.Acts
                .AsNoTracking()
                .Where(a => a.ClientId.HasValue && itemIds.Contains(a.ClientId.Value))
                .Select(a => new
                {
                    a.Id,
                    a.Number,
                    a.Title,
                    a.Date,
                    a.Amount,
                    a.ClientId,
                    a.LegalEntityId,
                })
                .ToListAsync(ct);

            var actLinksByClient = actRows
                .Where(row => row.ClientId.HasValue)
                .GroupBy(row => row.ClientId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(row => row.Date)
                        .Select(row => new RelatedDocumentLink
                        {
                            Id = row.Id,
                            Label = !string.IsNullOrWhiteSpace(row.Number)
                                ? $"№{row.Number}"
                                : !string.IsNullOrWhiteSpace(row.Title)
                                    ? row.Title!
                                    : $"Акт #{row.Id}",
                            SecondaryLabel = row.Title,
                            ClientId = row.ClientId,
                            LegalEntityId = row.LegalEntityId,
                            Date = row.Date,
                            Amount = row.Amount,
                        }).ToList());

            var invoiceRows = await _db.Payments
                .AsNoTracking()
                .Where(p => p.DocumentKind == DocumentKind.Invoice)
                .Where(p =>
                    (p.ClientId.HasValue && itemIds.Contains(p.ClientId.Value)) ||
                    (p.ExpenseClientId.HasValue && itemIds.Contains(p.ExpenseClientId.Value)))
                .Select(p => new
                {
                    p.Id,
                    p.ClientId,
                    p.ExpenseClientId,
                    p.Account,
                    p.LegacyAccountNumber,
                    p.Date,
                    p.Amount,
                    p.Type,
                })
                .ToListAsync(ct);

            var invoicesCountByClient = new Dictionary<int, int>();
            var invoiceLinksByClient = new Dictionary<int, List<RelatedDocumentLink>>();
            void AddInvoiceLink(int clientId, RelatedDocumentLink link)
            {
                if (!invoiceLinksByClient.TryGetValue(clientId, out var links))
                {
                    links = new List<RelatedDocumentLink>();
                    invoiceLinksByClient[clientId] = links;
                }

                links.Add(link);
            }

            foreach (var row in invoiceRows)
            {
                var number = !string.IsNullOrWhiteSpace(row.Account) ? row.Account : row.LegacyAccountNumber;
                var link = new RelatedDocumentLink
                {
                    Id = row.Id,
                    Label = !string.IsNullOrWhiteSpace(number) ? $"№{number}" : $"Счет #{row.Id}",
                    ClientId = row.ClientId ?? row.ExpenseClientId,
                    Date = row.Date,
                    Amount = row.Amount,
                    Type = row.Type.ToString(),
                };

                if (row.ClientId.HasValue && itemIds.Contains(row.ClientId.Value))
                {
                    invoicesCountByClient[row.ClientId.Value] = invoicesCountByClient.GetValueOrDefault(row.ClientId.Value) + 1;
                    AddInvoiceLink(row.ClientId.Value, link);
                }

                if (row.ExpenseClientId.HasValue && itemIds.Contains(row.ExpenseClientId.Value) && row.ExpenseClientId != row.ClientId)
                {
                    invoicesCountByClient[row.ExpenseClientId.Value] = invoicesCountByClient.GetValueOrDefault(row.ExpenseClientId.Value) + 1;
                    AddInvoiceLink(row.ExpenseClientId.Value, link);
                }
            }

            var paymentRows = await _db.Payments
                .AsNoTracking()
                .Where(p => p.DocumentKind != DocumentKind.Invoice)
                .Where(p =>
                    (p.ClientId.HasValue && itemIds.Contains(p.ClientId.Value)) ||
                    (p.ExpenseClientId.HasValue && itemIds.Contains(p.ExpenseClientId.Value)))
                .Select(p => new
                {
                    p.Id,
                    p.ClientId,
                    p.ExpenseClientId,
                    p.PaymentOrderNumber,
                    p.Description,
                    p.Date,
                    p.Amount,
                    p.Type,
                })
                .ToListAsync(ct);

            var paymentsCountByClient = new Dictionary<int, int>();
            var paymentLinksByClient = new Dictionary<int, List<RelatedDocumentLink>>();
            void AddPaymentLink(int clientId, RelatedDocumentLink link)
            {
                if (!paymentLinksByClient.TryGetValue(clientId, out var links))
                {
                    links = new List<RelatedDocumentLink>();
                    paymentLinksByClient[clientId] = links;
                }

                links.Add(link);
            }

            foreach (var row in paymentRows)
            {
                var link = new RelatedDocumentLink
                {
                    Id = row.Id,
                    Label = !string.IsNullOrWhiteSpace(row.PaymentOrderNumber)
                        ? $"№{row.PaymentOrderNumber}"
                        : !string.IsNullOrWhiteSpace(row.Description)
                            ? row.Description
                            : $"Платеж #{row.Id}",
                    ClientId = row.ClientId ?? row.ExpenseClientId,
                    Date = row.Date,
                    Amount = row.Amount,
                    Type = row.Type.ToString(),
                };

                if (row.ClientId.HasValue && itemIds.Contains(row.ClientId.Value))
                {
                    paymentsCountByClient[row.ClientId.Value] = paymentsCountByClient.GetValueOrDefault(row.ClientId.Value) + 1;
                    AddPaymentLink(row.ClientId.Value, link);
                }

                if (row.ExpenseClientId.HasValue && itemIds.Contains(row.ExpenseClientId.Value) && row.ExpenseClientId != row.ClientId)
                {
                    paymentsCountByClient[row.ExpenseClientId.Value] = paymentsCountByClient.GetValueOrDefault(row.ExpenseClientId.Value) + 1;
                    AddPaymentLink(row.ExpenseClientId.Value, link);
                }
            }

            foreach (var item in items)
            {
                item.CasesCount = casesCountByClient.GetValueOrDefault(item.Id);
                item.ContractsCount = contractsCountByClient.GetValueOrDefault(item.Id);
                item.InvoicesCount = invoicesCountByClient.GetValueOrDefault(item.Id);
                item.ActsCount = actsCountByClient.GetValueOrDefault(item.Id);
                item.PaymentsCount = paymentsCountByClient.GetValueOrDefault(item.Id);
                item.Cases = casesByClient
                    .Where(clientCase => clientCase.ClientId == item.Id)
                    .ToList();
                item.RelatedCases = caseLinksByClient.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                item.RelatedContracts = contractLinksByClient.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                item.RelatedInvoices = invoiceLinksByClient.GetValueOrDefault(item.Id)?.OrderByDescending(link => link.Date).ToList() ?? new List<RelatedDocumentLink>();
                item.RelatedActs = actLinksByClient.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                item.RelatedPayments = paymentLinksByClient.GetValueOrDefault(item.Id)?.OrderByDescending(link => link.Date).ToList() ?? new List<RelatedDocumentLink>();
            }
        }

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is not null)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
        return client is not null ? Ok(client) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var responsibleUserId = access.IsAdmin
            ? (model.ResponsibleUserId ?? access.UserId.Value)
            : access.UserId.Value;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        model.ResponsibleUserId = responsibleUserId;
        _db.Clients.Add(model);
        await _db.SaveChangesAsync(ct);
        await _db.Entry(model).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        model.ResponsibleUserName = FormatResponsibleUserName(model.ResponsibleUser);
        return Created($"/api/v2/clients/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var responsibleUserId = access.IsAdmin
            ? model.ResponsibleUserId
            : e.ResponsibleUserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        e.IsActive = model.IsActive;
        e.LegalEntityId = model.LegalEntityId;
        e.ResponsibleUserId = responsibleUserId;
        e.ClientStatusId = model.ClientStatusId;
        e.IsClientAndSupplier = model.IsClientAndSupplier;

        await _db.SaveChangesAsync(ct);
        await _db.Entry(e).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        e.ResponsibleUserName = FormatResponsibleUserName(e.ResponsibleUser);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync(ct);
        foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
        await _db.SaveChangesAsync(ct);

        _db.Clients.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

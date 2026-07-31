using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/electronic-signatures")]
[Authorize(Policy = "Admin")]
public sealed class ElectronicSignaturesController(
    PaymentContext db,
    IElectronicSignatureService service,
    IConfiguration configuration) : ControllerBase
{
    private static readonly string[] ProviderNames = ["KonturDiadoc", "Sbis", "Taxcom"];

    [HttpGet("configuration")]
    public IActionResult GetConfiguration() => Ok(new
    {
        Enabled = configuration.GetValue("ElectronicSignature:Enabled", false),
        PreparationEnabled = configuration.GetValue("ElectronicSignature:PreparationEnabled", true),
        DefaultSignatureType = configuration["ElectronicSignature:DefaultSignatureType"] ?? ElectronicSignatureTypes.Qualified,
        DefaultSigningOrder = configuration["ElectronicSignature:DefaultSigningOrder"] ?? ElectronicSignatureSigningOrders.Sequential,
        DefaultArchiveRetentionYears = configuration.GetValue("ElectronicSignature:DefaultArchiveRetentionYears", 10),
        Providers = ProviderNames.Select(name => new
        {
            Name = name,
            Enabled = configuration.GetValue($"ElectronicSignature:Providers:{name}:Enabled", false)
        })
    });

    [HttpGet("recipients")]
    public async Task<IActionResult> GetRecipients([FromQuery] int clientId, CancellationToken ct)
    {
        if (clientId <= 0) return BadRequest(new { message = "Клиент не выбран." });

        var recipients = await db.ClientPortalAccounts
            .AsNoTracking()
            .Where(item => item.IsActive
                && item.RevokedAt == null
                && (item.ClientId == clientId
                    || item.CaseGrants.Any(grant => grant.RevokedAt == null && grant.ClientCase.ClientId == clientId)))
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .Take(100)
            .Select(item => new
            {
                item.Id,
                item.DisplayName,
                item.AccountType,
                Email = item.Email,
                IsRepresentative = item.AccountType == "representative"
            })
            .ToListAsync(ct);
        return Ok(recipients);
    }

    [HttpPost("financial-documents/{documentType}/{documentId:int}/prepare")]
    [RequestSizeLimit(26L * 1024 * 1024)]
    public async Task<IActionResult> Prepare(
        string documentType,
        int documentId,
        [FromForm] IFormFile? file,
        [FromForm] int clientId,
        [FromForm] List<int>? signerAccountIds,
        [FromForm] string? signatureType,
        [FromForm] string? signingOrder,
        [FromForm] string? provider,
        [FromForm] int archiveRetentionYears = 10,
        [FromForm] bool requiresMachineReadablePowerOfAttorney = false,
        [FromForm] string? powerOfAttorneyReference = null,
        [FromForm] bool confirmSnapshotAndSigners = false,
        CancellationToken ct = default)
    {
        if (!confirmSnapshotAndSigners)
            return BadRequest(new { message = "Подтвердите версию документа и список подписантов." });
        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "Сформированный PDF не передан." });

        var normalizedType = documentType.Trim().ToLowerInvariant();
        var fileName = await ResolveFinancialDocumentFileName(normalizedType, documentId, clientId, ct);
        if (fileName is null)
            return BadRequest(new { message = "Документ не найден или не принадлежит выбранному клиенту." });

        var requestedSignerIds = (signerAccountIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .Take(21)
            .ToArray();
        if (requestedSignerIds.Length is 0 or > 20)
            return BadRequest(new { message = "Выберите от одного до двадцати подписантов." });

        var signers = await db.ClientPortalAccounts
            .AsNoTracking()
            .Where(item => requestedSignerIds.Contains(item.Id)
                && item.IsActive
                && item.RevokedAt == null
                && (item.ClientId == clientId
                    || item.CaseGrants.Any(grant => grant.RevokedAt == null && grant.ClientCase.ClientId == clientId)))
            .Select(item => new { item.Id, item.DisplayName, item.Email, item.AccountType })
            .ToListAsync(ct);
        if (signers.Count != requestedSignerIds.Length)
            return BadRequest(new { message = "Один из подписантов не имеет явного доступа к делам выбранного клиента." });

        var signerLookup = signers.ToDictionary(item => item.Id);
        var orderedSigners = requestedSignerIds
            .Select(id => signerLookup[id])
            .Select(item => new ElectronicSignatureSignerDraft(
                item.DisplayName,
                item.Email,
                item.AccountType,
                item.Id,
                requiresMachineReadablePowerOfAttorney,
                requiresMachineReadablePowerOfAttorney ? powerOfAttorneyReference : null))
            .ToArray();

        try
        {
            await using var stream = file.OpenReadStream();
            var envelope = await service.PrepareAsync(new ElectronicSignaturePreparation(
                normalizedType,
                documentId,
                1,
                clientId,
                signatureType ?? configuration["ElectronicSignature:DefaultSignatureType"] ?? ElectronicSignatureTypes.Qualified,
                signingOrder ?? configuration["ElectronicSignature:DefaultSigningOrder"] ?? ElectronicSignatureSigningOrders.Sequential,
                provider ?? ProviderNames[0],
                archiveRetentionYears,
                requiresMachineReadablePowerOfAttorney,
                User.GetUserId() ?? 0,
                orderedSigners), stream, fileName, ct);
            return Ok(ToDto(envelope));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("financial-documents/{documentType}/{documentId:int}")]
    public async Task<IActionResult> GetHistory(string documentType, int documentId, CancellationToken ct)
    {
        var normalizedType = documentType.Trim().ToLowerInvariant();
        var envelopes = await db.ElectronicSignatureEnvelopes
            .AsNoTracking()
            .Where(item => item.DocumentType == normalizedType && item.DocumentId == documentId)
            .Include(item => item.Signers)
            .Include(item => item.Events)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(50)
            .AsSplitQuery()
            .ToListAsync(ct);
        return Ok(envelopes.Select(ToDto));
    }

    [HttpPost("{publicId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid publicId,
        [FromBody] ElectronicSignatureCancelRequest? request,
        CancellationToken ct)
    {
        try
        {
            var envelope = await service.CancelPreparedAsync(
                publicId,
                User.GetUserId() ?? 0,
                request?.Reason,
                ct);
            return envelope is null ? NotFound() : Ok(ToDto(envelope));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<string?> ResolveFinancialDocumentFileName(string type, int documentId, int clientId, CancellationToken ct)
    {
        string? number = type switch
        {
            "invoice" => await db.Payments.AsNoTracking()
                .Where(item => item.Id == documentId && (item.ClientId == clientId || item.ExpenseClientId == clientId))
                .Select(item => item.Invoice != null ? item.Invoice.Number : item.Account)
                .FirstOrDefaultAsync(ct),
            "act" => await db.Acts.AsNoTracking()
                .Where(item => item.Id == documentId && item.ClientId == clientId)
                .Select(item => item.Number)
                .FirstOrDefaultAsync(ct),
            "contract" => await db.Contracts.AsNoTracking()
                .Where(item => item.Id == documentId && item.ClientContracts.Any(link => link.ClientId == clientId))
                .Select(item => item.Number)
                .FirstOrDefaultAsync(ct),
            _ => null
        };
        return string.IsNullOrWhiteSpace(number) ? null : $"{type}-{Sanitize(number)}.pdf";
    }

    private static object ToDto(ElectronicSignatureEnvelope item) => new
    {
        item.Id,
        item.PublicId,
        item.DocumentType,
        item.DocumentId,
        item.DocumentVersion,
        item.SignatureType,
        item.SigningOrder,
        item.Provider,
        item.Status,
        item.OriginalFileName,
        item.SizeBytes,
        item.ChecksumSha256,
        item.ArchiveRetentionYears,
        item.RequiresMachineReadablePowerOfAttorney,
        item.CreatedAt,
        item.SentAt,
        item.CompletedAt,
        item.CancelledAt,
        Signers = item.Signers.OrderBy(signer => signer.Order).Select(signer => new
        {
            signer.Id,
            signer.Order,
            signer.DisplayName,
            signer.MaskedContact,
            signer.PartyType,
            signer.Status,
            signer.RequiresMachineReadablePowerOfAttorney,
            signer.PowerOfAttorneyReference
        }),
        Events = item.Events.OrderByDescending(entry => entry.OccurredAt).Take(50).Select(entry => new
        {
            entry.EventType,
            entry.Status,
            entry.Message,
            entry.OccurredAt
        })
    };

    private static string Sanitize(string value)
    {
        var safe = new string(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        return safe.Length == 0 ? "document" : safe[..Math.Min(80, safe.Length)];
    }
}

public sealed record ElectronicSignatureCancelRequest(string? Reason);

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

public partial class MoneyOperationsController
{
    private sealed record InvoiceCandidateCursor(string Fingerprint, int Score, DateTime? InvoiceDate, int InvoiceId, int PaymentId);

    private static string InvoiceCandidateFingerprint(MoneyOperation operation, string? search)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Version = 1,
            operation.Id,
            operation.MoneyAccountId,
            operation.Source,
            operation.Direction,
            operation.AccountingType,
            operation.Date,
            operation.Amount,
            operation.Currency,
            operation.Purpose,
            operation.CounterpartyName,
            operation.ResponsibleUserId,
            operation.LinkedInvoiceId,
            operation.MatchStatus,
            Search = search,
        }))));

    private static bool TryReadInvoiceCandidateCursor(string token, out InvoiceCandidateCursor? cursor)
    {
        cursor = null;
        if (token.Length > 1024) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<InvoiceCandidateCursor>(WebEncoders.Base64UrlDecode(token));
            return cursor is { Fingerprint.Length: 64, Score: >= 0 and <= 200, InvoiceId: > 0, PaymentId: > 0 };
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string WriteInvoiceCandidateCursor(MoneyOperationInvoiceCandidateDto candidate, string fingerprint)
        => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new InvoiceCandidateCursor(
            fingerprint, candidate.Score, candidate.InvoiceDate, candidate.InvoiceId, candidate.PaymentId)));

    private static bool IsAfterInvoiceCandidate(MoneyOperationInvoiceCandidateDto candidate, InvoiceCandidateCursor cursor)
        => candidate.Score < cursor.Score || candidate.Score == cursor.Score
            && (Nullable.Compare(candidate.InvoiceDate, cursor.InvoiceDate) < 0
                || candidate.InvoiceDate == cursor.InvoiceDate && (candidate.InvoiceId < cursor.InvoiceId
                    || candidate.InvoiceId == cursor.InvoiceId && candidate.PaymentId < cursor.PaymentId));
}

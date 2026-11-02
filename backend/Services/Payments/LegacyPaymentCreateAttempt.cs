using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

// Legacy routes retain their existing binding/normalization. They use the same
// registered ownership row and financial transaction as the create-attempt API.
internal sealed class LegacyPaymentCreateAttempt(PaymentCreateAttempt attempt, string protocol, string identity)
{
    internal const string HeaderName = "Idempotency-Key";
    internal const string CurrentProtocol = "legacy-payment-create-v1";
    internal const string V1Protocol = "legacy-v1-payment-create-v1";
    internal const string V2Protocol = "legacy-v2-payment-create-v1";

    internal bool IsCommitted => attempt.State == PaymentCreateAttempt.Committed;
    internal int? CanonicalPaymentId => attempt.CanonicalPaymentId;

    internal static bool TryReadKey(HttpRequest request, out Guid key)
    {
        key = default;
        var values = request.Headers[HeaderName];
        return values.Count == 1 && values[0] is { Length: 36 } value
            && Guid.TryParseExact(value, "D", out key) && key != Guid.Empty;
    }

    internal static async Task<(LegacyPaymentCreateAttempt? Attempt, IActionResult? Error)> OpenAsync(
        ControllerBase controller, PaymentContext db, Guid key, int actorId, Payment model, string protocol, CancellationToken ct)
    {
        if (protocol is not (CurrentProtocol or V1Protocol or V2Protocol))
            throw new InvalidOperationException("An explicit legacy payment-create protocol is required.");
        var row = await PaymentCreateAttemptLease.LockAttemptAsync(db, key, actorId, ct);
        if (row is null || row.State == PaymentCreateAttempt.Cancelled
            || row.State == PaymentCreateAttempt.Draft && row.ExpiresAtUtc <= Now(controller))
            return (null, controller.StatusCode(StatusCodes.Status410Gone, new
            {
                code = "payment_operation_unavailable",
                message = "Попытка сохранения недоступна или закрыта. Сверьте результат по исходному ключу; не создавайте новый платёж, пока результат не выяснен."
            }));
        if (row.Operation != "primary" || row.PrimaryPaymentId.HasValue
            || row.SellerCompanyId != model.SellerCompanyId || row.SellerBranchId != model.SellerBranchId
            || row.State == PaymentCreateAttempt.Draft && row.IdentityVersion != PaymentCreateCommand.Version
            || row.State == PaymentCreateAttempt.Committed && row.IdentityVersion != protocol
            || row.State is not (PaymentCreateAttempt.Draft or PaymentCreateAttempt.Committed))
            return (null, Conflict(controller));
        var identity = BuildIdentity(model, protocol);
        if (row.State == PaymentCreateAttempt.Committed
            && (row.CanonicalPaymentId is not > 0 || !string.Equals(row.PayloadIdentity, identity, StringComparison.Ordinal)))
            return (null, Conflict(controller));
        return (new(row, protocol, identity), null);
    }

    internal void RecordOutcome(ControllerBase controller, Payment payment)
    {
        if (attempt.State != PaymentCreateAttempt.Draft || payment.Id <= 0)
            throw new InvalidOperationException("Only a newly saved payment can commit a draft operation.");
        var now = Now(controller);
        if (attempt.ExpiresAtUtc <= now) throw new PaymentCreateAttemptExpiredException();
        attempt.IdentityVersion = protocol;
        attempt.PayloadIdentity = identity;
        attempt.State = PaymentCreateAttempt.Committed;
        attempt.CanonicalPaymentId = payment.Id;
        attempt.CommittedAtUtc = now;
    }

    internal static string BuildIdentity(Payment model, string protocol)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in PaymentCreateInputRules.LogicalInputProperties)
            {
                writer.WritePropertyName(property.Name);
                var value = property.GetValue(model);
                // Decimal scale is not a distinct financial command. Do not hash
                // server defaults, navigation graphs, audit or generated fields.
                if (value is decimal number) writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture));
                else JsonSerializer.Serialize(writer, value, property.PropertyType);
            }
            writer.WriteEndObject();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(protocol + "\0"));
        hash.AppendData(buffer.ToArray());
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static DateTimeOffset Now(ControllerBase controller)
        => (controller.HttpContext.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    private static ConflictObjectResult Conflict(ControllerBase controller) => controller.Conflict(new
    {
        code = "payment_operation_conflict",
        message = "Ключ сохранения относится к другому запросу или способу создания платежа. Сверьте исходный результат; не повторяйте оплату с новым ключом."
    });
}

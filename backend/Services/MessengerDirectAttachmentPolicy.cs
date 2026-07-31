namespace PayPlanner.Api.Services;

public sealed record MessengerDirectAttachmentStatus(
    bool Requested,
    bool Enabled,
    IReadOnlyList<string> AllowedMimeTypes,
    long MaxBytes,
    bool AntivirusEnabled,
    string DeliveryMode,
    string? DisabledReason);

public static class MessengerDirectAttachmentPolicy
{
    public static MessengerDirectAttachmentStatus GetStatus(IConfiguration configuration)
    {
        var requested = configuration.GetValue("MessengerDocumentDelivery:DirectAttachments:Enabled", false);
        var antivirusEnabled = configuration.GetValue(
            "MessengerDocumentDelivery:DirectAttachments:Antivirus:Enabled",
            false);
        var maxBytes = Math.Max(
            0,
            configuration.GetValue<long>("MessengerDocumentDelivery:DirectAttachments:MaxBytes", 0));
        var allowedMimeTypes = configuration
            .GetSection("MessengerDocumentDelivery:DirectAttachments:AllowedMimeTypes")
            .Get<string[]>()?
            .Select(static item => item.Trim().ToLowerInvariant())
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        var enabled = requested
            && antivirusEnabled
            && maxBytes > 0
            && allowedMimeTypes.Length > 0;
        var disabledReason = enabled
            ? null
            : !requested
                ? "Direct attachments are disabled; protected one-time links are used."
                : !antivirusEnabled
                    ? "Direct attachments require an enabled antivirus scanner."
                    : maxBytes <= 0
                        ? "Direct attachments require a positive provider size limit."
                        : "Direct attachments require a non-empty MIME allowlist.";

        return new MessengerDirectAttachmentStatus(
            requested,
            enabled,
            allowedMimeTypes,
            maxBytes,
            antivirusEnabled,
            enabled ? "direct_attachment" : "protected_link",
            disabledReason);
    }
}

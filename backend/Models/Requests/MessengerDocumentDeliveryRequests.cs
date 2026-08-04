using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class CreateMessengerRecipientBindingRequest
{
    public int ClientId { get; set; }
    public int? ClientPortalAccountId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string VerificationMethod { get; set; } = string.Empty;
    public bool ConfirmContactOwnership { get; set; }
}

public class PrepareCaseDocumentMessengerDeliveryRequest
{
    public int RecipientBindingId { get; set; }
    public string? Message { get; set; }
    public bool ConfirmRecipientAndDocument { get; set; }
}

public class MessengerDocumentDeliveryCallbackRequest
{
    [Required, StringLength(256, MinimumLength = 1)]
    public string ProviderMessageId { get; set; } = string.Empty;

    [Required, StringLength(32, MinimumLength = 1)]
    public string Status { get; set; } = string.Empty;
    public DateTime? OccurredAt { get; set; }

    [StringLength(2000)]
    public string? Error { get; set; }
}

/// <summary>Секрет одноразовой ссылки, передаваемый в теле запроса без попадания в URL.</summary>
public sealed class MessengerDocumentDownloadRequest
{
    [Required, StringLength(512, MinimumLength = 1)]
    public string Token { get; set; } = string.Empty;
}

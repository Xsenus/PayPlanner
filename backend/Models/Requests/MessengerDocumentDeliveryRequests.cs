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

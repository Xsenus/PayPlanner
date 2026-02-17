using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class SendInvoiceEmailRequest
{
    [EmailAddress]
    public string? Email { get; set; }

    public string? Subject { get; set; }

    public string? Message { get; set; }

    public string Format { get; set; } = "word";
}

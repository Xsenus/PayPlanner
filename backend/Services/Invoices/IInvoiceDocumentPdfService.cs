namespace PayPlanner.Api.Services.Invoices;

public interface IInvoiceDocumentPdfService
{
    Task<byte[]> RenderAsync(string html, CancellationToken ct = default);
}

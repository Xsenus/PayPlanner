namespace PayPlanner.Api.Models.Responses;

public sealed class InvoiceReferenceDictionariesDto
{
    public IReadOnlyList<InvoiceUnitDictionaryItem> InvoiceUnits { get; set; } = Array.Empty<InvoiceUnitDictionaryItem>();
    public IReadOnlyList<ServiceDictionaryItem> Services { get; set; } = Array.Empty<ServiceDictionaryItem>();
    public IReadOnlyList<ServiceCategoryDictionaryItem> ServiceCategories { get; set; } = Array.Empty<ServiceCategoryDictionaryItem>();
}

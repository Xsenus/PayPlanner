using PayPlanner.Api.Services.Tax;
using System.Text.Json;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceLineItemStorage
{
    public const string Marker = "[pp:invoice-items]";
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static List<InvoiceLineItemDto> Normalize(IEnumerable<InvoiceLineItemRequest>? lineItems)
    {
        if (lineItems is null)
        {
            return new List<InvoiceLineItemDto>();
        }

        return lineItems
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
            .Select((item, index) => new InvoiceLineItemDto
            {
                SortOrder = index,
                SourceKey = BuildSourceKey(null, index),
                ServiceId = item.ServiceId is > 0 ? item.ServiceId : null,
                Name = item.Name!.Trim(),
                Quantity = Math.Max(item.Quantity, 0),
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? "шт" : item.Unit.Trim(),
                Price = Math.Max(item.Price, 0),
                VatRate = VatRates.Normalize(item.VatRate),
            })
            .ToList();
    }

    public static List<InvoiceLineItemDto> ExtractFromSystemNotes(string? systemNotes)
    {
        if (string.IsNullOrWhiteSpace(systemNotes))
        {
            return new List<InvoiceLineItemDto>();
        }

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(Marker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine))
        {
            return new List<InvoiceLineItemDto>();
        }

        var json = markerLine[Marker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<InvoiceLineItemDto>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<InvoiceLineItemDto>>(json, LegacyJsonOptions);
            return parsed?
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
                .Select((item, index) => new InvoiceLineItemDto
                {
                    Id = item.Id,
                    SortOrder = item.SortOrder ?? index,
                    SourceKey = string.IsNullOrWhiteSpace(item.SourceKey)
                        ? BuildSourceKey(item.Id, item.SortOrder ?? index)
                        : item.SourceKey,
                    ServiceId = item.ServiceId is > 0 ? item.ServiceId : null,
                    Name = item.Name.Trim(),
                    Quantity = item.Quantity,
                    Unit = string.IsNullOrWhiteSpace(item.Unit) ? "шт" : item.Unit.Trim(),
                    Price = item.Price,
                    VatRate = item.VatRate,
                })
                .ToList() ?? new List<InvoiceLineItemDto>();
        }
        catch
        {
            return new List<InvoiceLineItemDto>();
        }
    }

    public static string RemoveFromSystemNotes(string? systemNotes)
    {
        return string.Join(
            "\n",
            (systemNotes ?? string.Empty)
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(Marker, StringComparison.Ordinal)));
    }

    public static string MergeIntoSystemNotes(string? existingSystemNotes, IEnumerable<InvoiceLineItemRequest>? lineItems)
    {
        var baseNotes = RemoveFromSystemNotes(existingSystemNotes);
        var normalizedItems = Normalize(lineItems);
        if (normalizedItems.Count == 0)
        {
            return baseNotes;
        }

        var json = JsonSerializer.Serialize(normalizedItems);
        return string.IsNullOrWhiteSpace(baseNotes)
            ? $"{Marker} {json}"
            : $"{baseNotes}\n{Marker} {json}";
    }

    public static string BuildSourceKey(int? invoiceLineItemId, int sortOrder)
        => invoiceLineItemId.HasValue && invoiceLineItemId.Value > 0
            ? $"db:{invoiceLineItemId.Value}"
            : $"legacy:{sortOrder}";
}

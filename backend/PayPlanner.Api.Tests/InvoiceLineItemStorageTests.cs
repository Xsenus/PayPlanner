using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoiceLineItemStorageTests
{
    [Fact]
    public void Normalize_KeepsMultipleNamedItems_AndDefaultsUnit()
    {
        var source = new List<InvoiceLineItemRequest>
        {
            new() { ServiceId = 7, Name = "  Подготовка документов  ", Quantity = 1, Unit = "усл. ед.", Price = 10_000 },
            new() { Name = "Судебное сопровождение", Quantity = 2, Unit = "", Price = 7_500 },
            new() { Name = "   ", Quantity = 1, Unit = "шт", Price = 1_000 },
        };

        var result = InvoiceLineItemStorage.Normalize(source);

        Assert.Equal(2, result.Count);
        Assert.Equal(7, result[0].ServiceId);
        Assert.Equal("Подготовка документов", result[0].Name);
        Assert.Equal("усл. ед.", result[0].Unit);
        Assert.Equal("Судебное сопровождение", result[1].Name);
        Assert.Equal("шт", result[1].Unit);
    }

    [Fact]
    public void ExtractFromSystemNotes_ReadsLegacyMarker()
    {
        const string notes = """
            Обычная служебная заметка
            [pp:invoice-items] [{"serviceId":12,"name":"Экспертиза","quantity":1,"unit":"шт","price":30000}]
            """;

        var result = InvoiceLineItemStorage.ExtractFromSystemNotes(notes);

        var item = Assert.Single(result);
        Assert.Equal(12, item.ServiceId);
        Assert.Equal("Экспертиза", item.Name);
        Assert.Equal(1, item.Quantity);
        Assert.Equal("шт", item.Unit);
        Assert.Equal(30_000, item.Price);
    }

    [Fact]
    public void RemoveFromSystemNotes_RemovesOnlyLegacyLineItemsMarker()
    {
        const string notes = """
            Первая строка
            [pp:invoice-items] [{"name":"Экспертиза","quantity":1,"unit":"шт","price":30000}]
            Вторая строка
            """;

        var result = InvoiceLineItemStorage.RemoveFromSystemNotes(notes);

        Assert.Contains("Первая строка", result);
        Assert.Contains("Вторая строка", result);
        Assert.DoesNotContain(InvoiceLineItemStorage.Marker, result);
    }
}

using System.Reflection;
using PayPlanner.Api.Controllers;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ActsControllerInternalCoverageTests
{
    [Fact]
    public void ActLineItemDraft_StoresAllConstructorValues()
    {
        var draftType = typeof(ActsController).GetNestedType("ActLineItemDraft", BindingFlags.NonPublic);
        Assert.NotNull(draftType);

        var instance = Activator.CreateInstance(
            draftType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { 42, "db:42", 7, "Услуга", 3.5m, "час", 1_250m, "22" },
            culture: null);

        Assert.NotNull(instance);
        Assert.Equal(42, draftType!.GetProperty("InvoiceLineItemId")!.GetValue(instance));
        Assert.Equal("db:42", draftType.GetProperty("SourceKey")!.GetValue(instance));
        Assert.Equal(7, draftType.GetProperty("SourceSortOrder")!.GetValue(instance));
        Assert.Equal("Услуга", draftType.GetProperty("Name")!.GetValue(instance));
        Assert.Equal(3.5m, draftType.GetProperty("Quantity")!.GetValue(instance));
        Assert.Equal("час", draftType.GetProperty("Unit")!.GetValue(instance));
        Assert.Equal(1_250m, draftType.GetProperty("Price")!.GetValue(instance));
        Assert.Equal("22", draftType.GetProperty("VatRate")!.GetValue(instance));
    }
}

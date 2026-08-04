using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class FormRequirementsServiceTests
{
    [Fact]
    public async Task GetRequiredFieldsAsync_NormalizesAliasesAndRemovesDuplicates()
    {
        await using var db = await CreateContextAsync();
        db.FormFieldRequirements.AddRange(
            new FormFieldRequirement { Section = "clients-modal", FieldKey = "Name", IsRequired = true },
            new FormFieldRequirement { Section = "clients-modal", FieldKey = "FirstName", IsRequired = true },
            new FormFieldRequirement { Section = "clients-modal", FieldKey = "Notes", IsRequired = true },
            new FormFieldRequirement { Section = "clients-modal", FieldKey = "Phone", IsRequired = false });
        await db.SaveChangesAsync();

        var service = new FormRequirementsService(db);

        var fields = await service.GetRequiredFieldsAsync("clients-modal");

        Assert.Equal(2, fields.Count);
        Assert.Contains("FirstName", fields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Comment", fields, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMissingFieldsAsync_RecognizesEmptyAndPopulatedJsonArrays()
    {
        await using var db = await CreateContextAsync();
        db.FormFieldRequirements.AddRange(
            new FormFieldRequirement { Section = "test", FieldKey = "EmptyValues", IsRequired = true },
            new FormFieldRequirement { Section = "test", FieldKey = "Values", IsRequired = true });
        await db.SaveChangesAsync();

        var service = new FormRequirementsService(db);

        var missing = await service.GetMissingFieldsAsync("test", new RequirementModel());

        Assert.Equal(["EmptyValues"], missing);
    }

    [Fact]
    public async Task GetRequiredFieldsAsync_ObservesCancellation()
    {
        await using var db = await CreateContextAsync();
        var service = new FormRequirementsService(db);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetRequiredFieldsAsync("test", cts.Token));
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RequirementModel
    {
        public string EmptyValues { get; init; } = "[null, \"\", \"   \"]";
        public string Values { get; init; } = "[\"filled\"]";
    }
}

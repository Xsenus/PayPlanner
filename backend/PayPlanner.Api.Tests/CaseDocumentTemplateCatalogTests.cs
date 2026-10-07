using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseDocumentTemplateCatalogTests
{
    [Theory]
    [InlineData("custom", "custom")]
    [InlineData(" CUSTOM ", "custom")]
    [InlineData("isk", "claim")]
    [InlineData("letter", "court-letter")]
    public void ExplicitChoicePreservesActiveCustomAndLegacyKeys(string requested, string expected)
    {
        CaseDocumentTemplateDefinition[] library =
        [new("custom", "Свой шаблон", "Иск", "Заголовок"),
         new("claim", "Иск", "Иск", "Иск"),
         new("court-letter", "Письмо", "Переписка", "Письмо")];
        var result = CaseDocumentTemplateCatalog.Resolve(library, requested, "Иск");
        Assert.Null(result.Error);
        Assert.False(result.NotFound);
        Assert.Equal(expected, result.Template.Key);
    }

    [Fact]
    public void ExplicitInactiveChoiceCannotUseDefaultForMissingActiveTemplate()
    {
        CaseDocumentTemplateDefinition[] library =
        [new("claim", "Иск", "Иск", "Иск", IsActive: false)];
        var result = CaseDocumentTemplateCatalog.Resolve(library, "claim", "Иск");
        Assert.True(result.NotFound);
        Assert.Equal("Активный шаблон документа не найден.", result.Error);
        Assert.Null(result.Template.Key);
    }

    [Fact]
    public void InvalidExplicitChoiceIsValidationFailure()
    {
        var result = CaseDocumentTemplateCatalog.Resolve([], "../claim", "Иск");
        Assert.False(result.NotFound);
        Assert.Equal("Некорректный код шаблона.", result.Error);
        Assert.Null(result.Template.Key);
    }

    [Fact]
    public void ImplicitChoiceRetainsExistingStandardFallback()
    {
        CaseDocumentTemplateDefinition[] library =
        [new("claim", "Иск", "Иск", "Иск", IsActive: false)];
        var result = CaseDocumentTemplateCatalog.Resolve(library, null, "Иск");
        Assert.Null(result.Error);
        Assert.Equal("case-summary", result.Template.Key);
        Assert.Equal("Справка по делу", result.Template.Title);
    }

    [Fact]
    public void ApplicabilityUsesCaseTypeAndPreservesUnrestrictedTemplate()
    {
        var restricted = new CaseDocumentTemplateDefinition("claim", "Иск", "Иск", "Иск",
            [" Гражданское, Арбитражное "]);
        Assert.True(CaseDocumentTemplateCatalog.IsCaseDocumentTemplateApplicableToCase(restricted,
            new ClientCase { CaseType = " гражданское " }));
        Assert.False(CaseDocumentTemplateCatalog.IsCaseDocumentTemplateApplicableToCase(restricted,
            new ClientCase { CaseType = "Уголовное" }));
        Assert.False(CaseDocumentTemplateCatalog.IsCaseDocumentTemplateApplicableToCase(restricted,
            new ClientCase()));
        Assert.True(CaseDocumentTemplateCatalog.IsCaseDocumentTemplateApplicableToCase(restricted with { CaseTypes = null },
            new ClientCase()));
    }

    [Fact]
    public async Task ReadUsesCurrentDatabaseSettingWithoutTrackingOrCaching()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var setting = new SystemSetting
        {
            Key = CaseDocumentTemplateCatalog.CaseDocumentTemplatesSettingKey,
            Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Исходный\",\"documentType\":\"Иск\",\"heading\":\"Заголовок\",\"version\":2}]}"
        };
        db.SystemSettings.Add(setting);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var catalog = new CaseDocumentTemplateCatalog(db);
        var first = Assert.Single(await catalog.ReadAsync(CancellationToken.None));
        Assert.Equal("Исходный", first.Title);
        Assert.Equal(2, first.Version);
        Assert.Empty(db.ChangeTracker.Entries());

        await db.SystemSettings.Where(x => x.Key == setting.Key).ExecuteUpdateAsync(update =>
            update.SetProperty(x => x.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Изменённый\",\"isActive\":false}]}"));
        var current = Assert.Single(await catalog.ReadAsync(CancellationToken.None));
        Assert.Equal("Изменённый", current.Title);
        Assert.False(current.IsActive);
        Assert.Equal("Исходный", first.Title);
        Assert.Empty(db.ChangeTracker.Entries());
    }
}

using System.Reflection;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseDocumentVersionGroupTests
{
    [Fact]
    public void MapDocuments_ReportsTheSameGroupsUsedForLatestVersions()
    {
        ClientCaseDocument[] documents =
        [
            new() { Id = 1, ClientCaseId = 10, DocumentKey = "ß", Version = 1 },
            new() { Id = 2, ClientCaseId = 10, DocumentKey = "ẞ", Version = 2 },
            new() { Id = 3, ClientCaseId = 10, DocumentKey = "ς", Version = 1 },
            new() { Id = 4, ClientCaseId = 10, DocumentKey = "σ", Version = 2 },
            new() { Id = 5, ClientCaseId = 10, DocumentKey = "K", Version = 1 },
            new() { Id = 6, ClientCaseId = 10, DocumentKey = "K", Version = 2 },
            new() { Id = 7, ClientCaseId = 10, DocumentKey = "", Version = 1 },
        ];
        var method = typeof(CasesV2Controller).GetMethod("MapDocuments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(method.Invoke(null, [documents, null]));

        foreach (var document in documents.Where(item => item.DocumentKey.Length > 0))
        {
            var expectedGroup = documents.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.DocumentKey, document.DocumentKey)).ToArray();
            var mapped = Assert.Single(result, item => item.Id == document.Id);
            Assert.Equal(document.DocumentKey, mapped.DocumentKey);
            Assert.Equal(expectedGroup.Min(item => item.Id), mapped.VersionGroupId);
            Assert.Equal(document.Version == expectedGroup.Max(item => item.Version), mapped.IsLatestVersion);
        }

        Assert.Null(Assert.Single(result, item => item.Id == 7).VersionGroupId);
    }
}

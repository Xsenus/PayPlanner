using System.Data.Common;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    public static IEnumerable<object[]> CourtOriginPaths()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var managed in new[] { false, true })
                foreach (var legacyKey in new[] { false, true })
                    yield return [postgres, managed, legacyKey];
    }

    [Theory]
    [MemberData(nameof(CourtOriginPaths))]
    public async Task CourtOrigin_ManualKnownKeyRemainsManual(bool postgres, bool managed, bool legacyKey)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        var manual = await SeedCourtOriginDocumentAsync(f, source.CourtActs[0], legacyKey);
        CaseDocumentPublicationRecords.AddAuditEvent(f.Db, f.Case, manual, "Created",
            $"Документ «{manual.OriginalFileName}» загружен.", f.User.Id, f.User.FullName);
        await f.Db.SaveChangesAsync();
        var before = CourtOriginDocumentState(manual);
        var auditIds = await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.ObjectId == manual.Id)
            .Select(a => a.Id).OrderBy(id => id).ToArrayAsync();
        f.Db.ChangeTracker.Clear();
        var report = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(
            (await f.Controller(source, useManagedStorage: managed).RunCourtSync(f.Case.Id, null, default)).Result).Value);
        Assert.Contains(report.ConflictItems, item => item.Contains("ручной документ", StringComparison.Ordinal));
        Assert.Equal(0, report.CreatedCourtActDocuments);
        Assert.Equal(0, report.PendingCourtActDocuments);
        Assert.Equal(before, CourtOriginDocumentState(await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()));
        var deadline = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        Assert.Equal("active", deadline.Status);
        Assert.Null(deadline.ClientCaseDocumentId);
        Assert.Single(await f.Db.TaskJournalItems.Where(t => t.ClientCaseId == f.Case.Id
            && t.TagsJson.Contains($"case-deadline:entity-{deadline.Id}")).ToArrayAsync());
        deadline.ClientCaseDocumentId = manual.Id;
        deadline.Notes = "Сотрудник выбрал документ вручную";
        await f.Db.SaveChangesAsync();
        var dueAt = deadline.DueAt;
        f.Db.ChangeTracker.Clear();
        var repeated = await f.Controller(source, useManagedStorage: managed).RunCourtSync(f.Case.Id, null, default);
        Assert.IsType<OkObjectResult>(repeated.Result);
        var retained = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(manual.Id, retained.ClientCaseDocumentId);
        Assert.Equal(dueAt, retained.DueAt);
        Assert.Equal("Сотрудник выбрал документ вручную", retained.Notes);
        Assert.Equal(before, CourtOriginDocumentState(await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()));
        Assert.Equal(auditIds, await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.ObjectId == manual.Id)
            .Select(a => a.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(CourtOriginBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(manual.StoragePath)));
        Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
    }

    public static IEnumerable<object[]> CourtInvalidOriginProofs()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var invalid in new[] { "missing", "malformed", "array", "case", "key", "version",
                "type", "filename", "actor", "description", "duplicate", "document-type", "content-type" })
                yield return [postgres, invalid];
    }

    [Theory]
    [MemberData(nameof(CourtInvalidOriginProofs))]
    public async Task CourtOrigin_InvalidAuditCannotAdoptDocument(bool postgres, string invalid)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        var document = await SeedCourtOriginDocumentAsync(f, source.CourtActs[0], false);
        AddCourtOriginAudit(f, document, source.CourtActs[0]);
        var audit = f.Db.ChangeTracker.Entries<ObjectAuditEvent>()
            .Single(entry => entry.Entity.Description?.StartsWith("Сводка судебного события «", StringComparison.Ordinal) == true).Entity;
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(audit.MetadataJson!)!;
        switch (invalid)
        {
            case "missing": audit.MetadataJson = null; break;
            case "malformed": audit.MetadataJson = "{"; break;
            case "array": audit.MetadataJson = "[]"; break;
            case "actor": audit.ActorUserId = null; break;
            case "description": audit.Description = $"Документ «{audit.Description}» загружен."; break;
            case "duplicate": audit.MetadataJson = audit.MetadataJson![..^1] + ",\"version\":1}"; break;
            case "document-type": document.DocumentType = "Договор"; break;
            case "content-type": document.ContentType = "application/pdf"; break;
            default:
                var field = invalid switch
                {
                    "case" => "clientCaseId",
                    "key" => "documentKey",
                    "version" => "version",
                    "type" => "documentType",
                    "filename" => "fileName",
                    _ => throw new InvalidOperationException(invalid)
                };
                fields[field] = JsonSerializer.SerializeToElement(invalid is "case" or "version" ? (object)0 : "другие данные");
                audit.MetadataJson = JsonSerializer.Serialize(fields);
                break;
        }
        await f.Db.SaveChangesAsync();
        var before = CourtOriginDocumentState(document);
        f.Db.ChangeTracker.Clear();
        var report = await GenerateCourtAsync(f, source);
        Assert.Contains(report.ConflictItems, item => item.Contains("ручной документ", StringComparison.Ordinal));
        Assert.Equal(0, report.CreatedCourtActDocuments);
        Assert.Equal(before, CourtOriginDocumentState(await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()));
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Equal(CourtOriginBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(document.StoragePath)));
        Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(CourtOriginPaths))]
    public async Task CourtOrigin_AcceptedCreationCanResumeWithoutNewFile(bool postgres, bool managed, bool legacyKey)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        var document = await SeedCourtOriginDocumentAsync(f, source.CourtActs[0], legacyKey);
        AddCourtOriginAudit(f, document, source.CourtActs[0]);
        await f.Db.SaveChangesAsync();
        var auditId = (await f.Db.ObjectAuditEvents.SingleAsync(a => a.ObjectType == "client-case-document"
            && a.ObjectId == document.Id && a.Description!.StartsWith("Сводка судебного события «"))).Id;
        f.Db.ChangeTracker.Clear();
        var result = await f.Controller(source, useManagedStorage: managed).RunCourtSync(f.Case.Id, null, default);
        var report = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.DoesNotContain(report.ConflictItems, item => item.Contains("ручной документ", StringComparison.Ordinal));
        Assert.Equal(0, report.CreatedCourtActDocuments);
        var recovered = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(document.Id, recovered.Id);
        Assert.Equal(CourtOriginKey(source.CourtActs[0], false), recovered.DocumentKey);
        Assert.Equal("sudrf", recovered.SourceType);
        Assert.Equal(document.StoragePath, recovered.StoragePath);
        Assert.Equal(document.UploadedAt, recovered.UploadedAt);
        Assert.Equal(document.UploadedByUserId, recovered.UploadedByUserId);
        Assert.Equal(document.SizeBytes, recovered.SizeBytes);
        Assert.Equal(document.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Equal(auditId, (await f.Db.ObjectAuditEvents.SingleAsync(a => a.ObjectType == "client-case-document"
            && a.ObjectId == document.Id && a.Description!.StartsWith("Сводка судебного события «"))).Id);
        Assert.Equal(CourtOriginBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(recovered.StoragePath)));
        Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtOrigin_ProofLookupIsBatched(bool postgres)
    {
        var counter = new CourtOriginQueryCounter();
        await using var f = await CreateFixtureAsync(postgres, 1, false, counter);
        var source = new SudrfCourtCaseData
        {
            SourceUrl = f.Case.CourtUrl,
            Instance = "appeal",
            CourtActs = Enumerable.Range(0, 5).Select(index => new SudrfCourtAct
            {
                Title = $"Апелляционное определение {index}",
                ActDate = new DateTime(2026, 10, 1).AddDays(index),
                SourceUrl = f.Case.CourtUrl
            }).ToArray()
        };
        foreach (var act in source.CourtActs)
        {
            var document = await SeedCourtOriginDocumentAsync(f, act, false);
            AddCourtOriginAudit(f, document, act);
        }
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        counter.Armed = true;
        await GenerateCourtAsync(f, source);
        counter.Armed = false;
        Assert.Single(counter.Queries, sql => sql.Contains("\"MetadataJson\"", StringComparison.Ordinal)
            && !sql.Contains("\"CreatedAt\"", StringComparison.Ordinal));
        Assert.Equal(5, await f.Db.ClientCaseDocuments.CountAsync(d => d.SourceType == "sudrf"));
    }

    private static readonly byte[] CourtOriginBytes = Encoding.UTF8.GetBytes("<html>Сохранённый файл без изменений</html>");

    private static string CourtOriginKey(SudrfCourtAct act, bool legacyKey)
        => (string)typeof(CasesV2Controller).GetMethod(legacyKey ? "BuildLegacySudrfCourtActDocumentKey" : "BuildSudrfCourtActDocumentKey",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [act])!;

    private static async Task<ClientCaseDocument> SeedCourtOriginDocumentAsync(Fixture f, SudrfCourtAct act, bool legacyKey)
    {
        var key = CourtOriginKey(act, legacyKey);
        var path = f.LocalCaseStorage.BuildRelativePath(f.Case.Id, key + ".html");
        await f.LocalCaseStorage.SaveAsync(path, CourtOriginBytes, default);
        var document = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = key,
            DocumentType = CourtSyncNormalization.DocumentTypes.CourtEventSummary,
            Status = "received",
            OriginalFileName = "Сводка судебного события.html",
            StoredFileName = key + ".html",
            StoragePath = path,
            ContentType = "text/html; charset=utf-8",
            SizeBytes = CourtOriginBytes.LongLength,
            UploadedByUserId = f.User.Id,
            UploadedByName = f.User.FullName,
            UploadedAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        f.Db.ClientCaseDocuments.Add(document);
        await f.Db.SaveChangesAsync();
        return document;
    }

    private static void AddCourtOriginAudit(Fixture f, ClientCaseDocument document, SudrfCourtAct act)
        => CaseDocumentPublicationRecords.AddAuditEvent(f.Db, f.Case, document, "Created",
            $"Сводка судебного события «{act.Title}» добавлена из ГАС «Правосудие»; текст акта не загружен.", f.User.Id, f.User.FullName);

    private static string CourtOriginDocumentState(ClientCaseDocument document) => JsonSerializer.Serialize(new
    {
        document.Id,
        document.ClientCaseId,
        document.DocumentKey,
        document.Version,
        document.DocumentType,
        document.Status,
        document.OriginalFileName,
        document.StoredFileName,
        document.StoragePath,
        document.ContentType,
        document.SizeBytes,
        document.SourceType,
        document.SourceUrl,
        document.SourceCheckedAt,
        document.SourceConfidence,
        document.UploadedByUserId,
        document.UploadedByName,
        UploadedAt = document.UploadedAt.Ticks
    });

    private sealed class CourtOriginQueryCounter : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("ObjectAuditEvents", StringComparison.Ordinal)) Queries.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Models;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ActAnnualCreateTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    private const string AnnualOversizedNumber = "9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999";
    [Fact]
    public void AnnualAct_CreateNumberIsOptionalInActualMvcMetadata()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForProperty(typeof(UpsertActRequest), nameof(UpsertActRequest.Number));
        Assert.False(metadata.IsRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_KeylessLegacyPostsRemainDistinctAndIgnoreSubmittedInvoiceNumber(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var request = scope.Request(2001);
        request.Number = "INVOICE-774";
        request.InvoiceNumber = "INVOICE-774";
        var first = Created(await scope.Source.Controller.Create(request, default));
        var second = Created(await scope.Source.Controller.Create(request, default));

        Assert.Equal("1", first.Number);
        Assert.Equal("2", second.Number);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("INVOICE-774", first.InvoiceNumber);
        Assert.Empty(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Equal(2, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_KeyedReplayKeepsActAndNumberAndIgnoresNumberField(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var key = Guid.NewGuid();
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var request = scope.Request(2002);
        request.Number = "OLD-MANUAL";
        var first = Created(await controller.Create(request, default));
        request.Number = null!;
        var replay = Created(await controller.Create(request, default));

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal("1", replay.Number);
        Assert.Equal(key.ToString("D"), controller.Response.Headers["X-Upload-Key"].ToString());
        Assert.Single(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Single(scope.Source.Notifications.Calls, name => name == nameof(INotificationService.NotifyActCreatedAsync));
    }

    [Theory]
    [InlineData(false, "amount")]
    [InlineData(true, "amount")]
    [InlineData(false, "date")]
    [InlineData(true, "date")]
    public async Task AnnualAct_ChangedBusinessCommandUnderKnownKeyConflictsWithoutAdvancing(bool postgres, string field)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var request = scope.Request(2003);
        var first = Created(await controller.Create(request, default));
        if (field == "amount") request.Amount += 1m;
        else request.Date = request.Date.AddDays(1);

        Assert.IsType<ConflictObjectResult>((await controller.Create(request, default)).Result);

        Assert.Equal("1", (await scope.Db.Acts.AsNoTracking().SingleAsync(item => item.Id == first.Id)).Number);
        Assert.Single(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Single(scope.Source.Notifications.Calls, name => name == nameof(INotificationService.NotifyActCreatedAsync));
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "zero")]
    [InlineData(true, "zero")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "multiple")]
    [InlineData(true, "multiple")]
    public async Task AnnualAct_InvalidUploadKeyCannotFallBackToUntrackedCreate(bool postgres, string input)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = input switch
        {
            "empty" => new StringValues(string.Empty),
            "zero" => new StringValues(Guid.Empty.ToString("D")),
            "multiple" => new StringValues([Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")]),
            _ => new StringValues("invalid-key")
        };
        var before = await scope.Db.Acts.CountAsync();

        Assert.IsType<BadRequestObjectResult>((await controller.Create(scope.Request(), default)).Result);

        Assert.Equal(before, await scope.Db.Acts.CountAsync());
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Empty(scope.Source.Notifications.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_DeletedCanonicalActDoesNotReopenItsCreateKey(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        var key = Guid.NewGuid();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var request = scope.Request(2004);
        var created = Created(await controller.Create(request, default));
        scope.Db.Acts.Remove(await scope.Db.Acts.SingleAsync(item => item.Id == created.Id));
        await scope.Db.SaveChangesAsync();

        Assert.Equal(410, Assert.IsType<ObjectResult>((await controller.Create(request, default)).Result).StatusCode);
        Assert.IsType<NotFoundResult>((await controller.GetCreateAttempt(key, default)).Result);
        Assert.Single(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var next = Created(await controller.Create(request, default));
        Assert.Equal("2", next.Number);
        Assert.NotEqual(created.Id, next.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_RecoveryRequiresCurrentViewAndTheOriginalActor(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var key = Guid.NewGuid();
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Created(await controller.Create(scope.Request(2005), default));
        var own = Assert.IsType<ActDto>(Assert.IsType<OkObjectResult>((await controller.GetCreateAttempt(key, default)).Result).Value);
        Assert.Equal(created.Id, own.Id);
        Assert.IsType<NotFoundResult>((await controller.GetCreateAttempt(Guid.NewGuid(), default)).Result);
        scope.Db.RolePermissions.Add(new RolePermission
        {
            RoleId = scope.Source.Other.RoleId,
            Section = RolePermissionCatalog.Acts,
            CanView = true,
            ViewScope = DataVisibilityScope.All
        });
        await scope.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>((await scope.Controller(scope.Source.Other.Id).GetCreateAttempt(key, default)).Result);

        var permission = await scope.Db.RolePermissions.SingleAsync(item => item.RoleId == scope.Source.Actor.RoleId && item.Section == RolePermissionCatalog.Acts);
        permission.CanView = false;
        await scope.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await scope.Controller().GetCreateAttempt(key, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Create(scope.Request(2005), default)).Result);
        permission.CanView = true;
        await scope.Db.SaveChangesAsync();
        var act = await scope.Db.Acts.SingleAsync(item => item.Id == created.Id);
        act.ClientId = scope.Source.ForeignClient.Id;
        act.ResponsibleId = scope.Source.Other.Id;
        await scope.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>((await scope.Controller().GetCreateAttempt(key, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Create(scope.Request(2005), default)).Result);
        Assert.Single(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_PostCommitNotificationFailureReplaysWithoutAnotherActOrNotification(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var notifications = DispatchProxy.Create<INotificationService, FailingNotification>();
        var probe = (FailingNotification)(object)notifications;
        var key = Guid.NewGuid();
        var controller = scope.Controller(notifications: notifications);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var request = scope.Request(2006);
        await Assert.ThrowsAsync<IOException>(() => controller.Create(request, default));
        var receipt = await scope.Db.ActCreateReceipts.AsNoTracking().SingleAsync();

        var replay = Created(await controller.Create(request, default));

        Assert.Equal(receipt.ActId, replay.Id);
        Assert.Equal("1", replay.Number);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Single(await scope.Db.ActCreateReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnnualAct_NumberRequirementIsRemovedOnlyFromCreateFormRequirements(bool postgres, bool anotherRequiredField)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var requirements = new FixedRequirements(anotherRequiredField ? ["Number", "Title"] : ["Number"]);
        var result = await scope.Controller(requirements: requirements).Create(scope.Request(2007), default);
        if (anotherRequiredField)
        {
            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        }
        else Assert.Equal("1", Created(result).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_KeyedNullLineValidationDoesNotThrowOrConsumeNumber(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var request = scope.Request(2008);
        request.LineItems = [null!];
        controller.ProblemDetailsFactory = new DefaultProblemDetailsFactory(Options.Create(new ApiBehaviorOptions()));

        var result = await controller.Create(request, default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeActionResult>(result.Result).StatusCode);
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("LineItems[0]", problem.Errors.Keys);
        Assert.Contains("Позиция акта не может быть пустой.", problem.Errors["LineItems[0]"]);
        Assert.Empty(await scope.Db.Acts.Where(item => item.Date.Year == 2008).ToArrayAsync());
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Empty(scope.Source.Notifications.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_FinancialSaveFailureRollsBackCounterActAndReceipt(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var request = scope.Request(2009);
        var armed = true;
        scope.Db.SavingChanges += (_, _) =>
        {
            if (armed && scope.Db.ChangeTracker.Entries<Act>().Any(item => item.State == EntityState.Added))
                throw new IOException("Synthetic failure before the financial save commit.");
        };
        await Assert.ThrowsAsync<IOException>(() => controller.Create(request, default));
        armed = false;
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await scope.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Empty(await scope.Db.Acts.Where(item => item.Date.Year == 2009).ToArrayAsync());
        Assert.Equal("1", Created(await controller.Create(request, default)).Number);
    }

    [Theory]
    [InlineData(false, "1", "2")]
    [InlineData(true, "1", "2")]
    [InlineData(false, "00042", "43")]
    [InlineData(true, "00042", "43")]
    [InlineData(false, AnnualOversizedNumber, "1")]
    [InlineData(true, AnnualOversizedNumber, "1")]
    public async Task AnnualAct_UpdateRetainsManualNumberRulesAndRaisesTheDestinationYearFloor(bool postgres, string updatedNumber, string nextNumber)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var controller = scope.Source.Controller;
        var request = scope.Request(2010);
        var created = Created(await controller.Create(request, default));
        request.Number = updatedNumber;
        request.Date = new DateTime(2011, 1, 1);

        var updated = Assert.IsType<ActDto>(Assert.IsType<OkObjectResult>((await controller.Update(created.Id, request, default)).Result).Value);

        Assert.Equal(updatedNumber, updated.Number);
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync(item => item.Year == 2010)).LastNumber);
        Assert.Equal(nextNumber, Created(await controller.Create(scope.Request(2011), default)).Number);
        var previousFloor = await scope.Db.ActAnnualNumberCounters.AsNoTracking().Where(item => item.Year == 2011)
            .Select(item => item.LastNumber).SingleAsync();
        request.Number = string.Empty;
        controller.ProblemDetailsFactory = new DefaultProblemDetailsFactory(Options.Create(new ApiBehaviorOptions()));

        var invalid = await controller.Update(created.Id, request, default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeActionResult>(invalid.Result).StatusCode);
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(invalid.Result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains(nameof(UpsertActRequest.Number), problem.Errors.Keys);
        Assert.Contains("Номер акта обязателен.", problem.Errors[nameof(UpsertActRequest.Number)]);
        Assert.Equal(updatedNumber, await scope.Db.Acts.AsNoTracking().Where(item => item.Id == created.Id)
            .Select(item => item.Number).SingleAsync());
        Assert.Equal(previousFloor, await scope.Db.ActAnnualNumberCounters.AsNoTracking().Where(item => item.Year == 2011)
            .Select(item => item.LastNumber).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_CorrectionKeepsItsSuffixAndDoesNotConsumeTheOrdinarySeries(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var request = scope.Request(2012);
        var created = Created(await scope.Source.Controller.Create(request, default));

        var correction = Created(await scope.Source.Controller.CreateCorrection(created.Id, new CreateActCorrectionRequest { Reason = "Уточнение" }, default));

        Assert.Equal("1-К1", correction.Number);
        Assert.Equal(created.Id, correction.OriginalActId);
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal("2", Created(await scope.Source.Controller.Create(request, default)).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_HintIsReadOnlyAndTheDocumentYearUsesCivilDateInsteadOfProfileConversion(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        scope.Db.UserSettings.Add(new UserSetting { UserId = scope.Source.Actor.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "Pacific/Kiritimati" });
        await scope.Db.SaveChangesAsync();
        var date = new DateTime(2013, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var hint = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await scope.Source.Controller.GetNextNumber(date, default)).Value);
        Assert.Equal(2013, hint.GetProperty("year").GetInt32());
        Assert.Equal("1", hint.GetProperty("nextNumber").GetString());
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        var request = scope.Request(2013);
        request.Date = date;

        var created = Created(await scope.Source.Controller.Create(request, default));

        Assert.Equal(new DateTime(2013, 12, 31), created.Date.Date);
        Assert.Equal(2013, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).Year);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ExhaustedYearReturns422AndTheUncommittedKeyCanUseAnotherDate(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var request = scope.Request(2014);
        scope.Db.Acts.Add(new Act { Number = long.MaxValue.ToString(), Date = request.Date });
        await scope.Db.SaveChangesAsync();
        var controller = scope.Source.Controller;
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");

        Assert.IsType<UnprocessableEntityObjectResult>(await controller.GetNextNumber(request.Date, default));
        Assert.IsType<UnprocessableEntityObjectResult>((await controller.Create(request, default)).Result);
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await scope.Db.ActCreateReceipts.ToArrayAsync());
        request.Date = request.Date.AddYears(1);
        Assert.Equal("1", Created(await controller.Create(request, default)).Number);
    }

    private static ActDto Created(ActionResult<ActDto> result)
        => Assert.IsType<ActDto>(Assert.IsType<CreatedResult>(result.Result).Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_MovingAnUninitialisedHistoricalNumberKeepsTheOldYearFloor(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var historical = new Act { Number = "42", Date = new DateTime(2045, 3, 4), ClientId = scope.Source.OwnClient.Id, ResponsibleId = scope.Source.Actor.Id };
        scope.Db.Acts.Add(historical);
        await scope.Db.SaveChangesAsync();
        var request = scope.Request(2046);
        request.Number = "MANUAL-HISTORY";

        var updated = Assert.IsType<ActDto>(Assert.IsType<OkObjectResult>((await scope.Source.Controller.Update(historical.Id, request, default)).Result).Value);

        Assert.Equal("MANUAL-HISTORY", updated.Number);
        Assert.Equal(42, (await scope.Db.ActAnnualNumberCounters.SingleAsync(item => item.Year == 2045)).LastNumber);
        Assert.Equal("43", Created(await scope.Source.Controller.Create(scope.Request(2045), default)).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_PhysicalDeletionOfUninitialisedHistoryKeepsItsIssuedFloor(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var permission = await scope.Db.RolePermissions.SingleAsync(item => item.RoleId == scope.Source.Actor.RoleId && item.Section == RolePermissionCatalog.Acts);
        permission.CanDelete = true;
        var historical = new Act { Number = "42", Date = new DateTime(2047, 3, 4), ClientId = scope.Source.OwnClient.Id, ResponsibleId = scope.Source.Actor.Id };
        scope.Db.Acts.Add(historical);
        await scope.Db.SaveChangesAsync();
        await using var physical = postgres
            ? (PaymentContext)new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(scope.ConnectionString!).Options, useObjectAuditModel: false)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(scope.Db.Database.GetDbConnection()).Options, useObjectAuditModel: false);
        var controller = new ActsController(physical, new ActAnnualTestScope.NoopRequirements()) { ControllerContext = ActAnnualTestScope.Context(scope.Source.Actor.Id) };

        Assert.IsType<NoContentResult>(await controller.Delete(historical.Id, default));

        Assert.False(await scope.Db.Acts.IgnoreQueryFilters().AnyAsync(item => item.Id == historical.Id));
        Assert.Equal(42, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("43", await new ActAnnualNumberingService(scope.Db).NextAsync(historical.Date, default));
        await transaction.CommitAsync();
    }

    private sealed class FixedRequirements(List<string> missing) : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(missing.ToList());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(missing.ToList());
    }

    public class FailingNotification : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotificationService.NotifyActCreatedAsync))
            {
                Calls++;
                return Task.FromException(new IOException("Synthetic lost acknowledgement after financial commit."));
            }
            return Task.CompletedTask;
        }
    }
}

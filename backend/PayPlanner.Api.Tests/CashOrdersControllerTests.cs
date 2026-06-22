using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrdersControllerTests
{
    [Fact]
    public async Task GetNextNumber_ReturnsNextUnusedCashOrderNumber()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 1),
                Amount = 1_000m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 2),
                Amount = 2_000m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetNextNumber(
            CashOrderType.Incoming,
            new DateTime(2026, 4, 20),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(ok.Value);
        var value = ok.Value;
        var number = value.GetType().GetProperty("number")?.GetValue(value);
        var type = value.GetType().GetProperty("type")?.GetValue(value);
        var date = value.GetType().GetProperty("date")?.GetValue(value);

        Assert.Equal("ПКО-2026-0004", number);
        Assert.Equal(CashOrderType.Incoming, type);
        Assert.Equal(new DateTime(2026, 4, 20), date);
    }

    [Fact]
    public async Task DefaultDates_UseProfileTimeZoneForCashOrderToday()
    {
        await using var db = await CreateContextAsync();
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var userId = await CreateUserWithTimeZoneAsync(db, selectedZone.TimeZoneId);
        var controller = CreateController(db, userId);

        var nextNumberAction = await controller.GetNextNumber(
            CashOrderType.Incoming,
            date: null,
            CancellationToken.None);

        var nextNumberOk = Assert.IsType<OkObjectResult>(nextNumberAction);
        Assert.NotNull(nextNumberOk.Value);
        var nextNumberDate = nextNumberOk.Value.GetType().GetProperty("date")?.GetValue(nextNumberOk.Value);
        Assert.Equal(selectedZone.ProfileToday, nextNumberDate);

        var createAction = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = default,
            Amount = 1_200m,
            Currency = "RUB",
            Account = "Main cashbox",
            PayerName = "Client",
            Basis = "Cash payment",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedResult>(createAction);
        var created = Assert.IsType<CashOrderDto>(createdResult.Value);
        Assert.Equal(selectedZone.ProfileToday, created.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", created.Number);

        var cashBookAction = await controller.GetCashBook(
            from: null,
            to: null,
            accountName: "Main cashbox",
            ct: CancellationToken.None);

        var cashBookOk = Assert.IsType<OkObjectResult>(cashBookAction.Result);
        var cashBook = Assert.IsType<CashBookResponseDto>(cashBookOk.Value);
        Assert.Equal(selectedZone.ProfileToday, cashBook.From);
        Assert.Equal(selectedZone.ProfileToday, cashBook.To);
        Assert.Equal(1_200m, cashBook.Income);

        var inventoryAction = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = default,
                AccountName = "Main cashbox",
                ActualBalance = 1_200m,
                CreateCorrection = false,
            },
            CancellationToken.None);

        var inventoryCreated = Assert.IsType<CreatedResult>(inventoryAction);
        var inventory = Assert.IsType<CashInventoryDto>(inventoryCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, inventory.Date);
        Assert.StartsWith($"ИНВК-{selectedZone.ProfileToday.Year}-", inventory.Number);

        var copyAction = await controller.Copy(created.Id, CancellationToken.None);
        var copyCreated = Assert.IsType<CreatedResult>(copyAction);
        var copied = Assert.IsType<CashOrderDto>(copyCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, copied.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", copied.Number);
    }

    [Fact]
    public async Task CreateInventory_CalculatesDifferenceAndCreatesCorrectionOrder()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 1_000m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 250m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 500m,
                Account = "Резервная касса",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db);

        var action = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = new DateTime(2026, 5, 3),
                AccountName = " Основная касса ",
                ActualBalance = 800m,
                Comment = "Излишек по пересчету",
                CreateCorrection = true,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var inventory = Assert.IsType<CashInventoryDto>(created.Value);
        Assert.Equal("ИНВК-2026-0001", inventory.Number);
        Assert.Equal(750m, inventory.AccountingBalance);
        Assert.Equal(800m, inventory.ActualBalance);
        Assert.Equal(50m, inventory.Difference);
        Assert.NotNull(inventory.CorrectionCashOrderId);

        var correction = await db.CashOrders.SingleAsync(order => order.Id == inventory.CorrectionCashOrderId);
        Assert.Equal(CashOrderType.Incoming, correction.Type);
        Assert.Equal(CashOrderStatus.Posted, correction.Status);
        Assert.Equal(50m, correction.Amount);
        Assert.Equal("Основная касса", correction.Account);

        var balanceAfterCorrection = await db.CashOrders
            .Where(order => order.Status == CashOrderStatus.Posted && order.Account == "Основная касса")
            .SumAsync(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount);
        Assert.Equal(800m, balanceAfterCorrection);
    }

    [Fact]
    public async Task GetInventories_FiltersByAccountAndPeriod()
    {
        await using var db = await CreateContextAsync();
        db.CashInventories.AddRange(
            new CashInventory
            {
                Number = "ИНВК-2026-0001",
                Date = new DateTime(2026, 5, 1),
                AccountName = "Основная касса",
                AccountingBalance = 1_000m,
                ActualBalance = 1_000m,
                Difference = 0m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashInventory
            {
                Number = "ИНВК-2026-0002",
                Date = new DateTime(2026, 5, 2),
                AccountName = "Резервная касса",
                AccountingBalance = 500m,
                ActualBalance = 450m,
                Difference = -50m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db);

        var action = await controller.GetInventories(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            accountName: "Основная касса",
            page: 1,
            pageSize: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<PagedResponse<CashInventoryDto>>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal("ИНВК-2026-0001", item.Number);
        Assert.Equal("Основная касса", item.AccountName);
        Assert.Equal(1, response.Total);
    }

    [Fact]
    public async Task CashBook_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetCashBook(null, null, null, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task Create_WithoutPaymentsCreatePermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task PrintRegister_WithoutPaymentsExportPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.PrintRegister(null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Register_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetRegister(null, null, null, null, null, 1, 25, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task GetById_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetById(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task RegisterAndCashBook_ApplyPaymentsVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var visibleClient = new Client { Name = "Visible cash register client", ResponsibleUserId = userId };
        var hiddenClient = new Client { Name = "Hidden cash register client", ResponsibleUserId = hiddenUserId };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "PKO-VISIBLE",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 1_000m,
                ClientId = visibleClient.Id,
                Account = "Основная касса",
            },
            new CashOrder
            {
                Number = "PKO-HIDDEN",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 7_000m,
                ClientId = hiddenClient.Id,
                Account = "Основная касса",
            },
            new CashOrder
            {
                Number = "RKO-OWN",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 300m,
                CreatedByUserId = userId,
                Account = "Основная касса",
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var registerAction = await controller.GetRegister(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            1,
            25,
            "Основная касса",
            CancellationToken.None);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(registerAction.Result).Value);
        Assert.Equal(2, register.Total);
        Assert.Equal(1_000m, register.IncomingAmount);
        Assert.Equal(300m, register.OutgoingAmount);
        Assert.DoesNotContain(register.Items, item => item.Number == "PKO-HIDDEN");

        var cashBookAction = await controller.GetCashBook(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 2),
            "Основная касса",
            ct: CancellationToken.None);
        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(cashBookAction.Result).Value);
        Assert.Equal(1_000m, cashBook.Income);
        Assert.Equal(300m, cashBook.Expense);
        Assert.Equal(700m, cashBook.ClosingBalance);
    }

    [Fact]
    public async Task CashBook_AppliesLinkedPaymentFilters()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);

        var targetClient = new Client { Name = "Target Client" };
        var otherClient = new Client { Name = "Other Client" };
        var targetCase = new ClientCase { CaseNumber = "CASE-1", Client = targetClient };
        var otherCase = new ClientCase { CaseNumber = "CASE-2", Client = otherClient };
        var targetContract = new Contract { Number = "CONTRACT-1", Date = new DateTime(2026, 5, 1) };
        var otherContract = new Contract { Number = "CONTRACT-2", Date = new DateTime(2026, 5, 1) };
        var targetIncomeType = new IncomeType { Name = "Target income", PaymentType = PaymentType.Income };
        var otherIncomeType = new IncomeType { Name = "Other income", PaymentType = PaymentType.Income };
        db.AddRange(targetClient, otherClient, targetCase, otherCase, targetContract, otherContract, targetIncomeType, otherIncomeType);
        await db.SaveChangesAsync();

        var targetPayment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Amount = 1_500m,
            PaidAmount = 1_500m,
            Description = "Target cash payment",
            CounterpartyName = "Target payer",
            ClientId = targetClient.Id,
            ClientCaseId = targetCase.Id,
            ContractId = targetContract.Id,
            IncomeTypeId = targetIncomeType.Id,
        };
        var otherPayment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Amount = 9_000m,
            PaidAmount = 9_000m,
            Description = "Other cash payment",
            CounterpartyName = "Other payer",
            ClientId = otherClient.Id,
            ClientCaseId = otherCase.Id,
            ContractId = otherContract.Id,
            IncomeTypeId = otherIncomeType.Id,
        };
        db.Payments.AddRange(targetPayment, otherPayment);
        await db.SaveChangesAsync();

        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "PKO-FILTER-TARGET",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 10),
                Amount = 1_500m,
                Account = "Main cashbox",
                PayerName = "Target payer",
                ClientId = targetClient.Id,
                PaymentId = targetPayment.Id,
            },
            new CashOrder
            {
                Number = "PKO-FILTER-OTHER",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 10),
                Amount = 9_000m,
                Account = "Main cashbox",
                PayerName = "Other payer",
                ClientId = otherClient.Id,
                PaymentId = otherPayment.Id,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetCashBook(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Main cashbox",
            counterpartyName: "Target",
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: targetContract.Id,
            incomeTypeId: targetIncomeType.Id,
            ct: CancellationToken.None);

        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1_500m, cashBook.Income);
        Assert.Equal(0m, cashBook.Expense);
        Assert.Equal(1_500m, cashBook.ClosingBalance);
        Assert.Equal(1, cashBook.IncomingCount);
    }

    [Fact]
    public async Task GetById_HiddenCashOrder_ReturnsNotFoundForLimitedRole()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenClient = new Client { Name = "Hidden cash order client", ResponsibleUserId = hiddenUserId };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();
        var hiddenOrder = new CashOrder
        {
            Number = "PKO-HIDDEN-ID",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 7_000m,
            ClientId = hiddenClient.Id,
        };
        db.CashOrders.Add(hiddenOrder);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetById(hiddenOrder.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task Update_WithoutPaymentsEditPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Update(1, new CashOrder
        {
            Type = CashOrderType.Incoming,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Delete_WithoutPaymentsDeletePermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canDelete: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Delete(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Print_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Print(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task CreateInventory_WithoutPaymentsEditPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = new DateTime(2026, 5, 1),
                AccountName = "Main cashbox",
                ActualBalance = 1_000m,
                CreateCorrection = false,
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task GetById_IncludesOriginAndAuditFields()
    {
        await using var db = await CreateContextAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        var order = new CashOrder
        {
            Number = "PKO-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 1_000m,
            MoneyOperationId = operation.Id,
            CreatedByUserId = 7,
            CreatedByUserName = "Бухгалтер",
            CreatedAt = DateTime.UtcNow,
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 1);

        var action = await controller.GetById(order.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var dto = Assert.IsType<CashOrderDto>(ok.Value);
        Assert.Equal("moneyOperation", dto.OriginType);
        Assert.Equal(7, dto.CreatedByUserId);
        Assert.Equal("Бухгалтер", dto.CreatedByUserName);
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

    private static async Task<int> CreateUserWithTimeZoneAsync(PaymentContext db, string timeZoneId)
    {
        var role = new Role
        {
            Name = $"cash-order-timezone-role-{Guid.NewGuid():N}",
            Description = "Cash order timezone test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"cash-order-timezone-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Cash order timezone user",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static async Task<int> CreateUserWithPaymentsPermissionAsync(
        PaymentContext db,
        bool canView = false,
        bool canCreate = false,
        bool canEdit = false,
        bool canDelete = false,
        bool canExport = false,
        string viewScope = DataVisibilityScope.All)
    {
        var role = new Role
        {
            Name = $"cash-order-permissions-{Guid.NewGuid():N}",
            Description = "Cash order permissions test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"cash-order-permissions-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Cash order permissions user",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.RolePermissions.Add(new RolePermission
        {
            Role = role,
            Section = RolePermissionCatalog.Payments,
            CanView = canView,
            CanCreate = canCreate,
            CanEdit = canEdit,
            CanDelete = canDelete,
            CanExport = canExport,
            ViewScope = viewScope,
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static CashOrdersController CreateController(PaymentContext db, int userId, bool isAdminClaim = true)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        isAdminClaim
                            ?
                            [
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                                new Claim(ClaimTypes.Role, "admin"),
                            ]
                            :
                            [
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            ],
                        authenticationType: "Test")),
                },
            },
        };

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }
}

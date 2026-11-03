using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Tests;

public partial class ClientsControllerTests
{
    internal static ClientsController AtomicCreateController(PaymentContext db, int actorId)
        => CreateController(db, actorId);
}

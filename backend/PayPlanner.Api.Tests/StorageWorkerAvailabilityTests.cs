using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageWorkerAvailabilityTests
{
    [Fact]
    public void CaptureReflectsHostedWorkerRegistrationWithoutStartingAnyService()
    {
        var services = new ServiceCollection();
        var absent = StorageWorkerAvailability.Capture(services);
        Assert.False(absent.IsRegistered);
        services.AddHostedService<StorageOperationWorker>();
        var present = StorageWorkerAvailability.Capture(services);
        Assert.True(present.IsRegistered);
        Assert.False(absent.IsRegistered);
        services.Clear();
        Assert.True(present.IsRegistered);
        Assert.False(StorageWorkerAvailability.Capture(services).IsRegistered);
    }
}

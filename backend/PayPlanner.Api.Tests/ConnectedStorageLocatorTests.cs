using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ConnectedStorageLocatorTests
{
    [Theory]
    [InlineData("owned/folder/file.txt", true, "owned")]
    [InlineData("/owned/folder/file.txt/", true, "owned")]
    [InlineData("owned\\folder\\file.txt", true, "owned")]
    [InlineData("owned", false, "")]
    [InlineData("owned/", false, "")]
    [InlineData("../file", false, "")]
    [InlineData("owned/../foreign", false, "")]
    [InlineData("", false, "")]
    public void NativeConnectedAdaptersParseSameConnectionWithoutCallingRemoteServices(string locator, bool valid, string expectedConnection)
    {
        foreach (var provider in new IStorageConnectedLocatorProvider[] { new S3StorageProvider(null!), new YandexDiskStorageProvider(null!) })
        {
            Assert.Equal(valid, provider.TryGetConnectionId(locator, out var connection));
            Assert.Equal(expectedConnection, connection);
            if (valid)
            {
                var factory = (IStorageRepairDestinationProvider)provider;
                var generation = Guid.NewGuid();
                var recovery = factory.CreateRepairLocator(locator, generation, "owned-operation");
                Assert.True(provider.TryGetConnectionId(recovery, out var recoveryConnection));
                Assert.Equal(expectedConnection, recoveryConnection);
                Assert.Equal(recovery, factory.CreateRepairLocator(locator, generation, "owned-operation"));
            }
        }
    }
}

using AgentRelay.Windows;
using Xunit;

namespace AgentRelay.IntegrationTests;

public sealed class GlobalAgyLeaseTests
{
    [Fact]
    public void TryAcquire_ExcludesConcurrentOwnerAndReleasesOnDispose()
    {
        var name = $"AgentRelay-test-{Guid.NewGuid():N}";
        using (var first = GlobalAgyLease.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.True(first.IsHeld);
            Assert.Null(GlobalAgyLease.TryAcquire(name));
        }

        using var next = GlobalAgyLease.TryAcquire(name);
        Assert.NotNull(next);
    }
}

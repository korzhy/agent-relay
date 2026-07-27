using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class DuplicateHashGuardTests
{
    [Fact]
    public void TryAccept_AcceptsFirstHash_RejectsDuplicates_RejectsNull()
    {
        var guard = new DuplicateHashGuard();

        Assert.False(guard.TryAccept(null));
        Assert.False(guard.TryAccept(""));
        Assert.False(guard.TryAccept("   "));

        Assert.True(guard.TryAccept("abc123hash"));
        Assert.False(guard.TryAccept("abc123hash"));
        Assert.False(guard.TryAccept("ABC123HASH")); // Case insensitive duplicate

        Assert.True(guard.TryAccept("def456hash"));
        Assert.False(guard.TryAccept("def456hash"));
    }
}

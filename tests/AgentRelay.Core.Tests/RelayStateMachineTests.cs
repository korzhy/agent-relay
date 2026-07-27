using System;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class RelayStateMachineTests
{
    [Theory]
    [InlineData(RelayState.Ready, RelayState.Running)]
    [InlineData(RelayState.Ready, RelayState.Paused)]
    [InlineData(RelayState.Running, RelayState.Waiting)]
    [InlineData(RelayState.Running, RelayState.Stalled)]
    [InlineData(RelayState.Running, RelayState.QuotaExhausted)]
    [InlineData(RelayState.Running, RelayState.ReportReady)]
    [InlineData(RelayState.Running, RelayState.Paused)]
    [InlineData(RelayState.Waiting, RelayState.Running)]
    [InlineData(RelayState.Waiting, RelayState.ReportReady)]
    [InlineData(RelayState.Stalled, RelayState.Running)]
    [InlineData(RelayState.Stalled, RelayState.Ready)]
    [InlineData(RelayState.QuotaExhausted, RelayState.Ready)]
    [InlineData(RelayState.ReportReady, RelayState.Ready)]
    [InlineData(RelayState.Paused, RelayState.Ready)]
    public void Transition_ValidTransitions_Succeed(RelayState current, RelayState next)
    {
        var now = DateTimeOffset.UtcNow;
        var initial = new ProjectRuntimeState(1, "p1", current, null, null, null, null, null, now, null, null, null);

        var updated = RelayStateMachine.Transition(initial, next, now, "Transition test");

        Assert.Equal(next, updated.State);
        Assert.Equal("Transition test", updated.Detail);
    }

    [Theory]
    [InlineData(RelayState.Ready, RelayState.ReportReady)]
    [InlineData(RelayState.Stalled, RelayState.Waiting)]
    [InlineData(RelayState.QuotaExhausted, RelayState.Waiting)]
    [InlineData(RelayState.ReportReady, RelayState.Waiting)]
    public void Transition_InvalidTransitions_ThrowInvalidOperationException(RelayState current, RelayState next)
    {
        var now = DateTimeOffset.UtcNow;
        var initial = new ProjectRuntimeState(1, "p1", current, null, null, null, null, null, now, null, null, null);

        Assert.Throws<InvalidOperationException>(() => RelayStateMachine.Transition(initial, next, now));
    }
}

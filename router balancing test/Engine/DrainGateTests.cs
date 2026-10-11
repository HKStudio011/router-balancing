using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class DrainGateTests
{
    [Fact]
    public void BeginDrain_SetsFlagAndRaisesChanged()
    {
        var gate = new DrainGate();
        var raised = 0;
        gate.Changed += () => raised++;

        gate.BeginDrain();

        Assert.True(gate.IsDraining);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CancelDrain_ClearsFlagAndRaisesChanged()
    {
        var gate = new DrainGate();
        gate.BeginDrain();
        var raised = 0;
        gate.Changed += () => raised++;

        gate.CancelDrain();

        Assert.False(gate.IsDraining);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void BeginDrain_Twice_RaisesChangedOnce()
    {
        var gate = new DrainGate();
        var raised = 0;
        gate.Changed += () => raised++;

        gate.BeginDrain();
        gate.BeginDrain();

        Assert.True(gate.IsDraining);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CancelDrain_WhenNotDraining_DoesNotRaise()
    {
        var gate = new DrainGate();
        var raised = 0;
        gate.Changed += () => raised++;

        gate.CancelDrain();

        Assert.False(gate.IsDraining);
        Assert.Equal(0, raised);
    }
}

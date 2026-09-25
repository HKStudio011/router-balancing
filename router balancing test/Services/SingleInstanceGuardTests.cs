using RouterBalancing.Core.Security;

namespace router_balancing_test.Services;

public class SingleInstanceGuardTests : IDisposable
{
    public SingleInstanceGuardTests() => SingleInstanceGuard.Release();

    public void Dispose() => SingleInstanceGuard.Release();

    [Fact]
    public void TryAcquire_WhenCalledTwice_FirstWinsSecondFails()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        Assert.False(SingleInstanceGuard.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AfterRelease_Succeeds()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        SingleInstanceGuard.Release();

        Assert.True(SingleInstanceGuard.TryAcquire());
    }
}

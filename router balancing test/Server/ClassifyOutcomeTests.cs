using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class ClassifyOutcomeTests
{
    public static IEnumerable<object?[]> Cases() =>
    [
        [new DispatchOutcome.Handled(), TraceStage.Finished, true, null],
        [new DispatchOutcome.Passthrough(200, null, [], null), TraceStage.Finished, true, 200],
        [new DispatchOutcome.Passthrough(503, null, [], null), TraceStage.Finished, false, 503],
        [new DispatchOutcome.Error(400, "m", "invalid_request_error", null, null), TraceStage.Finished, false, 400],
        [new DispatchOutcome.Cancelled(), TraceStage.Canceled, null, null],
        [new DispatchOutcome.Aborted(), TraceStage.Canceled, null, null],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClassifyOutcome_MapsDispatchOutcomeToTraceStage(
        DispatchOutcome outcome, TraceStage stage, bool? success, int? status)
    {
        var (actualStage, actualSuccess, actualStatus) = ProxyApp.ClassifyOutcome(outcome);

        Assert.Equal(stage, actualStage);
        Assert.Equal(success, actualSuccess);
        Assert.Equal(status, actualStatus);
    }
}

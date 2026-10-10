namespace RouterBalancing.Core.Stats;

/// <summary>
/// Marker cho journal writer: ProxyHost nhận qua ctor để eager-create singleton từ app
/// container — writer subscribe <c>ITraceFeed.Published</c> trong ctor nhưng không endpoint
/// nào inject nó nên DI không tự resolve (spec dashboard-stats §4).
/// </summary>
public interface IRequestJournalWriter
{
}

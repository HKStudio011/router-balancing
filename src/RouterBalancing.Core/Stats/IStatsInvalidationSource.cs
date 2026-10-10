namespace RouterBalancing.Core.Stats;

/// <summary>Nguồn báo dữ liệu stats đã thay đổi — Task 4 (service) và Task 6 (UI) subscribe.</summary>
public interface IStatsInvalidationSource
{
    /// <summary>Phát sau mỗi lần <see cref="Invalidate"/> — StatsPanel debounce rồi refetch (spec dashboard-stats §6).</summary>
    event Action Changed;

    /// <summary>Báo dữ liệu stats cần làm mới — writer gọi sau khi ghi dòng terminal thành công.</summary>
    void Invalidate();
}

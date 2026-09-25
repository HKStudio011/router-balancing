namespace RouterBalancing.Core.Security;

/// <summary>
/// Chặn mở app lần 2: named mutex giữ nguyên qua vòng đời process;
/// instance thứ 2 nhận false và thoát — tránh 2 Kestrel giành port/tray trùng.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "router-balancing-single-instance";
    private static Mutex? _mutex;
    private static readonly object Sync = new();

    /// <summary>True nếu đây là instance đầu tiên; false nếu mutex đã tồn tại (instance khác).</summary>
    public static bool TryAcquire()
    {
        lock (Sync)
        {
            if (_mutex is not null) return false;

            // initiallyOwned=false: chỉ giữ quyền sở hữu object mutex, không cần lock tức thì
            var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            // Giữ reference tĩnh — nếu để GC thu, mutex bị giải và instance khác vào được
            _mutex = mutex;
            return true;
        }
    }

    /// <summary>Giải mutex — chỉ dùng trong test.</summary>
    public static void Release()
    {
        lock (Sync)
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}

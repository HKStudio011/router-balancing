namespace RouterBalancing.Core.Proxies;

/// <summary>
/// G4 backend: ánh xạ proxy đang cooldown → tập account hiển thị countdown badge trong Live Trace.
/// Pure logic, không giữ state — Task 13 gọi <see cref="ResolveAsync"/> định kỳ và tự giữ map cũ khi nhận <see langword="null"/>.
/// </summary>
public sealed class CooldownTracker(
    Func<IReadOnlyList<ProxyRuntimeStatus>> snapshot,
    Func<CancellationToken, Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>> reverseAssignments,
    Func<long, CancellationToken, Task<IReadOnlyList<ProxyAssignment>>> providerAssignments,
    Action<Exception>? onError = null)
{
    /// <summary>
    /// Trả map accountName → max(DownUntil); <c>empty</c> = authoritative "không còn cooldown" (clear badge);
    /// <see langword="null"/> = lỗi source (giữ map cũ ở caller) + đã gọi <c>onError</c>.
    /// </summary>
    /// <param name="ct">Token hủy cho các call DB.</param>
    /// <remarks>Không ném exception ra ngoài — mọi lỗi đều nuốt sau khi báo qua <c>onError</c>.</remarks>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> ResolveAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var down = new Dictionary<long, DateTimeOffset>();
            foreach (var proxy in snapshot())
            {
                if (proxy.IsDown && proxy.DownUntil is { } until && until > now)
                {
                    down[proxy.Id] = until;
                }
            }

            // Rỗng → return empty dict NGAY, không đụng DB:
            // cooldown hết hạn là authoritative clear dù pool chưa flip IsDown.
            if (down.Count == 0)
            {
                return new Dictionary<string, DateTimeOffset>();
            }

            var reverse = await reverseAssignments(ct).ConfigureAwait(false);
            var result = new Dictionary<string, DateTimeOffset>();
            foreach (var (proxyId, usages) in reverse)
            {
                if (!down.TryGetValue(proxyId, out var downUntil))
                {
                    continue;
                }

                // Dedup provider trong cùng 1 proxy — tránh gọi providerAssignments nhiều lần
                // khi provider xuất hiện nhiều usage row cho cùng proxy.
                var visitedProviders = new HashSet<long>();
                foreach (var usage in usages)
                {
                    if (!usage.IsProvider)
                    {
                        // Account gắn trực tiếp proxy down → badge luôn hiển thị.
                        Merge(result, usage.ScopeName, downUntil);
                        continue;
                    }

                    if (!visitedProviders.Add(usage.ScopeId))
                    {
                        continue;
                    }

                    // Provider dùng proxy down → kế thừa D1: account không có proxy
                    // riêng (ProxyIds rỗng) cũng dùng proxy đó → hiện badge.
                    var assignments = await providerAssignments(usage.ScopeId, ct).ConfigureAwait(false);
                    foreach (var assignment in assignments)
                    {
                        if (!assignment.IsProvider && assignment.ProxyIds.Count == 0)
                        {
                            Merge(result, assignment.Name, downUntil);
                        }
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            // null = caller giữ map cũ (spec §3.5); onError gọi đúng 1 lần cho mọi lỗi nguồn.
            onError?.Invoke(ex);
            return null;
        }
    }

    /// <summary>Ghi <paramref name="downUntil"/> vào map, giữ giá trị xa nhất khi key đã tồn tại.</summary>
    private static void Merge(
        Dictionary<string, DateTimeOffset> map, string accountName, DateTimeOffset downUntil)
    {
        if (!map.TryGetValue(accountName, out var existing) || downUntil > existing)
        {
            map[accountName] = downUntil;
        }
    }
}

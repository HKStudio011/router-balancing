namespace RouterBalancing.Core.Platform;

/// <summary>Nền tảng không phải Windows không có mục Run — no-op (Phase 1 Windows-first).</summary>
public sealed class NullStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;

    public void SetEnabled(bool enabled)
    {
    }
}

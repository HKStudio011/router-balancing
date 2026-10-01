namespace RouterBalancing.Core.Providers;

/// <summary>
/// Lỗi sync model free — service/UI phân biệt được với lỗi hệ thống:
/// UI hiện message thân thiện, periodic log warning và bỏ qua provider đó (spec provider-free §9).
/// </summary>
public sealed class FreeModelSyncException : Exception
{
    public FreeModelSyncException(string message)
        : base(message)
    {
    }

    public FreeModelSyncException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

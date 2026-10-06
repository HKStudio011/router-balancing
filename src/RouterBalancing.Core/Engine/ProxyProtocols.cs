namespace RouterBalancing.Core.Engine;

/// <summary>
/// Registry protocol theo <see cref="ProxyEndpoint"/> — singleton stateless, handler/dispatcher
/// resolve qua <see cref="For"/> (spec v1-responses §3.1).
/// </summary>
public static class ProxyProtocols
{
    /// <summary>Protocol chat completions — hành vi cũ không đổi.</summary>
    public static IProxyProtocol Chat { get; } = new ChatCompletionsProtocol();

    /// <summary>Protocol responses — validate/prepare theo spec §4.1/§4.2; tee ở Task 3.</summary>
    public static IProxyProtocol Responses { get; } = new ResponsesProtocol();

    /// <summary>Protocol cho endpoint — switch exhaustive trên <see cref="ProxyEndpoint"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Endpoint không có protocol.</exception>
    public static IProxyProtocol For(ProxyEndpoint endpoint) => endpoint switch
    {
        ProxyEndpoint.Chat => Chat,
        ProxyEndpoint.Responses => Responses,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint,
            "Endpoint chưa có protocol đăng ký."),
    };
}

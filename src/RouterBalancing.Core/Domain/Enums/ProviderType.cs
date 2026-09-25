namespace RouterBalancing.Core.Domain;

/// <summary>Chuẩn API của provider — quyết định lớp dịch thuật.</summary>
public enum ProviderType
{
    /// <summary>OpenAI-compatible: passthrough /v1/chat/completions.</summary>
    OpenAI = 0,

    /// <summary>Anthropic Messages API — cần dịch 2 chiều.</summary>
    Anthropic = 1,
}

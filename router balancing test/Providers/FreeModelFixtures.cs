namespace router_balancing_test.Providers;

/// <summary>
/// JSON fixture rút gọn từ probe thật 2026-10-01 (spec provider-free §4) — shape giữ nguyên,
/// chỉ cắt bớt model. Dùng chung cho detector + sync tests.
/// </summary>
internal static class FreeModelFixtures
{
    /// <summary>OpenRouter: zero-pricing, ":free" (kể cả pricing > 0), và model trả phí.</summary>
    public const string OpenRouter = """
    {
      "data": [
        { "id": "inclusionai/ling-3.0-flash-sante:free", "name": "Ling 3.0 Flash Sante",
          "pricing": { "prompt": "0", "completion": "0" }, "context_length": 262144 },
        { "id": "stealth/space-bunny-alpha", "name": "Space Bunny Alpha",
          "pricing": { "prompt": "0.0", "completion": "0.0" }, "context_length": 131072 },
        { "id": "vendor/mispriced:free", "name": "Mispriced",
          "pricing": { "prompt": "0.1", "completion": "0.1" } },
        { "id": "openai/gpt-4o", "name": "GPT-4o",
          "pricing": { "prompt": "0.0000025", "completion": "0.00001" }, "context_length": 128000 }
      ]
    }
    """;

    /// <summary>OpenRouter toàn model trả phí — detector lọc rỗng → service guard không xoá gì.</summary>
    public const string OpenRouterAllPaid = """
    {
      "data": [
        { "id": "openai/gpt-4o", "name": "GPT-4o",
          "pricing": { "prompt": "0.0000025", "completion": "0.00001" }, "context_length": 128000 },
        { "id": "anthropic/claude-sonnet", "name": "Claude Sonnet",
          "pricing": { "prompt": "0.003", "completion": "0.015" }, "context_length": 200000 }
      ]
    }
    """;

    /// <summary>OpenCode Zen: pricing null, free = id hậu tố "-free".</summary>
    public const string OpenCode = """
    {
      "object": "list",
      "data": [
        { "id": "deepseek-v4-flash-free", "name": "DeepSeek V4 Flash Free", "context_length": 131072 },
        { "id": "mimo-v2.5-free", "name": "MiMo V2.5 Free" },
        { "id": "gpt-5.5", "name": "GPT 5.5", "context_length": 400000 }
      ]
    }
    """;

    /// <summary>NVIDIA NIM: không pricing — AllFree, name/context có thể vắng.</summary>
    public const string Nvidia = """
    {
      "object": "list",
      "data": [
        { "id": "meta/llama-3.1-8b-instruct" },
        { "id": "mistralai/mixtral-8x22b-instruct-v0.1", "name": "Mixtral 8x22B" }
      ]
    }
    """;
}

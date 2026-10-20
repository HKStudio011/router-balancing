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

    /// <summary>
    /// OpenRouter kèm capability ngay trong list, theo shape probe API THẬT 2026-10-20 (Task 5,
    /// brief đã sửa theo ruling R-T5-3/4/5): (1) multimodal + file input + tools + reasoning object
    /// kèm supported_efforts; (2) chỉ text, supported_parameters rỗng, KHÔNG có key reasoning
    /// → full-list khẳng định "không" (KnownAbsent); (3) reasoning object với supported_efforts
    /// rỗng và supported_parameters không chứa "reasoning" → Reasoning=true, không effort nào.
    /// </summary>
    public const string OpenRouterWithCapabilities = """
    {
      "data": [
        { "id": "vendor/capable-model:free", "name": "Capable Model", "created": 1795000000,
          "description": "Multimodal reasoner with tools",
          "architecture": { "modality": "text+image+audio+file->text", "tokenizer": "Other",
            "input_modalities": ["text", "image", "audio", "file"], "output_modalities": ["text"] },
          "supported_parameters": ["max_tokens", "reasoning", "response_format", "tools"],
          "reasoning": { "mandatory": false, "supported_efforts": ["low", "high"], "default_effort": "low" },
          "pricing": { "prompt": "0", "completion": "0" },
          "top_provider": { "context_length": 262144, "is_moderated": false },
          "context_length": 262144 },
        { "id": "vendor/plain-model:free", "name": "Plain Model", "created": 1794000000,
          "architecture": { "modality": "text->text", "tokenizer": "Other",
            "input_modalities": ["text"], "output_modalities": ["text"] },
          "supported_parameters": [],
          "pricing": { "prompt": "0", "completion": "0" },
          "context_length": 131072 },
        { "id": "vendor/silent-reasoner:free", "name": "Silent Reasoner", "created": 1793000000,
          "architecture": { "modality": "text->text", "tokenizer": "Other",
            "input_modalities": ["text"], "output_modalities": ["text"] },
          "supported_parameters": ["temperature", "tools"],
          "reasoning": { "supported_efforts": [] },
          "pricing": { "prompt": "0", "completion": "0" },
          "context_length": 65536 }
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

    /// <summary>Catalog models.opencode.ai — shape thật 2026-10-19 (rút gọn): free / half-free / paid / deprecated / toggle.</summary>
    public const string OpenCodeCatalog = """
    {
      "opencode": {
        "models": {
          "big-pickle": { "name": "Big Pickle", "attachment": false, "tool_call": true, "reasoning": true,
            "reasoning_options": [], "modalities": { "input": ["text"], "output": ["text"] },
            "cost": { "input": 0, "output": 0, "cache_read": 0 }, "limit": { "context": 200000, "output": 32000 } },
          "deepseek-v4.1-flash-free": { "name": "DeepSeek V4.1 Flash Free", "attachment": true, "tool_call": true, "reasoning": true,
            "reasoning_options": [ { "type": "effort", "values": ["low", "high", "max"] } ],
            "modalities": { "input": ["text", "image"], "output": ["text"] },
            "cost": { "input": 0, "output": 0 }, "limit": { "context": 200000, "output": 32000 } },
          "toggle-model": { "tool_call": true, "reasoning": true,
            "reasoning_options": [ { "type": "toggle" } ],
            "cost": { "input": 0, "output": 0 } },
          "dead-model-free": { "cost": { "input": 0, "output": 0 }, "status": "deprecated" },
          "paid-model": { "cost": { "input": 3, "output": 15 } },
          "half-free": { "cost": { "input": 0, "output": 2 } }
        }
      }
    }
    """;

    /// <summary>Catalog toàn model trả phí — detector lọc rỗng → guard không xoá list.</summary>
    public const string OpenCodeCatalogAllPaid = """
    { "opencode": { "models": { "paid-a": { "cost": { "input": 3, "output": 15 } } } } }
    """;

    /// <summary>2 entry THẬT từ snapshot (giữ field lạ: description/family/interleaved/provider/canonical_model_id/limit.input…)
    /// + 1 model thật deprecated — chứng minh parser bỏ qua field lạ và map đúng shape sống.</summary>
    public const string OpenCodeCatalogRealSlice = """
    {
      "opencode": {
        "models": {
          "big-pickle": { "id": "big-pickle", "name": "Big Pickle", "description": "Reasoning model for deliberate analysis",
            "family": "big-pickle", "attachment": false, "reasoning": true, "reasoning_options": [], "tool_call": true,
            "interleaved": { "field": "reasoning_content" }, "structured_output": true, "temperature": true,
            "knowledge": "2025-01", "release_date": "2025-10-17", "last_updated": "2025-10-17",
            "modalities": { "input": ["text"], "output": ["text"] }, "open_weights": false,
            "limit": { "context": 200000, "input": 160000, "output": 32000 },
            "provider": { "api": "https://opencode.ai/inference/openai/v1" },
            "cost": { "input": 0, "output": 0, "cache_read": 0, "cache_write": 0 } },
          "nemotron-3-ultra-free": { "id": "nemotron-3-ultra-free", "name": "Nemotron 3 Ultra Free", "family": "nemotron-free",
            "attachment": false, "reasoning": true, "reasoning_options": [], "tool_call": true, "temperature": true,
            "knowledge": "2026-02", "modalities": { "input": ["text"], "output": ["text"] }, "open_weights": true,
            "limit": { "context": 1000000, "output": 128000 },
            "provider": { "api": "https://opencode.ai/inference/openai/v1" },
            "cost": { "input": 0, "output": 0, "cache_read": 0 },
            "canonical_model_id": "nvidia/nemotron-3-ultra-550b-a55b" },
          "muse-spark-1.3-contributor-free": { "id": "muse-spark-1.3-contributor-free", "name": "Muse Spark 1.3 Free",
            "attachment": true, "reasoning": true, "reasoning_options": [ { "type": "effort", "values": ["minimal", "low", "medium", "high", "xhigh"] } ],
            "tool_call": true, "modalities": { "input": ["text", "image", "video", "pdf", "audio"], "output": ["text"] },
            "limit": { "context": 1048576, "output": 131072 }, "status": "deprecated",
            "provider": { "npm": "@ai-sdk/openai", "api": "https://opencode.ai/inference/openai/v1" },
            "cost": { "input": 0, "output": 0, "cache_read": 0 } }
        }
      }
    }
    """;
}

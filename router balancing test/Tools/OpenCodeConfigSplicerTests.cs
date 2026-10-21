using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test splicer thuần (không I/O): định vị block bằng token rồi splice text — phần ngoài block
/// phải giữ nguyên byte, kể cả comment/non-ASCII (offset byte ≠ offset char).
/// </summary>
public class OpenCodeConfigSplicerTests
{
    private static JsonObject Block(string name = "Router Balancing") =>
        JsonNode.Parse($$"""
        {
            "name": "{{name}}",
            "package": "@opencode/ai/providers/openai-compatible",
            "settings": { "baseURL": "http://127.0.0.1:8317/v1", "timeout": false },
            "models": {}
        }
        """)!.AsObject();

    private static JsonDocument Parse(string text) => JsonDocument.Parse(text);

    private static JsonDocument ParseJsonc(string text) => JsonDocument.Parse(text, new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    [Fact]
    public void Upsert_ExistingOldBlocks_RemovesThemAndInsertsIntoProviders_PreservingOtherText()
    {
        const string input = """
        {
            "$schema": "https://opencode.ai/config.json",
            "plugin": ["oh-my-opencode"],
            "mcp": {
                "some-mcp": {
                    "type": "local",
                    "command": ["node", "server.js"]
                }
            },
            "agent": {
                "builder": {
                    "model": "anthropic/claude"
                }
            },
            "provider": {
                "other": {
                    "name": "Other"
                },
                "router-balancing": {
                    "name": "Old shape",
                    "models": {}
                }
            },
            "providers": {
                "other": {
                    "name": "Other v2"
                },
                "router-balancing": {
                    "name": "Old shape v2",
                    "models": {}
                }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        // Phần khác còn nguyên văn (substring exact — không normalize)
        Assert.Contains("\"$schema\": \"https://opencode.ai/config.json\",", result);
        Assert.Contains("\"plugin\": [\"oh-my-opencode\"],", result);
        Assert.Contains("\"command\": [\"node\", \"server.js\"]", result);
        Assert.Contains("\"model\": \"anthropic/claude\"", result);
        Assert.Contains("\"name\": \"Other\"", result);
        Assert.Contains("\"name\": \"Other v2\"", result);
        Assert.Contains("\n            \"name\": \"Router Balancing\",", result); // indent 4 → field mới 12 space
        Assert.EndsWith("}", result); // cuối file không tự thêm/bớt newline

        using var doc = Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("provider").TryGetProperty("router-balancing", out _));
        Assert.True(root.GetProperty("provider").TryGetProperty("other", out _));
        var provider = root.GetProperty("providers").GetProperty("router-balancing");
        Assert.Equal("Router Balancing", provider.GetProperty("name").GetString());
        Assert.False(provider.GetProperty("settings").GetProperty("timeout").GetBoolean());
        Assert.Equal("Other v2", root.GetProperty("providers").GetProperty("other").GetProperty("name").GetString());
    }

    [Fact]
    public void Upsert_MissingProviders_CreatesProvidersObject()
    {
        const string input = """
        {
            "plugin": ["x"],
            "provider": {
                "other": {}
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        Assert.Contains("\"plugin\": [\"x\"],", result);
        using var doc = Parse(result);
        var root = doc.RootElement;
        Assert.Equal("Router Balancing",
            root.GetProperty("providers").GetProperty("router-balancing").GetProperty("name").GetString());
        Assert.True(root.GetProperty("provider").TryGetProperty("other", out _));
    }

    [Fact]
    public void Upsert_EmptyRootObject_CreatesProviders()
    {
        var result = OpenCodeConfigSplicer.Upsert("{}", Block());

        using var doc = Parse(result);
        Assert.Equal("Router Balancing",
            doc.RootElement.GetProperty("providers").GetProperty("router-balancing").GetProperty("name").GetString());
    }

    [Fact]
    public void Upsert_ProvidersWithoutOtherMembers_NoDanglingComma()
    {
        const string input = """
        {
            "providers": {
                "router-balancing": {
                    "name": "Old"
                }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        using var doc = Parse(result);
        var providers = doc.RootElement.GetProperty("providers");
        Assert.Equal("Router Balancing", providers.GetProperty("router-balancing").GetProperty("name").GetString());
        Assert.Single(providers.EnumerateObject());
    }

    [Fact]
    public void Upsert_ProvidersWithOtherMember_KeepsOtherMember()
    {
        const string input = """
        {
            "providers": {
                "other": { "name": "Keep me" },
                "router-balancing": { "name": "Old" }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        using var doc = Parse(result);
        var providers = doc.RootElement.GetProperty("providers");
        Assert.Equal("Keep me", providers.GetProperty("other").GetProperty("name").GetString());
        Assert.Equal(2, providers.EnumerateObject().Count());
    }

    [Fact]
    public void Upsert_OldBlockFirstMember_LeavesValidComma()
    {
        const string input = """
        {
            "providers": {
                "router-balancing": { "name": "Old" },
                "other": { "name": "Keep me" }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        using var doc = Parse(result);
        var providers = doc.RootElement.GetProperty("providers");
        Assert.Equal("Keep me", providers.GetProperty("other").GetProperty("name").GetString());
        Assert.Equal("Router Balancing", providers.GetProperty("router-balancing").GetProperty("name").GetString());
    }

    [Fact]
    public void Upsert_OldBlockLastMember_LeavesValidComma()
    {
        const string input = """
        {
            "providers": {
                "first": {},
                "router-balancing": { "name": "Old" }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        using var doc = Parse(result);
        var providers = doc.RootElement.GetProperty("providers");
        Assert.True(providers.TryGetProperty("first", out _));
        Assert.Equal("Router Balancing", providers.GetProperty("router-balancing").GetProperty("name").GetString());
    }

    [Fact]
    public void Upsert_OldBlockOnlyMember_LeavesValidBraces()
    {
        const string input = """
        {
            "providers": {
                "router-balancing": {
                    "name": "Old"
                }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        using var doc = Parse(result);
        Assert.Single(doc.RootElement.GetProperty("providers").EnumerateObject());
    }

    [Fact]
    public void Upsert_JSONCWithComments_PreservesCommentsOutsideBlock()
    {
        // Comment chứa ký tự non-ASCII: offset byte của Utf8JsonReader ≠ offset char của string —
        // splice phải convert đúng, nếu không phần comment/tiếng Việt sẽ bị cắt lệch.
        const string input = """
        {
            // giữ comment này
            "plugin": ["x"],
            "providers": {
                // comment trong providers
                "other": {},
                "router-balancing": { "name": "Old" }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        Assert.Contains("// giữ comment này", result);
        Assert.Contains("// comment trong providers", result);
        using var doc = ParseJsonc(result);
        Assert.True(doc.RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("settings").TryGetProperty("timeout", out _));
    }

    [Fact]
    public void Upsert_DuplicateRouterBalancingBlocks_LeavesExactlyOne()
    {
        // RF-4 + review fix: run block trùng nằm CUỐI object từng để lại dấu phẩy thừa;
        // parse STRICT (không AllowTrailingCommas) mới bắt được — guard của splicer phải chịu được.
        const string input = """
        {
            "provider": {
                "router-balancing": { "name": "Old provider" }
            },
            "providers": {
                "other": {},
                "router-balancing": { "name": "Old one" },
                "router-balancing": { "name": "Old two" }
            }
        }
        """;

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        Assert.Single(Regex.Matches(result, Regex.Escape("\"router-balancing\"")).Cast<Match>());
        using var doc = Parse(result);
        Assert.Equal("Router Balancing",
            doc.RootElement.GetProperty("providers").GetProperty("router-balancing").GetProperty("name").GetString());
    }

    [Fact]
    public void Upsert_InvalidJson_ThrowsFormat()
    {
        Assert.Throws<OpenCodeConfigFormatException>(() =>
            OpenCodeConfigSplicer.Upsert("{ \"providers\": { \"a\": } }", Block()));
    }

    [Fact]
    public void Upsert_RootNotObject_ThrowsFormat()
    {
        Assert.Throws<OpenCodeConfigFormatException>(() => OpenCodeConfigSplicer.Upsert("[1, 2]", Block()));
    }

    [Fact]
    public void Upsert_ProvidersNotObject_ThrowsFormat()
    {
        Assert.Throws<OpenCodeConfigFormatException>(() =>
            OpenCodeConfigSplicer.Upsert("{ \"providers\": 5 }", Block()));
    }

    [Fact]
    public void Upsert_TrailingContent_ThrowsFormat()
    {
        Assert.Throws<OpenCodeConfigFormatException>(() => OpenCodeConfigSplicer.Upsert("{} {}", Block()));
    }

    [Fact]
    public void Upsert_UsesFileIndentAndCrlf()
    {
        var input = string.Join("\r\n", new[]
        {
            "{",
            "    \"providers\": {",
            "        \"router-balancing\": {",
            "            \"name\": \"Old\"",
            "        }",
            "    }",
            "}",
            "",
        });

        var result = OpenCodeConfigSplicer.Upsert(input, Block());

        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Contains("\r\n            \"name\": \"Router Balancing\",", result); // indent 4 → field mới 12 space
        using var doc = Parse(result);
        Assert.Equal("Router Balancing",
            doc.RootElement.GetProperty("providers").GetProperty("router-balancing").GetProperty("name").GetString());
    }
}

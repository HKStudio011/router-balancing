using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test writer I/O: backup .bak, ghi tạm rồi đổi tên, giữ BOM/CRLF, lỗi thì file gốc không bị đụng.
/// </summary>
public sealed class OpenCodeConfigFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rb-config-writer-" + Guid.NewGuid().ToString("N"));

    private readonly OpenCodeConfigFileWriter _writer = new();

    public OpenCodeConfigFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                // File .bak copy từ file read-only giữ attribute read-only — phải clear trước khi xoá.
                foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Thư mục tạm không dọn được — best-effort, không che test failure.
        }
    }

    private string FileIn(string name) => Path.Combine(_dir, name);

    private static JsonObject Block() =>
        JsonNode.Parse("""
        {
            "name": "Router Balancing",
            "package": "@opencode/ai/providers/openai-compatible",
            "settings": { "baseURL": "http://127.0.0.1:8317/v1", "timeout": false },
            "models": {}
        }
        """)!.AsObject();

    private static string ProviderName(string text) =>
        JsonDocument.Parse(text).RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("name").GetString()!;

    [Fact]
    public async Task WriteAsync_NewFile_CreatesDirectoryAndFile_NoBackup()
    {
        var path = FileIn(Path.Combine("nested", "opencode.json"));

        var result = await _writer.WriteAsync(path, Block());

        Assert.True(File.Exists(path));
        Assert.Null(result.BackupPath);
        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal("Router Balancing", ProviderName(await File.ReadAllTextAsync(path)));
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_CreatesBackupWithOriginalBytes()
    {
        var path = FileIn("opencode.json");
        const string original = "{ \"plugin\": [\"oh-my-opencode\"] }";
        await File.WriteAllTextAsync(path, original);

        var result = await _writer.WriteAsync(path, Block());

        Assert.Equal(path + ".bak", result.BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(result.BackupPath!));
        var written = await File.ReadAllTextAsync(path);
        Assert.Contains("\"plugin\": [\"oh-my-opencode\"]", written);
        Assert.Equal("Router Balancing", ProviderName(written));
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_ReplacesBlock_PreservesBomAndCrlf()
    {
        var path = FileIn("opencode.json");
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
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        await File.WriteAllBytesAsync(path, [.. bom, .. Encoding.UTF8.GetBytes(input)]);

        await _writer.WriteAsync(path, Block());

        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(bom, written[..3]);
        var text = Encoding.UTF8.GetString(written, 3, written.Length - 3);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal("Router Balancing", ProviderName(text));
    }

    [Fact]
    public async Task WriteAsync_InvalidJson_ThrowsAndLeavesFileUntouched_NoBackup()
    {
        var path = FileIn("broken.json");
        const string original = "{ \"providers\": { \"a\": } }";
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAsync<OpenCodeConfigFormatException>(() => _writer.WriteAsync(path, Block()));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task WriteAsync_ReadOnlyTarget_ThrowsAndLeavesOriginalUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL/attribute khác nhau giữa OS — hành vi Windows là môi trường ship của app
        }

        var path = FileIn("readonly.json");
        const string original = "{ \"plugin\": [] }";
        await File.WriteAllTextAsync(path, original);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => _writer.WriteAsync(path, Block()));

            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*")); // temp phải được dọn
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task WriteAsync_UnrelatedTextPreserved()
    {
        var path = FileIn("opencode.json");
        const string original = """
        {
            "mcp": {
                "some-mcp": {
                    "type": "local"
                }
            },
            "providers": {
                "router-balancing": { "name": "Old" }
            }
        }
        """;
        await File.WriteAllTextAsync(path, original);

        await _writer.WriteAsync(path, Block());

        var written = await File.ReadAllTextAsync(path);
        Assert.Contains("\"mcp\": {\n        \"some-mcp\": {\n            \"type\": \"local\"\n        }\n    },", written);
        Assert.Equal("Router Balancing", ProviderName(written));
    }
}

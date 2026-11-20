using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Dataset;

public class DatasetPayloadManifestFactoryTests
{
    [Fact]
    public void BuildManifest_ContainsSourceDocumentsAndReferences()
    {
        const string exportId = "0123456789abcdef0123456789abcdef";
        var finishedAt = new DateTimeOffset(2026, 11, 17, 10, 12, 3, 123, TimeSpan.FromHours(2));
        var capture = new DatasetCapture(
            RequestId: "request-v2",
            Endpoint: ProxyEndpoint.Chat,
            FinishedAt: finishedAt,
            ClientBody: Encoding.UTF8.GetBytes("""{"model":"gpt-vision","messages":[{"role":"user","content":"look"}]}"""),
            CompletionJson: """{"id":"completion-1","choices":[]}""",
            CompletionComplete: true,
            Provider: "openrouter",
            Account: "acct-1",
            ModelUpstream: "gpt-vision-upstream",
            ModelRequested: "gpt-vision",
            Mode: "balancer",
            ClientKeyId: 3,
            PromptTokens: 10,
            CompletionTokens: 20,
            Lane: DatasetLane.KeyedFingerprint,
            ToolsRewritten: true);
        var reference = new DatasetPayloadReference(
            Document: "request",
            JsonPointer: "/messages/0/content/1/image_url/url",
            Url: $"payload://{exportId}/image-000-deadbeef.png",
            Path: $"payloads/2026-11-17/{exportId}/image-000-deadbeef.png",
            Kind: "base64",
            MediaType: "image/png",
            Bytes: 1_048_576,
            Sha256: "deadbeef");
        var transformed = new DatasetPayloadTransformResult(
            new JsonObject { ["messages"] = new JsonArray() },
            new JsonObject { ["id"] = "completion-1" },
            [new DatasetPayloadFile(reference.Path, [1, 2, 3])],
            [reference]);

        var manifest = (JsonObject)JsonNode.Parse(
            DatasetPayloadManifestFactory.Build(capture, transformed, exportId))!;

        Assert.Equal(2, manifest["v"]!.GetValue<int>());
        Assert.Equal("2026-11-17T08:12:03.123Z", manifest["ts"]!.GetValue<string>());
        Assert.Equal("request-v2", manifest["request_id"]!.GetValue<string>());
        Assert.Equal(exportId, manifest["export_id"]!.GetValue<string>());
        Assert.Equal("chat", manifest["endpoint"]!.GetValue<string>());
        Assert.Equal("gpt-vision", manifest["model_requested"]!.GetValue<string>());
        Assert.Equal("gpt-vision-upstream", manifest["model_upstream"]!.GetValue<string>());
        Assert.Equal("balancer", manifest["mode"]!.GetValue<string>());
        Assert.Equal("openrouter", manifest["provider"]!.GetValue<string>());
        Assert.Equal("acct-1", manifest["account"]!.GetValue<string>());
        Assert.Equal(3, manifest["client_key_id"]!.GetValue<long>());
        Assert.Equal("keyed_fingerprint", manifest["lane"]!.GetValue<string>());
        Assert.True(manifest["tools_rewritten"]!.GetValue<bool>());
        Assert.Equal(10, manifest["usage"]!["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(20, manifest["usage"]!["completion_tokens"]!.GetValue<int>());

        var documents = manifest["documents"]!;
        Assert.Equal($"payloads/2026-11-17/{exportId}/request.json",
            documents["request"]!.GetValue<string>());
        Assert.Equal($"payloads/2026-11-17/{exportId}/completion.json",
            documents["completion"]!.GetValue<string>());

        var payloadReference = Assert.Single(manifest["payload_refs"]!.AsArray())!;
        Assert.Equal("request", payloadReference["document"]!.GetValue<string>());
        Assert.Equal("/messages/0/content/1/image_url/url",
            payloadReference["json_pointer"]!.GetValue<string>());
        Assert.Equal(reference.Url, payloadReference["url"]!.GetValue<string>());
        Assert.Equal(reference.Path, payloadReference["path"]!.GetValue<string>());
        Assert.Equal("base64", payloadReference["kind"]!.GetValue<string>());
        Assert.Equal("image/png", payloadReference["media_type"]!.GetValue<string>());
        Assert.Equal(1_048_576, payloadReference["bytes"]!.GetValue<long>());
        Assert.Equal("deadbeef", payloadReference["sha256"]!.GetValue<string>());
        Assert.DoesNotContain('\\', documents["request"]!.GetValue<string>());
        Assert.DoesNotContain('\\', payloadReference["path"]!.GetValue<string>());
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Engine;

namespace RouterBalancing.Core.Dataset;

/// <summary>Dựng manifest v2 trỏ tới source documents và binary payloads của một export.</summary>
public static class DatasetPayloadManifestFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Tạo một dòng manifest v2 với route metadata của capture; mọi path tương đối lấy từ kết quả
    /// <see cref="DatasetPayloadTransformer"/> và cùng ngày UTC của <see cref="DatasetCapture.FinishedAt"/>.
    /// </summary>
    public static string Build(
        DatasetCapture capture,
        DatasetPayloadTransformResult transformed,
        string exportId)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(transformed);
        ArgumentNullException.ThrowIfNull(exportId);

        var datasetDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime);
        var manifest = new JsonObject
        {
            ["v"] = 2,
            ["ts"] = DatasetRecordBuilder.FormatTimestamp(capture.FinishedAt),
            ["request_id"] = capture.RequestId,
            ["export_id"] = exportId,
            ["endpoint"] = capture.Endpoint == ProxyEndpoint.Chat ? "chat" : "responses",
        };
        AddIfPresent(manifest, "model_requested", capture.ModelRequested);
        AddIfPresent(manifest, "model_upstream", capture.ModelUpstream);
        AddIfPresent(manifest, "mode", capture.Mode);
        AddIfPresent(manifest, "provider", capture.Provider);
        AddIfPresent(manifest, "account", capture.Account);
        if (capture.ClientKeyId is long keyId)
        {
            manifest["client_key_id"] = keyId;
        }
        manifest["lane"] = capture.Lane switch
        {
            DatasetLane.FreeFingerprint => "free_fingerprint",
            DatasetLane.KeyedFingerprint => "keyed_fingerprint",
            _ => "plain",
        };
        manifest["tools_rewritten"] = capture.ToolsRewritten;
        if (capture.PromptTokens is int || capture.CompletionTokens is int)
        {
            var usage = new JsonObject();
            if (capture.PromptTokens is int promptTokens)
            {
                usage["prompt_tokens"] = promptTokens;
            }
            if (capture.CompletionTokens is int completionTokens)
            {
                usage["completion_tokens"] = completionTokens;
            }
            manifest["usage"] = usage;
        }

        manifest["documents"] = new JsonObject
        {
            ["request"] = DatasetPaths.PayloadRelativePath(datasetDay, exportId, "request.json"),
            ["completion"] = DatasetPaths.PayloadRelativePath(datasetDay, exportId, "completion.json"),
        };
        var references = new JsonArray();
        foreach (var reference in transformed.References)
        {
            references.Add(new JsonObject
            {
                ["document"] = reference.Document,
                ["json_pointer"] = reference.JsonPointer,
                ["url"] = reference.Url,
                ["path"] = reference.Path,
                ["kind"] = reference.Kind,
                ["media_type"] = reference.MediaType,
                ["bytes"] = reference.Bytes,
                ["sha256"] = reference.Sha256,
            });
        }
        manifest["payload_refs"] = references;
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    private static void AddIfPresent(JsonObject manifest, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            manifest[key] = value;
        }
    }
}

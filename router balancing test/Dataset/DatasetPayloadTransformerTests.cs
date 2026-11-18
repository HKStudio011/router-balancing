using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Dataset;

public class DatasetPayloadTransformerTests
{
    private const string ExportId = "export-task-3";
    private const string DefaultCompletion = """{"id":"completion-1","choices":[]}""";
    private static readonly DateTimeOffset FinishedAt = new(2026, 11, 17, 10, 12, 3, TimeSpan.Zero);

    [Fact]
    public void Transform_SmallBase64_ReturnsNull()
    {
        var binary = new byte[DatasetRecordBuilder.InlineMaxFieldBytes / 4 * 3];
        var base64 = Convert.ToBase64String(binary);
        Assert.Equal(DatasetRecordBuilder.InlineMaxFieldBytes, base64.Length);
        var capture = Capture(ChatImageRequest($"data:image/png;base64,{base64}"));

        var result = DatasetPayloadTransformer.Transform(capture, ExportId);

        Assert.Null(result);
    }

    [Fact]
    public void Transform_InvalidBase64_LeavesInlineAndReturnsNull()
    {
        var invalidBase64 = "!" + new string('A', DatasetRecordBuilder.InlineMaxFieldBytes);
        var request = ChatImageRequest($"data:image/png;base64,{invalidBase64}");
        var capture = Capture(request);
        var originalBody = capture.ClientBody.ToArray();

        var result = DatasetPayloadTransformer.Transform(capture, ExportId);

        Assert.Null(result);
        Assert.Equal(originalBody, capture.ClientBody);
    }

    [Fact]
    public void Transform_LongOrdinaryText_ReturnsNull()
    {
        var longText = "ordinary text " + new string('x', DatasetRecordBuilder.InlineMaxFieldBytes);
        var request = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = longText,
            }),
        }.ToJsonString();

        var result = DatasetPayloadTransformer.Transform(Capture(request), ExportId);

        Assert.Null(result);
    }

    [Fact]
    public void Transform_DataUriImage_ExternalizesBinaryAndPointer()
    {
        var binary = LargePng();
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(binary)}";
        var capture = Capture(ChatImageRequest(dataUri));

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(capture, ExportId));

        var file = Assert.Single(result.Files);
        var reference = Assert.Single(result.References);
        Assert.Equal(binary.Length, file.Content.Length);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, file.Content[..4]);
        Assert.Equal("request", reference.Document);
        Assert.Equal("/messages/0/content/1/image_url/url", reference.JsonPointer);
        Assert.Equal("base64", reference.Kind);
        Assert.Equal("image/png", reference.MediaType);
        Assert.Equal(binary.Length, reference.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant(), reference.Sha256);

        var fileName = reference.Url.Split('/')[^1];
        Assert.Equal($"image-000-{reference.Sha256[..8]}.png", fileName);
        Assert.Equal($"payload://{ExportId}/{fileName}", reference.Url);
        var replacedUrl = result.RequestDocument["messages"]![0]!["content"]![1]!
            ["image_url"]!["url"]!.GetValue<string>();
        Assert.Equal($"payload://{ExportId}/{fileName}", replacedUrl);

        var expectedPath = DatasetPaths.PayloadRelativePath(
            DateOnly.FromDateTime(FinishedAt.UtcDateTime), ExportId, fileName);
        Assert.Equal(expectedPath, reference.Path);
        Assert.Equal(expectedPath, file.RelativePath);
    }

    [Fact]
    public void Transform_ResponsesInputImage_UsesRequestPointer()
    {
        var dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(LargeBinary(0x4A))}";
        var request = new JsonObject
        {
            ["model"] = "gpt-vision",
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = dataUri,
                }),
            }),
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(
                Capture(request, endpoint: ProxyEndpoint.Responses), ExportId));

        var reference = Assert.Single(result.References);
        Assert.Equal("request", reference.Document);
        Assert.Equal("/input/0/content/0/image_url", reference.JsonPointer);
        Assert.Equal("image/jpeg", reference.MediaType);
        Assert.Equal(reference.Url,
            result.RequestDocument["input"]![0]!["content"]![0]!["image_url"]!.GetValue<string>());
        Assert.EndsWith(".jpg", result.Files[0].RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Transform_B64JsonAndInputAudio_WritesExpectedMediaTypes()
    {
        var audio = LargeBinary(0x52);
        var image = LargePng();
        var request = new JsonObject
        {
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(audio),
                    ["format"] = "wav",
                },
            }),
        }.ToJsonString();
        var completion = new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject
            {
                ["b64_json"] = Convert.ToBase64String(image),
            }),
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(Capture(request, completion), ExportId));

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(2, result.References.Count);
        var audioReference = result.References[0];
        var imageReference = result.References[1];
        Assert.Equal("audio/wav", audioReference.MediaType);
        Assert.Equal("/input/0/input_audio/data", audioReference.JsonPointer);
        Assert.Equal($"audio-000-{audioReference.Sha256[..8]}.wav",
            result.Files[0].RelativePath.Split('/')[^1]);
        Assert.Equal("image/png", imageReference.MediaType);
        Assert.Equal("completion", imageReference.Document);
        Assert.Equal("/data/0/b64_json", imageReference.JsonPointer);
        Assert.Equal($"image-001-{imageReference.Sha256[..8]}.png",
            result.Files[1].RelativePath.Split('/')[^1]);
        Assert.Equal(audioReference.Url,
            result.RequestDocument["input"]![0]!["input_audio"]!["data"]!.GetValue<string>());
        Assert.Equal(imageReference.Url,
            result.CompletionDocument["data"]![0]!["b64_json"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("jpeg", "image/jpeg", ".jpg")]
    [InlineData("webp", "image/webp", ".webp")]
    public void Transform_B64JsonImageSignature_DetectsTypeAndRoundTrips(
        string signature, string expectedMediaType, string expectedExtension)
    {
        var binary = LargeBinary(0x20);
        if (signature == "jpeg")
        {
            new byte[] { 0xFF, 0xD8, 0xFF }.AsSpan().CopyTo(binary);
        }
        else
        {
            "RIFF"u8.ToArray().AsSpan().CopyTo(binary);
            "WEBP"u8.ToArray().AsSpan().CopyTo(binary.AsSpan(8));
        }

        var originalBase64 = Convert.ToBase64String(binary);
        var completion = new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["b64_json"] = originalBase64 }),
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(Capture(ChatImageRequest("inline"), completion), ExportId));

        var file = Assert.Single(result.Files);
        var reference = Assert.Single(result.References);
        Assert.Equal(expectedMediaType, reference.MediaType);
        Assert.Equal($"image-000-{reference.Sha256[..8]}{expectedExtension}",
            file.RelativePath.Split('/')[^1]);
        Assert.Equal(reference.Url,
            ResolvePointer(result.CompletionDocument, reference.JsonPointer)!.GetValue<string>());
        Assert.Equal(originalBase64, Convert.ToBase64String(file.Content));
    }

    [Fact]
    public void Transform_B64JsonUnknownSignature_UsesSafeBinaryFallback()
    {
        var originalBase64 = Convert.ToBase64String(LargeBinary(0x20));
        var completion = new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["b64_json"] = originalBase64 }),
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(Capture(ChatImageRequest("inline"), completion), ExportId));

        var file = Assert.Single(result.Files);
        var reference = Assert.Single(result.References);
        Assert.Equal("application/octet-stream", reference.MediaType);
        Assert.Equal($"file-000-{reference.Sha256[..8]}.bin", file.RelativePath.Split('/')[^1]);
        Assert.Equal(originalBase64, Convert.ToBase64String(file.Content));
    }

    [Theory]
    [InlineData("image/svg+xml;charset=utf-8", ".svg")]
    [InlineData("text/plain;charset=utf-8", ".txt")]
    public void Transform_ParameterizedDataUri_PreservesMediaTypeAndRoundTrips(
        string mediaType, string expectedExtension)
    {
        var dataUri = $"data:{mediaType};base64,{Convert.ToBase64String(LargeBinary(0x41))}";
        var capture = Capture(ChatImageRequest(dataUri));

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(capture, ExportId));

        var file = Assert.Single(result.Files);
        var reference = Assert.Single(result.References);
        Assert.Equal(mediaType, reference.MediaType);
        Assert.EndsWith(expectedExtension, file.RelativePath, StringComparison.Ordinal);
        Assert.Equal(reference.Url,
            ResolvePointer(result.RequestDocument, reference.JsonPointer)!.GetValue<string>());
        var reconstructed = $"data:{reference.MediaType};base64,{Convert.ToBase64String(file.Content)}";
        Assert.Equal(dataUri, reconstructed);
    }

    [Fact]
    public void Transform_PointerEscapesTildeAndSlash()
    {
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(LargePng())}";
        var request = new JsonObject
        {
            ["odd~key/part"] = dataUri,
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(Capture(request), ExportId));

        var reference = Assert.Single(result.References);
        Assert.Equal("/odd~0key~1part", reference.JsonPointer);
        Assert.Equal(reference.Url, result.RequestDocument["odd~key/part"]!.GetValue<string>());
    }

    [Fact]
    public void Transform_CompletionDataUri_UsesCompletionDocument()
    {
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(LargePng())}";
        var completion = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["image_url"] = new JsonObject { ["url"] = dataUri },
                    }),
                },
            }),
        }.ToJsonString();

        var result = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(Capture(ChatImageRequest("inline"), completion), ExportId));

        var reference = Assert.Single(result.References);
        Assert.Equal("completion", reference.Document);
        Assert.Equal("/choices/0/message/images/0/image_url/url", reference.JsonPointer);
        Assert.Equal(reference.Url, result.CompletionDocument["choices"]![0]!["message"]!
            ["images"]![0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("inline", result.RequestDocument["messages"]![0]!["content"]![1]!
            ["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Transform_SameRequestIdDifferentExportId_DoesNotCollide()
    {
        var capture = Capture(ChatImageRequest(
            $"data:image/png;base64,{Convert.ToBase64String(LargePng())}"), requestId: "same-request");

        var firstResult = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(capture, "export-first"));
        var secondResult = Assert.IsType<DatasetPayloadTransformResult>(
            DatasetPayloadTransformer.Transform(capture, "export-second"));

        Assert.NotEqual(firstResult.References[0].Path, secondResult.References[0].Path);
        Assert.Contains("/export-first/", firstResult.References[0].Path, StringComparison.Ordinal);
        Assert.Contains("/export-second/", secondResult.References[0].Path, StringComparison.Ordinal);
        Assert.NotEqual(firstResult.References[0].Url, secondResult.References[0].Url);
    }

    private static byte[] LargePng()
    {
        var binary = LargeBinary(0x20);
        new byte[] { 0x89, 0x50, 0x4E, 0x47 }.AsSpan().CopyTo(binary);
        return binary;
    }

    private static byte[] LargeBinary(byte fill)
    {
        var binary = new byte[DatasetRecordBuilder.InlineMaxFieldBytes];
        binary.AsSpan().Fill(fill);
        return binary;
    }

    private static string ChatImageRequest(string url) => new JsonObject
    {
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "look" },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url },
                }),
        }),
    }.ToJsonString();

    private static JsonNode? ResolvePointer(JsonNode document, string pointer)
    {
        JsonNode? current = document;
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var token = segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj[token],
                JsonArray array => array[int.Parse(token)],
                _ => throw new InvalidOperationException($"Pointer không resolve được tại '{token}'."),
            };
        }

        return current;
    }

    private static DatasetCapture Capture(
        string requestJson,
        string completionJson = DefaultCompletion,
        ProxyEndpoint endpoint = ProxyEndpoint.Chat,
        string requestId = "request-task-3") => new(
            RequestId: requestId,
            Endpoint: endpoint,
            FinishedAt: FinishedAt,
            ClientBody: Encoding.UTF8.GetBytes(requestJson),
            CompletionJson: completionJson,
            CompletionComplete: true,
            Provider: "openrouter",
            Account: "acct-1",
            ModelUpstream: "gpt-vision",
            ModelRequested: "gpt-vision",
            Mode: "balancer",
            ClientKeyId: 3,
            PromptTokens: 10,
            CompletionTokens: 20,
            Lane: DatasetLane.Plain,
            ToolsRewritten: false);
}

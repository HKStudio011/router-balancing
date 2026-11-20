# Dataset Large Record & Base64 Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Export complete text records up to 16 MiB/field and externalize base64 payloads larger than 1 MiB without moving any currently exported v1 record or increasing queue memory.

**Architecture:** Keep the existing request-time bounded channel and single background writer. `DatasetRecordBuilder` classifies normal/large v1 records; `DatasetPayloadTransformer` converts large base64 values into binary files plus source-shaped request/completion documents; `DatasetSink` writes normal, large and payload JSONL artifacts with manifest-last ordering and unified retention.

**Tech Stack:** .NET 10, C# nullable enabled, `System.Text.Json.Nodes`, `System.Threading.Channels`, xUnit v3, .NET MAUI Blazor Hybrid.

**Spec:** `docs/superpowers/specs/2026-11-18-dataset-large-record-export-design.md`

## Global Constraints

- `InlineMaxFieldBytes = 1 * 1024 * 1024`; `MaxExportFieldBytes = 16 * 1024 * 1024`.
- Records currently exported remain in `requests-YYYY-MM-DD.jsonl` with `v:1`; base64 ≤1 MiB stays inline.
- A field >16 MiB is `TooLarge`; no raw overflow, `CaptureAsync`, extra backpressure or spool task in this version.
- Queue remains 128 records / 32 MiB pending bytes; `Capture` does no JSON parsing or IO.
- Writer is sequential. `datasetDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime)` for every artifact of a record.
- Each v2 record uses `exportId = Guid.NewGuid().ToString("N")`; `request_id` remains only a correlation id.
- Write source documents and binaries with temp files + atomic rename; write the v2 manifest last.
- Retention covers only the three documented JSONL patterns and matching `payloads/YYYY-MM-DD` directories; `0` means keep forever.
- Manifest paths use `/` on every OS; filesystem paths are resolved from dataset root only by the sink.
- Do not include unrelated metadata-provider edits or the pre-existing untracked design document in task commits.

## Review Focus

1. **UTF-8 and exact boundaries:** 1 MiB and 16 MiB ±1 byte must classify correctly even with multi-byte text — Task 1 tests `Build_BodyAtInlineThreshold_KeepsNormalClassification`, `Build_BodyOverInlineThreshold_MarksLargeWithoutSkipping`, `Build_BodyOverExportThreshold_SkipsTooLarge`, `Build_CompletionUtf8OverInlineThreshold_MarksLarge`.
2. **Existing small-base64 records:** values ≤1 MiB, malformed base64 and long ordinary text must not move to v2 — Task 3 tests `Transform_SmallBase64_ReturnsNull`, `Transform_InvalidBase64_LeavesInlineAndReturnsNull`, `Transform_LongOrdinaryText_ReturnsNull`.
3. **Pointer/source correctness:** RFC 6901 escaping and Responses source shapes must resolve in the selected source document — Task 3 tests `Transform_PointerEscapesTildeAndSlash`, `Transform_ResponsesInputImage_UsesRequestPointer`, `Transform_CompletionDataUri_UsesCompletionDocument`.
4. **Identity/day/retention:** duplicate `requestId`, records finishing around UTC midnight and retention during mixed writes must not overwrite or orphan artifacts — Task 4 tests `WritePayload_SameRequestIdDifferentExportId_DoesNotOverwrite`, `WritePayload_FinishedAtControlsAllArtifactDays`, `Retention_RemovesPayloadFilesAndDirectoriesOnlyWhenExpired`.
5. **Partial IO failure and client behavior:** an artifact failure must not leave a manifest, and upstream/client bytes must remain unchanged — Task 4 tests `WritePayload_ArtifactFailure_DoesNotWriteManifest` and `ChatRequest_LargeBase64Image_ProviderGetsOriginalWhileManifestReferencesDecodedBlob`.

---

### Task 1: Classify Large Text Records

**Files:**
- Modify: `src/RouterBalancing.Core/Dataset/DatasetBuildResult.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs`
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs`
- Test: `router balancing test/Dataset/DatasetRecordBuilderTests.cs`
- Test: `router balancing test/Engine/UsageCaptureTests.cs`

**Interfaces:**
- Consumes: existing `DatasetCapture`, `ChatSseAggregateState`.
- Produces:
  ```csharp
  public const int InlineMaxFieldBytes = 1 * 1024 * 1024;
  public const int MaxExportFieldBytes = 16 * 1024 * 1024;
  public sealed record DatasetBuildResult(
      string? JsonLine, DatasetSkipReason? Skip, bool IsLargeRecord = false);
  ```

- [ ] **Step 1: Write failing tests**

Add tests named:
- `Build_BodyAtInlineThreshold_KeepsNormalClassification`
- `Build_BodyOverInlineThreshold_MarksLargeWithoutSkipping`
- `Build_BodyOverExportThreshold_SkipsTooLarge`
- `Build_CompletionUtf8OverInlineThreshold_MarksLarge`
- `Tee_Sse_CaptureCompletion_ContentBetweenOneAndSixteenMiB_KeepsCompletion`
- `Tee_Sse_CaptureCompletion_OverSixteenMiB_PreservesClientBytes`

Core assertions:
```csharp
Assert.False(Build(captureAt1MiB).IsLargeRecord);
Assert.True(Build(captureAt1MiBPlus1).IsLargeRecord);
Assert.Equal(DatasetSkipReason.TooLarge, Build(captureAt16MiBPlus1).Skip);
Assert.True(Build(captureWithMultibyteCompletion).IsLargeRecord);
Assert.NotNull(sseResult.CompletionJson); // >1 MiB and <=16 MiB
Assert.Equal(originalClientBytes, forwardedBytes); // including >16 MiB
```

Update existing boundary tests to use `MaxExportFieldBytes` for `TooLarge` and `InlineMaxFieldBytes` for large classification; keep assertions for `Truncated`, `Empty` and `Refusal` unchanged.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetRecordBuilderTests|FullyQualifiedName~UsageCaptureTests" --no-restore`
Expected: new behavior tests fail; compilation failure from the not-yet-defined result flag/constants is an acceptable RED for the new API.

- [ ] **Step 3: Implement classification and SSE budget**

In `DatasetRecordBuilder`:
- Replace the 1 MiB export rejection with `MaxExportFieldBytes`.
- Compute `IsLargeRecord` from `ClientBody.Length` or UTF-8 `CompletionJson` bytes exceeding `InlineMaxFieldBytes`.
- Return the flag only for a successful JSONL build; every skip returns the existing default `false`.
- Keep the existing order `TooLarge → Truncated → Empty → Refusal`.

In `UsageCapture`, use `DatasetRecordBuilder.MaxExportFieldBytes` for `ChatSseAggregateState`; never change forwarded client bytes or the existing overflow/truncated semantics.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command. Expected: all filtered tests PASS, including 1 MiB and 16 MiB ±1 byte cases.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Dataset/DatasetBuildResult.cs src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs src/RouterBalancing.Core/Engine/UsageCapture.cs "router balancing test/Dataset/DatasetRecordBuilderTests.cs" "router balancing test/Engine/UsageCaptureTests.cs"
git commit -m "feat: allow large text dataset records"
```

### Task 2: Store Large V1 Records and Verify End to End

**Files:**
- Modify: `src/RouterBalancing.Core/Dataset/DatasetPaths.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetStats.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSink.cs`
- Test: `router balancing test/Dataset/DatasetPathsTests.cs`
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`
- Test: `router balancing test/Server/DatasetExportIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1 `DatasetBuildResult.IsLargeRecord`.
- Produces:
  ```csharp
  public const string LargeFileNamePrefix = "requests-large-";
  public const string PayloadFileNamePrefix = "requests-payload-";
  public const string PayloadRootDirectoryName = "payloads";
  public static string LargeFileName(DateOnly utcDay);
  public static string PayloadFileName(DateOnly utcDay);
  public static string PayloadRelativeDirectory(DateOnly day, string exportId);
  public static string PayloadRelativePath(DateOnly day, string exportId, string fileName);
  // DatasetStats appended optional counters, preserving existing constructor call sites:
  int LargeRecordsWritten = 0,
  int PayloadRecordsWritten = 0,
  int ExternalizedPayloadCount = 0,
  long ExternalizedPayloadBytes = 0
  ```

- [ ] **Step 1: Write failing unit and integration tests**

Add:
- `FileName_LargeAndPayload_UseExpectedUtcDay`
- `RecordLarge_WritesOnlyLargeFile`
- `MixedRecords_CountsLargeAndTracksLastWrittenFile`
- `LargeFile_RotationAndRetention_UseFinishedAtDay`
- `Retention_LeavesUnrelatedJsonlAndKeepsForeverSetting`
- `ChatRequest_BodyOverOneMiB_WritesFullRecordToLargeFile`
- `ChatCompletion_OverOneMiB_WritesLargeFile_AndClientUnchanged`

Core assertions:
```csharp
Assert.False(File.Exists(normalPath));
Assert.True(File.Exists(largePath));
Assert.Equal(2, sink.Stats.RecordsWritten);
Assert.Equal(1, sink.Stats.LargeRecordsWritten);
Assert.Equal(largePath, sink.Stats.CurrentFile); // large written last
Assert.Equal(Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 17))), largePath);
Assert.Equal(originalCompletionBytes, clientResponseBytes);
```

Integration readers include normal + large candidates for today and yesterday UTC and assert full JSON content, `LargeRecordsWritten == 1`, and no line in the normal file.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetPathsTests|FullyQualifiedName~DatasetSinkTests|FullyQualifiedName~DatasetExportIntegrationTests" --no-restore`
Expected: fail because large/payload paths, the large writer and stats counter are missing.

- [ ] **Step 3: Implement large-file storage**

- Add the path helpers above; payload helpers are used by Tasks 3–4 and return manifest paths with `/` separators.
- In `DatasetSink`, manage a large writer alongside the normal writer and use `capture.FinishedAt` to select the record day.
- Route `IsLargeRecord=true` only to `requests-large-YYYY-MM-DD.jsonl`; increment `RecordsWritten` and `LargeRecordsWritten` once.
- Track `CurrentFile` as the file most recently written, not merely the file currently open.
- Close/flush both writers on day change and dispose.
- Extend `TryParseDay` and purge to normal + large files only; keep the current file and unrelated `.jsonl` files untouched. Do not change queue limits or `Capture`.
- Update integration readers/helpers to include large files without weakening existing assertions.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command. Expected: all filtered tests PASS; existing normal-file tests and upstream/client JSON remain correct.

- [ ] **Step 5: Run the phase checkpoint**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Dataset/DatasetPaths.cs src/RouterBalancing.Core/Dataset/DatasetStats.cs src/RouterBalancing.Core/Dataset/DatasetSink.cs "router balancing test/Dataset/DatasetPathsTests.cs" "router balancing test/Dataset/DatasetSinkTests.cs" "router balancing test/Server/DatasetExportIntegrationTests.cs"
git commit -m "feat: export large records to separate jsonl"
```

### Task 3: Transform Large Base64 into Source-Document References

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/DatasetPayloadReference.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetPayloadTransformResult.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetPayloadTransformer.cs`
- Create: `router balancing test/Dataset/DatasetPayloadTransformerTests.cs`

**Interfaces:**
- Consumes: Task 1 inline threshold; Task 2 payload path helpers and `DatasetCapture`.
- Produces:
  ```csharp
  public sealed record DatasetPayloadReference(
      string Document, string JsonPointer, string Url, string Path, string Kind,
      string MediaType, long Bytes, string Sha256);

  public sealed record DatasetPayloadFile(string RelativePath, byte[] Content);

  public sealed record DatasetPayloadTransformResult(
      JsonNode RequestDocument,
      JsonNode CompletionDocument,
      IReadOnlyList<DatasetPayloadFile> Files,
      IReadOnlyList<DatasetPayloadReference> References);

  public static class DatasetPayloadTransformer
  {
      public static DatasetPayloadTransformResult? Transform(
          DatasetCapture capture, string exportId);
  }
  ```

`Document` is exactly `"request"` or `"completion"`; `Kind` is `"base64"`; paths are relative to dataset root.

- [ ] **Step 1: Write failing tests**

Add:
- `Transform_SmallBase64_ReturnsNull`
- `Transform_InvalidBase64_LeavesInlineAndReturnsNull`
- `Transform_LongOrdinaryText_ReturnsNull`
- `Transform_DataUriImage_ExternalizesBinaryAndPointer`
- `Transform_ResponsesInputImage_UsesRequestPointer`
- `Transform_B64JsonAndInputAudio_WritesExpectedMediaTypes`
- `Transform_PointerEscapesTildeAndSlash`
- `Transform_CompletionDataUri_UsesCompletionDocument`
- `Transform_SameRequestIdDifferentExportId_DoesNotCollide`

Core assertions:
```csharp
Assert.Null(DatasetPayloadTransformer.Transform(smallOrInvalidCapture, exportId));
Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.Files[0].Content[..4]);
Assert.Equal("request", reference.Document);
Assert.Equal("/messages/0/content/1/image_url/url", reference.JsonPointer);
Assert.Equal($"payload://{exportId}/{fileName}", replacedUrl);
Assert.Equal(Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant(), reference.Sha256);
Assert.NotEqual(firstResult.References[0].Path, secondResult.References[0].Path);
```

Include `b64_json`, `input_audio.data`, `data:` URI and JSON keys containing `~` and `/` so RFC 6901 escaping is observable.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetPayloadTransformerTests" --no-restore`
Expected: fail because transformer/types do not exist.

- [ ] **Step 3: Implement the pure transformer**

- Parse `ClientBody` and `CompletionJson` into independent source-shaped `JsonNode` documents.
- Recursively inspect string values; accept only valid `data:<media-type>;base64,...` and explicit `b64_json` / `input_audio.data` fields.
- Externalize only encoded payloads >`InlineMaxFieldBytes`; leave malformed candidates inline and return `null` when no valid large candidate exists.
- Decode on this writer-side path, compute lowercase SHA-256, generate sequential file names with an 8-character hash prefix and MIME allowlist extension (fallback `.bin`).
- Replace the source value with `payload://{exportId}/{fileName}`; generate RFC 6901 pointers against the selected source document and relative paths through `DatasetPaths`.
- Use `capture.FinishedAt` for every path; perform no filesystem IO in the transformer.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command. Expected: all transformer tests PASS; no file is created during tests.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Dataset/DatasetPayloadReference.cs src/RouterBalancing.Core/Dataset/DatasetPayloadTransformResult.cs src/RouterBalancing.Core/Dataset/DatasetPayloadTransformer.cs "router balancing test/Dataset/DatasetPayloadTransformerTests.cs"
git commit -m "feat: transform large base64 dataset payloads"
```

### Task 4: Persist V2 Artifacts and Verify Base64 End to End

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/DatasetPayloadManifestFactory.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSink.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetStats.cs` only if counter shape needs correction
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`
- Create: `router balancing test/Dataset/DatasetPayloadManifestFactoryTests.cs`
- Test: `router balancing test/Server/DatasetExportIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 3 `Transform` result; Task 2 path helpers and stats counters.
- Produces:
  ```csharp
  public static class DatasetPayloadManifestFactory
  {
      public static string Build(
          DatasetCapture capture,
          DatasetPayloadTransformResult transformed,
          string exportId);
  }
  // DatasetRecordBuilder.FormatTimestamp becomes internal for shared UTC formatting.
  ```

Manifest fields: `v:2`, common v1 route metadata, `export_id`, `documents.request`, `documents.completion`, `payload_refs`; documents and references use Task 3 relative paths.

- [ ] **Step 1: Write failing unit and integration tests**

Add:
- `BuildManifest_ContainsSourceDocumentsAndReferences`
- `WritePayload_WritesSourceDocumentsBinaryThenManifest`
- `WritePayload_SameRequestIdDifferentExportId_DoesNotOverwrite`
- `WritePayload_FinishedAtControlsAllArtifactDays`
- `WritePayload_ArtifactFailure_DoesNotWriteManifest`
- `Retention_RemovesPayloadFilesAndDirectoriesOnlyWhenExpired`
- `WritePayload_UpdatesPayloadAndExternalizedStats`
- `ChatRequest_LargeBase64Image_ProviderGetsOriginalWhileManifestReferencesDecodedBlob`

Assertions:
```csharp
Assert.Equal(2, manifest["v"]!.GetValue<int>());
Assert.Equal(exportId, manifest["export_id"]!.GetValue<string>());
Assert.True(File.Exists(requestDocumentPath));
Assert.True(File.Exists(completionDocumentPath));
Assert.True(File.Exists(binaryPath));
Assert.False(File.Exists(payloadJsonlPath)); // forced artifact failure before manifest
Assert.Equal(1, sink.Stats.PayloadRecordsWritten);
Assert.Equal(binary.Length, sink.Stats.ExternalizedPayloadBytes);
Assert.Equal(originalRequestBytes, upstreamObservedRequestBytes);
Assert.Equal(reference.Sha256,
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_dir, reference.Path))))
        .ToLowerInvariant());
```

For the integration pointer assertion, navigate `requestDoc["messages"]![0]!["content"]![1]!["image_url"]!["url"]` and assert it equals the reference `payload://...` URL; also assert the small-base64 record remains in the normal v1 file.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetPayloadManifestFactoryTests|FullyQualifiedName~DatasetSinkTests|FullyQualifiedName~DatasetExportIntegrationTests" --no-restore`
Expected: fail because v2 manifest/artifact writing is missing.

- [ ] **Step 3: Implement manifest-last artifact persistence**

- After `DatasetRecordBuilder.Build` succeeds, call `DatasetPayloadTransformer.Transform(capture, Guid.NewGuid().ToString("N"))`; if it returns non-null, write v2 artifacts instead of the v1 line.
- Serialize transformed source documents to their `request.json` and `completion.json` paths.
- Write every binary/source file as a sibling temp file, flush, then atomic rename without overwrite. Generate a fresh `exportId` per record.
- Build and append the v2 manifest only after all artifacts succeed. Update total, payload and externalized stats once; `BytesWritten` remains JSONL bytes while `ExternalizedPayloadBytes` is binary bytes.
- Extend rotation/purge to `requests-payload-...` and matching `payloads/YYYY-MM-DD` directories. Retention stays on the same sequential writer and skips current artifacts/unrelated data.
- Preserve existing record-error/disable behavior and do not change `DatasetCapture`, `TeeResult`, queue limits or `ProxyRequestHandler`.
- Update integration readers/helpers to include payload files without weakening existing assertions.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command plus Task 3 tests. Expected: all PASS; failed artifact leaves no manifest and duplicate `requestId` never overwrites.

- [ ] **Step 5: Run the phase checkpoint**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Dataset/DatasetPayloadManifestFactory.cs src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs src/RouterBalancing.Core/Dataset/DatasetSink.cs src/RouterBalancing.Core/Dataset/DatasetStats.cs "router balancing test/Dataset/DatasetSinkTests.cs" "router balancing test/Dataset/DatasetPayloadManifestFactoryTests.cs" "router balancing test/Server/DatasetExportIntegrationTests.cs"
git commit -m "feat: persist externalized dataset payloads"
```

### Task 5: UI Statistics and Final Verification

**Files:**
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Modify: `docs/superpowers/specs/2026-11-18-dataset-large-record-export-design.md`
- Test: `router balancing test/Localization/TranslationParityTests.cs`

**Interfaces:**
- Consumes: all prior stats counters and Task 3–4 v2 documents/references.

- [ ] **Step 1: Write the failing localization test**

Add `DatasetStats_BothLanguages_FormatAllPayloadCounters` in `TranslationParityTests`.

Format both language strings with sequential sample values and assert the newly exposed counters are present at their intended positions:

```csharp
// Arguments {8}..{11} are 12, 34, 56 and 78.9 respectively.
Assert.Contains("12", formattedStats);   // large records
Assert.Contains("34", formattedStats);   // payload manifests
Assert.Contains("56", formattedStats);   // externalized payload files
Assert.Contains("78.9", formattedStats); // externalized MiB
```

Keep the existing `{0}`–`{7}` meanings unchanged; use `{8}` large, `{9}` payload manifests, `{10}` externalized files and `{11}` externalized MiB.

- [ ] **Step 2: Run the test to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests" --no-restore`
Expected: fail because the status format lacks the four new counters.

- [ ] **Step 3: Implement UI text and mark the spec implemented**

- Append `LargeRecordsWritten`, `PayloadRecordsWritten`, `ExternalizedPayloadCount` and `ExternalizedPayloadBytes` in `DatasetStatsLine()` with English/Vietnamese format strings at the same placeholder indexes.
- Use the existing MiB formatting convention for `ExternalizedPayloadBytes`.
- After all behavior is verified, change the spec status to `Đã triển khai`.

- [ ] **Step 4: Run focused tests to verify GREEN**

Run the Step 2 command. Expected: both languages format every counter without `FormatException` or missing values.

- [ ] **Step 5: Run final verification**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

Run: `git diff --check`
Expected: no output. Review `git status --short` and ensure only intended files plus unrelated pre-existing user work remain.

- [ ] **Step 6: Commit**

```bash
git add router-balancing/Components/Pages/SettingsPanel.razor src/RouterBalancing.Core/Localization/Translations.cs docs/superpowers/specs/2026-11-18-dataset-large-record-export-design.md "router balancing test/Localization/TranslationParityTests.cs"
git commit -m "feat: complete dataset payload export"
```

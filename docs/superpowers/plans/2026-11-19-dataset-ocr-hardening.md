# Dataset OCR Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve all 13 OCR delegate findings without losing records, deleting user data, breaking manifest integrity or exceeding the dataset SSE memory budget.

**Architecture:** Keep the existing bounded channel and writer. Move payload-writer opening before artifact IO, add generation-aware writer handoff/dispose locking, make retention ownership- and manifest-aware, use default-JSON UTF-8 budgeting for dataset capture, and complete semantic/integration coverage through a small stats formatter seam.

**Tech Stack:** .NET 10, C# nullable enabled, `System.Text.Json`/`JsonNode`, channels, xUnit v3, MAUI Blazor Hybrid, OCR delegation mode.

**Spec:** `docs/superpowers/specs/2026-11-19-dataset-ocr-hardening-design.md`

## Global Constraints

- Queue stays 128 records / 32 MiB; synchronous `Capture` performs no IO and client bytes never change.
- `InlineMaxFieldBytes = 1_048_576`, `MaxExportFieldBytes = 16_777_216`; >16 MiB remains `TooLarge`.
- No raw overflow, `CaptureAsync`, extra backpressure, blob dedup, cross-process retention lock or database changes.
- `datasetDay` uses `FinishedAt` UTC; `exportId` uses GUID N; manifest paths use `/`; manifest v2 remains last after artifacts succeed.
- Recovery only happens for Settings saved after the failure was published; save-before-failure is not recovery. No handoff after dispose.
- Retention only deletes recognized sink layouts and never deletes unrelated user entries; a surviving payload manifest blocks artifact deletion for that day.
- Dataset SSE accounting uses the default JSON encoder used by `ChatSseAggregateState.BuildCompletionJson`; client aggregation remains unchanged.
- Do not include metadata-provider edits or the pre-existing idle-timeout design document in task commits.

## Review Focus

1. **Settings failure/recovery races:** after-failure save must replay, save-before-failure must not reset the active failure, and dispose must not race a new handoff — Task 1 tests `WriterRecovery_OneCaptureAfterFailureSave_HandoffsAndDrains`, `WriterRecovery_SaveBeforeFailure_IsNotRecovery`, `WriterDispose_RacingHandoff_DoesNotStartNewWriter`.
2. **Payload open/artifact order:** manifest-open failure creates no artifact and retains capture; later artifact failures still produce no manifest/counter increment — Task 1 tests `OpenFailure_PayloadManifest_RetainsCaptureWithoutCreatingArtifacts`, `WritePayload_CompletionArtifactFailure_DoesNotWriteManifestOrCounters`, `WritePayload_BinaryArtifactFailure_DoesNotWriteManifestOrCounters`.
3. **Retention ownership and reference integrity:** unknown/user entries survive and a locked manifest prevents artifact deletion — Task 2 tests `Retention_ExpiredPayloadDay_PreservesUserEntries`, `Retention_ManifestDeletionFailure_PreservesArtifacts`, `Retention_ManifestDeletionFailureAfterPreviousPayloadDeleteFailure_Retries`.
4. **UTF-8/default-encoder memory bound and semantic loss:** non-ASCII/escaped deltas must overflow at the serialized UTF-8 boundary while source documents differ only at payload pointers — Task 3 tests `Tee_Sse_CaptureCompletion_NonAsciiEscapedContent_UsesDefaultJsonUtf8Budget`, `Transform_SourceDocuments_DifferOnlyAtPayloadReferences`.
5. **Consumer-visible byte/label correctness:** upstream/client bytes and EN/VI label-values must match production call sites — Task 4 tests `ChatRequest_LargeTextAndBase64_PreservesUpstreamAndClientBytes`, `DatasetStatsFormatter_EnglishAndVietnamese_RenderEveryCounterAtItsLabel`.

---

### Task 1: Preserve Captures During Open Failures and Recovery Races

**Files:**
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSink.cs`
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`
- Test: `router balancing test/Server/DatasetExportIntegrationTests.cs` only if an integration recovery assertion is missing

**Interfaces:**
- Consumes: existing `RetainCaptureForReplay`, `OpenWriter`, `DatasetPayloadTransformer.Transform`.
- Produces:
  ```csharp
  private void WritePayload(
      DatasetCapture capture,
      DatasetPayloadTransformResult transformed,
      string exportId,
      DateOnly recordDay,
      StreamWriter writer);
  ```
  Recovery state is internal: `_settingsGeneration`, `_failureGeneration`, `_startGate`, `_writerTask`, `_disabled`, `_disposed`; no new public/test-only API.

- [ ] **Step 1: Write failing tests**

Add:
- `OpenFailure_PayloadManifest_RetainsCaptureWithoutCreatingArtifacts`
- `WriterRecovery_OneCaptureAfterFailureSave_HandoffsAndDrains`
- `WriterRecovery_SaveBeforeFailure_IsNotRecovery`
- `WriterDispose_RacingHandoff_DoesNotStartNewWriter`
- `WritePayload_CompletionArtifactFailure_DoesNotWriteManifestOrCounters`
- `WritePayload_BinaryArtifactFailure_DoesNotWriteManifestOrCounters`

Assertions:
```csharp
Assert.False(Directory.Exists(Path.Combine(_dir, "payloads", Day(capture.FinishedAt), exportId));
Assert.Same(capture, PendingCapture(sink));
Assert.Equal(RawSize(capture), PendingBytes(sink));
Assert.Equal(new[] { "req-first", "req-resume" }, ReadRequestIds(normalFile));
Assert.DoesNotContain("req-disabled", ReadRequestIds(normalFile));
Assert.False(File.Exists(payloadJsonlPath));
Assert.Empty(Directory.GetFiles(exportDirectory, "*.tmp-*", SearchOption.AllDirectories));
Assert.Equal(0, sink.Stats.PayloadRecordsWritten);
```

For the save-before-failure case, prove the active failure still requires a later save. For dispose, assert no new task is started after `_disposed` is set; if handoff wins first, dispose captures and waits for that new task.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetSinkTests" --no-restore`
Expected: payload-open creates artifacts before failing and recovery/dispose cases fail before implementation.

- [ ] **Step 3: Implement payload-open recovery and handoff**

- In `Process`, open the payload JSONL writer inside a dedicated catch before calling `WritePayload`; on failure retain, disable before log, publish Changed and return before any artifact IO.
- Pass the opened writer into `WritePayload` using the signature above; keep source documents → binaries → manifest ordering and stats publication after manifest write.
- Increment `_settingsGeneration` in `OnSettingsChanged`; record `_failureGeneration` when publishing an active failure. Save-before-failure must leave the new failure active until a later save.
- At the end of `WriterLoopAsync`, close files then use `_startGate` to clear the completed task and hand off only when not disposed/channel-incomplete and no active failure. Keep `EnsureStarted()` under the same lock.
- In `DisposeAsync`, use `_startGate` when setting `_disposed`, completing the channel and capturing the writer task to await. Linearize dispose vs handoff; never start after dispose.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command and the DatasetExportIntegrationTests filter. Expected: all focused tests PASS without extra capture priming and without artifacts on payload-open failure.

- [ ] **Step 5: Run full suite and commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

```bash
git add src/RouterBalancing.Core/Dataset/DatasetSink.cs "router balancing test/Dataset/DatasetSinkTests.cs" "router balancing test/Server/DatasetExportIntegrationTests.cs"
git commit -m "fix: preserve dataset capture during storage failures"
```

### Task 2: Make Retention Ownership-Safe and Manifest-Consistent

**Files:**
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSink.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetPaths.cs` only if path validation helpers belong there
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`
- Test: `router balancing test/Dataset/DatasetPathsTests.cs` only if a helper is added there

**Interfaces:**
- Consumes: Task 1 recovery/pending behavior and existing retention patterns.
- Produces internal retention predicates: GUID N export ids, recognized artifact filenames, per-day manifest-deletion outcome; no public retention API.

- [ ] **Step 1: Write failing tests**

Add:
- `Retention_ExpiredPayloadDay_PreservesUserEntries`
- `Retention_UnknownExportDirectoryLayout_IsNotDeleted`
- `Retention_ManifestDeletionFailure_PreservesArtifacts`
- `Retention_ManifestDeletionFailureAfterPreviousPayloadDeleteFailure_Retries`
- `Retention_OwnedArtifactsAndManifest_ExpireTogether`

Fixtures must include user files and nested directories inside an expired `payloads/YYYY-MM-DD`, a GUID-looking directory containing an unknown file, a locked payload manifest and a locked artifact directory.

Assertions:
```csharp
Assert.True(File.Exists(userFile));
Assert.True(Directory.Exists(userNestedDirectory));
Assert.True(File.Exists(lockedManifest));
Assert.True(File.Exists(lockedArtifact));
Assert.False(File.Exists(expiredManifest));
Assert.False(Directory.Exists(expiredOwnedExportDirectory));
```

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetSinkTests|FullyQualifiedName~DatasetPathsTests" --no-restore`
Expected: recursive day deletion removes user data and manifest/artifact failures diverge.

- [ ] **Step 3: Implement ownership and manifest-aware purge**

- Consider only GUID N export directories. Before deleting one, require every entry to be `request.json`, `completion.json`, or `(image|audio|file)-NNN-<8 lowercase hex>.<allowlisted extension|bin>`; reject any subdirectory or unknown entry.
- Delete only fully recognized export directories, then delete a day directory with `recursive:false` only when empty; otherwise preserve it and all unknown entries.
- If a payload manifest exists but cannot be deleted, skip all artifact deletion for that day. If it was deleted or is absent, purge recognized artifacts. A failed artifact delete is retried at the next purge, including when the manifest disappeared earlier.
- Keep the scope documented in spec: one sink and no external writer during purge; no cross-process lock.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command. Expected: all retention cases PASS, including retry after unlock and unrelated-data preservation.

- [ ] **Step 5: Run full suite, Windows build and commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

```bash
git add src/RouterBalancing.Core/Dataset/DatasetSink.cs src/RouterBalancing.Core/Dataset/DatasetPaths.cs "router balancing test/Dataset/DatasetSinkTests.cs" "router balancing test/Dataset/DatasetPathsTests.cs"
git commit -m "fix: preserve dataset artifacts during retention"
```

### Task 3: Preserve Semantic Fidelity and the SSE UTF-8 Budget

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ChatSseAggregateState.cs`
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs`
- Modify: `src/RouterBalancing.Core/Dataset/DatasetPayloadTransformer.cs`
- Test: `router balancing test/Engine/UsageCaptureTests.cs`
- Test: `router balancing test/Dataset/DatasetPayloadTransformerTests.cs`
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`
- Test: `router balancing test/Server/DatasetExportIntegrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class ChatSseAggregateState(
      int? contentBudgetChars = null,
      bool budgetInUtf8Bytes = false);
  ```
  Dataset mode is enabled only by `UsageCapture` when `captureCompletion = true`; default client aggregation remains character-based.

- [ ] **Step 1: Write failing tests**

Add:
- `Tee_Sse_CaptureCompletion_NonAsciiEscapedContent_UsesDefaultJsonUtf8Budget`
- `Tee_Sse_CaptureCompletion_SurrogateSplit_NeverOverrunsUtf8Budget`
- `Transform_PngPrefixOnly_UsesUnknownFallback`
- `Transform_SourceDocuments_DifferOnlyAtPayloadReferences`

Assertions:
```csharp
Assert.Equal(ExpectedDefaultEncodedByteCount(text), ActualDatasetAccumulatorSize(text));
Assert.True(result.Overflowed || result.CompletionJson is null);
Assert.Equal("application/octet-stream", prefixOnlyReference.MediaType);
Assert.Equal(".bin", Path.GetExtension(prefixOnlyReference.Path));
Assert.True(JsonNode.DeepEquals(expectedRequest, transformed.RequestDocument));
Assert.True(JsonNode.DeepEquals(expectedCompletion, transformed.CompletionDocument));
Assert.True(JsonNode.DeepEquals(expectedRequest, ReadJson(requestDocumentPath)));
Assert.True(JsonNode.DeepEquals(expectedCompletion, ReadJson(completionDocumentPath)));
```

Expected documents must include metadata, `model`, `role`, `stream_options`, JSON null, arrays and fields not emitted by v1 normalization. Expected values are cloned from source and changed only at payload pointers.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~DatasetPayloadTransformerTests|FullyQualifiedName~DatasetSinkTests|FullyQualifiedName~DatasetExportIntegrationTests" --no-restore`
Expected: UTF-8/default-encoder and semantic assertions fail before implementation.

- [ ] **Step 3: Implement byte accounting, full PNG signature and deep preservation**

- Add `budgetInUtf8Bytes` to `ChatSseAggregateState`; dataset mode uses `Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text)) - 2` per text/tool-argument delta, matching the default encoder of `BuildCompletionJson`.
- Enable that mode only in `UsageCapture` dataset capture. Keep client aggregation and forwarded bytes unchanged; surrogate splits may overflow early but never late.
- Replace the PNG prefix with all eight bytes `89 50 4E 47 0D 0A 1A 0A`; retain JPEG/WebP and unknown fallback.
- Ensure transformer/source-document writing replaces values only at generated payload pointers and preserves every unrelated node; do not normalize source documents.

- [ ] **Step 4: Run tests to verify GREEN**

Run the Step 2 command. Expected: all focused tests PASS, including deep equals on returned and on-disk documents and the prefix-only fallback.

- [ ] **Step 5: Run full suite and commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

```bash
git add src/RouterBalancing.Core/Engine/ChatSseAggregateState.cs src/RouterBalancing.Core/Engine/UsageCapture.cs src/RouterBalancing.Core/Dataset/DatasetPayloadTransformer.cs "router balancing test/Engine/UsageCaptureTests.cs" "router balancing test/Dataset/DatasetPayloadTransformerTests.cs" "router balancing test/Dataset/DatasetSinkTests.cs" "router balancing test/Server/DatasetExportIntegrationTests.cs"
git commit -m "fix: preserve dataset semantics and capture memory"
```

### Task 4: Complete Boundary, Integration and Localization Coverage

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/DatasetStatsFormatter.cs`
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor`
- Modify: `docs/superpowers/specs/2026-11-19-dataset-ocr-hardening-design.md`
- Test: `router balancing test/Dataset/DatasetRecordBuilderTests.cs`
- Test: `router balancing test/Dataset/DatasetPayloadTransformerTests.cs`
- Test: `router balancing test/Engine/UsageCaptureTests.cs`
- Test: `router balancing test/Server/DatasetExportIntegrationTests.cs`
- Create: `router balancing test/Dataset/DatasetStatsFormatterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal static class DatasetStatsFormatter
  {
      internal static string Format(
          DatasetStats stats, Func<string, string> translate);
  }
  ```
  `SettingsPanel.DatasetStatsLine()` delegates with `stats => DatasetStatsFormatter.Format(stats, key => L[key])`.

- [ ] **Step 1: Write failing tests**

Boundary matrix:
- `Build_BodyAndCompletionThresholds_UseLiteralBoundaries`
- `Tee_Sse_CaptureCompletion_ExactBudgetAndPlusOneByte`
- `Transform_Base64EncodedCutoff_ExactNAndNextValidLength`
- `Transform_OrdinaryBase64AlphabetText_IsNotExternalized`

Integration/localization:
- `ChatRequest_LargeTextAndBase64_PreservesUpstreamAndClientBytes`
- `DatasetStatsFormatter_EnglishAndVietnamese_RenderEveryCounterAtItsLabel`

Assertions:
```csharp
Assert.Equal(1_048_576, DatasetRecordBuilder.InlineMaxFieldBytes);
Assert.Equal(16_777_216, DatasetRecordBuilder.MaxExportFieldBytes);
Assert.Equal(originalUpstreamBody, upstreamObservedBody);
Assert.Equal(originalClientSseBytes, clientResponseBytes);
Assert.Null(DatasetPayloadTransformer.Transform(ordinaryBase64AlphabetCapture, exportId));
Assert.Contains("12 large records", englishOutput);
Assert.Contains("34 payload", englishOutput);
Assert.Contains("56", vietnameseOutput);
Assert.Contains("78.9", vietnameseOutput);
```

Assert the exact label-value pair for every counter rather than only checking that numbers appear somewhere. Use literal expected values, not a self-created format string. Exercise exact N/N±1 body/completion boundaries, encoded N and N+4, and exact upstream/client bytes.

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DatasetRecordBuilderTests|FullyQualifiedName~DatasetPayloadTransformerTests|FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~DatasetExportIntegrationTests|FullyQualifiedName~DatasetStatsFormatterTests" --no-restore`
Expected: literal boundary/call-site tests fail before implementation.

- [ ] **Step 3: Implement the stats seam and coverage**

- Extract only argument construction/formatting into `DatasetStatsFormatter.Format`; preserve `{0}`–`{7}`, append `{8}` large, `{9}` payload manifests, `{10}` externalized files and `{11}` externalized MiB using the existing convention.
- Make `SettingsPanel.DatasetStatsLine()` call that formatter so tests exercise the production path with real English/Vietnamese dictionaries.
- Add the boundary matrix and exact upstream/client-byte assertions; keep ordinary base64-alphabet text out of externalization.
- Change the hardening spec status to `Đã triển khai` only after all behavior tests pass.

- [ ] **Step 4: Run focused tests to verify GREEN**

Run the Step 2 command. Expected: all focused tests PASS with literal values and exact byte comparisons.

- [ ] **Step 5: Run final verification and OCR delegate re-review**

Run: `dotnet test "router balancing test/router balancing test.csproj" --no-restore`
Expected: all tests PASS, 0 skipped.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

Run: `git diff --check`
Expected: no output.

Run `ocr delegate preview` and `ocr delegate rule` over the complete hardening range, then review every listed file using those rules. Expected: 100% reviewed and no remaining finding corresponding to the 13 original issues; any new finding goes through a new fix/review cycle instead of being silently ignored.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Dataset/DatasetStatsFormatter.cs router-balancing/Components/Pages/SettingsPanel.razor docs/superpowers/specs/2026-11-19-dataset-ocr-hardening-design.md docs/superpowers/plans/2026-11-19-dataset-ocr-hardening.md "router balancing test/Dataset/DatasetRecordBuilderTests.cs" "router balancing test/Dataset/DatasetPayloadTransformerTests.cs" "router balancing test/Engine/UsageCaptureTests.cs" "router balancing test/Server/DatasetExportIntegrationTests.cs" "router balancing test/Dataset/DatasetStatsFormatterTests.cs"
git commit -m "test: complete dataset hardening coverage"
```

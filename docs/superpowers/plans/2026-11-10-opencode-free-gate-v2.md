# OpenCode Free Gate v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the Go sidecar to v2 parity with the documented opencode.ai `/inference` contract (CLI 2.x on Bun shape) so free-tier traffic stops dying at the 403 "can only be used from within OpenCode", keep the keyed lane working via passthrough, and give C# a 3-way error classification (shape-403 / IP-abuse-403 / quota-429).

**Architecture:** The Go sidecar becomes the single shaping authority for host `opencode.ai` (identity + body + path); C# stops transforming bodies/headers and gains outcome-level handling of a new `DispatchOutcome.Blocked` (proxy exit block, provider-wide gate on direct egress) plus dataset flags driven by two informational response headers from the sidecar.

**Tech Stack:** Go 1.24 (`go.mod`, `GOTOOLCHAIN=local`, dep: `golang.org/x/net/proxy` only) · .NET 10 / MAUI Blazor Hybrid (no new deps) · xUnit + `go test`.

**Spec:** `docs/superpowers/specs/2026-11-10-opencode-free-gate-v2-design.md` — read it first; this plan argues from it. Reference implementation for contract understanding ONLY (GPL, never copy): `C:\Users\trucn\AppData\Local\Temp\opencode\oc2api`. MIT source for assets: `C:\Users\trucn\AppData\Local\Temp\opencode\opencode-src` (anomalyco/opencode @ tag v2.1.1).

## Global Constraints

- **License:** NO code/data copied from `jasonxu114514/opencode2api` (GPL-3.0). Assets (`prompt_v2.txt`, `tools_v2.json`) are re-derived from `anomalyco/opencode` (MIT) tag `v2.1.1` with an attribution comment in the embedding Go file.
- **Go:** module `github.com/HKStudio011/router-balancing/sidecar/opencode-proxy`; `go 1.24.0`, `GOTOOLCHAIN=local`; no new dependencies (uTLS is stage-2 only and OUT of this plan). Test command: `go test ./...` from `sidecar/opencode-proxy/` (never with `-tags liveprobe`).
- **C# build gate:** `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0` · test: `dotnet test "router balancing test/router balancing test.csproj"` — both green after every C# task. Known full-suite flakes that pass in isolation (do NOT chase): `LogRetentionWorkerTests.Start_ThenDispose_PurgesOnStartAndStopsCleanly`, `UpdateCoordinatorTests.StartUpdateAsync_WhenProxyInFlightPositive_WaitsBeforeSnapshot`.
- **Host↔sidecar contract (unchanged except 2 additive response headers):** stdout `LISTENING <port>` · `GET /health` → 200 · `POST /v1/chat/completions` · request header `X-Proxy-Url` (optional). Sidecar adds exactly: `X-Sidecar-Shaped: tools+prompt|none` and `X-Sidecar-Title: synthesized` (only when it synthesizes a title).
- **Upstream free route:** `https://opencode.ai/inference/openai/v1/chat/completions` · keyless auth `Bearer public` (case-sensitive) · UA `opencode/latest/2.1.1/cli`.
- **Pinned values:** `max_completion_tokens` default `32000`; title synthesis ≤ 6 words / ≤ 40 chars, fallback `"New Session"`; proxy block TTL `15m` (`TimeSpan.FromMinutes(15)`); blocked retry cap `2`.
- **Commits:** conventional, English, one commit per task step-set as marked.

## Review Focus

Failure modes the spec implies but whose tests must be pinned explicitly (each is added to the owning task):

1. **SSE chunk fragmentation** — upstream may split a `tool_calls[].function.name` JSON across TCP chunks; the streaming path must buffer to complete `data:` lines before restoring (partial bytes pass through untouched) → T7 step 1 test `TestPipeline_SSERestore_EndToEnd`.
2. **Orphaned tool references** — client history referencing a tool no longer declared, or `tool_choice` naming a renamed tool → request stays self-consistent (drop orphan pair; rewrite choice to wire name) → T5 steps 3–4.
3. **Implausible official session** — client sends a well-formed `ses_…` whose decoded timestamp is outside `[today−31d, today]` → re-minted, not kept (upstream would 429 it like quota exhaustion) → T2 step 3.
4. **Quota E2 shape without Retry-After** — 429 + `AI_APICallError` + `Invalid credential` must open the provider-wide gate with the settings fallback TTL and not loop retries on the same provider → T9 step 5.
5. **Keyed-lane byte fidelity** — a keyed request body must reach upstream byte-identical (no stream forcing, no key reorder, no prompt/tools), `Authorization` preserved, `X-Sidecar-Shaped: none`, no SSE name restore → T7 step 4.

---

### Task 1: Sidecar assets — wire capture of the official CLI 2.1.1

**Files:**
- Create: `sidecar/opencode-proxy/prompt_v2.txt`
- Create: `sidecar/opencode-proxy/tools_v2.json`
- (temp, NOT committed) capture server + opencode config under `$env:TEMP\opencode\capture\`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: two embed assets used by T4/T5 — `prompt_v2.txt` = the exact system-message text the CLI 2.1.1 sends for a marker-prompt model family; `tools_v2.json` = the exact wire JSON array of official tool definitions (`{type:"function",function:{name,description,parameters,…}}`).

- [ ] **Step 1: Install CLI 2.1.1**

Run: `npm i -g opencode-ai@2.1.1` then `opencode --version`. Expected: version prints `2.1.1`. If the npm package name is wrong, check `anomalyco/opencode` repo `packages/cli/package.json` for the published name and retry with it.

- [ ] **Step 2: Write the capture server (temp dir)**

Create `$env:TEMP\opencode\capture\server.mjs` — an HTTP server that binds `127.0.0.1:0`, prints its port, and for every request writes `{url, headers, body}` (body parsed as JSON) to `$env:TEMP\opencode\capture\captured.json`, then answers `200 text/event-stream` with one content chunk (`"ok"`) + `data: [DONE]`.

- [ ] **Step 3: Write the opencode config (temp dir)**

Create `$env:TEMP\opencode\capture\opencode.json`: a custom provider named `capture` with `npm: "@ai-sdk/openai-compatible"`, `options.baseURL` = the capture server URL + `/v1`, and ONE model whose **id contains the substring `trinity`** (e.g. `trinity-free-x`) — that substring selects the official marker prompt (`ArceePlugin` in `packages/core/src/plugin/optimize.ts`).

- [ ] **Step 4: Run one non-interactive session against the capture server**

Run from the capture dir: `opencode run --model capture/trinity-free-x "say hi"`. Expected: CLI completes; `captured.json` exists with a POST body containing `messages[0].role == "system"` and a `tools` array.

- [ ] **Step 5: Extract the assets**

From `captured.json`: write `body.messages[0].content` (verbatim) → `sidecar/opencode-proxy/prompt_v2.txt`; write `body.tools` (the JSON array, verbatim bytes) → `sidecar/opencode-proxy/tools_v2.json`. Identify any dynamic segments in the prompt text (date, working directory, model name) and note them in a Go doc comment added to `prompt.go` later (T4) — do NOT put comments inside the .txt.

- [ ] **Step 6: Verify acceptance criteria**

Check all; on any failure go to Step 7:
- `tools_v2.json` has EXACTLY 13 entries, names in order: `edit, execute, glob, grep, question, read, shell, skill, subagent, webfetch, websearch, write, patch`.
- Every entry is `{type:"function", function:{name, description (non-empty), parameters (object)}}`.
- `prompt_v2.txt` starts with / contains the marker line `You are opencode, an interactive CLI tool`.

If the tool set differs from 13 (e.g. `execute` missing — it is a codemode tool and may be config-gated): re-run Step 4 with the closest config that enables it; if still different, keep the captured set as-is (capture is ground truth) and record the discrepancy in the commit message.

- [ ] **Step 7 (fallback only, if Steps 1–6 fail — e.g. `opencode run` unavailable): manual re-derivation from MIT source**

Prompt: `packages/core/src/plugin/system-prompt/trinity.txt` @ v2.1.1 rendered with the tool-guidance lines from `SessionSystemPrompt.render` (`packages/core/src/session/system-prompt.ts` — the three `- Prefer dedicated tools…` / `- Use the write tool…` / `- Use the edit tool…` bullets for shell/write/edit). Tools: convert the zod schemas in `packages/core/src/tool/plugin/*.ts` (+ `codemode/tool.ts` for `execute`) to JSON Schema by hand. Record in the commit message that fallback was used and why.

- [ ] **Step 8: Commit**

```bash
git add sidecar/opencode-proxy/prompt_v2.txt sidecar/opencode-proxy/tools_v2.json
git commit -m "feat(sidecar): add v2 official prompt + tool assets captured from CLI 2.1.1 (MIT anomalyco/opencode)"
```

---

### Task 2: Sidecar identity — slow-clock session id + project id

**Files:**
- Create: `sidecar/opencode-proxy/identity.go`
- Test: `sidecar/opencode-proxy/identity_test.go`
- Modify: `sidecar/opencode-proxy/main.go` (delete `clientProjectID` L187-190, `canonicalSessionID`/`descendingTimePart`/`base62Fixed`/`sessionSignal` L286-345 — replaced by identity.go; keep `canonicalSessionPattern` if moved here)

**Interfaces:**
- Consumes: nothing.
- Produces (used by T7):
  - `func sessionID(signal string) string` — keeps the signal verbatim when it is a well-formed official session with a plausible timestamp; otherwise mints deterministically per `(signal, bootNonce, UTC day)`.
  - `func projectID(clientValue string) string` — `""` → random 40-hex (20 bytes); 40 lowercase hex → verbatim; anything else → `hex(sha256("opencode-project\x00"+value)[:20])`.
  - `var bootNonce [16]byte` — random per process (`init()`).

- [ ] **Step 1: Write failing tests**

`identity_test.go`, table-driven, style of existing `main_test.go`:
- `TestSessionID_DecodedTimestampWithinPlausibleWindow` — mint for signal `"conv-1"`; decode the 12-hex time part back to ms (`ts = (~word & 0xFFFFFFFFFFFF) >> 12` where word = parsed hex); assert `midnightUTC(today)-31d <= ts <= midnightUTC(today)` and the last 3 hex chars are `"ffe"`.
- `TestSessionID_StableWithinSameDay_DiffersAcrossSignals` — same signal twice → equal; two signals → different.
- `TestSessionID_OfficialPlausibleSession_KeptVerbatim` — construct `ses_` + time part for `ts = today−5d` (counter 1) + 14 base62 chars → returned unchanged.
- `TestSessionID_ImplausibleOfficialSession_Reminted` (Review Focus 3) — well-formed shape but decoded ts = `today+60d` (future) AND one with `ts = today−200d` → both re-minted (result ≠ input, passes the window test).
- `TestProjectID_RandomWhenEmpty_40Hex` — 10 calls all match `^[0-9a-f]{40}$`, at least two distinct.
- `TestProjectID_Client40HexKept_OtherHashed` — `"a"*40` → verbatim; `"prj_abc"` and `"global"` → 40-hex, deterministic (same input twice equal).

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run 'TestSessionID|TestProjectID' ./...` from `sidecar/opencode-proxy/`. Expected: FAIL (undefined functions).

- [ ] **Step 3: Implement `identity.go`**

Slow clock: `offset = int(sha256("ses\x00"+signal+"\x00"+bootNonce)[0..4]) % (30*24h in ms)`; `ts = UTC midnight today − offset`; time part = `~(uint64(ts)<<12 | 1) & 0xFFFFFFFFFFFF` rendered as 12 lowercase hex; random part = 14 base62 chars from the next hash bytes (reuse the existing `base62Fixed` algorithm, moved here). Plausibility check for "keep verbatim": match `^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$` AND decoded ts within `[midnightUTC(today)−31d, midnightUTC(today)]`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `go test ./...`. Expected: PASS.

- [ ] **Step 5: Remove the replaced v1 functions from `main.go` and commit**

Delete the listed v1 helpers (keep file compiling — `handleChat` still calls `canonicalSessionID`/`clientProjectID`; rename call sites to `sessionID`/`projectID` with the same arguments). Move `sessionSignal(r *http.Request, body []byte) string` into `identity.go` unchanged (it is identity logic; T7 will feed it from headers/body exactly as today).

```bash
git add sidecar/opencode-proxy/
git commit -m "feat(sidecar): slow-clock session id and per-request project id (v2 contract)"
```

---

### Task 3: Sidecar body shaping — free-lane chat contract + key order

**Files:**
- Create: `sidecar/opencode-proxy/body.go`
- Test: `sidecar/opencode-proxy/body_test.go`

**Interfaces:**
- Consumes: nothing.
- Produces (used by T7):
  - `func shapeChatBody(body []byte) []byte` — forces `stream=true`, `stream_options.include_usage=true`, adds `max_completion_tokens=32000` when neither `max_completion_tokens` nor `max_tokens` is present, and serializes with the client-2.x key order: `model, messages, tools, stream, stream_options, max_completion_tokens`, then remaining keys sorted alphabetically. Unparseable JSON → return input unchanged (upstream answers a clear error).
  - `func marshalOrdered(payload map[string]any) []byte` — the ordered serializer used by `shapeChatBody` (pinned prefix keys first if present, then remaining keys alphabetical); exported so T5's rewrite path re-serializes with identical ordering.

- [ ] **Step 1: Write failing tests**

`body_test.go`:
- `TestShapeChatBody_ForceStreamUsageAndMaxTokens` — input `{model, messages, stream:false}` → output has `stream==true`, `stream_options.include_usage==true`, `max_completion_tokens==32000`.
- `TestShapeChatBody_ExistingLimitNotOverridden` — input with `max_tokens: 500` → no `max_completion_tokens` added, `max_tokens` preserved.
- `TestShapeChatBody_KeyOrderMatchesClient2x` (Review Focus — order is part of the fingerprint) — assert via byte index: `strings.Index(out, "\"model\"") < strings.Index(out, "\"messages\"") < strings.Index(out, "\"tools\"") < strings.Index(out, "\"stream\"") < strings.Index(out, "\"stream_options\"") < strings.Index(out, "\"max_completion_tokens\"")`.
- `TestShapeChatBody_ExtraKeysSortedAlphabetically` — input with extra keys `zeta`, `alpha` → both present, `alpha` before `zeta`, after the pinned prefix.
- `TestShapeChatBody_MalformedJSON_ReturnedUnchanged` — input `{"model":` (truncated) → byte-identical output.

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run TestShapeChatBody ./...`. Expected: FAIL (undefined).

- [ ] **Step 3: Implement `shapeChatBody` in `body.go`**

Approach: unmarshal to `map[string]any`; apply the three normalizations; then hand-assemble the JSON — for each key in pinned order that exists, append `"key":` + `json.Marshal(value)`; then remaining keys sorted alphabetically the same way. Do NOT use `json.Marshal(map)` for the whole object (Go sorts all keys alphabetically and would break the pinned prefix).

- [ ] **Step 4: Run tests to verify they pass**

Run: `go test ./...`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add sidecar/opencode-proxy/body.go sidecar/opencode-proxy/body_test.go
git commit -m "feat(sidecar): free-lane chat body shaping with client 2.x key order"
```

---

### Task 4: Sidecar official prompt injection

**Files:**
- Create: `sidecar/opencode-proxy/prompt.go`
- Test: `sidecar/opencode-proxy/prompt_test.go`
- Modify: none (embeds T1's `prompt_v2.txt`)

**Interfaces:**
- Consumes: `prompt_v2.txt` (T1).
- Produces (used by T7):
  - `const opencodePromptMarker = "You are opencode, an interactive CLI tool"`
  - `func injectOfficialPrompt(messages []any, model string) []any` — if ANY message content already contains the marker → return input unchanged (idempotent). Otherwise: fold ALL client `system`/`developer` messages (in order, joined by blank line) into ONE leading system message = official prompt text + `\n\n` + folded client text; drop the original system/developer entries; keep all other messages untouched.
  - Doc comment on the embed var must carry the MIT attribution: source repo/tag (`anomalyco/opencode` v2.1.1), derivation method (T1 capture or fallback), date — and list the dynamic segments found in T1 Step 5 (date/cwd/model name) with how they are filled (e.g. `Today's date` → UTC now, working dir → sidecar cwd, model → request `model`).

- [ ] **Step 1: Write failing tests**

`prompt_test.go`:
- `TestInjectOfficialPrompt_PrependsAndFoldsSystemMessages` — messages `[system "A", user "hi", developer "B"]` → result `[0]` role system, content starts with the marker, contains `"A"` before `"B"`, no other system/developer entries remain, user message intact at its relative position.
- `TestInjectOfficialPrompt_MarkerPresent_NotInjected` (idempotency) — messages whose first system already contains the marker → returned slice content byte-equal to input.
- `TestInjectOfficialPrompt_NoSystemMessage_StillPrepends` — `[user "hi"]` → result starts with one system message containing the marker, user preserved.

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run TestInjectOfficialPrompt ./...`. Expected: FAIL (undefined).

- [ ] **Step 3: Implement `prompt.go`**

Embed with `//go:embed prompt_v2.txt`. If T1 found dynamic segments, render them via `fmt.Sprintf` over a template copy of the embedded text; if none were found, use the embedded text verbatim. Either way the marker check runs on message content strings (string scan, not JSON parse).

- [ ] **Step 4: Run tests to verify they pass**

Run: `go test ./...`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add sidecar/opencode-proxy/prompt.go sidecar/opencode-proxy/prompt_test.go
git commit -m "feat(sidecar): official v2 system prompt injection with idempotency marker"
```

---

### Task 5: Sidecar tool shaping — `clint_` rename, official 13 set, SSE restore

**Files:**
- Create: `sidecar/opencode-proxy/toolshape.go`
- Test: `sidecar/opencode-proxy/toolshape_test.go`
- (T7 deletes `anonymous-tools.json` + its embed)

**Interfaces:**
- Consumes: `tools_v2.json` (T1).
- Produces (used by T7):
  - `type ToolRenames struct{ /* toWire, toClient maps */ }` — zero value / nil is safe no-op.
  - `func NewToolRenames(clientNames []string) *ToolRenames` — each client name → `clint_` + first 12 hex of `SHA-256(name)`; on collision append `_2`, `_3`…; total wire name ≤ 64 chars. Also records official-alias mapping: an official tool name that case-insensitively matches a declared client name maps back to that client name (e.g. client declares `Bash`, model calls official `shell`-family alias → restore to `Bash`).
  - `func (r *ToolRenames) RewriteBody(payload map[string]any)` — rename entries in `tools[].function.name`; rewrite every reference so the request stays self-consistent: assistant `tool_calls[].function.name`, `role:"tool"` messages' `tool_call_id`-paired names, and `tool_choice` (string form → wire name; object form with `function.name` → same); a reference to a tool no longer declared → drop the pair (the `role:"tool"` message AND the originating `tool_call` entry).
  - `func (r *ToolRenames) AppendOfficial(payload map[string]any)` — append the embedded 13 tools after client tools; on this host (`runtime.GOOS == "windows"`) rewrite the `shell` description fragment `on Linux using bash` → `on Windows using powershell`.
  - `func (r *ToolRenames) Resolve(wire string) (string, bool)` — response-side: exact wire name → client name; official alias → matching client name; `clint_` prefix stripped as best-effort recovery; case/separator fold only when exactly ONE declared client tool matches; never guess between two.
  - `func (r *ToolRenames) RestoreSSELine(line string) string` — for a single SSE line: if it starts with `data:` and the payload is a JSON object containing `choices[].delta.tool_calls[].function.name`, rewrite those names via `Resolve`; every other byte of the line untouched; non-`data:` lines and unparseable payloads returned verbatim.

- [ ] **Step 1: Write failing tests**

`toolshape_test.go`:
- `TestNewToolRenames_DeterministicDigestAndCollision` — name `"Read"` → `clint_` + first 12 hex of sha256("Read"); two client names whose 12-hex prefix collides (construct by feeding the same digest source twice via a helper, or use a known-colliding pair computed in-test) → second gets `_2`.
- `TestRewriteBody_RenamesToolsAndAllReferences` — payload with tools `[Bash]`, history `[assistant tool_calls Bash, role:tool result]`, `tool_choice "Bash"` → all become the wire name; no client name survives anywhere in the re-marshaled JSON (assert via `strings.Contains` on `"Bash"`).
- `TestRewriteBody_OrphanReferenceDropped` (Review Focus 2) — history references tool `Ghost` not in declared tools → the `role:"tool"` message and its `tool_call` entry are gone; neighboring messages intact.
- `TestRewriteBody_ToolChoiceObjectForm_Rewritten` — `tool_choice {type:"function", function:{name:"Bash"}}` → wire name.
- `TestAppendOfficial_13NamesStrictFalsePowershellOnWindows` — after append, tool names = client wire names + exactly the 13 official in T1 order; every official entry has `strict == false` (when present in source) and on Windows the `shell` description contains `"on Windows using powershell"`.
- `TestResolve_WireAliasAndPrefixRecovery` — declared `[Bash]`: `Resolve(wireName("Bash"))` → `Bash`; `Resolve("shell")` → `Bash` (official alias); `Resolve("clint_"+digest12)` → `Bash`; `Resolve("unknown_tool")` → unchanged, `ok==false`.
- `TestRestoreSSE_LineScenarios` — a complete `data:` line whose JSON contains a wire tool name is restored, non-`data:` lines (`event: x`, empty keep-alive) and unparseable `data:` payloads pass through byte-identical. (Cross-chunk splitting of one frame is pinned end-to-end in T7's `TestPipeline_SSERestore_EndToEnd`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run 'TestNewToolRenames|TestRewriteBody|TestAppendOfficial|TestResolve|TestRestoreSSE' ./...`. Expected: FAIL (undefined).

- [ ] **Step 3: Implement `toolshape.go`**

Embed `tools_v2.json` (`//go:embed`). Digest: `hex(sha256(name))[:12]`. RewriteBody operates on the already-parsed `map[string]any` (T7 passes the shaped payload); re-marshal is T7's job via `marshalOrdered` from `body.go` (T3).

- [ ] **Step 4: Run tests to verify they pass**

Run: `go test ./...`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add sidecar/opencode-proxy/toolshape.go sidecar/opencode-proxy/toolshape_test.go
git commit -m "feat(sidecar): clint_ tool rename with history rewrite and SSE name restore"
```

---

### Task 6: Sidecar title detection + synthesized fallback

**Files:**
- Create: `sidecar/opencode-proxy/title.go`
- Test: `sidecar/opencode-proxy/title_test.go`

**Interfaces:**
- Consumes: nothing.
- Produces (used by T7):
  - `func isTitleRequest(payload map[string]any) bool` — true iff: no `tools` key; ≤ 3 messages; no `assistant`/`tool` role turns; and any message content contains one of the official title fragments: `"you name conversations so the user can find them later in a list of past sessions"`, `"you generate a descriptive title for this conversation"`, `"you decide whether the session title still matches the conversation"`, `"reply with a title and nothing else"`, `"reply with a title for it and nothing else"`, `"generate a title for this conversation"`.
  - `func synthesizeTitle(payload map[string]any) string` — first non-empty line of the first user message → trim to ≤ 6 words / ≤ 40 chars (cut at word boundary); no usable text → `"New Session"`.

- [ ] **Step 1: Write failing tests**

`title_test.go`:
- `TestIsTitleRequest_V2Fragment_NoTools_FewMessages_True` — 2 messages, system with fragment #1 + user text, no tools → true.
- `TestIsTitleRequest_WithToolsOrAssistantTurn_False` — same but with `tools: []` non-empty / an assistant turn / 4 messages → false (3 sub-cases).
- `TestIsTitleRequest_NoFragment_False` — plain chat → false.
- `TestSynthesizeTitle_TrimsToSixWordsFortyChars` — long first line → ≤ 6 words AND ≤ 40 chars, ends at word boundary; empty user text → `"New Session"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run 'TestIsTitleRequest|TestSynthesizeTitle' ./...`. Expected: FAIL (undefined).

- [ ] **Step 3: Implement `title.go`**

- [ ] **Step 4: Run tests to verify they pass**

Run: `go test ./...`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add sidecar/opencode-proxy/title.go sidecar/opencode-proxy/title_test.go
git commit -m "feat(sidecar): title-request detection and synthesized title fallback"
```

---

### Task 7: Sidecar v2 pipeline — lane split, headers, wiring, v1 removal

**Files:**
- Modify: `sidecar/opencode-proxy/main.go` (rewrite `handleChat` + header set; delete `normalizeBody`, paraphrase title-gen block L192-284, `anonymous-tools.json` embed L57-58)
- Delete: `sidecar/opencode-proxy/anonymous-tools.json`
- Test: `sidecar/opencode-proxy/pipeline_test.go` (new; existing `main_test.go` tests that referenced deleted v1 functions are removed in this task)

**Interfaces:**
- Consumes: T2 `sessionID`/`projectID`, T3 `shapeChatBody`, T4 `injectOfficialPrompt`, T5 `ToolRenames`, T6 `isTitleRequest`/`synthesizeTitle`.
- Produces: the running sidecar. Behavior contract (all asserted by tests):
  - Upstream URL: `https://opencode.ai/inference/openai/v1/chat/completions`; UA `opencode/latest/2.1.1/cli`.
  - Lane detection: keyless ⇔ `Authorization` absent OR equal-fold to `"Bearer public"`. Keyed → body byte-identical upstream, client `Authorization` preserved, identity headers still set, `X-Sidecar-Shaped: none`, no prompt/tools/SSE restore.
  - Free lane: title request? → inject prompt only, `X-Sidecar-Shaped: none`; else full shaping (prompt + rename + official 13 + body contract), `X-Sidecar-Shaped: tools+prompt`.
  - Free-lane headers exactly: `x-opencode-client: cli`; `x-opencode-session`/`x-session-affinity`/`X-Session-Id` = root session; `x-opencode-session-id` = child (client's `x-opencode-session-id` if present and well-formed, else root); `x-opencode-project` per T2; NO `x-opencode-request`.
  - SSE restore on free-lane response stream via `RestoreSSELine` per line.
  - Title request where upstream returns non-2xx or transport error → sidecar answers 200 itself: `X-Sidecar-Title: synthesized`, body = one SSE chunk with the synthesized title + `data: [DONE]`.

- [ ] **Step 1: Write failing pipeline tests (fake upstream via `httptest`)**

`pipeline_test.go` — helper `runSidecar(t, upstream *httptest.Server, req *http.Request) (*http.Response, []byte)` that serves the real `handleChat` on a local mux and points the binary's upstream at the test server (extract the upstream base URL into a `var upstreamBaseURL = "https://opencode.ai/inference"` package var so tests can override it; route path stays `/openai/v1/chat/completions`).
- `TestPipeline_FreeLane_RequestShape` — keyless chat request with 2 client tools + history → fake upstream asserts: URL path, `Authorization == "Bearer public"`, UA, all §headers present, no `x-opencode-request`; body: marker in first system message, tools = 2 wire names + 13 official (15 total), key order per T3, `stream==true`. Response headers include `X-Sidecar-Shaped: tools+prompt`.
- `TestPipeline_KeyedLane_BodyByteIdentical` (Review Focus 5) — request with `Authorization: Bearer sk-real` + arbitrary body → fake upstream receives body byte-equal, same Authorization; response header `X-Sidecar-Shaped: none`; an SSE frame containing a tool name is NOT altered on the way back.
- `TestPipeline_KeyedPublicValue_TreatedKeyless` — `Authorization: Bearer PUBLIC` (wrong case) → free lane (`Bearer public` sent upstream).
- `TestPipeline_SSERestore_EndToEnd` — fake upstream streams a frame with the wire tool name split across two Write calls mid-name → client sees the restored client name, no corrupted bytes.
- `TestPipeline_TitleRequest_Upstream429_Synthesized200` — title-shaped body, fake upstream answers 429 → sidecar returns 200 + `X-Sidecar-Title: synthesized` + non-empty SSE title; `X-Sidecar-Shaped: none`.
- `TestPipeline_ErrorPassthrough_KeyedAndFree` — fake upstream 403 with body `{"error":{"message":"...abusive traffic..."}}` → sidecar returns 403 + identical body bytes (no reshaping of error bodies).

- [ ] **Step 2: Run tests to verify they fail**

Run: `go test -run TestPipeline ./...`. Expected: FAIL.

- [ ] **Step 3: Rewrite `handleChat` in `main.go`**

Pipeline per Interfaces. Streaming read: `bufio.Reader` + `ReadString('\n')` over the upstream body, run each complete line through `RestoreSSELine` (free lane only), write + flush per line; on read error mid-stream stop silently (client disconnect is normal). Delete v1 artifacts listed in Files; drop `main_test.go` tests that exercised deleted functions (`normalizeBody`, paraphrase title prompt) — their replacements live in T3–T7 tests.

- [ ] **Step 4: Run full Go suite**

Run: `go test ./...`. Expected: PASS (all tasks' tests green together).

- [ ] **Step 5: Commit**

```bash
git add -A sidecar/opencode-proxy/
git commit -m "feat(sidecar): v2 pipeline — lane split, official shaping, SSE restore; drop v1 fingerprint artifacts"
```

---

### Task 8: Live probe (V1) — free lane verification

**Files:**
- Create: `sidecar/opencode-proxy/probe_test.go` (`//go:build liveprobe`)

**Interfaces:**
- Consumes: T7 pipeline internals (reuse the same shaping functions; do NOT open a sidecar listener).
- Produces: recorded probe evidence (status, upstream request id, message snippet — never conversation content) in the task report + SDD ledger.

Budget and discipline (spec §8): **3 real requests total for the free lane**, sequential, hard-stop on IP-abuse markers (`abusive traffic` / `abuse traffic` / `abuse of traffic`, or `free models aren't available` + `from your current network`) → abort remaining, no retry, report. 400 "Model is unavailable"/410 → inconclusive, may swap to another live free model (counts as the same request). 429 → inconclusive, no retry. Shape-403 ("within OpenCode") → STOP and report: stage-2 TLS (uTLS) is a follow-up decision, NOT part of this plan.

- [ ] **Step 1: Write `probe_test.go`**

`//go:build liveprobe` · `func TestLiveProbe_FreeChat(t *testing.T)` — model `glm-5.3-flash-free`, route `/inference/openai/v1/chat/completions`, full v2 shaping (session/project per T2, prompt/tools per T4/T5, body per T3), payload user message `"Reply with exactly: OK"`, `stream:true`. Assert 200 + non-empty completion content; log status + upstream request id + first 200 chars of any error message.

- [ ] **Step 2: Run the probe**

Run from `sidecar/opencode-proxy/`: `go test -tags liveprobe -run TestLiveProbe_FreeChat -v`. Record the outcome verbatim in the task report. Do NOT run without the tag (it must never enter CI/normal suites — verify with plain `go test ./...` that it compiles out).

- [ ] **Step 3: If shape-403 → stop and report**

No code changes, no stage-2 implementation in this plan. Report to controller for the uTLS decision (spec §5.10 stage 2).

- [ ] **Step 4: Commit**

```bash
git add sidecar/opencode-proxy/probe_test.go
git commit -m "test(sidecar): add live free-lane probe behind liveprobe build tag"
```

---

### Task 9: C# — 3-way error classification, `Blocked` outcome, proxy exit block, quota E2

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatchOutcome.cs` (add record)
- Create: `src/RouterBalancing.Core/Engine/OpencodeGateClassifier.cs`
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:363-364` (refine 403 after `ClassifyFatal`)
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (primary ctor +1 param; Blocked branch before L295; `NoteNetworkBlocked`; extend `IsProviderWideQuotaFailure` L668-675)
- Modify: `src/RouterBalancing.Core/Engine/RetryState.cs:62` (+`BlockedRetries`)
- Modify: `src/RouterBalancing.Core/Proxies/ProxyContext.cs` (+`LastPick`)
- Modify: `src/RouterBalancing.Core/Proxies/IProxyPool.cs`, `ProxyPool.cs` (+`ReportBlocked`, `_blockedUntil`, `BlockedTtl`)
- Modify: `src/RouterBalancing.Core/Proxies/ProxyHealthHandler.cs` (set `LastPick` at the 3 pick sites: global loop L75, round-robin ~L219, fallback ~L268)
- Test: `router balancing test/Engine/OpencodeGateClassifierTests.cs` (new), extend `DispatcherLoopTests.cs`, `ProxyPoolTests` (or the existing pool test file), `ProviderQuotaGateTests.cs` if needed

**Interfaces:**
- Consumes: sidecar response headers/body pass-through (T7) — no protocol change.
- Produces:
  - `public sealed record Blocked(int Status, string? ContentType, byte[] Body) : DispatchOutcome;` in `DispatchOutcome.cs`.
  - `public static class OpencodeGateClassifier` with `public static bool IsIpAbuse(byte[] body)` (lowercase scan: any of `"abusive traffic"`, `"abuse traffic"`, `"abuse of traffic"`, OR both `"free models aren't available"` + `"from your current network"`) and `public static bool IsShapeRefusal(byte[] body)` (`"within OpenCode"`).
  - `void IProxyPool.ReportBlocked(long proxyId, TimeSpan ttl)`; `ProxyPool.BlockedTtl = TimeSpan.FromMinutes(15)`.
  - `DispatcherLoop` primary ctor gains `IProxyPool pool` (DI resolves — already registered in `ProxyApp.ConfigureServices`).

- [ ] **Step 1: Write failing tests for the classifier**

`OpencodeGateClassifierTests.cs`: `TestIsIpAbuse_MarkersCaseInsensitive`, `TestIsIpAbuse_BothPartComboRequired` (`"free models aren't available"` alone → false), `TestIsShapeRefusal_WithinOpenCode`, `TestIsIpAbuse_Plain403_False`.

- [ ] **Step 2: Run to verify fail** → `dotnet test "router balancing test/router balancing test.csproj" --filter OpencodeGateClassifier` — FAIL (type missing).

- [ ] **Step 3: Implement classifier + `DispatchOutcome.Blocked`**

- [ ] **Step 4: Write failing DispatcherLoop/ProxyPool tests**

- `DispatcherLoopTests`: `TestServeAsync_IpAbuse403_ReportsBlockedAndRotatesExitOnce` — fake handler returns `Blocked`; pool has 2 proxies → first attempt uses proxy A (set `ProxyContext.LastPick` in the fake), assert `pool.ReportBlocked(A, 15m)` recorded and a second attempt happens on the same route; with `BlockedRetries` exhausted or single-proxy → request completes with the Blocked outcome and `quotaGate.Report(providerId, …, 15m)` was called.
- `DispatcherLoopTests`: `TestServeAsync_ShapeRefusal403_FailsFastAtProviderLevel` — `Fatal(Provider)` from the refinement path skips remaining same-provider candidates (assert no second attempt on that provider).
- `DispatcherLoopTests` (Review Focus 4): `TestIsProviderWideQuotaFailure_E2Shape_AI_APICallError_InvalidCredential_True` — 429 body containing both markers, no Retry-After → gate opens with fallback TTL (settings default 120s), and a second 429 on the same provider during the window does not produce another upstream attempt.
- Pool tests: `TestReportBlocked_SkipsProxyInGetNext_ReportSuccessDoesNotClear` — blocked proxy is skipped by `GetNext`; calling `ReportSuccess` on it keeps it blocked until TTL; after TTL (fake `TimeProvider`) it returns to rotation.

- [ ] **Step 5: Run to verify fail** → `dotnet test … --filter "DispatcherLoop|ProxyPool"` — FAIL.

- [ ] **Step 6: Implement**

`ProxyRequestHandler`: after L363 `ClassifyFatal`, when `OpenCodeFreeFingerprint.IsApplicable(provider)` and outcome is `Fatal { Status: 403 }` → `IsIpAbuse(errorBody)` ? `new DispatchOutcome.Blocked(403, contentType, errorBody)` : (`IsShapeRefusal` ? log ERROR with `RequestId = ClientKeyItems.RequestIdOf(ctx)` + `new DispatchOutcome.Fatal(FailoverLevel.Provider, 403, contentType, errorBody, retryAfter)` : unchanged).
`DispatcherLoop`: Blocked branch (before the L295 non-retryable check): `pick = ProxyContext.LastPick`; if not null → `pool.ReportBlocked(pick.Id, ProxyPool.BlockedTtl)` and `if (request.Retry.BlockedRetries < 2) { request.Retry.BlockedRetries++; continue; }` (same route — round-robin picks the next non-blocked exit); then (direct egress OR retries exhausted) → `NoteNetworkBlocked(candidate, blocked)` = `quotaGate.Report(provider.Id, new RetryState.Failure(403, blocked.ContentType, blocked.Body, ProxyPool.BlockedTtl), ProxyPool.BlockedTtl)` + Warning log; complete the request with the Blocked outcome.
`ProxyContext.LastPick`: second sticky `AsyncLocal<ProxyAttempt?>`, set in `ProxyHealthHandler` right after each `ProxyContext.Current = pick` (3 sites), never reset.
`ProxyPool`: `_blockedUntil Dictionary<long, DateTimeOffset>` guarded by the existing lock; `IsDown` true when either down OR blocked-not-expired; `ReportSuccess` does NOT remove from `_blockedUntil`; prune expired on access.

- [ ] **Step 7: Run full C# suite + build gate**

Run: `dotnet test "router balancing test/router balancing test.csproj"` then the build gate command. Expected: PASS (ignore the two known flakes if they appear — rerun isolated).

- [ ] **Step 8: Commit**

```bash
git add src/ "router balancing test/"
git commit -m "feat(engine): 3-way OpenCode 403/429 classification with Blocked outcome and proxy exit block"
```

---

### Task 10: C# — fingerprint removal (decision A) + diagnostics replacement

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/OpenCodeFreeFingerprint.cs` → keep ONLY `IsApplicable` (+ class doc updated to "lane selection only; shaping lives in the Go sidecar")
- Modify: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs:72-78` (delete the Apply block)
- Modify: `src/RouterBalancing.Core/Engine/OpenAiUpstreamClient.cs:34-35` (delete `RestoreMapFor` content wrap)
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:279, 438-457` (`WarnOnDecoyToolCalls` → `WarnOnUnknownToolCalls`)
- Test: `router balancing test/Providers/OpenCodeFreeFingerprintTests.cs` (delete tests for removed members; keep `IsApplicable` coverage), `ProxyRequestHandlerTests.cs` (adjust the opencode comment at L44 + add unknown-tool warning test)

**Interfaces:**
- Consumes: T9's classifier is independent of this task.
- Produces: `OpenCodeFreeFingerprint.IsApplicable(Provider)` — the only surviving member, still used by `LaneFor` (L465), T9's refinement gate, and dataset lane logic.
  - `WarnOnUnknownToolCalls(ctx, provider, body, responseBody)`: declared names = `tools[].function.name` parsed from the ORIGINAL client `body`; scan `responseBody` SSE lines for `tool_calls[].function.name` values NOT in the declared set → one Warning per name (same `LogEntry` shape as before, message: model called a tool the client did not declare — sidecar already alias-restored, so this means the model used an official tool with no client counterpart).

- [ ] **Step 1: Write failing test for the new diagnostic**

`ProxyRequestHandlerTests`: `TestForwardAsync_ModelCallsUndeclaredTool_WarnsOncePerName` — fake upstream returns SSE with a tool_call name not in the request's tools; assert a Warning log entry containing that name; and a control case where the called name IS declared → no warning.

- [ ] **Step 2: Run to verify fail** (method missing / old behavior) → `dotnet test … --filter ProxyRequestHandler`.

- [ ] **Step 3: Implement** — swap the diagnostic, delete the fingerprint members and the two call sites (`ProviderRequestFactory` block, `OpenAiUpstreamClient` wrap). Leave a one-line comment at each deleted site pointing to the sidecar as shaping authority.

- [ ] **Step 4: Clean up `OpenCodeFreeFingerprintTests.cs`** — remove every test touching deleted members; keep/adjust `IsApplicable` tests so the class still has coverage.

- [ ] **Step 5: Run full C# suite + build gate** — both green.

- [ ] **Step 6: Commit**

```bash
git add src/ "router balancing test/"
git commit -m "refactor(providers): OpenCode shaping moved to sidecar; fingerprint reduced to lane selection"
```

---

### Task 11: C# — dataset wiring via sidecar headers + backlog/spec amendments

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:312-343` (dataset block: header-driven flags + synthetic skip)
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSkipReason.cs` (+`Synthetic = 4`)
- Modify: `src/RouterBalancing.Core/Dataset/DatasetStats.cs` (+`int SkippedSynthetic`, appended after `SkippedQueueFull` — positional record, single construction site + tests)
- Modify: `src/RouterBalancing.Core/Dataset/DatasetSink.cs` (`CountSkip` L459 → `internal`; stats construction L108-116; counter field)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (`settings.status.datasetStats` EN ~L180 + VN ~L744 — add one placeholder for synthetic)
- Test: extend `DatasetSinkTests.cs`, `DatasetExportIntegrationTests.cs`; new C# test for the header→flag mapping in `ProxyRequestHandlerTests.cs`

**Interfaces:**
- Consumes: T7 response headers `X-Sidecar-Shaped`, `X-Sidecar-Title`.
- Produces: dataset record semantics per spec §7.2 — `tools_rewritten == true` iff the sidecar shaped tools (header contains `tools`); responses tagged `X-Sidecar-Title: synthesized` are NOT recorded and count in `SkippedSynthetic`.

- [ ] **Step 1: Write failing tests**

- `ProxyRequestHandlerTests`: `TestDataset_ToolsRewritten_FromSidecarShapedHeader` — response with `X-Sidecar-Shaped: tools+prompt` → capture's `ToolsRewritten == true`; header absent (non-opencode provider) or `none` → false.
- `DatasetSinkTests`: `TestCountSkip_Synthetic_IncrementsStatsCounter` + stats shape includes the new field.
- Integration test: a request whose response carries both headers (`tools+prompt` + `synthesized`) → no JSONL line written, `Stats.SkippedSynthetic == 1`.

- [ ] **Step 2: Run to verify fail** → `dotnet test … --filter "Dataset|ProxyRequestHandler"`.

- [ ] **Step 3: Implement**

In the dataset block (L312-343): read headers off `response` (in scope): `shaped = response.Headers.TryGetValues("X-Sidecar-Shaped", out …)` joined; `synthetic = X-Sidecar-Title contains "synthesized"`. Replace L329 with `var toolsRewritten = lane != DatasetLane.Plain && shaped.Contains("tools");`. Before `dataset.Capture(…)`: `if (synthetic) { dataset.CountSkip(DatasetSkipReason.Synthetic); } else { …existing Capture… }`. Update the i18n format strings (both languages) with the new counter position.

- [ ] **Step 4: Backlog + spec amendments**

`docs/backlog.md`: mark E1 and E2 `[x]` with a one-line resolution note pointing at this branch. Dataset spec (`docs/superpowers/specs/2026-11-06-dataset-jsonl-export-design.md` §7.4 area): add an amendment note — `tools_rewritten` is now sidecar-header-driven (true for all shaped free-lane requests, false for title-gen and keyed passthrough), plus the new `SkippedSynthetic` counter.

- [ ] **Step 5: Run full C# suite + build gate** — both green.

- [ ] **Step 6: Commit**

```bash
git add src/ "router balancing test/" docs/backlog.md docs/superpowers/specs/2026-11-06-dataset-jsonl-export-design.md
git commit -m "feat(dataset): sidecar-header-driven tools_rewritten and synthesized-title skip"
```

---

## Final verification (after all tasks)

- [ ] `go test ./...` from `sidecar/opencode-proxy/` — green.
- [ ] Build gate + full C# suite — green.
- [ ] Probe report (T8) attached to the branch summary; if shape-403, stage-2 uTLS is filed as a follow-up and NOT silently skipped.
- [ ] Whole-branch review against the spec's §11 decision list (all 10 controller calls + Q1 keyed passthrough implemented).

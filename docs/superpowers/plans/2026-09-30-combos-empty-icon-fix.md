# Combos Empty-State Icon Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sửa emoji empty-state hiện text hỏng (`&#128218;`) bằng ký tự UTF-8 trực tiếp, kèm guard test chống tái phát.

**Architecture:** Razor không decode HTML entity khi truyền component parameter → thay entity bằng ký tự literal. Guard test `RazorParameterEntityTests.cs` đã có (untracked), chỉ cần commit.

**Tech Stack:** .NET 10 / Blazor Hybrid / xUnit

## Global Constraints

- `dotnet test "router balancing test/router balancing test.csproj"` phải xanh
- Commit message tiếng Anh, conventional commit
- Không sửa test logic — chỉ fix source razor
- Nhánh thực thi: `feat/retry-circuit-3c`

Spec: [`docs/superpowers/specs/2026-09-30-combos-empty-icon-fix-design.md`](../specs/2026-09-30-combos-empty-icon-fix-design.md)

---

### Task 1: Fix 2 violations + commit guard test

**Files:**
- Modify: `router-balancing/Components/Pages/Combos.razor:23`
- Modify: `router-balancing/Components/Pages/Providers.razor:27`
- Add (đã có, đang untracked): `router balancing test/Components/RazorParameterEntityTests.cs`

- [ ] **Step 1.** Viết spec + plan doc này.

- [ ] **Step 2. Chạy guard test — kỳ vọng FAIL (red):**

  ```bash
  dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RazorParameterEntityTests
  ```

  Expected: FAIL với 2 violations: `Combos.razor:23`, `Providers.razor:27`

- [ ] **Step 3. Fix cả 2 dòng:**

  `Combos.razor:23`:

  ```razor
  <EmptyState Icon="📖" Message="@L["combos.empty"]" />
  ```

  `Providers.razor:27`:

  ```razor
  <EmptyState Icon="⚙️" Message="@L["providers.empty"]" />
  ```

- [ ] **Step 4. Chạy lại guard test — kỳ vọng PASS:**

  ```bash
  dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RazorParameterEntityTests
  ```

- [ ] **Step 5. Chạy full test suite — kỳ vọng PASS:**

  ```bash
  dotnet test "router balancing test/router balancing test.csproj"
  ```

- [ ] **Step 6. Commit (2 commit nhỏ):**

  ```bash
  git add docs/superpowers/specs/2026-09-30-combos-empty-icon-fix-design.md docs/superpowers/plans/2026-09-30-combos-empty-icon-fix.md
  git commit -m "docs: add spec and plan for empty-state icon fix"
  git add "router-balancing/Components/Pages/Combos.razor" "router-balancing/Components/Pages/Providers.razor" "router balancing test/Components/RazorParameterEntityTests.cs"
  git commit -m "fix: render empty-state icons as literal UTF-8 instead of HTML entities"
  ```

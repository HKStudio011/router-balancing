#!/usr/bin/env bash
# e2e smoke slice 3C (retry, circuit & watchdog) — chạy TAY, không thuộc dotnet test.
# Prerequisites (fail-fast nếu thiếu):
#   1. App đang chạy, proxy tại $BASE (mặc định http://127.0.0.1:8317), MaxRetry = 3 (default)
#   2. node scripts/mock-upstream.mjs 9999 đang chạy (bản 3C có POST /__config)
#   3. 2 provider 'e2e-mock' + 'e2e-mock-2': BaseUrl http://127.0.0.1:9999, type OpenAI,
#      mỗi provider >=1 model enabled id $MODEL_ID_3C (mặc định e2e-mock-3c), >=1 account key.
#      Cùng 1 mock nên thứ tự provider không quan trọng.
#   4. Model e2e-mock-3c CHƯA mở fuse trong app đang chạy — circuit in-memory, chạy lại
#      e2e-3c cần restart app. e2e-3a dùng model khác nên chạy TRƯỚC không ảnh hưởng.
# Cách chạy: bash scripts/e2e-3c.sh   (hoặc MODEL_ID_3C=... BASE=... MOCK=... bash scripts/e2e-3c.sh)
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:8317}"
MOCK="${MOCK:-http://127.0.0.1:9999}"
MODEL_ID_3C="${MODEL_ID_3C:-e2e-mock-3c}"
BODY_FILE="$(mktemp)"
HDR_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE" "$HDR_FILE"' EXIT
FAIL=0

check() { # $1 = tên, $2 = status mong đợi, còn lại = args curl (luôn kèm -D để expect_header)
  local name="$1" expected="$2" actual
  shift 2
  actual=$(curl -s -o "$BODY_FILE" -D "$HDR_FILE" -w '%{http_code}' "$@") || actual=000
  if [[ "$actual" == "$expected" ]]; then
    echo "PASS - $name"
  else
    echo "FAIL - $name (nhận $actual, mong đợi $expected)"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_body() { # $1 = chuỗi cần có trong body, $2 = tên
  if grep -qF "$1" "$BODY_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_header() { # $1 = chuỗi cần có trong header (không phân biệt hoa thường), $2 = tên
  if grep -qiF "$1" "$HDR_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$HDR_FILE"; echo
    FAIL=1
  fi
}

mock_config() { # $1 = số request chat fail (429) đầu tiên
  if ! curl -sf -X POST "$MOCK/__config" -H 'Content-Type: application/json' \
      -d "{\"failFirst\":$1}" >/dev/null; then
    echo "FAIL - mock_config failFirst=$1 (mock chưa chạy bản 3C /__config?)"
    FAIL=1
  fi
}

if ! curl -sf "$BASE/health" >/dev/null; then
  echo "FAIL - proxy không phản hồi tại $BASE/health (app chưa chạy?)"
  exit 1
fi
echo "PASS - proxy alive"

CHAT=( -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json'
  -d "{\"model\":\"$MODEL_ID_3C\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}" )

# Phase 1 — failover: mock fail đúng 1 lần đầu → provider đầu 429, candidate kế 200 (§3.2)
mock_config 1
check "failover: first 429 -> next candidate 200" 200 "${CHAT[@]}"
expect_body 'data:' "failover: client chỉ thấy 200 SSE"

# Phase 2-4 — exhaustion (mock luôn 429): passthrough response cuối + giữ Retry-After (§3.3/§3.6)
# Mỗi request = +1 distinct model → 3 request = đủ MaxRetry mở fuse
mock_config 99
check "exhaustion #1 -> 429 passthrough" 429 "${CHAT[@]}"
expect_body 'rate limited' "exhaustion #1: body provider nguyên"
expect_header 'retry-after: 5' "exhaustion #1: client nhận lại Retry-After"
check "exhaustion #2 -> 429 passthrough" 429 "${CHAT[@]}"
check "exhaustion #3 -> 429 passthrough (fuse mo)" 429 "${CHAT[@]}"

# Phase 5 — gate: đủ MaxRetry=3 → request mới 503 TRƯỚC khi vào queue (§3.4/§4)
check "gate -> 503 before queue" 503 "${CHAT[@]}"
expect_body 'temporarily unavailable' "gate: message 503 mới"
check "gate la 2 -> 503" 503 "${CHAT[@]}"

if [[ $FAIL -eq 0 ]]; then
  echo "ALL PASS"
else
  echo "CÓ TEST FAIL"
  exit 1
fi

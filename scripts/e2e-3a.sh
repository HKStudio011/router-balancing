#!/usr/bin/env bash
# e2e smoke slice 3A (proxy core) — chạy TAY, không thuộc dotnet test.
# Prerequisites (fail-fast nếu thiếu):
#   1. App đang chạy, proxy tại $BASE (mặc định http://127.0.0.1:8317)
#   2. node scripts/mock-upstream.mjs 9999 đang chạy
#   3. Provider 'e2e-mock' trong app: BaseUrl http://127.0.0.1:9999, type OpenAI,
#      >=1 model enabled (id = $MODEL_ID, mặc định e2e-mock-model), >=1 account key enabled
# Cách chạy: bash scripts/e2e-3a.sh   (hoặc MODEL_ID=... BASE=... bash scripts/e2e-3a.sh)
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:8317}"
MODEL_ID="${MODEL_ID:-e2e-mock-model}"
BODY_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE"' EXIT
FAIL=0

check() { # $1 = tên, $2 = status mong đợi, còn lại = args curl
  local name="$1" expected="$2" actual
  shift 2
  actual=$(curl -s -o "$BODY_FILE" -w '%{http_code}' "$@") || actual=000
  if [[ "$actual" == "$expected" ]]; then
    echo "PASS - $name"
  else
    echo "FAIL - $name (nhận $actual, mong đợi $expected)"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_body() { # $1 = chuỗi cần có, $2 = tên
  if grep -qF "$1" "$BODY_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

if ! curl -sf "$BASE/health" >/dev/null; then
  echo "FAIL - proxy không phản hồi tại $BASE/health (app chưa chạy?)"
  exit 1
fi
echo "PASS - proxy alive"

check "GET /health -> 200" 200 "$BASE/health"

check "chat stream -> 200" 200 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d "{\"model\":\"$MODEL_ID\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}"
expect_body 'data:' "SSE có data: chunk"
expect_body '[DONE]' "SSE kết thúc [DONE]"

check "JSON hỏng -> 400" 400 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' -d '{broken'
expect_body 'invalid_request_error' "400 type invalid_request_error"
expect_body 'Invalid JSON body' "400 message"

check "model lạ -> 404" 404 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d '{"model":"no-such-model","messages":[{"role":"user","content":"hi"}]}'
expect_body 'model_not_found' "404 code model_not_found"

check "thiếu messages -> 400" 400 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d "{\"model\":\"$MODEL_ID\"}"
expect_body "'messages'" "400 param messages"

if [[ $FAIL -eq 0 ]]; then
  echo "ALL PASS"
else
  echo "CÓ TEST FAIL"
  exit 1
fi

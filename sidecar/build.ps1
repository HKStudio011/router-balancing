# Build sidecar opencode-proxy.exe cho win-x64 và copy vào runtimes/.
# Run một lần local (user decision 2026-10-14: commit binary, không CI).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$outPath = Join-Path $root "runtimes/win-x64/native/opencode-proxy.exe"
New-Item -ItemType Directory -Force -Path (Split-Path $outPath -Parent) | Out-Null

Push-Location (Join-Path $PSScriptRoot "opencode-proxy")
try {
    $env:GOOS = "windows"
    $env:GOARCH = "amd64"
    $env:CGO_ENABLED = "0"
    $env:GOTOOLCHAIN = "local"
    go build -ldflags="-s -w" -o $outPath .
    if ($LASTEXITCODE -ne 0) { throw "go build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
Write-Host "Built: $outPath ($((Get-Item $outPath).Length) bytes)"

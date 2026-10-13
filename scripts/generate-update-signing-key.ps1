<#
.SYNOPSIS
    Sinh ECDSA P-256 keypair cho release package signing.

.DESCRIPTION
    - Private key (PKCS#8 Base64): copy vào GitHub secret UPDATE_SIGNING_KEY.
      KHÔNG commit vào repo, KHÔNG lưu cạnh source.
    - Public key (SPKI Base64): paste vào PackageSignatureVerifier.ReleasePublicKeyBase64.

    Chỉ cần chạy 1 lần; mất private key → regenerate rồi update public key
    và mọi client đã build với public key cũ sẽ từ chối update (fail-closed).

.EXAMPLE
    ./generate-update-signing-key.ps1
#>
$ErrorActionPreference = 'Stop'

$ecdsa = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $privateKey = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey())
    $publicKey  = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())

    Write-Host ''
    Write-Host '=== PRIVATE KEY (GitHub secret UPDATE_SIGNING_KEY — KHÔNG commit) ===' -ForegroundColor Red
    Write-Host $privateKey -ForegroundColor Yellow
    Write-Host ''
    Write-Host '=== PUBLIC KEY (paste vào PackageSignatureVerifier.ReleasePublicKeyBase64) ===' -ForegroundColor Green
    Write-Host $publicKey -ForegroundColor Green
    Write-Host ''
    Write-Host 'Private key chỉ hiển thị trên màn hình — copy ngay rồi xóa khỏi scrollback.' -ForegroundColor DarkGray
}
finally {
    $ecdsa.Dispose()
}

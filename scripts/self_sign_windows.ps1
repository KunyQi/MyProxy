[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [string]$OutputDirectory,
    [string]$ArchivePath,
    [string]$SecretsDirectory,
    [string]$Subject = "CN=MyProxy Development Self-Signed"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function New-RandomSecureString {
    $bytes = New-Object byte[] 48
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    $plainText = [Convert]::ToBase64String($bytes)
    try {
        return ConvertTo-SecureString -String $plainText -AsPlainText -Force
    }
    finally {
        $plainText = $null
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-OrCreateSigningCertificate {
    param([Parameter(Mandatory = $true)][string]$StoreSubject)

    $pfxPath = Join-Path $script:SecretsRoot "MyProxy-Development-SelfSigned.pfx"
    $passwordPath = Join-Path $script:SecretsRoot "MyProxy-Development-SelfSigned.pfx-password.dpapi"
    $cerPath = Join-Path $script:SecretsRoot "MyProxy-Development-SelfSigned.cer"
    $metadataPath = Join-Path $script:SecretsRoot "MyProxy-Development-SelfSigned.json"

    $certificate = Get-ChildItem -Path "Cert:\CurrentUser\My" |
        Where-Object {
            $_.Subject -eq $StoreSubject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30)
        } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $certificate -and (Test-Path -LiteralPath $pfxPath)) {
        if (-not (Test-Path -LiteralPath $passwordPath)) {
            throw "PFX exists but its DPAPI-protected password file is missing: $passwordPath"
        }
        $securePassword = Get-Content -LiteralPath $passwordPath -Raw | ConvertTo-SecureString
        $certificate = Import-PfxCertificate `
            -FilePath $pfxPath `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -Password $securePassword `
            -Exportable
    }

    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $StoreSubject `
            -FriendlyName "MyProxy Development Self-Signed Code Signing" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy Exportable `
            -NotAfter (Get-Date).AddYears(2)
    }

    if (-not (Test-Path -LiteralPath $pfxPath)) {
        if (Test-Path -LiteralPath $passwordPath) {
            throw "Refusing to reuse a password file without its matching PFX: $passwordPath"
        }
        $securePassword = New-RandomSecureString
        $protectedPassword = ConvertFrom-SecureString -SecureString $securePassword
        [System.IO.File]::WriteAllText($passwordPath, $protectedPassword, [System.Text.Encoding]::ASCII)
        Export-PfxCertificate `
            -Cert $certificate `
            -FilePath $pfxPath `
            -Password $securePassword `
            -ChainOption EndEntityCertOnly | Out-Null
    }

    Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null
    [ordered]@{
        subject = $certificate.Subject
        thumbprint = $certificate.Thumbprint
        not_before = $certificate.NotBefore.ToUniversalTime().ToString("o")
        not_after = $certificate.NotAfter.ToUniversalTime().ToString("o")
        purpose = "Temporary development self-signing only; not publicly trusted"
    } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

    return $certificate
}

$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$outputRoot = if ($OutputDirectory) {
    Get-AbsolutePath $OutputDirectory
}
else {
    "$sourceRoot-selfsigned"
}
$archive = if ($ArchivePath) {
    Get-AbsolutePath $ArchivePath
}
else {
    "$outputRoot.zip"
}
$resolvedSecretsDirectory = if ($SecretsDirectory) {
    $SecretsDirectory
}
else {
    Join-Path $PSScriptRoot "..\secrets\code-signing"
}
$script:SecretsRoot = Get-AbsolutePath $resolvedSecretsDirectory

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "MyProxy.exe") -PathType Leaf)) {
    throw "MyProxy.exe is missing from source directory: $sourceRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "MyProxy.dll") -PathType Leaf)) {
    throw "MyProxy.dll is missing from source directory: $sourceRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "Core\xray.exe") -PathType Leaf)) {
    throw "Core/xray.exe is missing from source directory: $sourceRoot"
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists; remove it manually or choose another path: $outputRoot"
}
if (Test-Path -LiteralPath $archive) {
    throw "Archive already exists; remove it manually or choose another path: $archive"
}

New-Item -ItemType Directory -Path $script:SecretsRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceRoot -Destination $outputRoot -Recurse

$notice = @"
MyProxy temporary self-signed build

MyProxy.exe and MyProxy.dll are signed with a project-generated development certificate.
The certificate is not issued by a publicly trusted CA, so Windows and antivirus products may
still warn or block this build on machines that do not explicitly trust the certificate.

Core/xray.exe is an upstream third-party binary. It is deliberately not re-signed as MyProxy.
The private signing key is not included in this package.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot "SIGNING-NOTICE.txt"),
    $notice.Trim() + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)

try {
    $certificate = Get-OrCreateSigningCertificate -StoreSubject $Subject
    $targets = @(
        (Join-Path $outputRoot "MyProxy.dll"),
        (Join-Path $outputRoot "MyProxy.exe")
    )
    foreach ($target in $targets) {
        $result = Set-AuthenticodeSignature `
            -LiteralPath $target `
            -Certificate $certificate `
            -HashAlgorithm SHA256 `
            -IncludeChain NotRoot
        $status = $result.Status.ToString()
        if ($null -eq $result.SignerCertificate -or
            $result.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $status -notin @("Valid", "UnknownError", "NotTrusted")) {
            throw "Authenticode signing failed for $target (status=$($result.Status))"
        }
    }

    foreach ($target in $targets) {
        $signature = Get-AuthenticodeSignature -LiteralPath $target
        $status = $signature.Status.ToString()
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $status -notin @("Valid", "UnknownError", "NotTrusted")) {
            throw "Authenticode verification failed for $target (status=$($signature.Status))"
        }
        Write-Output "SIGNED=$target status=$($signature.Status) thumbprint=$($certificate.Thumbprint)"
    }

    Compress-Archive -LiteralPath $outputRoot -DestinationPath $archive -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$archive.sha256",
        "$archiveHash  $([System.IO.Path]::GetFileName($archive))`n",
        [System.Text.Encoding]::ASCII
    )

    Write-Output "CERTIFICATE_SUBJECT=$($certificate.Subject)"
    Write-Output "CERTIFICATE_THUMBPRINT=$($certificate.Thumbprint)"
    Write-Output "CERTIFICATE_NOT_AFTER=$($certificate.NotAfter.ToUniversalTime().ToString('o'))"
    Write-Output "ARCHIVE=$archive"
    Write-Output "SHA256=$archiveHash"
    Write-Warning "This is a self-signed development build. It is not publicly trusted and does not guarantee antivirus acceptance."
}
catch {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    if (Test-Path -LiteralPath "$archive.sha256") {
        Remove-Item -LiteralPath "$archive.sha256" -Force
    }
    throw
}

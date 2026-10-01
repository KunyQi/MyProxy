param(
    [string]$OutputDirectory,
    [string]$PythonExecutable,
    [string]$DotnetExecutable
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DotnetExecutable)) {
    $DotnetExecutable = $env:MYPROXY_DOTNET
}
if ([string]::IsNullOrWhiteSpace($DotnetExecutable)) {
    $localSdk = Join-Path $repoRoot '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) {
        $DotnetExecutable = $localSdk
    } else {
        $DotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$dotnet = $DotnetExecutable
$project = Join-Path $repoRoot 'windows\MyProxy\MyProxy.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'dist\MyProxy-portable-win-x64'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK is missing: $dotnet"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Output already exists; choose a new directory: $OutputDirectory"
}

if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = (Get-Command python -ErrorAction Stop).Source
}

& $PythonExecutable (Join-Path $repoRoot 'scripts\release_verify.py') check --require-configured
if ($LASTEXITCODE -ne 0) { throw 'Release input verification failed.' }

& $dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable GUI publish failed.' }

$published = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse)
if ($published.Count -ne 1 -or $published[0].Name -ne 'MyProxy.exe') {
    throw "Portable GUI publish must contain only MyProxy.exe; found $($published.Count) files."
}

$exe = $published[0]
$hash = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "PORTABLE_GUI_EXE=$($exe.FullName)"
Write-Output "SHA256=$hash"

[CmdletBinding()]
param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$Version = "0.3.4",
  [string]$DotNet = "dotnet",
  [string]$Iscc = ""
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publish = Join-Path $root "artifacts\publish"
$output = Join-Path $root "outputs"

& $DotNet test (Join-Path $root "AgentRelay.sln") `
  -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
  throw "dotnet test failed with exit code $LASTEXITCODE."
}

& $DotNet publish (Join-Path $root "src\AgentRelay.App\AgentRelay.App.csproj") `
  -c $Configuration `
  -r $Runtime `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:Version=$Version `
  -o $publish `
  --nologo
if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ([string]::IsNullOrWhiteSpace($Iscc)) {
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    (Join-Path $root "work\inno\ISCC.exe")
  )
  $Iscc = $candidates | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf)
  } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($Iscc) -or -not (Test-Path -LiteralPath $Iscc -PathType Leaf)) {
  throw "Inno Setup 7.0.2 compiler not found. Run scripts\bootstrap-inno.ps1 or pass -Iscc."
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
& $Iscc `
  "/DMyAppVersion=$Version" `
  "/DPublishDir=$publish" `
  "/DOutputDir=$output" `
  (Join-Path $root "installer\AgentRelay.iss")
if ($LASTEXITCODE -ne 0) {
  throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$artifact = Join-Path $output "AgentRelaySetup-x64.exe"
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
  throw "Expected installer was not created: $artifact"
}

$checksum = "$artifact.sha256"
$hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
Set-Content -LiteralPath $checksum -Value "$hash  AgentRelaySetup-x64.exe" -Encoding ascii
Get-FileHash -Algorithm SHA256 -LiteralPath $artifact

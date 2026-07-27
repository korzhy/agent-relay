[CmdletBinding()]
param(
  [string]$Destination = ""
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Destination)) {
  $Destination = Join-Path $root "work\inno"
}
$version = Get-Content -Raw -LiteralPath (Join-Path $root "installer\inno-version.json") |
  ConvertFrom-Json
$download = Join-Path $root "work\innosetup-$($version.version)-$($version.architecture).exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $download) -Force | Out-Null
Invoke-WebRequest -Uri $version.url -OutFile $download

$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "Pyrsys") {
  throw "Inno Setup signature validation failed: $($signature.Status) $($signature.StatusMessage)"
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$process = Start-Process `
  -FilePath $download `
  -ArgumentList @(
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/CURRENTUSER",
    "/DIR=`"$Destination`""
  ) `
  -WindowStyle Hidden `
  -Wait `
  -PassThru
if ($process.ExitCode -ne 0) {
  throw "Inno Setup bootstrap failed with exit code $($process.ExitCode)."
}

$compiler = Join-Path $Destination "ISCC.exe"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
  throw "ISCC.exe was not installed at $compiler."
}
Write-Output $compiler

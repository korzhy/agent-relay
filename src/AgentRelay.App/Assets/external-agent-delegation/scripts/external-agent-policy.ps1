[CmdletBinding()]
param(
  [ValidateSet("get", "set-level")]
  [string]$Action = "get",
  [ValidateSet("off", "low", "medium", "high")]
  [string]$Level,
  [string]$GlobalConfigPath = "$HOME\.codex\external-agent-delegation.json"
)

$ErrorActionPreference = "Stop"

if ($Action -eq "set-level") {
  if ([string]::IsNullOrWhiteSpace($Level)) {
    throw "-Level is required."
  }
  $value = [ordered]@{
    schemaVersion = 1
    enabled = ($Level -ne "off")
    level = $Level
    preferredExecutor = [ordered]@{
      provider = "Antigravity"
      model = "latest-gemini-flash-high"
    }
    updatedAt = (Get-Date).ToUniversalTime().ToString("o")
  }
  $directory = Split-Path -Parent $GlobalConfigPath
  New-Item -ItemType Directory -Path $directory -Force | Out-Null
  $temporary = Join-Path $directory (
    "." + [IO.Path]::GetFileName($GlobalConfigPath) + "." + [guid]::NewGuid().ToString("N") + ".tmp"
  )
  try {
    [IO.File]::WriteAllText(
      $temporary,
      ($value | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
      [Text.UTF8Encoding]::new($false)
    )
    Move-Item -LiteralPath $temporary -Destination $GlobalConfigPath -Force
  }
  finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
  }
}

if (-not (Test-Path -LiteralPath $GlobalConfigPath -PathType Leaf)) {
  throw "Policy is not installed: $GlobalConfigPath"
}

Get-Content -LiteralPath $GlobalConfigPath -Raw | ConvertFrom-Json |
  ConvertTo-Json -Depth 10

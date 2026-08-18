param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot
$distribution = Join-Path $workspace "dist"
$plugin = Join-Path $distribution "user\mods\CrossRagfair"
$hub = Join-Path $distribution "hub-linux"

dotnet build (Join-Path $workspace "MutiplayRagfair.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

New-Item -ItemType Directory -Path $plugin, $hub -Force | Out-Null

$sptOutput = Join-Path $workspace "src\CrossRagfair.Spt\bin\$Configuration\net9.0"
Copy-Item -LiteralPath (Join-Path $sptOutput "CrossRagfair.Spt.dll") -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $sptOutput "CrossRagfair.Contracts.dll") -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $sptOutput "CrossRagfair.Core.dll") -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $workspace "src\CrossRagfair.Spt\config.json") -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $workspace "LICENSE") -Destination $plugin -Force

dotnet publish (Join-Path $workspace "src\CrossRagfair.Hub\CrossRagfair.Hub.csproj") -c $Configuration --no-self-contained -p:UseAppHost=false -o $hub
if ($LASTEXITCODE -ne 0) { throw "Hub publish failed." }
Copy-Item -LiteralPath (Join-Path $workspace "LICENSE") -Destination $hub -Force
Copy-Item -LiteralPath (Join-Path $workspace "deploy\linux\crossragfair-hub.service") -Destination $hub -Force
Copy-Item -LiteralPath (Join-Path $workspace "deploy\linux\hub.env.example") -Destination $hub -Force

$forbidden = Get-ChildItem -LiteralPath $plugin -File | Where-Object {
    $_.Name -like "SPTarkov.*" -or $_.Name -like "SPT.Server*" -or $_.Name -eq "0Harmony.dll"
}
if ($forbidden) { throw "Forbidden server-owned dependency found in plugin package: $($forbidden.Name -join ', ')" }

Write-Host "Package created at $distribution"

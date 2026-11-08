<#
.SYNOPSIS  Publishes DynamicIsland.exe to artifacts\publish.
.DESCRIPTION
  Default: self-contained single-file x64 exe (no .NET runtime needed on the target PC).
  -FrameworkDependent: small exe that requires the .NET 8 Desktop Runtime.
.EXAMPLE   .\build\publish.ps1
.EXAMPLE   .\build\publish.ps1 -FrameworkDependent
#>
param(
    [switch] $FrameworkDependent,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WindowsDynamicIsland\WindowsDynamicIsland.csproj'
$out     = Join-Path $root 'artifacts\publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$dotnetArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $out,
    '-p:PublishSingleFile=true',
    '-p:DebugType=none'
)
if ($FrameworkDependent) {
    $dotnetArgs += '--self-contained', 'false'
} else {
    $dotnetArgs += '--self-contained', 'true',
             '-p:IncludeNativeLibrariesForSelfExtract=true',
             '-p:EnableCompressionInSingleFile=true'
}

Write-Host "==> dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
dotnet @dotnetArgs
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$exe = Join-Path $out 'DynamicIsland.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green
Write-Host "Установщик: откройте installer\DynamicIsland.iss в Inno Setup 6 и нажмите Compile."

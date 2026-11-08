<#
.SYNOPSIS  Restore, build (Release) and run the unit tests.
.EXAMPLE   .\build\build.ps1
.EXAMPLE   .\build\build.ps1 -Configuration Debug -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln  = Join-Path $root 'WindowsDynamicIsland.sln'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
}

Write-Host "==> restore" -ForegroundColor Cyan
dotnet restore $sln
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Write-Host "==> build ($Configuration)" -ForegroundColor Cyan
dotnet build $sln -c $Configuration --no-restore
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "==> test" -ForegroundColor Cyan
    dotnet test $sln -c $Configuration --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

Write-Host "Готово." -ForegroundColor Green

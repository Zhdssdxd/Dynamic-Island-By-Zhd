@echo off
rem Builds, collects warnings and runs unit tests; output goes to build\test.log.
cd /d "%~dp0.."
set DOTNET_CLI_UI_LANGUAGE=en
set VSLANG=1033
taskkill /IM DynamicIsland.exe /F >nul 2>&1
echo === %date% %time% > build\test.log
dotnet build WindowsDynamicIsland.sln -c Debug -nologo --no-incremental -v quiet "-clp:NoSummary" >> build\test.log 2>&1
dotnet test WindowsDynamicIsland.sln -c Debug --no-build -nologo >> build\test.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build\test.log
echo DONE >> build\test.log

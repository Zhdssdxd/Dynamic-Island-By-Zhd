@echo off
rem Compiles the solution and writes the result to build\build.log (used for remote build checks).
cd /d "%~dp0.."
set DOTNET_CLI_UI_LANGUAGE=en
set VSLANG=1033
taskkill /IM DynamicIsland.exe /F >nul 2>&1
echo === %date% %time% > build\build.log
dotnet --list-sdks >> build\build.log 2>&1
dotnet build WindowsDynamicIsland.sln -c Debug -nologo -v quiet "-clp:ErrorsOnly;NoSummary" >> build\build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build\build.log
echo DONE >> build\build.log

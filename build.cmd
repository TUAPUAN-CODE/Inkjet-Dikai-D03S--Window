@echo off
rem build + test + copy exe to dist\   (requires .NET SDK 8)
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build src\DikaiD03S\DikaiD03S.csproj -c Release || goto :err
dotnet build tools\PrinterSimulator\PrinterSimulator.csproj -c Release || goto :err
dotnet run --project tests\Protocol.Tests\Protocol.Tests.csproj -c Release || goto :err
if not exist dist mkdir dist
copy /y src\DikaiD03S\bin\Release\net48\DikaiD03S.exe dist\ >nul
copy /y tools\PrinterSimulator\bin\Release\net48\PrinterSimulator.exe dist\ >nul
echo OK -^> dist\
exit /b 0
:err
echo BUILD FAILED
exit /b 1

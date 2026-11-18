#!/usr/bin/env bash
# build + test + copy exe ไป dist/   (ต้องมี .NET SDK 8)
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build src/DikaiD03S/DikaiD03S.csproj -c Release
dotnet build tools/PrinterSimulator/PrinterSimulator.csproj -c Release
dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj -c Release
mkdir -p dist
cp src/DikaiD03S/bin/Release/net48/DikaiD03S.exe dist/
cp tools/PrinterSimulator/bin/Release/net48/PrinterSimulator.exe dist/
echo "OK -> dist/"

@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Building from source requires the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  echo The prebuilt StorageXray.exe does not require the SDK.
  pause
  exit /b 1
)
dotnet run --project tests/StorageXray.Tests/StorageXray.Tests.csproj -c Release
if errorlevel 1 exit /b 1
dotnet publish src/StorageXray.App/StorageXray.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o release/StorageXray
if errorlevel 1 exit /b 1
echo Built release\StorageXray\StorageXray.exe
pause

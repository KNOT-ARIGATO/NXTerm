# Builds both downloads into .\publish, the same way the GitHub release workflow does:
#   NXTerm.exe             small, needs the .NET 10 Desktop Runtime
#   NXTerm-standalone.exe  carries its own .NET runtime, needs nothing installed
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'NXTerm\NXTerm.csproj'
$out = Join-Path $root 'publish'
$tmp = Join-Path $root 'publish\_standalone'
$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:DebugType=none', '-nologo', '-v', 'q')

dotnet publish $project @common -p:SelfContained=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
dotnet publish $project @common -p:SelfContained=true -p:Flavor=standalone `
    -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $tmp
if ($LASTEXITCODE -ne 0) { throw 'standalone publish failed' }
Move-Item (Join-Path $tmp 'NXTerm.exe') (Join-Path $out 'NXTerm-standalone.exe') -Force
Remove-Item -LiteralPath $tmp -Recurse -Force

Get-ChildItem $out -Filter *.exe | ForEach-Object { '{0,-24} {1,6:N1} MB   v{2}' -f $_.Name, ($_.Length / 1MB), $_.VersionInfo.ProductVersion }

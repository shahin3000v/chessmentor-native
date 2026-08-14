$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts\publish\win-x64'

dotnet restore (Join-Path $repo 'ChessMentor.sln')
dotnet test (Join-Path $repo 'ChessMentor.sln') -c Release --no-restore
dotnet publish (Join-Path $repo 'src\ChessMentor.Desktop\ChessMentor.Desktop.csproj') `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $output

Write-Host "Published to $output"

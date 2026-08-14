[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$solution = Join-Path $repo 'ChessMentor.sln'
$desktopProject = Join-Path $repo 'src\ChessMentor.Desktop\ChessMentor.Desktop.csproj'
$testProject = Join-Path $repo 'tests\ChessMentor.Tests\ChessMentor.Tests.csproj'
$logDirectory = Join-Path $repo 'artifacts\verification\phase3'

function Invoke-DotNetGate {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $logPath = Join-Path $logDirectory ("{0}.log" -f $Name)
    Write-Host "`n[$Name] dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments *>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Phase 3 $Name failed with exit code $exitCode. Full log: $logPath"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found. Install the .NET 10 SDK and the Visual Studio .NET desktop development workload.'
}

$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $sdkVersion.StartsWith('10.')) {
    throw "Phase 3 requires the .NET 10 SDK. Detected version: $sdkVersion"
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Set-Location $repo

try {
    # WPF RangeBase.Value binds TwoWay by default. This guard prevents a build-
    # clean but runtime-fatal binding to the read-only progress property.
    $studioMarkup = Get-Content (Join-Path $repo 'src\ChessMentor.Desktop\StudioWindow.xaml') -Raw
    $safeProgressBinding = 'Value="{Binding TranslationPercentage, Mode=OneWay}"'
    if (-not $studioMarkup.Contains($safeProgressBinding)) {
        throw 'Studio TranslationPercentage ProgressBar must use an explicit OneWay binding.'
    }

    $viewerMarkup = Get-Content (Join-Path $repo 'src\ChessMentor.Desktop\MainWindow.xaml') -Raw
    $fullWidthCommentRow = '<ContentControl Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="4"'
    if (-not $studioMarkup.Contains($fullWidthCommentRow) -or
        -not $viewerMarkup.Contains($fullWidthCommentRow)) {
        throw 'Viewer and Studio comments must render in an independent full-width row.'
    }

    Invoke-DotNetGate -Name 'restore' -Arguments @('restore', $solution)
    Invoke-DotNetGate -Name 'build' -Arguments @('build', $solution, '-c', $Configuration, '--no-restore')
    Invoke-DotNetGate -Name 'test' -Arguments @('test', $testProject, '-c', $Configuration, '--no-build', '--no-restore')

    Write-Host "`nPhase 3 build and tests passed." -ForegroundColor Green
    Write-Host "Logs: $logDirectory" -ForegroundColor Green

    if (-not $NoLaunch) {
        Write-Host 'Launching ChessMentor Desktop. Use the PGN Studio button in the header.' -ForegroundColor Green
        & dotnet run --project $desktopProject -c $Configuration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "ChessMentor Desktop exited with code $LASTEXITCODE."
        }
    }
}
catch {
    Write-Host "`nPHASE 3 ACCEPTANCE FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Send the matching log from: $logDirectory" -ForegroundColor Yellow
    exit 1
}

exit 0

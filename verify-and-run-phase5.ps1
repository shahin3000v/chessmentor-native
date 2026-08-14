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
$logDirectory = Join-Path $repo 'artifacts\verification\phase5'

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
        throw "Phase 5 $Name failed with exit code $exitCode. Full log: $logPath"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found. Install the .NET 10 SDK and the Visual Studio .NET desktop development workload.'
}

$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $sdkVersion.StartsWith('10.')) {
    throw "Phase 5 requires the .NET 10 SDK. Detected version: $sdkVersion"
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Set-Location $repo

try {
    $trainerMarkup = Get-Content (Join-Path $repo 'src\ChessMentor.Desktop\MoveTrainerWindow.xaml') -Raw
    foreach ($requiredText in @(
        'ChessBoardControl',
        'AcceptedMovesEditor',
        'DailyNewLimit',
        'RetryMistakesCommand'
    )) {
        if (-not $trainerMarkup.Contains($requiredText)) {
            throw "MoveTrainer native acceptance marker is missing: $requiredText"
        }
    }

    $migrationSource = Get-Content (Join-Path $repo 'src\ChessMentor.Persistence\DatabaseMigrator.cs') -Raw
    if (-not $migrationSource.Contains('CurrentVersion = 4') -or
        -not $migrationSource.Contains('CREATE TABLE practice_cards')) {
        throw 'MoveTrainer SQLite v4 migration is missing.'
    }

    Invoke-DotNetGate -Name 'restore' -Arguments @('restore', $solution)
    Invoke-DotNetGate -Name 'build' -Arguments @('build', $solution, '-c', $Configuration, '--no-restore')
    Invoke-DotNetGate -Name 'test' -Arguments @('test', $testProject, '-c', $Configuration, '--no-build', '--no-restore')

    Write-Host "`nPhase 5 build and tests passed." -ForegroundColor Green
    Write-Host "Logs: $logDirectory" -ForegroundColor Green

    if (-not $NoLaunch) {
        Write-Host 'Launching ChessMentor Desktop. Use the MoveTrainer button in the header.' -ForegroundColor Green
        & dotnet run --project $desktopProject -c $Configuration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "ChessMentor Desktop exited with code $LASTEXITCODE."
        }
    }
}
catch {
    Write-Host "`nPHASE 5 ACCEPTANCE FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Send the matching log from: $logDirectory" -ForegroundColor Yellow
    exit 1
}

exit 0

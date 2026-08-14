# ChessMentor Native Desktop

Native Windows port of **PGN Persian Chess Studio / ChessMentor**, based on source snapshot:

- Repository: `shahin3000v/pgn-persian-chess-studio-final`
- Branch: `agent/add-movetrainer-course-builder`
- Commit: `c3af1d5599b6f2da537414b77be8987b6ff06ed3`

This repository is intentionally phase-based. Branch `phase-3` is the **Phase 3 Windows acceptance candidate**: it keeps the accepted Viewer 1 work and adds native PGN Studio, authored/lossless PGN editing, local Draft revisions, server-connected translation, offline caches/queues, publishing metadata and move-linked audio. It becomes the accepted Phase 3 delivery only after `verify-and-run-phase3.ps1` passes on Windows. Viewer 2, Trainer and Course workflows remain assigned to their later phases.

## Phase 2 Viewer 1

- Opens or appends multiple PGN files without reparsing the current workspace.
- Resolves SAN to legal UCI/FEN on worker threads, including castling, en-passant, promotion and disambiguation.
- Preserves nested variations, comments, NAGs, headers, multi-game structure and original token text.
- Supports previous/next move, previous/next game, explicit branch selection, mainline selection, mouse board input and keyboard navigation.
- Uses recycling virtualization for game/move lists and changes only old/new active rows during navigation.
- Persists board skin, coordinates, header/game-panel state, panel widths, notation, comment size, panel mode and move sound in SQLite.

## Phase 3 Studio and translation

- Opens/appends multi-game PGN and supports native board authoring, comments, nested branches, branch promotion/deletion and deterministic edited-PGN export.
- Preserves stable game/node/source identities in versioned Draft packages and immutable local SQLite revisions.
- Reuses the current FastAPI server for session/login, categories, Draft save/resume, publish, TM preflight, translation, TM propagation and move audio.
- Uses local translation cache first, bounded/cancellable server workers second, and a durable transient backlog when connectivity fails.
- Includes a virtualized Translation DB editor, exact usage mapping, featured images, credits/category metadata, custom comment fonts and public/private move audio.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio with the .NET desktop development workload, or the .NET CLI

## Build and run

Recommended Phase 3 verification and launch:

```powershell
.\verify-and-run-phase3.ps1
```

The script writes restore, build and test logs to `artifacts/verification/phase3` and launches the app only when all mandatory gates pass. Select **PGN Studio** in the Viewer header to open Studio.

Equivalent manual commands:

```powershell
dotnet restore .\ChessMentor.sln
dotnet build .\ChessMentor.sln -c Debug
dotnet test .\ChessMentor.sln -c Debug
dotnet run --project .\src\ChessMentor.Desktop\ChessMentor.Desktop.csproj
```

## Self-contained Windows x64 publish

```powershell
.\scripts\publish-windows-x64.ps1
```

Output is written to `artifacts/publish/win-x64`.

## Key engineering decisions

- WPF rendering is native. There is no WebView, Electron, DOM, or embedded browser.
- `ChessBoardControl` derives from `FrameworkElement`, draws 64 equal squares directly, and caches piece bitmaps once.
- PGN source-of-truth is a custom token-preserving parser/AST. A managed rules adapter enriches it with UCI/FEN but never owns serialization.
- SQLite calls are serialized and moved off the UI thread. This is deliberate because `Microsoft.Data.Sqlite` async methods execute synchronously underneath.
- All long-running service contracts accept `CancellationToken`.

Start with [Phase 0 source inventory](docs/PHASE-0-SOURCE-INVENTORY.md), [parity map](docs/PARITY-MAP.md), [architecture](docs/ARCHITECTURE.md), and [Phase 3 verification](docs/PHASE-3-VERIFICATION.md).

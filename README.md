# ChessMentor Native Desktop

Native Windows port of **PGN Persian Chess Studio / ChessMentor**, based on source snapshot:

- Repository: `shahin3000v/pgn-persian-chess-studio-final`
- Branch: `ext-course-builder`
- Commit: `620ae69a9f75d1262453cc5a1ae2953fdf63001a`

This repository is intentionally phase-based. Branch `phase-5` is the **Phase 5 Windows acceptance candidate**. Branch `phase-6` continues from it with the native Course Builder checkpoints. Viewer 2 remains intentionally skipped by product decision.

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

## Phase 5 MoveTrainer

- Builds editable training courses from one or more multi-game PGN files while retaining stable course/game/node identities.
- Supports primary, alternate and soft-fail answers, text hints, wrong-move feedback and transposition-aware acceptance.
- Uses due-first daily queues with configurable new/review/session limits and White/Black/Both filtering.
- Persists attempts, wrong-piece context, cards, reviews, active sessions, statistics and deterministic no-fuzz FSRS state in SQLite schema v4.
- Preserves and imports legacy `fsrs_state` rows without deleting or resetting the original progress.
- Runs on the same native cached `ChessBoardControl` used by Viewer and Studio.

## Phase 6 Course Builder checkpoint

- Adds the native four-panel Sources / Course Canvas / Inspector / Preview workspace.
- Loads multi-game PGN sources in the background and supports drag/drop into Canvas.
- Persists stable blocks, source references, Play behavior and LEGO Text attachments.
- Includes duplicate/delete/reorder, undo/redo, debounced autosave and immutable SQLite revisions.
- Uses the shared native board in Preview; this checkpoint is runnable but is not final Phase 6 acceptance.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio with the .NET desktop development workload, or the .NET CLI

## Build and run

Recommended Phase 5 verification and launch:

```powershell
.\verify-and-run-phase5.ps1
```

The script writes restore, build and test logs to `artifacts/verification/phase5` and launches the app only when all mandatory gates pass. Select **MoveTrainer** in the Viewer header.

For the Phase 6 Course Builder checkpoint:

```powershell
.\verify-and-run-phase6.ps1
```

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

Start with [Phase 0 source inventory](docs/PHASE-0-SOURCE-INVENTORY.md), [source delta](docs/SOURCE-DELTA-620AE69.md), [parity map](docs/PARITY-MAP.md), [architecture](docs/ARCHITECTURE.md), and [Phase 5 verification](docs/PHASE-5-VERIFICATION.md).

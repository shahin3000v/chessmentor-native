# Phase 2 Windows acceptance candidate — Viewer 1

This document records implemented scope, not a claim that a Windows build has already passed. Run the root `verify-and-run-phase2.ps1`; only a zero-error build and passing test run promote this candidate to the accepted Phase 2 delivery.

## Delivered

- `ChessMentor.Viewer` domain project and runnable WPF Viewer 1 composition.
- Background multi-file load/append with UTF-8 BOM, strict UTF-8 and Windows-1252 decoding.
- Managed legal-move/SAN/UCI/FEN implementation with king-safety, castling, en-passant, promotion and disambiguation.
- Non-destructive semantic PGN enrichment and stable transposition-group identity.
- Multi-game selection, exact single/bulk deletion, previous/next game and current-game preservation during append.
- Previous/next move, mainline sibling and explicit multi-branch chooser matching Viewer 1 keyboard semantics.
- Direct native board interaction restricted to legal continuations already present in the viewed PGN.
- Recycled virtualized move/game lists, incremental active-row state and active-move reveal.
- All/training/mobile move-panel modes, RTL comments with isolated LTR move notation, letters/figurines and Latin display digits.
- Persisted header/game-panel collapse, splitter widths, skin, coordinates, notation, panel mode, comment size and sound.
- Pre-generated/reused native PCM move sounds and extended debug metrics for parse and semantic resolution.

## Source reconciliation notes

| Snapshot behavior | Native Phase 2 behavior |
|---|---|
| `first` / `last` controls change games | Previous/next-game commands; Home/End keep the same behavior |
| Left/right traverse move parent/child | Native commands and global keyboard routing |
| Multiple children open branch canvas | Modal native branch list; Up/Down, Enter, Right and Escape are handled |
| Append parses only picked files and preserves current node | `ViewerDocumentLoader` loads only new paths; `ViewerSession.Append` preserves object/node references |
| Move panel modes `all`, `active`, `mobile` | All, Training and Mobile native presentations with SQLite persistence |
| Active move reveal and independent panel scroll | Virtualized ListBox selection reveal; no page/window scrolling |
| Game list body and SAN use LTR inside Persian UI | Explicit LTR layout boundaries; the chessboard also remains physically LTR |
| Synthesized move sounds with persistent toggle | Cached PCM/`MediaPlayer` service with persistent toggle |

## Regression coverage added

- Initial legal move count and SAN/UCI/FEN resolution.
- Castling, en-passant, four promotions, SAN disambiguation and pinned-piece legality.
- Nested variation UCI/FEN enrichment, transposition identity and non-destructive invalid-SAN diagnostics.
- Branch-selection navigation, append reference preservation, variation flattening/black numbering and exact game removal.
- UTF-8 BOM/Windows-1252 decoding, Latin comment digits and Phase 2 display-setting persistence.

## Verification commands on Windows

Preferred:

```powershell
.\verify-and-run-phase2.ps1
```

Manual equivalent:

```powershell
dotnet restore .\ChessMentor.sln
dotnet build .\ChessMentor.sln -c Release
dotnet test .\ChessMentor.sln -c Release
dotnet run --project .\src\ChessMentor.Desktop\ChessMentor.Desktop.csproj -c Release
```

Then open the same PGN corpus in the browser Viewer 1 and Native Viewer 1 and compare game count, branch order, comments/NAGs, black numbering, keyboard paths, board orientation and player labels.

## Environment note

This preparation workspace does not contain the .NET SDK or a Windows WPF runtime. Static syntax/XML/reference/asset checks are performed here, but the Windows `build` and `test` commands above remain the mandatory acceptance gate before declaring the phase compiled or delivered.

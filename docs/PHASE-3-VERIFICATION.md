# Phase 3 Windows acceptance candidate — Studio and translation

This is an acceptance candidate, not a claim that a Windows build has already passed. Run `verify-and-run-phase3.ps1` on Windows; only a zero-error build and passing test run promote it to accepted Phase 3.

## Delivered

- Native `StudioWindow` using the one shared `ChessBoardControl`; no browser or WebView.
- Multi-file append, multi-game selection, single/bulk deletion, legal branch insertion, promotion, branch promotion/deletion, keyboard navigation, active-move reveal and deterministic PGN export.
- Authored PGN AST serializer with nested variations, starting/ending comments, symbolic/numeric NAGs, explicit black numbering, duplicate headers and SetUp/FEN numbering support.
- Source-compatible embedded legal-move repair and sequential nested `پاسخ:` disclosure.
- Debounced local autosave, explicit save, resume and immutable SQLite draft revisions while retaining game/node/source identity.
- Existing FastAPI session/login, category, draft, publish, translation-config, exhaustive TM preflight, translation, TM edit/propagation and move-audio endpoint adapters.
- Translation queue with local-cache-first reads, exhaustive server TM preflight, phrase deduplication, bounded configurable workers, transient retry, cancellation, immediate partial results, progress and durable transient backlog.
- Virtualized local Translation DB search/edit with exact course/game/node/comment-field usage records and workspace propagation.
- Offline-first draft, translation edit and audio queues with retry metadata and exponential backoff.
- Native move-audio recording, play/pause, seek, delete, public/private scopes and local caching without board render-path I/O.
- Draft IDs and published Course IDs remain distinct after publish; queued audio resolves its current game index from the stable game ID after multi-game deletion/reindexing.
- Featured image validation/persistence, category/credit metadata, custom comment fonts and globally synchronized board/display settings.

## Automated regression areas

- PGN authored edit/reparse, nested variations, comments, NAG/annotation, multi-game, explicit black numbering and stable identity.
- Embedded move repair, private knight glyphs, annotation/NAG conversion and prose preservation.
- Draft save/reopen, revision history, featured-image metadata and exact server IDs.
- Translation cache, server TM hits, deduplication, partial failure, retry, cancellation, durable offline backlog and approved-update propagation.
- Existing FastAPI request shapes for session/CSRF, translation, draft, featured image and multipart move audio.
- SQLite schema migration, cache batching, usage mapping, sync queue and exact audio identity.

## Verification on Windows

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\verify-and-run-phase3.ps1
```

To verify without launching:

```powershell
.\verify-and-run-phase3.ps1 -Configuration Release -NoLaunch
```

Logs are written to `artifacts\verification\phase3`.

## Manual smoke test

1. Launch the app and select **PGN Studio** in the Viewer header.
2. Open `samples\phase3-studio-translation-smoke.pgn`.
3. Navigate all three games; confirm nested branches, `19...`, Persian/Latin direction and `پاسخ:` disclosure.
4. Edit a comment, add a legal branch on the board, save a Draft, close Studio, reopen and resume it.
5. Confirm that Play/drag/promotion and branch context actions do not freeze the board.
6. Disconnect the network, edit/save/translate, and confirm local work remains. Reconnect and confirm the Sync count falls after successful operations.
7. With an admin session, save a Server Draft, reopen it by numeric ID, select a featured image and publish.
8. Record a move audio item, play/pause/seek it, reopen the Draft and then delete it.

## Phase boundary

Phase 3 consumes the current server's glossary/TM and supports admin TM edits from Studio. Full account registration/OTP/recovery, contribution suggestion moderation, multiplier/credit administration and general admin pages remain assigned to Phase 8; they are listed in the parity map and are not removed.

## Environment note

The preparation environment has no .NET SDK or Windows WPF runtime. C# syntax trees, XAML XML, event-handler wiring, repository structure and patch cleanliness are checked here. The Windows script above remains the mandatory compile/runtime gate.

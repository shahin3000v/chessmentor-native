# Final solution architecture

## Project graph

```text
ChessMentor.Desktop
  -> Core, Chess, Pgn, Viewer, Persistence, ServerClient, Translation,
     Studio, CourseBuilder, CourseRuntime, MoveTrainer, Audio

Feature projects -> Core + their explicit domain dependencies
Viewer -> Core + Chess + Pgn
Persistence -> Core + Chess (typed persisted board settings)
ServerClient -> Core
Pgn -> Core + Chess (one-way semantic enrichment dependency)
Chess -> Core
Core -> no project dependency
```

Projects:

- `ChessMentor.Desktop`: WPF shell, views, view models, native controls and composition root.
- `ChessMentor.Core`: common contracts, MVVM primitives, IDs and performance metrics.
- `ChessMentor.Chess`: board geometry, FEN/position types, move/rules contracts and interaction state.
- `ChessMentor.Pgn`: token-preserving PGN parser, AST, serializer and semantic UCI/FEN enrichment.
- `ChessMentor.Viewer`: Viewer 1 workspace, immutable flattened move projection, navigation/branch state and background document loader.
- `ChessMentor.Persistence`: versioned SQLite, repositories, settings, caches and sync queue.
- `ChessMentor.ServerClient`: typed `HttpClient` adapters and auth/translation/course/contribution contracts.
- `ChessMentor.Translation`: local cache/queue orchestration; server remains translation authority.
- `ChessMentor.Studio`: authored PGN workspace, identity-preserving Draft packages, comments, server payloads, featured images and publishing orchestration.
- `ChessMentor.CourseBuilder`: authored document, block commands, undo/revisions and source projection.
- `ChessMentor.CourseRuntime`: compile to stages, gating, attempts, replay and history.
- `ChessMentor.MoveTrainer`: trainer authoring, answer policy, transpositions, FSRS and statistics.
- `ChessMentor.Audio`: recording/playback/storage abstractions.
- `ChessMentor.Tests`: cross-platform domain regressions; Windows UI smoke tests will be a separate lane when needed.

## Threading and performance rules

- UI thread mutates presentation state and draws only.
- PGN parse and semantic enrichment run through cancellable background tasks.
- SQLite work is serialized off the UI thread. Writes are transactional and batched.
- HTTP and translation are truly async with bounded concurrency and cancellation.
- Engine analysis gets a dedicated background worker with generation IDs to discard stale results.
- Lists use WPF recycling virtualization; view models expose incremental collections rather than rebuilding full trees.
- Board FEN is parsed only when the position changes. Rendering reuses frozen brushes and cached frozen bitmaps.
- Autosave is debounced and creates an incremental revision/checkpoint, never a transaction per keystroke.
- No polling loop or observer analogue is permitted. Diagnostics update on real operations.

## Phase 3 Studio flow

```text
PGN files
  -> background lossless parse + semantic enrichment + embedded-move repair
  -> StudioWorkspace / shared ViewerSession
  -> authored AST edits
  -> deterministic PGN export or versioned SQLite Draft revision
  -> existing FastAPI Draft/publish payload
```

The WPF view raises intent only. `StudioWorkspace` owns mutations and stable identities; `LocalDraftRepository` owns transactions; `ServerApiClient` owns HTTP contracts. No View or control issues SQL, HTTP or PGN parser calls.

## Phase 3 translation flow

```text
Exact comment locations
  -> local SQLite cache
  -> exhaustive server TM preflight
  -> bounded provider batches
  -> durable cache before UI apply
  -> exact workspace propagation
  -> transient backlog for reconnect
```

Server-returned source hashes remain authoritative for shared Translation Memory. Translation provider credentials and glossary logic never move to Desktop. The local cache holds results and exact course/game/node/field usage; it is not a competing translation authority.

Move audio uses native Windows recording/playback adapters. Files and HTTP run outside the board render path, while SQLite metadata retains local Draft, game, node, user and scope identity.

## Native board contract

One `ChessBoardControl : FrameworkElement` is shared by every feature. It receives immutable position/overlay/legal-target state and raises intent events; it does not own course, trainer, PGN, SQL or HTTP logic.

Geometry and hit testing live in `ChessMentor.Chess`, making 8×8, empty-rank and drag invariants unit-testable without WPF.

The Persian shell inherits RTL, but the board control establishes an explicit LTR rendering boundary. This prevents WPF from mirroring file coordinates, pointer math, overlays, and the piece artwork; board orientation remains controlled only by `BoardOrientation`.

## Local data ownership

SQLite is local authority for offline-authored work and cached copies. Server is authority for identity, wallet/credits, approved shared translations, contributions, published shared courses and conflict revisions.

Every syncable aggregate carries local revision, remote revision, dirty state and last-sync information. Conflicts create a recoverable local branch; no silent last-write-wins.

## Runtime attempt model

`course_runtime_current_progress` contains disposable replay state. `course_runtime_history` contains immutable completed-attempt summaries. Replay replaces current state and cancels timers; it never deletes history.

The `ext-course-builder` source adds two related layers without merging their ownership:

```text
flat authored blocks
  -> resolve LEGO text attachments
  -> resolve explicit Stage containers
  -> sanitize learner payloads
  -> runtime stages + completion gates
  -> checkpoint runs / score events / immutable attempt history

Runtime or MoveTrainer response
  -> practice attempt + context
  -> mistake/soft-fail policy
  -> shared practice card
  -> deterministic FSRS review log
  -> per-course practice profile/statistics
```

Current runtime progress remains disposable and replayable. Checkpoint runs, score events, practice attempts, reviews, and completion history are append-only records. `practice_cards` is mutable scheduling state, not attempt history.

Legacy trainer records are imported transactionally into the unified practice model and then marked archived. Import is idempotent and preserves original IDs, transposition/result-position aliases, FSRS snapshots, due dates, daily-limit counters, and review history. No migration path resets progress or deletes the legacy source rows before verification.

## Builder and runtime server boundary

Authored documents and offline revisions are local-first. Course publication, shared audio assets, learner-safe answer validation, entitlements, and shared aggregate statistics remain server-connected capabilities behind `ICourseSyncApi`, `IAudioApi`, and typed runtime/practice clients.

The Desktop may cache answer results and audio for an entitled offline course, but must not make hidden authored answers part of the ordinary learner view model. Admin preview obtains a separately authorized payload. Builder and Runtime never call `HttpClient` directly.

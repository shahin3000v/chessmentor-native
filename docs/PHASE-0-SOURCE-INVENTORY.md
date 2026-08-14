# Phase 0 — Source inventory and reconciliation

## Canonical input

The current source of truth is repository `shahin3000v/pgn-persian-chess-studio-final`, branch `ext-course-builder`, commit `620ae69a9f75d1262453cc5a1ae2953fdf63001a`.

The original uploaded snapshot identified commit `c3af1d5599b6f2da537414b77be8987b6ff06ed3` on branch `agent/add-movetrainer-course-builder` and contained 182 files. The current commit is 220 commits ahead, with a reconciled 91-file delta. See [`SOURCE-DELTA-620AE69.md`](SOURCE-DELTA-620AE69.md) for the exact behavioral additions and phase assignment.

Original uploaded artifacts:

- Source ZIP SHA-256: `eca59f8e6a628189a7522b4e481943f80ba141c80bf239515cbf314e7eb759ce`
- Migration Pack SHA-256: `b6d10f8b59471ba1b4dc16ea7c4fbd17c744d20e8d3f97b7d6ee5fde47ff9b62`

- 26 HTML application pages
- 24 JavaScript/CSS UI files
- Python/FastAPI backend and migration scripts
- 270 regression test functions
- 12 Cburnett PNG piece assets
- 501-entry English–Persian chess glossary (`1.0.1`)

The Migration Pack was reconciled against the implementation, current README, schema, routes, and tests. Source behavior wins.

## Current application surfaces

### Studio / Viewer 1

- Load one PGN or append several files; multi-game workspace.
- Delete one game or bulk-select and delete games.
- Deterministic export from the live workspace, not original files.
- Native logical tree of moves, nested variations, comments, starting comments, NAGs, SAN, UCI, FEN, turn and deterministic path IDs (`gN...`).
- Manual legal variation insertion, promotion, board flip, player labels and turn indicator.
- Independent games/moves panels, resizable panels, panel/header collapse, active-move reveal and focus preservation.
- Branch chooser with mouse, wheel and keyboard navigation.
- Display modes: all moves, active move/training, and compact/mobile comment strip.
- Letter/figurine notation, custom Persian font, global site fonts, font size and three board skins.
- Mixed RTL/LTR comment rendering, Latin comment digits and click-safe SAN sequences.
- Move sounds and move-linked recorded audio with public teacher/private user scopes.
- Draft/save/resume/publish and featured-image/category/credit metadata.
- Context menus, long press, admin translation editing and translation suggestions.

### Viewer 2

- Separate interaction semantics over the same game tree.
- Keyboard arrows/Home/End, board orientation, coordinates and shared board skin.
- Active move alignment to the top of its own move scroller.
- Click-to-select and unrestricted piece drag callbacks, including wrong-piece attempts for training.
- Reused in articles, home, MoveTrainer, and Studio 2.

### Translation

- Up to six independent OpenAI-compatible providers with bounded concurrency and failover.
- Global Translation Memory preflight before provider calls.
- Conservative Unicode/source normalization and SHA-256 phrase identity.
- Deduplication, batching by item and character count, batch splitting, transient retry and per-provider timeout.
- Structured JSON response with compatibility fallbacks and single-comment fallback.
- 501-entry glossary, critical terms, editor, raw JSON download, atomic save and backup.
- Exact source/course/game/node/comment-field usage mapping.
- Suggestions, permission suspension, approval/rejection, rank, multiplier, one-time credit award, revision history and propagation.
- Database editor with search, pagination and exact usage locations.

### Courses, account and administration

- Draft and published courses, custom categories, pagination and configurable archive sizes.
- Featured image validation/resizing and course purchase with credit ledger.
- Account library, credits, contributions and MoveTrainer statistics.
- Setup, login by username/email/mobile, registration OTP, login OTP, logout, session/CSRF security, account lockout, password rotation and email/OTP password recovery.
- Admin users, permissions, credit adjustment, display settings, authentication/SMS configuration, general notifications and site feedback.
- Public browsing switch and first-load/progressive course game delivery.

### Chess articles

- Single-game article authoring, draft/publish, featured image, sanitized rich HTML.
- Move-linked explanations, diagrams, copied FEN, analysis branches and two-column output.
- Public interactive board, inline analysis, board collapse, article archive/home list.
- Legacy article-linked MoveTrainer data/progress remains migration-relevant even though the standalone trainer is the strategic runtime.

### Standalone MoveTrainer v2.1

- Multi-game PGN authoring, course versions and stable point identities.
- Primary, alternate and soft-fail answers; custom wrong-move feedback.
- Text/from/to/arrow/move hints with penalties.
- Wrong move, wrong piece, input method, timing and reveal recording.
- Persistent server-owned queues and resumable sessions.
- Due/learn/line/random move/random variation/difficult/mistakes modes.
- White/black/both filters, custom depth, spaced/cyclical/custom schedule.
- Transposition acceptance through position/result-position keys.
- FSRS v6 card/review-log snapshots, deterministic no-fuzz scheduling and daily new/review quotas.
- Private notes, retry mistakes, reset, stats, activity chart and learning library.

### Course Builder / Runtime

- Professional Sources / Canvas / Inspector / Preview layout.
- Text, Position, Interactive Move, Move Sequence, Variation and Hint blocks.
- Source soft-links, stale-source detection, drag/drop, reorder, duplicate, delete, undo/redo, import/export and debounced local save.
- Server document with optimistic `expectedRevision`, 409 conflict, checkpoints and revision restore.
- Play as persisted `autoAdvanceSeconds` behavior, not a block.
- LEGO `Text.data.attachedToBlockId`, including multiple attached texts per non-text target.
- Compile-time composition removes attached text from independent stage count.
- Server-side answer hiding/validation for learners; answers visible to admin preview.
- Fixed board panel reserves prompt/board/feedback space, compact side navigation and collapsed-header preference.
- Movable/resizable/persisted native-equivalent text window geometry with smart placement.
- Correct answer auto-next, cancellable authored auto-advance and replay reset behavior.
- Advanced Sequence Challenge, Multiple Choice, and ranked accepted Interactive Moves with score/grade/feedback.
- Persisted Reveal and Wait/Pause modifiers alongside Play, plus annotations, Stage containers, Checkpoints, score rules, and multi-audio attachments.
- Server-owned answer privacy/validation, interactive/demonstration Move Sequence modes, practice-side control, checkpoint runs, score events, audio gating, progression locks, and designer preview.
- Unified practice telemetry shared by Course Runtime and MoveTrainer, while legacy trainer progress remains migration-relevant.

## Source behaviors missing from the handoff prompt

The following are now explicit parity items rather than accidental omissions:

1. Chess article authoring/reading, diagrams and inline analysis.
2. Legacy article-linked trainer data and migration.
3. Site feedback and admin triage.
4. Registration/login OTP, SMS settings and welcome/general notifications.
5. Email/password recovery, session revocation and account lockout.
6. Site-wide uploaded fonts plus per-user custom comment font.
7. Featured images and server-side media sanitization.
8. Course marketplace behavior: categories, pagination, entitlement and credit purchases.
9. Progressive loading of published games and node re-keying to real game indices.
10. Embedded legal move repair from prose comments, including figurines, NAG conversion and bare black ellipsis.
11. Sequential `پاسخ:` / `پاسخ :` disclosure inside comments.
12. Synthesized move/capture/castle/check sounds.
13. Bulk game selection/deletion and exact live-workspace export semantics.
14. Site display settings, admin archive controls and public access mode.
15. Dark navigation/footer theme and privacy-safe compact header behavior.
16. Exact player/result/turn-indicator presentation and empty-panel RTL behavior.
17. Desktop equivalents for narrow-window tabs, long-press/context actions, scroll locks and fixed compact panel sizing.
18. Platform-neutral audio activation/recovery behavior corresponding to the source's Safari/mobile fallbacks.

Browser-only implementation workarounds (Safari gesture activation, DOM scroll locks and mobile CSS ordering) are not copied literally. Their user-visible intent is mapped to native audio-device recovery, pointer/context commands and adaptive WPF layouts.

## Known source limitations corrected by the native architecture

- Some existing paths use `python-chess` reserialization, while translation intentionally edits raw comments in place. Desktop uses one lossless syntax owner for both.
- Existing node IDs are path-based and can shift after sibling insertion. Desktop uses deterministic content anchors in PGN plus persisted IDs in local documents.
- Existing runtime stores current completion but has no dedicated immutable attempt-history model. Desktop separates them from schema v1.
- Browser IndexedDB/localStorage/server state is split across several layers. Desktop centralizes typed local state in versioned SQLite while retaining remote authority.
- Runtime UI relies on multiple Mutation/Resize observers. Native state changes and explicit invalidation replace observer loops.

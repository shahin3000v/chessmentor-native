# Source delta — `ext-course-builder` at `620ae69`

## Immutable baseline

- Repository: `shahin3000v/pgn-persian-chess-studio-final`
- Previous reviewed source: `agent/add-movetrainer-course-builder` at `c3af1d5599b6f2da537414b77be8987b6ff06ed3`
- Current source of truth: `ext-course-builder` at `620ae69a9f75d1262453cc5a1ae2953fdf63001a`
- Git comparison: current source is 220 commits ahead and 0 behind the previous snapshot.
- Reconciled delta: 91 added, changed, or deleted files.

The exact commit was reconstructed over the uploaded baseline and checked file-for-file against the GitHub comparison. This document records behavioral changes, not merely filenames. Where this document conflicts with an older migration note, the current source and its regression tests win.

## Course Builder additions

### Advanced exercises

- Sequence Challenge is stored as a move-sequence block with `advancedKind=sequence-challenge`. It supports authored answer branches of 3–8 moves and up to 32 branches.
- Multiple Choice is stored as a text block with `advancedKind=multiple-choice`. Options may be text, move, position, or image, with per-option correctness, score, and feedback; up to 20 options are supported.
- Interactive Move supports up to 24 ranked accepted moves with Best/Good/Playable grades, score, and feedback rather than one binary answer only.

### Authoring tools and modifiers

- Board annotations include arrows and square highlights with accent, green, yellow, and red tones.
- Reveal is a persisted modifier with off/click modes and content/annotations/all targets.
- Wait/Pause is a persisted modifier with off/click/seconds modes, a 0.2–120 second duration, and an authored label.
- Play remains a modifier (`autoAdvanceSeconds`), not a block.
- Modifiers are draggable, show removable badges, and expose their settings in the Inspector.
- Comments can be split from their source context into independent authored material.
- A Position source can be created from the currently selected move. Shift-selected move ranges must remain on one PGN line.
- Range drops offer multiple authoring modes instead of always producing one fixed block type.

### Stages, checkpoints, score, and audio

- A Stage is a real persisted container of member block IDs. Members can be reordered and detached; runtime compiles the container as one stage.
- Checkpoint blocks reference interactive source blocks, define a pass percentage, and support retry, go-to-stage, show-explanation, and continue failure actions.
- Blocks may have up to eight audio attachments from URL or microphone, with autoplay, play-once, and wait/continue flow behavior.
- Score modifiers define first-attempt, second-attempt, later-attempt, and hint-penalty rules.
- Server-backed builder microphone assets have stable course ownership and authenticated access.

### Chess text rendering

- The tokenizer recognizes SAN, castling with `0` or `O`, coordinate moves with optional dash spacing, white/black numbered forms, and Persian/Arabic digits.
- Rendered move numbers remain Latin and LTR; black compact numbering uses `N...`; castling uses the letter `O`.
- Consecutive chess tokens are isolated into safe LTR runs without changing Persian paragraph alignment.
- Inputs, editors, and content-editable regions are excluded from automatic token rewriting.

## Course Runtime additions

- Learner payloads never expose authored answers; permissioned admin preview may include them.
- Interactive Move validation supports ranked accepted moves and returns score, grade, and authored feedback while retaining legacy-answer fallback.
- Sequence Challenge validation accepts only configured branch prefixes and returns correctness, completion, history, current FEN, and remaining depth.
- Multiple Choice validation is server-owned and returns its per-option score and feedback.
- Move Sequence has interactive/demonstration modes and white/black/both practice-side control. Opponent moves can autoplay, but learners cannot skip required forward moves.
- Stage containers use `all-required` completion semantics for their composed members.
- Checkpoint attempts and outcomes are persisted separately. Retrying replaces the active attempt without deleting historical runs.
- Score state and score events are persisted; one completion award is issued with attempt- and hint-sensitive points.
- Audio may gate progression when its flow is `wait`, or play without gating when its flow is `continue`.
- Only the first incomplete stage and already accessible stages are navigable; future stage titles may be visible while locked.
- Designer preview can unlock navigation without mutating real learner progress.
- Revisit protection prevents already-complete stages from causing synthetic or runaway autoplay.
- LEGO Text attachments still compile into the target stage and never increment progress independently.
- Fixed board/prompt/feedback geometry, cancellable timers, persisted floating text geometry, and true Replay reset remain mandatory.

## Unified practice and MoveTrainer

The web source replaces isolated trainer pages with a shared practice telemetry model used by Course Runtime and MoveTrainer:

- `practice_attempts` records course/block/type/source, position, response, correctness, score, grade, timing, and JSON context.
- `practice_cards` owns scheduling state, mistake/success/soft-fail counts, source provenance, and FSRS fields.
- `practice_reviews` stores requested/applied rating, outcome, response, and FSRS before/after/log snapshots.
- `practice_attempt_contexts` stores the block snapshot, input method, hints, and client context.
- `move_trainer_profiles` aggregates per-user/per-course activity from both `course_runtime` and `move_trainer` sources.
- Correct first attempts contribute telemetry but do not create mistake cards. Lower-ranked accepted moves can become soft failures.
- Queue order is due cards first, followed by due time, difficulty from mistake/success history, and update time.
- A practice card identity includes course ID, block ID, and normalized position FEN (first four fields).

The old standalone trainer authoring pages were removed by the source, but existing trainer tables are archived rather than deleted. Desktop must preserve the earlier transposition-aware answer behavior, deterministic FSRS, daily limits, and all existing progress while importing it into the unified model. Removal of a browser page is not permission to discard user data or a native capability already required by the Desktop specification.

## Server API delta retained for Desktop

- Authenticated Course Builder audio upload/read/range/delete endpoints remain server-owned; the Desktop client caches media and metadata offline.
- Runtime block-answer, progress, preview, stage, checkpoint, score, and practice-attempt endpoints remain behind typed client abstractions.
- MoveTrainer exposes source, next-card, review, course stats, and account stats endpoints over unified practice data.
- FastAPI is not rewritten in this phase. HTTP DTOs stay in `ChessMentor.ServerClient`; UI projects never call routes directly.

## Phase allocation

- Phase 4: keep Viewer 2 parity work isolated; do not import Course Builder DOM behavior into it.
- Phase 5: implement unified practice attempts/cards/reviews/profiles, deterministic FSRS, migration of legacy trainer progress, retry mistakes, transposition policy, daily limits, and statistics.
- Phase 6: implement Builder sources/canvas/inspector/preview, advanced exercises, modifiers, stage containers, checkpoints, score rules, audio attachments, chess text tokenization, undo/revisions, and autosave.
- Phase 7: compile Builder documents into runtime stages; implement answer privacy/validation, gating, checkpoints, scoring, audio flow, telemetry, LEGO composition, fixed board geometry, floating Text, timer cancellation, and Replay.
- Phase 8: complete connected account/admin/designer permissions, shared audio, contribution, profile, and aggregate-stat workflows.
- Phase 9: import/export legacy builder documents, trainer tables, practice telemetry, course progress/history, audio, and settings with dry-run reports and idempotency.

No Phase 5–7 feature is folded prematurely into the Phase 3 Studio UI. Each phase must remain runnable and must move its parity rows from Planned to Implemented only after regression coverage passes.

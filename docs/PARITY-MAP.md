# Behavioral parity map

Status values: **Implemented Pn** has code and regression coverage in that phase; **Foundation** exists but its end-user workflow is later; **Planned Pn** is assigned to a later runnable phase; **Server retained** means FastAPI stays authoritative until its client phase.

| Domain | Canonical behavior | Desktop owner | Status |
|---|---|---|---|
| Shared chess board | Equal 8×8 geometry, cached pieces, piece-only drag, click/drag, flip, coordinates, skins, overlays/legal targets | `ChessMentor.Chess` + WPF `ChessBoardControl` | Implemented P1/P2 |
| Chess rules | FEN, legal moves, SAN/UCI, castling, en-passant, promotion, disambiguation, transposition keys | `ManagedChessRules` behind `IChessRules` | Implemented P2; trainer result policy P5 |
| PGN syntax | Headers, comments, NAG, annotations, black numbering, variations, multi-game, exact round trip | `ChessMentor.Pgn` custom tokenizer/AST | Implemented P1/P3; corpus expansion continues |
| PGN semantic enrichment | Node FEN/UCI, legality, transposition identity, non-destructive diagnostics | PGN + chess rules adapter | Implemented P2 |
| Viewer 1 core | Multi-file/multi-game, game delete/bulk delete, branch navigation, active move/auto-scroll, board input, resize/collapse, synthesized audio, mixed direction and core display settings | `ChessMentor.Viewer` + Desktop | Implemented P2 |
| Viewer-adjacent authoring | Live deterministic export, legal manual branch insertion, draft/publish and context actions | Studio over shared Viewer session | Implemented P3 |
| Comment enhancements | Embedded prose-move repair, click-safe SAN runs, sequential `پاسخ:` disclosure and custom font file | Pgn/Studio/Desktop | Implemented P2/P3 |
| Move-linked audio | Teacher/public and user/private records, seek/delete/record and exact identity | Audio + Studio + ServerClient | Implemented P3 for Studio; Runtime P7 |
| Studio | Append/delete/edit/translate/draft/publish/audio | `ChessMentor.Studio` | Implemented P3 acceptance candidate |
| Viewer 2 | Distinct alignment/interaction semantics | Desktop shared viewer core | Planned P4 |
| Translation providers | Server provider routing, failover, glossary/TM | Existing FastAPI | Server retained; native client implemented P3 |
| Local translation cache | Offline hits, pending queue, exact usage mapping and reconnect retry | Translation + Persistence | Implemented P3 |
| Translation DB editing | Virtualized search/edit, approved propagation and server TM update | Studio + Translation + FastAPI | Implemented P3 for admin Studio |
| Translation contributions | Suggestions, approve/reject, permissions, ranks, multipliers and credits | Existing FastAPI + native admin/account UI | Server retained; client P8 |
| Featured course image | JPEG/PNG/WebP validation, Draft persistence and publish payload | Studio + ServerClient | Implemented P3 |
| Course marketplace | Categories, entitlement, credits, purchase | Existing FastAPI | Client P8 |
| Course Builder | Sources/canvas/inspector/preview and revisions | CourseBuilder | Contracts foundation; P6 |
| Play behavior | Persisted block auto-advance with cancellation/gating | CourseBuilder/Runtime | Planned P6/P7 |
| Reveal / Wait modifiers | Persisted click/timed reveal and pause behavior with removable badges | CourseBuilder/Runtime | Planned P6/P7 |
| Advanced exercises | Ranked Interactive Move, Sequence Challenge and typed Multiple Choice options | CourseBuilder/Runtime | Planned P6/P7 |
| Stage authoring | Member containers, reorder/detach, `all-required` runtime composition | CourseBuilder/Runtime | Planned P6/P7 |
| Checkpoints and scoring | Pass thresholds, failure actions, immutable runs, attempt/hint-sensitive score events | CourseBuilder/Runtime + Persistence | Planned P6/P7 |
| Builder/runtime audio | Up to eight attachments, microphone/URL, autoplay/play-once and wait/continue gating | Audio + CourseBuilder/Runtime + ServerClient | Studio recording P3; course flow P6/P7 |
| Chess text tokenization | Mixed Persian text with isolated SAN/UCI/numbered LTR runs and Latin move numbers | Core presentation service + Desktop | Planned P6/P7 |
| LEGO composition | Attached text compiled into one target stage | CourseRuntime compiler | Planned P7 |
| Course Runtime | Blocks, answer privacy, gating, progression locks, fixed board panel, text window, replay/history and designer preview | CourseRuntime | Schema foundation; P7 |
| Unified practice telemetry | Attempts, cards, reviews, contexts and per-course profiles shared by Runtime and MoveTrainer | MoveTrainer + CourseRuntime + Persistence | Planned P5/P7 |
| MoveTrainer | Authoring, answers, hints, queues, deterministic FSRS, transpositions, daily limits, retry mistakes and stats | MoveTrainer | Contracts foundation; P5 |
| Audio | Synthesized navigation sound plus public/private move audio recording/playback | Audio | Viewer sound P2; Studio linked audio P3; Runtime P7 |
| Account/auth | Session/login needed by Studio now; registration/OTP/recovery and account shell later | Existing FastAPI + secure Windows token vault | Session/login P3; full client P8 |
| Contributions/admin | Suggestions, users, permissions, credits and moderation | Existing FastAPI | Client P8; TM editor itself P3 |
| Articles | Authoring, rich chess content, diagram/analysis viewer | Studio/article module | Planned after P4; migration tracked |
| Migration | Dry-run package export/import, backup, report, idempotency | Persistence + old Python exporter | Contract plan; P9 |
| Diagnostics | Parse/render/DB/queue/memory counters | Core diagnostics + Viewer/Studio panels | Implemented foundation P1–P3 |

No item marked Planned is silently dropped. Phase completion requires moving its row to implemented and adding regression coverage.

# Dependencies and risk review

## Accepted runtime dependencies

| Dependency | Version | Use | Risk and control |
|---|---:|---|---|
| .NET / WPF | 10 | Native Windows UI/runtime | Windows-only by design. Release build and smoke test must run on Windows even if domain code is checked elsewhere. |
| `Microsoft.Data.Sqlite` | 10.0.11 | SQLite ADO.NET provider | Its async APIs execute synchronously because SQLite has no async I/O. All DB calls therefore run on a controlled background executor; WAL, busy timeout and short transactions are mandatory. |
| `System.Text.Json` | inbox | Versioned JSON payloads | Schema changes require explicit converters/migrations; unknown fields must be preserved in migration packages where possible. |
| `HttpClient` | inbox | Existing FastAPI APIs | One long-lived handler/client, cookie container, timeout layers, cancellation, transient-only retry and redacted logging. |
| xUnit v3 | 3.2.2 | Regression tests | Test-only; pin stable version and use its built-in Microsoft Testing Platform integration selected by `.NET 10` `global.json`. |

Phase 3 adds no third-party runtime package. Native move audio uses WPF `MediaPlayer` plus event-driven WinMM waveform-input buffers; recording therefore needs a usable Windows input device and OS permission. WAV duration is derived from captured PCM bytes rather than the UI clock. The adapter is isolated behind `IMoveAudioRecorder`/`IMoveAudioPlayer`, and failures leave Draft/board state intact.

Custom TTF/OTF files and featured images are copied into application-owned LocalAppData folders after format/signature validation. They are never loaded as executable content. Server-side media validation remains authoritative at publish time.

## Deliberately rejected for source-of-truth

- No C# PGN library is trusted as the canonical parser until it passes the full nested-variation/comment/NAG/round-trip corpus. Most chess libraries prioritize semantic game models and normalized export, which is not enough here.
- No EF Core: raw `Microsoft.Data.Sqlite` keeps startup, allocations, migrations and query behavior explicit.
- No MVVM framework in Phase 1: small local primitives avoid another dependency. Re-evaluate only if generated command/property boilerplate becomes a measurable maintenance cost.
- No WebView2/Electron/embedded browser.
- No third-party chess-rules package was introduced in Phase 2. `ManagedChessRules` keeps viewer legality deterministic and dependency-free; perft/corpus coverage must continue growing before MoveTrainer relies on it in Phase 5.
- Stockfish integration later uses a process/UCI boundary so engine lifetime and cancellation are isolated.

## Licensing gates

- The bundled Cburnett pieces remain CC BY-SA 3.0; attribution and license obligations must ship with the app.
- `py-fsrs` behavior must be ported or replaced by a license-compatible C# implementation with deterministic parity fixtures.
- The exact Open-Chessable version/license used only as product-behavior inspiration must be reviewed before commercial release. No code is copied into this foundation.

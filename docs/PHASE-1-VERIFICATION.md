# Phase 1 verification

## Delivered

- Modular .NET 10 solution and WPF shell.
- Shared direct-drawing native chess board with cached piece assets and three global skins.
- Board geometry/FEN/interaction domain foundation.
- Token-preserving PGN tokenizer, AST, parser, comment edit and serializer.
- Versioned SQLite schema for settings, documents, drafts, translation cache, builder revisions, runtime current/history, trainer/FSRS, audio and sync.
- Typed settings service and off-UI-thread SQLite executor.
- Server API abstractions and concrete session/auth/translation/course/contribution skeleton.
- Debug/benchmark panel for parse, node/game count, board render, DB, translation queue and memory.
- xUnit regression foundation.

## Checks completed in the handoff workspace

- All 37 C# files parsed with the C# tree-sitter grammar: 0 syntax errors.
- All 17 XAML/MSBuild XML files parsed: 0 XML errors.
- All 13 projects are present in the solution; all 51 project-reference targets resolve.
- All 12 piece resources are valid 60×60 RGBA PNG files.
- 20 xUnit fact/theory methods cover the PGN, board, persistence, IDs, promotion and server-contract foundations.

These are static checks, not a substitute for the Windows compiler/test gate below.

## First Windows build corrections

- `ChessMentor.Tests` is explicitly an executable, as required by xUnit v3 with Microsoft Testing Platform.
- Board drag-distance calculation uses `Math.Sqrt(dx² + dy²)` rather than the unavailable `Math.Hypot` API.
- xUnit analyzer compliance uses `TestContext.Current.CancellationToken` for every cancellable test call and dedicated string assertions.
- The shared WPF board forces an LTR rendering boundary so an RTL parent cannot mirror files or piece bitmaps.

## Verification commands on Windows

```powershell
dotnet --info
dotnet restore .\ChessMentor.sln
dotnet build .\ChessMentor.sln -c Release
dotnet test .\ChessMentor.sln -c Release
.\scripts\publish-windows-x64.ps1
.\artifacts\publish\win-x64\ChessMentor.Desktop.exe
```

## Environment note

The handoff workspace used to prepare Phase 1 did not contain a .NET SDK or pytest, so compilation and execution could not honestly be claimed there. The project is structured for the commands above, and a Windows/.NET 10 build is the mandatory acceptance gate before Phase 2.

# Phase 6 checkpoint — native Course Builder foundation

This is the first runnable Phase 6 checkpoint, not final Phase 6 acceptance.

Run on Windows from the repository root:

```powershell
.\verify-and-run-phase6.ps1
```

The checkpoint includes:

- native Sources, Course Canvas, Inspector and Preview panels;
- monitor-safe maximized startup and responsive panel columns, including
  accessible Sources on smaller displays and Windows scaling;
- background multi-file PGN source loading;
- draggable source items and virtualized source/canvas lists;
- stable block IDs, duplicate/delete/reorder and undo/redo;
- the shared native board in Preview;
- debounced autosave, explicit save and immutable SQLite revisions;
- persisted Play (`autoAdvanceSeconds`) behavior;
- persisted LEGO Text attachment with multiple texts per target and detach;
- persisted Stage member containers and deterministic stage preview compilation;
- typed foundations for Reveal, Wait, scoring, ranked moves, multiple choice,
  annotations and up to eight audio attachments.

Smoke path:

1. Select **Course Builder** in the main header.
2. Open `samples\phase3-studio-translation-smoke.pgn`.
3. Drag a Comment and a Position from Sources into Course Canvas.
4. Add a Text block, select it and attach it to the Position through LEGO.
5. Enable Play and set its delay.
6. Save, close Course Builder, reopen the same document and verify the block,
   Play badge and LEGO target are retained.

Remaining before final Phase 6 acceptance: full Stage member authoring UI,
advanced exercise inspectors, Reveal/Wait/score editors, audio attachment UI,
checkpoints and chess-text token rendering.

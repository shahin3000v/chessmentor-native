# Phase 5 verification — MoveTrainer

Phase 5 is accepted only after the native Windows build, the complete regression suite and the following smoke path all pass. Viewer 2 is intentionally skipped by the product owner's explicit decision.

## Automated gate

From PowerShell at the repository root:

```powershell
.\verify-and-run-phase5.ps1
```

For verification without launching the UI:

```powershell
.\verify-and-run-phase5.ps1 -Configuration Release -NoLaunch
```

Logs are written to `artifacts\verification\phase5`. Send `build.log` or `test.log` if the gate fails.

The automated coverage includes:

- primary, alternate, soft-fail and transposition answer evaluation;
- illegal/wrong-piece attempts, stay-on-item and Retry Mistakes reset;
- deterministic no-fuzz FSRS and rating mapping for hints/wrong/soft-fail;
- due-first ordering, daily limits, session limit and side-to-move filtering;
- multi-game PGN candidate generation with variation answers and stable source IDs;
- course, attempt, card, review, context, profile, session and statistics persistence;
- SQLite v3 to v4 legacy FSRS migration without deleting or resetting the old row.

## Native smoke path

1. Start the application and select **MoveTrainer** in the Viewer header.
2. Select **باز کردن PGN…** and choose two PGN files, or one multi-game PGN.
3. Confirm both games produce positions and that selecting an item updates the shared native board.
4. Edit the prompt, wrong feedback, accepted moves and hints. Save, close MoveTrainer, reopen it and reload the course.
5. Confirm the edits and course settings survived reopening.
6. Start daily training. Submit an illegal move or move the wrong piece; confirm the item remains active when retry is enabled.
7. Show a hint, then submit a primary, alternate or soft-fail response and confirm feedback and automatic progression.
8. Finish the queue, select **Retry Mistakes**, and confirm only mistaken items return with fresh session attempt counters.
9. Close and reopen the app; confirm statistics and due state remain intact.

## Database migration smoke path

Keep a backup of the original database. Use **ارتقای دیتابیس…** in the main window and choose a compatible ChessMentor SQLite file. The import must report schema compatibility and merge its MoveTrainer courses/items, legacy FSRS rows and v4 practice data without clearing current progress.

## Performance checks

- Opening MoveTrainer presents the window before database/course work can block interaction.
- PGN parsing and semantic enrichment stay on background work through `ViewerDocumentLoader`.
- SQLite provider work is serialized off the UI thread.
- Course and item lists use recycling virtualization.
- The board is a fixed native drawing control with cached piece bitmaps; no WebView is involved.

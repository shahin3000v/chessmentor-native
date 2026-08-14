# Migration plan

## Format

Prefer a versioned package over direct reads of production SQLite:

```text
Old Python App Exporter
  -> manifest.json
  -> settings.json
  -> pgn/*.pgn + document metadata
  -> glossary/*.json
  -> translations/*.jsonl
  -> drafts/*.json
  -> courses/*.json
  -> course-builder/*.json
  -> trainer/*.json + progress/history
  -> media index + checksums
  -> ZIP package
Desktop Importer -> dry-run report -> backup -> transaction batches
```

## Guarantees

- Export is read-only and versioned.
- Every file and record group has SHA-256 checksums.
- Import supports dry-run, reports ambiguous/unmatched identities and never guesses.
- Imported source IDs, game IDs, node IDs, course IDs and trainer point IDs are retained in mapping tables.
- Re-running the same package is idempotent via package/record IDs.
- Local database backup is made before mutation.
- Translation Memory fingerprints and row counts are checked before/after, matching the safety level of the current contribution migration.
- MoveTrainer progress migration replays historical attempts only when necessary to reconstruct FSRS; it never resets existing state.

## Direct SQLite fallback

Direct import is allowed only from a copied, offline database after `integrity_check`, schema fingerprinting and backup. It is never run against the live mounted database. Existing site DB, translation DB and media folders are treated as one consistency set.

## Current source entities requiring migration

- Site users/account metadata where policy permits, but never raw passwords/session secrets.
- Courses, drafts, categories, purchases and credit ledger references.
- `viewer_json`, PGN text, source filename metadata and translation usage links.
- Course Builder documents and revisions.
- Course Runtime current progress; desktop imports it into current state, not immutable history.
- Standalone trainer courses/games/points/answers/wrong responses/hints/versions.
- Trainer enrollments/settings/progress/sessions/session items/attempts/private notes/purchases.
- Legacy article trainer modules/progress/attempts.
- Translation Memory, revisions and contribution suggestions.
- Move audio plus site/course/article media and fonts.
- Display, auth and notification settings that have a desktop equivalent.

Migration tooling is delivered in Phase 9 after schemas stabilize; mappings and package contracts are designed earlier and fixture-tested throughout.

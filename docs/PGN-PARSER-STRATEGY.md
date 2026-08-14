# PGN parser strategy — fixed decision

## Decision

Use a custom two-layer implementation:

1. **Lossless syntax layer** owns original tokens and trivia: whitespace, headers, move numbers, SAN symbols, brace/semicolon comments, NAGs, annotations, results and parentheses.
2. **Semantic chess layer** enriches move nodes through an `IChessRules` adapter with UCI, FEN and legality. It never owns serialization.

Phase 2 uses the in-repository `ManagedChessRules` implementation behind the adapter. A future replacement may sit behind the same contract, but can never rewrite the PGN.

## Why

The current app already exposes the split: raw comment translation preserves all surrounding PGN text, while `python-chess` based viewer/export paths normalize structure. The desktop requirement is stricter: parse/edit/serialize/reparse must not lose nested variations, comments, NAGs, black numbering or unknown commands.

## Syntax representation

- `PgnToken` retains exact raw text and source location.
- `PgnDocument.Serialize()` concatenates current token text, producing exact no-edit round trips.
- `PgnGame` preserves ordered duplicate-capable header records plus a lookup view.
- `PgnMoveNode` forms the nested move tree and carries stable deterministic IDs.
- Comments point to their exact token and owning root/move node; editing only changes that token.
- Symbolic annotations are mapped to their standard NAG value while their original token remains untouched.
- Diagnostics are non-destructive: malformed/unclosed material stays in the token stream.

## Phase 3 authored serialization

Imported, unedited text can still use token concatenation for byte-exact output. Structural Studio edits use `PgnAstSerializer`, then reparse before a restored Draft becomes active. The serializer emits ordered duplicate headers, nested sibling variations, starting/ending comments, symbolic annotations, non-duplicate numeric NAGs and explicit black move numbers. SetUp/FEN games use semantic `IsWhiteMove` and `FullmoveNumber`; numbering is not incorrectly rebased to ply one.

`StudioDraftPackage` stores an identity tree beside PGN. Reparse reconstructs syntax and chess semantics, then reapplies exact external game/node IDs only when the structural shape matches. A mismatch is an explicit recoverable error rather than guessed identity.

The source application's embedded prose-move repair is ported as a separate legal, cancellable adapter. It requires at least two legal plies, supports figurines/private knight glyphs and annotations, and leaves illegal or single-ply prose untouched.

## Stable IDs

Persisted desktop documents store generated node IDs alongside syntax anchors. Initial import derives deterministic IDs from game identity, parent ID, normalized SAN and same-SAN occurrence. This survives comment edits and reserialization. Structural editor operations preserve the assigned ID directly; export to plain PGN may omit private IDs, while the desktop document/package retains them.

## Variation algorithm

After a move, `(` changes the current position to that move's parent and pushes the mainline move as the return point. Nested parentheses repeat the same rule. `)` restores the saved return point. Comments after `(` or after a move number become starting comments for the next move; comments after SAN/NAG attach to the current move.

## Phase 2 semantic layer

- `PgnSemanticEnricher` starts each game from its `FEN` header or the standard initial position.
- Each branch is resolved independently from its parent position; nested variation order cannot leak state across siblings.
- Successful nodes receive UCI, resulting FEN, repetition-oriented position key and transposition group ID.
- An illegal/unsupported SAN creates a semantic diagnostic and blocks only its dependent subtree. Original tokens remain available and serialize byte-for-byte.
- Legal move calculation includes king-safety filtering, castling transit checks, en-passant, all four promotions and SAN disambiguation.
- Enrichment runs as a cancellable background task; the UI never parses PGN or calculates a complete workspace on its dispatcher thread.

## Remaining corpus gate

Continue expanding the parity corpus with:

- all 270 current server regressions relevant to PGN/viewers,
- ChessBase-style commands and private knight glyphs,
- nested variations at least 12 levels deep,
- duplicate/custom headers and SetUp/FEN games,
- brace and semicolon comments,
- symbolic and numeric NAGs,
- explicit/bare black ellipsis,
- malformed-but-recoverable input,
- 100+ game benchmark files.

No candidate rules library becomes canonical even if it passes legality tests; the syntax layer always remains source-of-truth.

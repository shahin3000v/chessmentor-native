# Existing FastAPI server reuse plan

## Boundary

Phase 1 does not rewrite the Python server. Desktop talks to it through:

- `IServerApi`
- `IAuthApi`
- `ITranslationApi`
- `ICourseSyncApi`
- `IContributionApi`

The concrete client owns `HttpClient`, cookies, CSRF propagation, JSON and error translation. View models never build URLs.

## Endpoint groups retained

- Auth/session/register/OTP/password recovery/account.
- Translation config, TM preflight, batch translation and glossary/database editors.
- Translation suggestions, approval/rejection, permissions, credits and propagation.
- Draft/course/category/publish/purchase/progressive games.
- Move audio metadata and streaming.
- Course Builder document/revision/checkpoint endpoints.
- Course Runtime answer/progress endpoints.
- Standalone MoveTrainer authoring, sessions, attempts, notes, progress and stats.
- Admin users/settings/notifications/feedback/articles.

## Compatibility process

1. Capture sanitized request/response fixtures from commit `c3af1d5`.
2. Write DTO contract tests before activating each connected feature.
3. Add `/api/desktop/capabilities` only if feature negotiation cannot be inferred safely from existing health/config responses.
4. Keep cookie-session support initially. Store any reusable token/session material with Windows-protected storage, never plain SQLite.
5. Send `clientVersion`, schema version and idempotency key on queued mutations once the server accepts them.

## Offline queue

Only modeled/idempotent operations are queued. Each entry stores operation ID, aggregate ID, base remote revision, JSON payload, attempt count and next-attempt time. Authentication failures pause the queue; conflicts create a visible conflict record; transient network/5xx/429 errors back off with jitter.

Translation provider secrets stay on the server. Desktop stores only cached translations, phrase identity, usage mapping and pending user contributions.

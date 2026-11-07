# Supplier Feed Throttle & Dedup Service

ASP.NET Core (.NET 9) Web API with SQLite (WAL) and Dapper (POCOs, hand-written SQL, no ORM). Suppliers push reservation
updates; the service drops exact duplicates, applies real changes, ignores stale ones, and rate-limits each supplier.
Specs: `docs/superpowers/specs/` (design), `docs/superpowers/plans/` (plan). The chat transcript is exported in the repo root.
The DB defaults to `data/supplierfeed.db` (working directory); override with `Database__Path`.

## Endpoints
- `POST /api/reservations/ingest`: 201 `created`, 200 `updated` / `unchanged` / `stale`, 400 invalid, 413 body > 64 KB, 429 throttled.
- `GET /api/reservations/stats/{supplierId}`: `{ supplierId, ingested, throttled }`; 503 if the database file is missing.

## Design
```
ReservationsController -> IngestRequestParser -> IngestRequestValidator -> IngestService -> ThrottleLog / ReservationStore / StatsCounters
StatsController        -> StatsQuery (read-only connection)
```
Controllers hold no SQL or rules. Writes (`Ingest/`) and reads (`Stats/`) are separate classes over the same SQLite file.
Tables: `reservations` (PK supplierId+reservationId), `request_log` (the throttle window), `supplier_stats` (lifetime counters).

Each ingest request:
1. Read the raw body (capped at 64 KB) and parse it field by field. Extract `supplierId`; with none, return 400 (not throttled).
2. Validate in memory, holding no lock.
3. One write-locked transaction (`BEGIN IMMEDIATE`), always committed: take the clock, prune expired log rows, insert this request,
   count the supplier's rows newer than `now - 60 s`. More than 100 -> bump `throttled`, return 429. Validation failed -> 400.
   Otherwise read the stored row and decide: none -> insert (`created`); stored timestamp newer -> `stale`; identical -> `unchanged`;
   else update (`updated`). `ingested` is bumped for created/updated only (`n = n + 1` in SQL).

Multi-instance correctness comes from the database, not memory: every instance serialises on SQLite's write lock, so the
count-then-act sequence can't interleave and the 101st request can't slip through.

## Tradeoffs
- **Sliding-window log over a token bucket.** Exact "100 in any rolling 60 s"; a bucket admits ~200 in one window. Costs one row per request.
- **SELECT inside the lock over a single `INSERT ... ON CONFLICT`.** An upsert can't tell `unchanged` from `stale`, or insert from update.
  The lock makes read-then-write safe. Costs a read per request.
- **Correctness over throughput.** One SQLite writer serialises everything: simple to reason about, fine at ~100 req/min/supplier.
  SQLite has no real async I/O, so a request waiting on the lock blocks a thread (up to the 30 s busy timeout).
- **Every request is a write**, even a 429 (log row plus counter). That is what makes the window exact, but a flood still hits the DB.
- **Rejected requests count toward the window.** A supplier that keeps sending stays blocked until it has been quiet for 60 s.
- **Hand-parsed body instead of model binding**, so the throttle runs before type validation and a wrong-typed field still counts.
- **Float price** (author's call): dedup compares doubles, which works for identical input text; money is normally decimal/cents.
- **Pruning on every request** (all suppliers) keeps the table bounded without a background job; it is a delete on the hot path.
- **Tie-break:** equal `updatedAtUtc` with different details is applied, so the last arrival wins across instances.
- **Tests:** concurrency is tested with in-process hosts sharing one file (fast, deterministic clock), plus a script with real processes.

## Ambiguity decisions
Decided by the author: .NET 9 + SQLite + Dapper; "multiple instances" = several processes on one machine sharing one SQLite file;
older `updatedAtUtc` is not applied, equal is applied; validation = `checkIn < checkOut` and `price >= 0`; invalid requests count toward
the limit but not as ingested; one project, separated by controllers and classes; no `Retry-After`; 64 KB cap (size arbitrary);
price stored as float; a missing database returns a response rather than creating the file.

Defaults Claude proposed, approved with the design doc:
- Identity is `(supplierId, reservationId)`; `supplierId` is case-sensitive and never trimmed. One reservation per request.
- A body with no usable `supplierId` (or invalid JSON) is a 400 and is never throttled: there is no key to count against.
- Also require `supplierId`/`reservationId`/`roomId` to be non-blank; a wrong-typed or missing field is a 400.
- Dates must be ISO-8601 (date, or date-time with `Z`, an offset, or none = UTC), normalised to UTC. A lenient parser accepted `"10:15"` as today's date.
- A stale update returns 200 `stale` (not an error: retrying can't fix it). Duplicates and stale updates still count toward the limit.
- `ingested` = created + updated; `throttled` = number of 429s; counters are lifetime; an unknown supplier returns zeroes.
- 413 for oversize bodies (not throttled); 503 for a missing database or a lock held past the busy timeout.
- SQLite can't be shared across hosts: a networked database (SQL Server/Postgres) is needed beyond one machine.

## Where the author changed Claude's suggestions
- **Throttle:** the author proposed a token bucket (100, +1 per 10/6 s). Claude showed it admits ~200 per window and that unbounded
  debt locks a burst sender out for ~25 min, and recommended the log. Adopted.
- **Claude's first upsert design was wrong** (one `ON CONFLICT` statement can't distinguish four outcomes). Fixed with the in-lock SELECT.
  The author's objection that unchanged/stale/throttled requests "don't touch the database" was half right: they read it and write a log row.
- **`Retry-After` dropped.** Claude suggested it; the author removed the retry mechanism.
- **Price as float.** Claude suggested a decimal stored as text.
- **One project, two controllers.** The author first asked for one project per endpoint, then collapsed it.
- **Claude's own review found its own bugs:** a test that couldn't detect a rewrite, a "chunked body" test that never reached the streaming
  path, lenient date parsing, and the clock read before the lock (a request waiting for it logged a stale time). All fixed; tests were
  checked by planting bugs and seeing them fail.

## With more time
- Money as decimal/integer cents; normalise or reject odd supplier IDs (case, whitespace).
- A networked database with database-side time, and a multi-process test in CI.
- `Retry-After`; cheaper blocked requests (rate-limit by IP before touching the DB); pruning off the hot path.
- Auth, structured logging and metrics; reconsider last-arrival-wins on timestamp ties across instances.
- A test that starts the app with no configuration override (a missing default once slipped past the automated tests).

# Supplier Feed Throttle & Dedup Service
Specs: `docs/superpowers/specs/` (design), `docs/superpowers/plans/` (plan). The chat transcript is exported in the repo root.
The DB defaults to `data/supplierfeed.db` (working directory); override with `Database__Path`.

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
- Decided by the author: .NET 9 + SQLite + Dapper; "multiple instances" = several processes on one machine sharing one SQLite file;
- "Multiple instances" = several processes on one machine sharing one SQLite file. SQLite can't span hosts (that needs SQL Server/Postgres), but all state lives in the database, so any instance can serve any request.
- Validation not specified : older `updatedAtUtc` is not applied,`checkIn < checkOut` and `price >= 0`; invalid requests count toward the limit but not as ingested; 
- one project, separated by controllers and classes; no Retry machenism; request body size capped at 64KB;
- price stored as float; a missing database returns a response rather than creating the file.
- A body with no usable `supplierId` (or invalid JSON) is a 400 and is never throttled: there is no key to count against.
- Also require `supplierId`/`reservationId`/`roomId` to be non-blank; a wrong-typed or missing field is a 400.
- Dates must be ISO-8601 (date, or date-time with `Z`, an offset, or none = UTC), normalised to UTC. A lenient parser accepted `"10:15"` as today's date.
- A stale update returns 200 `stale` (not an error: retrying can't fix it). Duplicates and stale updates still count toward the limit.
- `ingested` = created + updated; `throttled` = number of 429s; counters are lifetime; an unknown supplier returns zeroes.
- 413 for oversize bodies (not throttled); 503 for a missing database or a lock held past the busy timeout.

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
- ditch SqlLite in favor of Postgrss(more suitable for concurrency)
- A networked database with database-side time, and a multi-process test in CI.
- retry machenism for failed ingests
- Auth, structured logging and metrics; reconsider last-arrival-wins on timestamp ties across instances.

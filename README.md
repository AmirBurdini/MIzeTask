# Supplier Feed Throttle & Dedup Service

ASP.NET Core (.NET 9) Web API, SQLite (WAL) and Dapper. `POST /api/reservations/ingest` deduplicates, updates and
throttles supplier reservation updates; `GET /api/reservations/stats/{supplierId}` returns `ingested` and `throttled`.
Design: `docs/superpowers/specs/2026-11-06-supplier-feed-design.md`. Plan: `docs/superpowers/plans/`.

```
dotnet build
dotnet test                                   # 100 cases: unit + in-process multi-host concurrency
powershell scripts/two-instances.ps1          # two real processes on one DB file: 200 requests -> 100 accepted, 100 throttled
dotnet src/SupplierFeed.Api/bin/Debug/net9.0/SupplierFeed.Api.dll --urls http://localhost:5099
```
The database file defaults to `data/supplierfeed.db` (relative to the working directory); override with `Database:Path` (env `Database__Path`). `nuget.config` uses nuget.org only.

## Assumptions (spec unclear)
- **Multiple instances** = several processes on one machine sharing one SQLite file. SQLite can't be shared across hosts; that
  would need SQL Server/Postgres (the SQL barely changes). All throttle state is in the database, so any instance can serve any request.
- **Throttle** is an exact sliding-window log: at most 100 requests per supplier in any rolling 60 s, else 429. Every attributable request
  counts: rejected, invalid, duplicate and stale ones too (so a supplier that keeps sending stays blocked until it has been quiet for 60 s).
  A body with no usable `supplierId` (or invalid JSON) returns 400 and is never throttled, since there is nothing to key on.
- **Identity** is `(supplierId, reservationId)`; one reservation per request. `supplierId` is case-sensitive and not trimmed.
- **Dedup:** identical resend -> 200 `unchanged`, no write. Newer `updatedAtUtc` -> `updated`. Equal timestamp with different details -> `updated`
  (last arrival wins on a tie). Older timestamp -> 200 `stale`, ignored, no write. New -> 201 `created`.
- **Validation:** `checkIn < checkOut`, `price >= 0`, and (my addition) `supplierId`/`reservationId`/`roomId` non-blank. Dates must be ISO-8601 (a date, or a date-time with `Z`, an offset, or
  no offset = UTC) and are normalised to UTC; anything else is a 400. `price` is a float (see below).
- **Stats:** `ingested` = created + updated; lifetime counters; unknown supplier returns zeroes; 503 if the database file is missing.
- Bodies over 64 KB return 413 (arbitrary cap, not throttled).

## Where I disagreed with or changed Claude's suggestions
- **Throttle algorithm.** I proposed a token bucket (counter of 100, +1 every 10/6 s, rejected requests decrement). Claude showed it admits up to ~200
  requests in one 60 s window and that unbounded debt would lock a burst sender out for ~25 min, and recommended a sliding-window log. We moved to that.
- **Claude's upsert design was wrong, and I pushed back on a related point.** Its first design used one `INSERT ... ON CONFLICT DO UPDATE` and claimed the
  row count distinguished created/updated/unchanged/stale; it can't. Fix: validate in memory first, then one write-locked transaction that logs the request,
  checks the window, reads the stored row and decides. (My own objection that unchanged/stale/throttled requests "don't touch the database" was
  only half right: all of them read it and write a log row, which is why every request counts toward the limit.)
- **`Retry-After` dropped.** Claude suggested 429 + `Retry-After`; I removed the retry mechanism. I read "a stale request is just dropped" as meaning only that
  stale updates are ignored, so it still returns 200 `stale` (a guess; easy to change).
- **Price stored as float.** Claude suggested a decimal stored as text for exact comparison; I chose floats. Identical input text parses to the same double, so
  dedup works, but money should normally be a decimal or integer minor units.
- **One project, two controllers.** I first asked for one project per endpoint, then collapsed it into one project separated by controllers and classes.
- **Claude's tests had gaps.** On review, one test could not detect a rewrite and the "chunked body" test never reached the streaming path; both were fixed
  and the tests were checked by planting bugs (see the transcript).

## With more time
- Store money as decimal/integer cents; normalise or reject odd supplier IDs (case, whitespace).
- A networked database with database-side time, and a real multi-process test in CI (today it's a script plus in-process hosts).
- Return `Retry-After`; make blocked requests cheaper (every request, even a rejected one, is a write today, and the log grows under abuse until pruned).
- Rate-limit unattributable garbage by IP, add auth, and reconsider last-arrival-wins on timestamp ties across instances.

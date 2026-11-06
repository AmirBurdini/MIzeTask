# Supplier Feed Throttle & Dedup Service: Design

Date: 2026-11-06

## Intent

Mize receives reservation updates from external suppliers over a webhook-style feed, on several server instances. Suppliers resend the same update (retries, bugs) and some send far more traffic than they should. This service sits in front of the reservations table and handles both: it never rewrites an identical reservation, and it limits each supplier's request rate. It is a scoped take-home exercise, not a hardened production system. No auth and no observability.

## Goals

- `POST /api/reservations/ingest` saves, deduplicates and updates reservations, and throttles per supplier.
- `GET /api/reservations/stats/{supplierId}` returns how many reservations were ingested and how many requests were throttled.
- Correct under concurrency: several instances sharing one database must never exceed the rate limit or double-write.
- Clean structure that a reviewer can read quickly.

## Non-goals

- Authentication, observability, metrics, production hardening.
- Cross-host deployment. SQLite cannot be shared across hosts; a networked database would be needed (documented in the README).
- Room lookups or any validation of `roomId` beyond "present".
- Rejecting far-future or otherwise odd `updatedAtUtc` values.

## Anti-goals (a failure even if it technically works)

- A throttle that can be beaten by hitting different instances.
- Check-then-write races that allow the 101st request, or two writes of one reservation.
- Last-write-wins by arrival order, which would let a late retry overwrite newer data.
- Counters that drift from what actually happened.

## Tech stack (decided by the user)

- .NET 9, one ASP.NET Core Web API project, SQLite, Dapper with plain POCO classes (no EF, no ORM).
- Everything fits in one project, separated by controllers and classes. Tests are deferred and come after the main code. The exercise still requires them.
- The .NET 9 SDK (9.0.320) is installed on the development machine.

## Structure (decided by the user)

- `ReservationsController` for ingest and `StatsController` for stats. Controllers hold no SQL and no business logic.
- The write side has an ingest service and a repository (throttle, upsert, counters). The read side has a separate read-only query class. They use the same SQLite file.
- Validation lives in its own dedicated class.
- The project creates the schema at startup. Exact class names and folders are left to the builder.

## Behavior

### Ingest pipeline

Each request runs in order:

1. **Parse and validate in memory** (pure logic, no database access, and no database lock held while it runs). The result is held, not acted on yet.
2. **One write transaction, always committed, that takes the write lock up front** (`BEGIN IMMEDIATE`):
   1. Insert the request into the throttle log and count the window (see Throttling).
   2. Throttled: increment the throttled counter, commit, return 429.
   3. Validation failed: commit (the log row stays, so the request still counts toward the limit), return 400 with the error details.
   4. Otherwise read the stored reservation (a SELECT is safe because the write lock is held), decide created, updated, unchanged or stale, write only for created and updated, increment the ingested counter for those two, commit.

A throttled or invalid request still writes to the database (log row, plus the throttled counter for 429). Only the reservation upsert is skipped.

### Throttling

- Exact sliding-window log: at most 100 requests per supplier in any rolling 60 seconds. The 101st gets 429. There is no `Retry-After` header and no retry mechanism (user's decision); the supplier simply tries again later.
- Every request counts toward the window, including rejected, invalid, duplicate and stale ones (user's decision for invalid requests; the rest follows from "a supplier sends more than 100 requests").
- A throttled request increments the supplier's throttled counter.
- The log insert is unconditional (rejected requests are logged too). After inserting, count the supplier's rows in the window including the new one; the request is throttled if the count is greater than 100. The window contains rows newer than `now - 60s`. Break ties between rows with the same timestamp by row order (rowid), not by timestamp.
- The insert and count must run inside the write-lock-up-front transaction described in the pipeline. A plain deferred transaction (read, then upgrade to write) can fail with `SQLITE_BUSY` that the busy timeout does not retry in WAL mode.
- Old log rows (older than 60 seconds) are pruned. The mechanism is left to the builder, but it must be safe under concurrent use.
- Time comes from an injected clock so tests do not need to wait 60 seconds.

### Dedup and update (user's decisions)

- Identity is `(supplierId, reservationId)`. This is my call, not the user's, because two suppliers could reuse an ID.
- No existing record: insert. Response 201, status `created`.
- Existing record, incoming `updatedAtUtc` newer: apply. Response 200, status `updated`.
- Existing record, equal `updatedAtUtc`, different details: apply. Response 200, status `updated`. On a timestamp tie, the last request to arrive wins (documented limitation).
- Existing record, equal `updatedAtUtc`, identical details: no write. Response 200, status `unchanged`.
- Existing record, incoming `updatedAtUtc` older: ignore, no write. Response 200, status `stale`. This is my call on the response shape; the user decided the update must not be applied.
- Implement with a unique key on `(supplierId, reservationId)` as a safety net, and decide the outcome inside the write-lock-up-front transaction: SELECT the stored row, compare, then INSERT or UPDATE only for created and updated. This is race-safe because the lock is held for the whole transaction. A single `INSERT ... ON CONFLICT DO UPDATE ... WHERE` cannot distinguish unchanged from stale (both affect no row) or insert from update, so it is not enough on its own.
- "Same details" compares every field except the identity (`roomId`, `checkIn`, `checkOut`, `price`, `updatedAtUtc`).

### Validation (user's decisions, in a dedicated class)

- `checkIn` must be earlier than `checkOut` (equal is rejected).
- `price` must be zero or more.
- My addition: `supplierId`, `reservationId` and `roomId` must be present and non-blank. Without this a missing field becomes a null key.
- Invalid requests count toward the supplier's rate limit and do not count as ingested.

### Stats

- `ingested` = reservations created plus updated. `unchanged`, `stale` and invalid requests are not counted.
- `throttled` = number of requests rejected with 429.
- Lifetime counters. An unknown supplier returns zeroes, not 404 (my call).
- Counters are incremented in SQL (`n = n + 1`) inside the same transaction as the decision, never read then written back.
- The stats query runs read-only and does not block ingest (WAL mode).

## Request contract and input handling

Answered by the original exercise spec:
- One reservation per request. Flat JSON body with camelCase fields exactly as in the exercise spec (`supplierId`, `reservationId`, `roomId`, `checkIn`, `checkOut`, `price`, `updatedAtUtc`). `supplierId` is in the body, not a header or route. No batch support.

My calls (not the user's), made so the builder does not have to guess:
- **Throttle key.** The throttle runs before model binding, so the ingest endpoint reads the raw body and extracts `supplierId` itself. A request whose `supplierId` is missing, blank or unreadable (including unparseable JSON) has no throttle key, so it returns 400 without being throttled or counted. All other requests, including ones with other invalid fields, are throttled first and then validated.
- **Rejected requests are logged.** A 429 adds a row to the throttle log, as the user's original idea counted rejected requests. Consequence: a supplier that keeps sending while over the limit stays blocked until it has been under the limit for a full 60 seconds. Because there is no `Retry-After`, the supplier has no hint about when to retry (README note).
- **Types.** `checkIn`, `checkOut` and `updatedAtUtc` are `DateTimeOffset` values, normalised to UTC. A value with no offset is treated as UTC. Stored as fixed-format ISO-8601 text (so text ordering equals time ordering). `price` is a floating-point number (user's decision): JSON integers are converted to floats, it is a `double` in C# and a `REAL` column in SQLite, and "same details" compares the doubles. Identical input text always parses to the same double, so `450` and `450.00` compare equal. The README notes the trade-off: floats can't represent some decimal amounts exactly, so money is normally a decimal or integer minor units. A wrong type or missing date or price field returns 400 (the throttle has already counted it).
- **Body size limit.** The raw body read is capped at 64 KB (an arbitrary, generously large limit, as the user asked). A larger body returns 413 and is not throttled, because it is not read far enough to find a `supplierId`. Requests with no usable `supplierId` are never throttled, so garbage traffic is free; this is accepted for this scope and noted in the README.
- **Stats when the database file is missing.** The stats endpoint does not create the file. If it does not exist it returns 503 with a short error body saying the database is not initialised (user's decision that a corresponding response is returned; the status code is my call).
- **Stats response.** `{ "supplierId": "...", "ingested": n, "throttled": n }`. Any `supplierId` string is accepted and unknown ones return zeroes.

## Repository and configuration

- Run `git init` for the submission and add a `.gitignore` covering build output and the SQLite `.db`, `-wal` and `-shm` files.
- The database file path is configuration (for example `appsettings.json` or an environment variable), so several instances can be pointed at one shared file.

## Constraints

- "Multiple instances" means several processes on one machine sharing one SQLite file (user's decision). Use WAL mode and a busy timeout. The README states the networked-database limitation.
- All time comparisons use UTC.
- Throughput is limited by SQLite's single writer. This is acceptable at this scale.

## Testing (deferred, to be planned after the main code)

The exercise requires tests. At minimum: dedup (created, updated, unchanged, stale, tie), throttle (the 101st rejected, window clears, rejected requests count), validation, stats, and a concurrency test that fires 200 or more parallel requests at one supplier and asserts exactly 100 accepted. Whether those requests go through real separate processes or in-process hosts is left open.

## Left to the builder

Class and folder names, the exact SQL, schema column types, how pruning is triggered, the busy timeout value, error body shape, and wiring (Swagger, Dockerfile, none required).

## README deliverable

Assumptions, anything Claude suggested that the user disagreed with or changed, and what to do differently with more time. The submission also includes the repo and the chat transcript.

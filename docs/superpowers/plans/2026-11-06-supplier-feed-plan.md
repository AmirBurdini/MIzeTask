# Supplier Feed Throttle & Dedup Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an ASP.NET Core Web API that ingests supplier reservation updates with dedup and a per-supplier rolling-window throttle, and serves per-supplier stats.

**Architecture:** One Web API project with two controllers. Each ingest request is parsed and validated in memory, then runs in one `BEGIN IMMEDIATE` SQLite transaction that logs the request, checks the window, and (if accepted and valid) reads then writes the reservation and counters. Stats reads the same SQLite file read-only.

**Tech Stack:** .NET 9 SDK 9.0.320, ASP.NET Core controllers, SQLite (WAL) via `Microsoft.Data.Sqlite`, Dapper with POCOs, xUnit and `WebApplicationFactory` for tests.

**Spec:** `docs/superpowers/specs/2026-11-06-supplier-feed-design.md` (the user approved it; executors read both).

**Order note:** the user decided tests come after the main code. Phases 0 to 4 are therefore verified by building and by running the app (manual requests and two smoke scripts). Phase 5 adds the automated tests. Commit steps are for the executor to run only once the user has confirmed commits are wanted; end each commit message with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Global Constraints

- .NET 9, one ASP.NET Core Web API project, SQLite, Dapper with plain POCOs. No EF, no ORM. Everything in one project, separated by controllers and classes.
- Endpoints: `POST /api/reservations/ingest`, `GET /api/reservations/stats/{supplierId}`.
- Throttle: exact sliding-window log, at most 100 requests per supplier in any rolling 60 seconds; window = rows newer than `now - 60s`; the 101st gets 429; no `Retry-After` header.
- Every attributable request counts toward the window: rejected (429), invalid, duplicate and stale ones. Requests with no usable `supplierId` are never throttled (400).
- Order: parse and validate in memory (no DB lock), then one write transaction that takes the lock up front (`BEGIN IMMEDIATE`). The transaction is always committed.
- Identity is `(supplierId, reservationId)`. Newer or equal `updatedAtUtc` with different details: apply. Identical: `unchanged`, no write. Older: `stale`, no write. Responses: 201 `created`, 200 `updated`, 200 `unchanged`, 200 `stale`.
- Validation (own class): `checkIn < checkOut` (equal rejected), `price >= 0`, `supplierId`, `reservationId`, `roomId` present and non-blank. Failures return 400 with the error list.
- Types: `checkIn`, `checkOut`, `updatedAtUtc` are normalised to UTC (no offset means UTC), stored as fixed-format ISO-8601 text. `price` is a double (`REAL`); JSON integers are converted.
- Stats: `ingested` = created plus updated; `throttled` = 429 count; lifetime; unknown supplier returns zeroes; response `{ "supplierId", "ingested", "throttled" }`. If the database file does not exist, stats does not create it and returns 503 with a short body.
- Raw body capped at 64 KB (413 above that, not throttled).
- WAL mode, busy timeout, database path is configuration. Counters are incremented in SQL (`n = n + 1`). Time comes from an injected `IClock`.
- Several instances means several processes on one machine sharing one SQLite file (README states the networked-database limitation).
- No auth, no observability.

## Review Focus

1. A request with a valid `supplierId` but a wrong-typed or missing field (for example `"price": "abc"`) returns 400 and still counts toward the limit. Pinned in Task 12.
2. Body over 64 KB returns 413; an empty or non-JSON body returns 400 and is not throttled. Pinned in Task 12.
3. The same new reservation sent concurrently through several instances produces exactly one `created`, the rest `unchanged`. Pinned in Task 13.
4. `supplierId` is used as given: `Acme` and `acme` are different suppliers, and no trimming is applied to the key. Pinned in Task 12.
5. The same instant written with different offsets (`2026-08-01T02:00:00+02:00` and `2026-08-01T00:00:00Z`) compares equal, so a resend is `unchanged`. A supplier with only throttled requests still returns its stats. Pinned in Tasks 11 and 13.

## File Structure

```
SupplierFeed.sln
.gitignore
README.md
scripts/smoke.ps1                              single-instance request walkthrough
scripts/two-instances.ps1                      two real processes, one DB file
src/SupplierFeed.Api/
  Program.cs                                   wiring, startup schema init
  appsettings.json                             Database:Path
  Configuration/DatabaseOptions.cs
  Infrastructure/IClock.cs                     IClock, SystemClock
  Infrastructure/StoredFormat.cs               timestamp text format
  Infrastructure/ErrorHandling.cs              exception to ProblemDetails mapping
  Data/ISqliteConnectionFactory.cs             + SqliteConnectionFactory
  Data/SchemaInitializer.cs
  Models/IngestRequest.cs
  Models/Reservation.cs
  Models/IngestResult.cs                       IngestStatus, IngestResult
  Models/SupplierStats.cs
  Ingest/IngestRequestParser.cs                raw body to ParsedIngest
  Ingest/IngestRequestValidator.cs
  Ingest/ThrottleLog.cs                        write side
  Ingest/ReservationStore.cs                   write side
  Ingest/StatsCounters.cs                      write side
  Ingest/IngestService.cs                      the transaction
  Stats/StatsQuery.cs                          read side, read-only connection
  Controllers/ReservationsController.cs
  Controllers/StatsController.cs
tests/SupplierFeed.Tests/                      created in Phase 5
```

---

## Phase 0: Scaffold

### Task 1: Repo and solution scaffold

**Files:**
- Create: `.gitignore`, `SupplierFeed.sln`, `src/SupplierFeed.Api/*` (from the template)
- Modify: `src/SupplierFeed.Api/Program.cs`, `src/SupplierFeed.Api/appsettings.json`

**Interfaces:**
- Produces: a building solution; config key `Database:Path` (string, default `data/supplierfeed.db`).

- [ ] **Step 1:** In `C:\Users\I587719\SelfProjects\Mize` run `git init`. Add a `.gitignore` covering `bin/`, `obj/`, `.vs/`, `data/`, `*.db`, `*.db-wal`, `*.db-shm`.
- [ ] **Step 2:** If `dotnet` is not on PATH in the shell, use `C:\Program Files\dotnet\dotnet.exe`. Run `dotnet new sln -n SupplierFeed`, then `dotnet new webapi --use-controllers -n SupplierFeed.Api -o src/SupplierFeed.Api --framework net9.0`, then `dotnet sln add src/SupplierFeed.Api`.
- [ ] **Step 3:** Delete the template's sample controller, model and `.http` file. Add packages `Dapper` and `Microsoft.Data.Sqlite` to the API project. Add `"Database": { "Path": "data/supplierfeed.db" }` to `appsettings.json`.
- [ ] **Step 4:** Run `dotnet build`. Expected: Build succeeded, 0 errors.
- [ ] **Step 5:** Commit: `chore: scaffold solution and api project`.

---

## Phase 1: Data foundation

### Task 2: Options, clock, timestamp format, models

**Files:**
- Create: `Configuration/DatabaseOptions.cs`, `Infrastructure/IClock.cs`, `Infrastructure/StoredFormat.cs`, `Models/IngestRequest.cs`, `Models/Reservation.cs`, `Models/IngestResult.cs`, `Models/SupplierStats.cs`

**Interfaces:**
- Produces:
  - `DatabaseOptions { string Path }`, bound to section `Database`.
  - `IClock { DateTimeOffset UtcNow { get; } }`; `SystemClock : IClock`.
  - `static class StoredFormat { string Timestamp(DateTimeOffset value) }` returns UTC text `yyyy-MM-ddTHH:mm:ss.fffffffZ` (invariant culture) so ordinal ordering equals time ordering.
  - `IngestRequest { string? SupplierId; string? ReservationId; string? RoomId; DateTimeOffset? CheckIn; DateTimeOffset? CheckOut; double? Price; DateTimeOffset? UpdatedAtUtc }`.
  - `Reservation { string SupplierId; string ReservationId; string RoomId; string CheckIn; string CheckOut; double Price; string UpdatedAtUtc }` (times as stored text, property names matching the snake_case columns when `DefaultTypeMap.MatchNamesWithUnderscores = true`).
  - `enum IngestStatus { Created, Updated, Unchanged, Stale, Throttled, Invalid }`; `record IngestResult(IngestStatus Status, IReadOnlyList<string> Errors)`.
  - `record SupplierStats(string SupplierId, long Ingested, long Throttled)`.

- [ ] **Step 1:** Create the files above with exactly those names and shapes.
- [ ] **Step 2:** Run `dotnet build`. Expected: succeeded.
- [ ] **Step 3:** Commit: `feat: add options, clock, stored timestamp format and models`.

### Task 3: Connection factory and schema

**Files:**
- Create: `Data/ISqliteConnectionFactory.cs`, `Data/SchemaInitializer.cs`
- Modify: `Program.cs`

**Interfaces:**
- Consumes: `DatabaseOptions`.
- Produces:
  - `ISqliteConnectionFactory { SqliteConnection OpenWrite(); SqliteConnection OpenReadOnly(); bool DatabaseExists(); }`. `OpenWrite` uses `Mode=ReadWriteCreate` and `Default Timeout=30` (busy timeout in seconds). `OpenReadOnly` uses `Mode=ReadOnly` with the same timeout.
  - `SchemaInitializer.Initialize(ISqliteConnectionFactory)`: creates the parent directory, runs `PRAGMA journal_mode=WAL`, and creates these tables with `IF NOT EXISTS`:
    - `reservations(supplier_id TEXT, reservation_id TEXT, room_id TEXT, check_in TEXT, check_out TEXT, price REAL, updated_at_utc TEXT, PRIMARY KEY (supplier_id, reservation_id))`
    - `request_log(id INTEGER PRIMARY KEY AUTOINCREMENT, supplier_id TEXT NOT NULL, requested_at_utc TEXT NOT NULL)` with indexes on `(supplier_id, requested_at_utc)` and on `(requested_at_utc)`
    - `supplier_stats(supplier_id TEXT PRIMARY KEY, ingested INTEGER NOT NULL DEFAULT 0, throttled INTEGER NOT NULL DEFAULT 0)`

- [ ] **Step 1:** Implement both. In `Program.cs` bind `DatabaseOptions`, register the factory and `IClock` as singletons, set `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true`, and call `SchemaInitializer.Initialize` before `app.Run()`. Several instances may start at once, so the schema statements must be idempotent and tolerate the busy timeout.
- [ ] **Step 2:** Run `dotnet run --project src/SupplierFeed.Api` then stop it. Expected: `data/supplierfeed.db` exists under the project working directory.
- [ ] **Step 3:** Commit: `feat: sqlite connection factory and schema initialiser`.

---

## Phase 2: Ingest path

### Task 4: Parser and validator (pure, no DB)

**Files:**
- Create: `Ingest/IngestRequestParser.cs`, `Ingest/IngestRequestValidator.cs`

**Interfaces:**
- Consumes: `IngestRequest`.
- Produces:
  - `record ParsedIngest(string? SupplierId, IngestRequest? Request)`; `IngestRequestParser.Parse(string body) -> ParsedIngest`. `SupplierId` is non-null only if the body is a JSON object whose `supplierId` is a non-blank string. `Request` is null when the body is not a JSON object. Each field is read by hand from the JSON: a missing, null or wrong-typed field becomes `null` (the validator reports it). Dates are parsed with `DateTimeStyles.AssumeUniversal | AdjustToUniversal`; numbers use `TryGetDouble`, so integers convert.
  - `IngestRequestValidator.Validate(IngestRequest request) -> IReadOnlyList<string>` returns one message per problem: each of the three strings null or blank, `checkIn`, `checkOut`, `price`, `updatedAtUtc` null, `checkIn >= checkOut`, `price < 0`. Empty list means valid.

- [ ] **Step 1:** Implement both as plain classes (no DB, no clock).
- [ ] **Step 2:** Run `dotnet build`. Expected: succeeded. (Behaviour is exercised in Task 7 and pinned by tests in Task 11.)
- [ ] **Step 3:** Commit: `feat: ingest request parser and validator`.

### Task 5: Write-side data access

**Files:**
- Create: `Ingest/ThrottleLog.cs`, `Ingest/ReservationStore.cs`, `Ingest/StatsCounters.cs`

**Interfaces:**
- Consumes: `Reservation`, `StoredFormat`.
- Produces (all take the open connection and the open transaction from the caller; none opens its own):
  - `ThrottleLog.RecordAndCountAsync(SqliteConnection c, SqliteTransaction t, string supplierId, DateTimeOffset now) -> Task<int>`: deletes rows with `requested_at_utc` at or before `now - 60s` (all suppliers, this is the pruning), inserts a row for `now`, returns the count of that supplier's rows newer than `now - 60s` including the new one.
  - `ReservationStore.GetAsync(c, t, supplierId, reservationId) -> Task<Reservation?>`, `InsertAsync(c, t, Reservation)`, `UpdateAsync(c, t, Reservation)`.
  - `StatsCounters.AddAsync(c, t, string supplierId, int ingested, int throttled) -> Task`: upserts the `supplier_stats` row with `ingested = ingested + @ingested, throttled = throttled + @throttled`.

- [ ] **Step 1:** Implement with Dapper and parameterised SQL only.
- [ ] **Step 2:** Run `dotnet build`. Expected: succeeded.
- [ ] **Step 3:** Commit: `feat: throttle log, reservation store and stats counters`.

### Task 6: IngestService (the transaction)

**Files:**
- Create: `Ingest/IngestService.cs`

**Interfaces:**
- Consumes: Task 5 classes, `ISqliteConnectionFactory`, `IClock`, `StoredFormat`.
- Produces: `IIngestService { Task<IngestResult> IngestAsync(string supplierId, IngestRequest request, IReadOnlyList<string> validationErrors) }`, implemented by `IngestService`.

- [ ] **Step 1:** Implement `IngestAsync`: take `now` from `IClock` once. Open a write connection and begin a transaction that takes the write lock up front (`BeginTransaction(deferred: false)`; if that overload is unavailable, execute `BEGIN IMMEDIATE` manually). Call `RecordAndCountAsync`. If the count is greater than 100, `AddAsync(.., 0, 1)`, commit, return `Throttled`. Else if `validationErrors` is non-empty, commit, return `Invalid` with the errors. Else `GetAsync`: none, then `InsertAsync` and `AddAsync(.., 1, 0)` for `Created`. Existing with a stored `updated_at_utc` text greater than the incoming one, `Stale`, no write. Existing with identical `room_id`, `check_in`, `check_out`, `price`, `updated_at_utc`, `Unchanged`, no write. Otherwise `UpdateAsync` and `AddAsync(.., 1, 0)` for `Updated`. Always commit; roll back only on an exception.
- [ ] **Step 2:** Run `dotnet build`. Expected: succeeded.
- [ ] **Step 3:** Commit: `feat: ingest service with throttle, dedup and stale handling`.

### Task 7: ReservationsController and wiring

**Files:**
- Create: `Controllers/ReservationsController.cs`
- Modify: `Program.cs`

**Interfaces:**
- Consumes: `IngestRequestParser`, `IngestRequestValidator`, `IIngestService`.
- Produces: `POST /api/reservations/ingest`. Reads the raw body as text (no model binding). `SupplierId` null returns 400 with a problem body, no throttle. Otherwise validates, calls the service, and maps: `Created` 201 `{ "status": "created" }`; `Updated`, `Unchanged`, `Stale` 200 with the matching lowercase status; `Throttled` 429 `{ "error": "rate limit exceeded" }`; `Invalid` 400 problem body with an `errors` list. Add `public partial class Program { }` at the bottom of `Program.cs` so tests can host the app.

- [ ] **Step 1:** Implement the controller (no SQL, no business rules) and register the parser, validator and service.
- [ ] **Step 2:** Run the app and check by hand (PowerShell `Invoke-RestMethod`/`curl`): first valid body returns 201 `created`; the same body returns 200 `unchanged`; the same body with a changed price and the same `updatedAtUtc` returns 200 `updated`; the same body with an older `updatedAtUtc` returns 200 `stale`; `checkOut` before `checkIn` returns 400; `{}` returns 400.
- [ ] **Step 3:** Commit: `feat: ingest endpoint`.

---

## Phase 3: Stats

### Task 8: StatsQuery and StatsController

**Files:**
- Create: `Stats/StatsQuery.cs`, `Controllers/StatsController.cs`
- Modify: `Program.cs`

**Interfaces:**
- Consumes: `ISqliteConnectionFactory`, `SupplierStats`.
- Produces: `IStatsQuery { bool DatabaseExists(); Task<SupplierStats> GetAsync(string supplierId) }`. `GetAsync` opens `OpenReadOnly`, selects the `supplier_stats` row, and returns zeroes when none. `GET /api/reservations/stats/{supplierId}` returns 200 `{ "supplierId", "ingested", "throttled" }`, or 503 with `{ "error": "database not initialised" }` when `DatabaseExists()` is false.

- [ ] **Step 1:** Implement the query and controller.
- [ ] **Step 2:** Run the app and ingest one new reservation, then call stats for that supplier. Expected: `ingested` 1, `throttled` 0. An unknown supplier returns zeroes. With the database file removed and a fresh process that skips schema init (or by calling `DatabaseExists()` before the file is created), expected 503 and no file created.
- [ ] **Step 3:** Commit: `feat: stats endpoint`.

---

## Phase 4: Hardening and multi-instance proof

### Task 9: Body limit and error handling

**Files:**
- Create: `Infrastructure/ErrorHandling.cs`
- Modify: `Controllers/ReservationsController.cs`, `Program.cs`

**Interfaces:**
- Produces: raw body read capped at 64 KB (65536 bytes); above it returns 413 and is not throttled. An unhandled exception returns 500 `ProblemDetails` without internals; a `SqliteException` with the busy or locked code returns 503.

- [ ] **Step 1:** Implement the capped read (stop reading past the cap rather than buffering the whole body) and register the exception mapping.
- [ ] **Step 2:** Run the app. Post a 70 KB body: expected 413. Post an empty body: expected 400.
- [ ] **Step 3:** Commit: `feat: body size limit and error mapping`.

### Task 10: Two-process smoke script

**Files:**
- Create: `scripts/smoke.ps1`, `scripts/two-instances.ps1`

**Interfaces:**
- Produces: `smoke.ps1` runs the Task 7 walkthrough plus 101 requests for one supplier against a base URL parameter and prints the statuses. `two-instances.ps1` starts two `dotnet run` processes on ports 5001 and 5002 with the same `Database__Path` environment variable, fires 200 parallel requests for one supplier split across both, and prints the count of 201 or 200 responses versus 429 responses, then stops both processes.

- [ ] **Step 1:** Write both scripts.
- [ ] **Step 2:** Run `scripts/two-instances.ps1`. Expected: exactly 100 accepted and 100 throttled, and the stats endpoint reports `throttled` 100.
- [ ] **Step 3:** Commit: `chore: smoke scripts including two-process throttle check`.

---

## Phase 5: Automated tests

### Task 11: Test project, parser and validator tests

**Files:**
- Create: `tests/SupplierFeed.Tests/SupplierFeed.Tests.csproj`, `ParserTests.cs`, `ValidatorTests.cs`
- Modify: `SupplierFeed.sln`

**Interfaces:**
- Produces: an xUnit project referencing the API project and `Microsoft.AspNetCore.Mvc.Testing`.

- [ ] **Step 1:** Create the project and add it to the solution.
- [ ] **Step 2:** Write the tests: `Parse_NonObjectBody_HasNoSupplierId`; `Parse_BlankSupplierId_HasNoSupplierId`; `Parse_WrongTypePrice_LeavesPriceNull`; `Parse_IntegerPrice_ConvertsToDouble` (`450` gives `450.0`); `Parse_OffsetAndZuluSameInstant_AreEqual` (`2026-08-01T02:00:00+02:00` equals `2026-08-01T00:00:00Z`); `Parse_NoOffset_TreatedAsUtc`; `Validate_CheckInEqualCheckOut_Fails`; `Validate_NegativePrice_Fails`; `Validate_ZeroPrice_Passes`; `Validate_BlankReservationId_Fails`.
- [ ] **Step 3:** Run `dotnet test`. Expected: all pass.
- [ ] **Step 4:** Commit: `test: parser and validator`.

### Task 12: Ingest integration tests (dedup, throttle, input handling)

**Files:**
- Create: `tests/SupplierFeed.Tests/TestApp.cs`, `FakeClock.cs`, `IngestTests.cs`, `ThrottleTests.cs`

**Interfaces:**
- Produces: `FakeClock : IClock` with `Advance(TimeSpan)`. `TestApp` is a `WebApplicationFactory<Program>` that points `Database:Path` at a unique temp file and swaps in a `FakeClock`; several `TestApp`s can share one path.

- [ ] **Step 1:** Write the tests and assert: `Ingest_New_Returns201Created`; `Ingest_Identical_Returns200Unchanged`; `Ingest_SameTimestampDifferentPrice_Returns200Updated`; `Ingest_NewerTimestamp_Updates`; `Ingest_OlderTimestamp_Returns200StaleAndKeepsStoredValue`; `Ingest_SameInstantDifferentOffset_Returns200Unchanged`; `Ingest_SameReservationIdDifferentSuppliers_AreSeparate`; `Ingest_SupplierIdCaseDiffers_AreSeparateSuppliers`; `Throttle_101stRequest_Returns429` (first 100 accepted); `Throttle_RejectedRequestsStillCount`; `Throttle_WindowClears_After60SecondsWithoutRequests` using `FakeClock.Advance`; `Throttle_InvalidAndDuplicateRequestsCount`; `Throttle_OtherSupplierUnaffected`; `Ingest_WrongTypePrice_Returns400AndCountsTowardLimit`; `Ingest_NoSupplierId_Returns400AndIsNotThrottled`; `Ingest_NonJsonBody_Returns400`; `Ingest_BodyOver64KB_Returns413`.
- [ ] **Step 2:** Run `dotnet test`. Expected: all pass.
- [ ] **Step 3:** Commit: `test: dedup, throttle and input handling`.

### Task 13: Stats and concurrency tests

**Files:**
- Create: `tests/SupplierFeed.Tests/StatsTests.cs`, `ConcurrencyTests.cs`

- [ ] **Step 1:** Write `Stats_CountsCreatedAndUpdatedOnly` (unchanged, stale and invalid are not counted); `Stats_CountsThrottledRequests`; `Stats_OnlyThrottledRequests_StillReturnsStats`; `Stats_UnknownSupplier_ReturnsZeroes`; `Stats_MissingDatabase_Returns503AndDoesNotCreateFile`. Write the concurrency tests with three `TestApp`s sharing one database path: `Concurrent_200Requests_ForOneSupplier_Accept100Exactly` (parallel requests spread across the hosts, assert exactly 100 of 201/200 and 100 of 429, and stats `throttled` equals 100); `Concurrent_SameNewReservation_OneCreatedRestUnchanged` (50 parallel identical requests, exactly one 201, no 429s).
- [ ] **Step 2:** Run `dotnet test` three times in a row. Expected: all pass each time (guards against flakiness).
- [ ] **Step 3:** Commit: `test: stats and multi-host concurrency`.

---

## Phase 6: Documentation and wrap-up

### Task 14: README and final verification

**Files:**
- Create: `README.md`

- [ ] **Step 1:** Write a half-page README covering: assumptions (identity pair, stale/tie rules, rejected requests count, unattributable requests unthrottled, float price, 64 KB cap, 503 when the file is missing, several processes on one SQLite file, networked database needed across hosts); what Claude suggested that you changed (the token bucket was replaced by the sliding-window log, the single upsert statement was replaced by an in-transaction SELECT, `Retry-After` was dropped, price as float instead of decimal, one project instead of two); what you'd do differently (decimal money, prune strategy, `Retry-After`, a real shared database, real multi-process test in CI). Fill the "changed" section from the actual chat.
- [ ] **Step 2:** Run `dotnet build` and `dotnet test` from the repo root, and `scripts/two-instances.ps1`. Expected: clean build, all tests pass, 100 accepted and 100 throttled.
- [ ] **Step 3:** Commit: `docs: readme`.

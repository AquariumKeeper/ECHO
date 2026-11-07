# ADR 0007: Live session control over named pipes

Status: accepted, 2026-10-30.

## Context

C10 (ledger F85) gives the CLI control of a running desktop window, as the Go
app's `session` commands did: discovery, inspection, resolution, history,
revision-guarded edits, undo and redo. The plan asks for named pipes,
authenticated bounded requests, revision guards and atomic batches. The Go app's
registry and endpoint addresses are not reused (ADR 0002); this is the app's own
protocol.

## Decision

**Layers.** Application's `SessionEndpoint` answers requests for one
`PlannerSession` with no transport: `inspect` (optionally waiting up to 60 s for a
newer revision), `apply`, `undo` and `redo`. Edits go through the session's
revision guard as `EditOrigin.Session` entries, one undo step per batch.
Infrastructure owns the transport and registry. Desktop hosts its session. The
CLI is the client.

**Registry.** Each window publishes `<session id>.json` atomically in a per-user
folder: `%LOCALAPPDATA%\CharacterPlanner\alpha\sessions` by default, overridden by
an absolute `CHARACTER_PLANNER_SESSION_DIR` or the CLI's `--session-dir`. The entry
holds the session id, process id, start time, pack identity, pack folder and
manifest hash, pipe name and token. Closing the window removes it. A crash leaves
a stale entry. A registration counts as dead when its process id is not running, or
belongs to a process that started after the registration (a reused id). Clients
list dead entries as unavailable without contacting them, and the next window to
start removes them. Listing reads at most 128 valid entries, after filtering, and
never creates the folder. Clients probe windows in parallel.

**Transport.** One request and one reply per connection over a named pipe, each a
4-byte little-endian length and UTF-8 JSON (at most 16 MiB). Both ends use
`PipeOptions.CurrentUserOnly`, so only the same user can connect or serve. Each
request carries the window's random 256-bit token, compared in constant time; the
token is only in the per-user registry and is never printed. A connection's
request must arrive within 10 s. One accept loop hands each connection to its own
task, up to 64 at once, so clients waiting for a change never keep others out.
After replying, the window waits up to 10 s for the client to close. No client's
failure, malformed request or stalled read stops the window serving others.
Disposing removes the registration first; stopping the pipe is bounded to 2 s, never
throws, and runs off the closing UI thread.

**Revisions.** `apply`, `undo` and `redo` require the revision the caller read.
A stale one is refused unchanged with the current snapshot; the CLI exits 6.

**Resolution.** The window does not resolve for the CLI. `session resolve`
inspects the window, then resolves its build and assumptions with the CLI's own
resolver on the pack folder the window registered, after checking the manifest
hash still matches. Its output is `build resolve`'s, plus the session and revision.

**Hosting failures.** A window that cannot host keeps working and says live
control is unavailable in its status line.

## Consequences

- The CLI's exit codes gain 4 for no, unknown, ambiguous or unavailable sessions
  and 6 for a stale revision.
- Packaging's desktop smoke uses a scratch registry, and the packaged CLI must list
  the packaged window as an available session.
- Non-Windows hosts use .NET's Unix-socket implementation of named pipes and have
  not been verified.

## Addendum: a second probe before no window (2026-11-01)

Hosted CI failed intermittently on two session tests, both with `session-none`: on a
loaded runner a live window didn't answer the 2 s probe in time, so selection without
`--session` reported no window. A window busy with a long simulation can do the same
for a user. Selection now probes the silent registrations of running processes once
more when the first round finds no available window; registrations of ended processes
are still skipped without contact, and a round that finds a window is not repeated.
`CliAppTests.ASilentButRunningWindowIsProbedOnceMoreBeforeNoneIsReported` holds a fake
window silent past the first probe and relays later requests to a real one; it fails
without the second probe. The waiting-clients test now names its window, since it checks
serving under waiting clients, not discovery.

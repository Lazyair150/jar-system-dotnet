# ADR-0001: Quality gates and runtime lifecycle

## Status

Accepted

## Date

2026-10-18

## Context

The project already has a strong nine-step verification loop, a deterministic rules kernel, contract
snapshots, protocol checks, solver differential checks, and replay. The new engineering-skill rollout
requires this quality bar to become explicit, executable, and resistant to silent weakening. A second
review also found lifecycle and hosting gaps that the existing gates did not cover:

- `Reset()` cleared the world but left active/recent processes and process journal state alive;
- the process outcome used the execution session, not the final completion result;
- the focused xUnit project was outside the solution and the verification loop;
- direct DLL execution used the repository root as content root, so `wwwroot` was unavailable;
- HTTP scenario endpoints and WebSocket initial snapshots lacked explicit boundary control.

## Decision

1. Keep `重构报告/verify.bat` as the only full gate. Add the focused suite and `quality-gate.ps1` to it
   instead of creating a parallel verification path.
2. Treat reset as a process lifecycle boundary. Clear process work/active/recent/journal state, reset
   the logical clock, modifier window clock, and journal sequence numbers. Keep process IDs and the
   cumulative creation count monotonic so a stale handle cannot collide with a new process.
3. Use the final `IOrderExecutionCompletion` result as the process outcome whenever completion has run.
4. Keep the core package-free. Internal process ports may be visible to the focused test assembly only
   through `InternalsVisibleTo`.
5. Resolve Web content root and web root explicitly so `dotnet run`, direct DLL execution, and published
   output agree. Validate Host and Origin and cap scenario bodies at the HTTP boundary.
6. Do not enable the full .NET recommended-analyzer set in this change: it reports 42 existing naming,
   culture, and style findings that are not part of the current behavioral contract. Compiler warnings
   are errors, and the quality floor checks the structural invariants that matter here.

## Alternatives Considered

### Add a second CI script

Rejected. Two verification definitions drift. The existing script is already machine-checkable and is
used by both local runs and CI.

### Keep reset as a partial world reset

Rejected. A process holding references to removed workshops/orders can continue and write misleading
events into the new world.

### Make `dotnet test` the only test entry

Rejected for this repository. xUnit v3 uses the executable test runner; the project documents and verifies
`dotnet run --project test/JarSystem.Tests`. The gate calls that exact command.

### Add CSP now

Deferred. The page currently uses inline `onclick` handlers. A CSP without a markup refactor would need
`unsafe-inline` and would provide little additional protection; baseline headers and Host/Origin checks
are enforceable without changing the scene editor UI.

## Consequences

- `journalBytes` after the reset session shrinks because the journal is now correctly cleared.
- The focused suite is a hard gate; deleting or skipping it fails `QUALITY-OK`.
- Direct DLL execution serves static files and loads `appsettings.json` from the output directory.
- The remaining `eventHistory` schema looseness and volatile feasibility timing are explicit open items,
  not silently accepted contract behavior.

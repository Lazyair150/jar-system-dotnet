# Implementation Plan: Skill-driven quality hardening

Date: 2026-10-18

## Overview

Apply the newly installed engineering-skill workflows to the existing JarSystem repository without
weakening its deterministic rules kernel. The work is a hardening pass: establish a quality contract,
make every existing test suite a real gate, fix defects found by the review, and leave deployment/runtime
behavior verifiable from the repository.

## Architecture Decisions

- Keep the existing nine-step verification loop as the single source of truth; add the focused xUnit
  suite and the quality floor as sub-checks rather than create a second pipeline.
- Treat `Reset()` as a world-lifecycle boundary: active/recent processes, process journal, logical clock,
  and modifier window clock must restart with the world; process IDs and the cumulative creation count
  stay monotonic so stale handles cannot collide with new processes.
- Treat the final completion result as authoritative for process outcome. A session that walked its plan
  successfully is not necessarily a successfully delivered order.
- Keep the core free of package and host-framework references. Test packages remain in the test project.
- Resolve Web content root/web root explicitly so direct DLL execution and `dotnet run` behave the same.

## Task List

### Phase 1: Contract and quality floor

- [x] Read the available skill definitions and select the applicable lifecycle.
- [x] Write `CONSTRAINTS.md`, `AGENTS.md`, `.editorconfig`, `global.json`, and build defaults.
- [x] Add a machine-checkable floor guard and evidence-count checks.

### Checkpoint: Foundation

- [x] Release build is clean.
- [x] Existing nine-step gate still passes.

### Phase 2: Defects found by review

- [x] Add regression tests for reset lifecycle and completion outcome.
- [x] Fix process runtime reset and journal sequence reset.
- [x] Fix completion result / process outcome divergence.
- [x] Make `WorldQuery` vocabularies truly read-only.
- [x] Validate scenario windows and intensity factors; saturate intensity arithmetic.
- [x] Add the focused xUnit project to the solution and verification loop.

### Checkpoint: Kernel

- [x] Focused tests pass (21 total).
- [x] Legacy deterministic baseline remains 32.
- [x] Replay remains byte-identical.

### Phase 3: Web and input boundary

- [x] Resolve `wwwroot` for direct DLL execution.
- [x] Validate Host and Origin; add baseline response headers.
- [x] Cap scenario request bodies at 1 MiB.
- [x] Serialize initial WebSocket snapshots through the shared gate.
- [x] Make scenario saves atomic within the config directory.

### Checkpoint: Runtime

- [x] Direct DLL startup serves `/`.
- [x] `/api/state` returns 200 with security headers.
- [x] Invalid Host returns 400; cross-origin returns 403; oversized body returns 413.
- [x] WebSocket receives `world`, `init`, survives bad JSON with `error`, then serves `state`.

### Phase 4: Documentation and handoff

- [x] Record the architectural decisions in an ADR.
- [x] Record the skill matrix, findings, and verification evidence.
- [x] Update the process-system baseline and test README.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Contract drift during hardening | High | Byte snapshot, schema, protocol, replay, and config-equivalence gates stay in the same loop |
| Test project was previously outside the solution | High | It is now built by the Release gate and counted by the quality floor |
| Reset semantics change replay bytes | Medium | Both original and replay execute the same reset; replay gate confirms equality |
| Direct DLL execution differs from `dotnet run` | Medium | Explicit content-root and web-root resolution plus runtime smoke |
| Browser-only UI defects remain | Medium | Runtime HTTP/WS smoke covers transport; DevTools MCP is not configured |

## Open Questions

- Whether `eventHistory` should become a closed typed union (currently the only known `loose=1`).
- Whether `feasibilityResult.timestamp` should be renamed or replaced by logical-tick freshness.
- Whether scenario hot reload should be a host feature or a restart-only workflow.

# Constraints

Last reviewed: 2026-10-30

This is the project quality contract. It applies to every code, contract, and verification change.

## How To Read This File

The statements below are of three kinds, and they age differently. Keeping them in one
undifferentiated list is why this file read as harder than it actually is.

- **Invariants** — properties of any correct version of this system. They never expire, and
  violating one is a correctness bug rather than a style preference.
- **Boundaries** — they protect a design choice, not a truth. Each one states its reason and the
  condition under which it stops applying. A boundary may be moved, but only when there is
  evidence that the stated reason no longer holds — never because it is inconvenient for a change.
- **Baseline** — numbers measured by the last intentional verification. They are checkpoints to
  move deliberately, not ceilings and not prohibitions.

Moving a boundary requires editing this file **with the evidence, in its own change**, so that the
removal is reviewable on its own. That is the same rule as the last invariant, stated from the other
side.

## Invariants

| # | Rule | Why it is an invariant |
|---|---|---|
| I1 | Do not delete or weaken an assertion, deterministic case, contract key, protocol message, or replay check. | These are the repository's only independent evidence that behavior is preserved. Removing one does not make the system simpler — it makes the system unverifiable, which is the one thing a refactor cannot recover. |
| I2 | The same command sequence must replay to byte-identical state, command log, and process journal. | The system is event-sourced and `WorldRevision` is the log sequence (`src/JarSystem.Core/JarSystem.cs`), so replay equality is a consequence of the architecture, not an added test. Any behavior that breaks it has changed the world model. |
| I3 | A bug fix requires a regression test that fails before the fix. | Without the failing case there is no evidence the fix addresses the reported defect rather than a nearby one. This project has already produced fixes that were correct in intent and wrong in fact. |
| I4 | Contract changes move the snapshot, schema, `contract.d.ts`, protocol inventory, and replay asset together in one intentional change. | These five artifacts describe one surface. Letting them drift apart makes each of them individually unreadable and the combination self-contradictory. |
| I5 | Release builds carry zero compiler warnings. | Enforced mechanically by `Directory.Build.props` (`TreatWarningsAsErrors`), so this is a statement about the build, not a request to reviewers. |
| I6 | Do not commit secrets. Treat save files, scenario JSON, WebSocket payloads, and config files as untrusted input. | The system parses attacker-controlled documents and serves them over a WebSocket. Trust in this layer cannot be recovered by a later check. |
| I7 | This file is not relaxed in the same change that needed the relaxation. | Otherwise the contract is whatever the last change wanted it to be, and it stops being a contract. |
| I8 | Keep the core portable: no engine/UI/host framework, no timers, no core-owned concurrency, no unseeded randomness, and every wall-clock read registered with its reason. | The core is meant to be lifted into another engine, which supplies frames and callbacks but not deterministic simulation, replayable command logs, or domain projection. Each of those four prohibitions, if broken, would surface only as "the port behaves differently" — never as a failing test. Enforced mechanically by `test/JarSystem.Tests/PortabilityTests.cs`, whose allowlist records the exact count and the reason for every permitted occurrence, so a new occurrence cannot slip in unnoticed. |

## Boundaries

| # | Rule | Reason | Stops applying when |
|---|---|---|---|
| B1 | Keep the core (`src/JarSystem.Core`) free of external package references and host frameworks. | The core must run identically in two hosts (Web and Godot), must be deterministic under replay, and must not inherit a third party's release schedule or supply-chain risk. | A capability is needed that cannot be written in-repository at acceptable cost, **and** it is either deterministic and host-free, or it lives entirely behind a port that the core does not depend on. Such a capability is admitted as a new boundary with an explicit statement of which of the three reasons it does not violate. |
| B2 | Do not add `NotImplementedException`, empty `catch` blocks, `TODO`/`FIXME` placeholders, or disabled tests. | These read as "done" to every later reader and to every search for unfinished work. The cost is invisible until someone depends on the stub. | Never as a merge state. A short-lived local stub is a drafting aid, not a repository state. |
| B3 | Do not add `SuppressMessage`, `#pragma warning disable`, or test `Skip` without an explicit, dated exception in this file. | A suppression records that a rule was wrong once. Without a record, the next reader cannot tell a considered exception from an accident. | The exception is written into the Exceptions section below, with the reason and the date. The suppression itself then becomes part of the baseline. |
| B4 | Do not change package versions, world schema, command/message names, or persisted keys **as a side effect**. | These are the system's outward identity. Changing one incidentally makes an unrelated diff unreviewable. | The change is the point of the change: it is named in the commit, the five contract artifacts move with it per I4, and no unrelated behavior moves in the same change. The `as a side effect` qualifier is load-bearing — a deliberate single change is the mechanism this boundary leaves open. |

## Baseline

These are measured values from the last intentional verification. A row moves when a change
deliberately moves it and the change is reviewed as a baseline move; a row does not move by being
weakened or skipped. `AGENTS.md` states the operative rule: do not **silently** re-baseline.

| Dimension | Rule | Checked by | Runs at |
|---|---|---|---|
| Build | Release build, zero compiler warnings | `dotnet build JarSystem.slnx -c Release --no-incremental` | every verification |
| Godot host | Release build, zero compiler warnings | `dotnet build hosts/godot/Jar/jar.csproj -c Release` | every verification |
| Legacy suite | 0 bugs / 2000 random + 0 bugs / 3000 capacity | `test/JarSystem.Test` | every verification |
| Determinism | At least 32 deterministic cases; no reduction | `verify.bat` log + `quality-gate.ps1` | every verification |
| Focused suite | At least 132 xUnit tests; 0 failed/skipped/not-run | `test/JarSystem.Tests` | every verification |
| Contract snapshot | Exactly 226 state-key lines against the checked-in baseline | `--dump-contract` + `fc` | every verification |
| Schema | 11 DTO types, `degraded=0`, current known `loose=1` only | `--dump-schema` | every verification |
| Config/world equivalence | Byte-identical to built-in world | `CONFIG-WORLD-MATCH` | every verification |
| Protocol | 21 commands / 16 messages, payload guards pass | `--check-protocol` | every verification |
| Solver | Production solver agrees with reference solver | `SOLVER-OK` | every verification |
| Modifier differential | 400 scenarios / 1600 checks agree | `MODDIFF-OK` | every verification |
| Replay | State, command log, and process journal are byte-identical | `REPLAY-OK` | every verification |
| Scenario data | Shipped sample validates | `SCENARIO-OK` | every verification |
| JavaScript | `node --check` passes when Node is installed | `JS-OK` | every verification |
| Quality floor | No new suppressions, stubs, skipped tests, or core packages | `重构报告/quality-gate.ps1` | every verification |
| Portability | Zero engine/host/threading references in core; every wall-clock read and unseeded default registered with a count and a reason | `test/JarSystem.Tests/PortabilityTests.cs` | every verification |

The complete gate is `重构报告\\verify.bat`; it must end with both `VERDICT=OK` and `QUALITY-OK`.
CI consumes that same script, so there is only one verification definition.

## Measured, Not Yet Enforced

| Metric | Today | Direction |
|---|---|---|
| Full contract schema | `loose=1` (`eventHistory` value union) | tighten only with an explicit contract migration |
| Web runtime browser test | Not automated; no Chrome DevTools MCP configured | add when browser automation is available |
| Mutation score | Not measured | establish a scoped baseline before enforcing |

## Exceptions

None.

## Change Discipline

- Bug fixes require a regression test that fails before the fix.
- Structural changes must remove branches or concepts, not merely relocate them.
- Refactors and behavior changes are reviewed separately when practical.
- Contract changes require the snapshot, schema, `contract.d.ts`, protocol inventory, and replay asset
  to move together in one intentional change.

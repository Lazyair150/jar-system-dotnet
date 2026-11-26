# 2026-11-27 World V2 demand snapshot ordering test correction

## Goal

Resolve the single xUnit failure reported by Core PR #63 verification run `45860027791` without weakening the explicit non-default liquid identity/polarity contract.

## Starting point

- Repository: `Lazyair150/jar-system-dotnet`
- Branch: `architecture/core-liquid-identity-consolidation`
- Failing CI head: `b597dfafbe8e4df54b1d605f38f6863c474205a3`
- Failing run: `45860027791`
- Test: `WorldV2ApplicationServiceTests.ApplicationOperationsPreserveExplicitNonDefaultLiquidPolarities`

## Evidence and root cause

The archived `verify-log` artifact (artifact `15136918438`) contains the exact failure:

- `GetWorldSnapshot().Demands` returned the Passive template demand first (ID 2), then the Active direct demand (ID 1).
- The test created the Active demand first and the Passive demand second, then incorrectly treated snapshot enumeration as insertion order.
- The first collection callback therefore compared expected ID 1 with actual ID 2.
- The failure was in the test's ordering assumption, not in the liquid identity or polarity projection. The log's actual DTOs retained their distinct demand IDs, areas, and order-definition keys.

## Change

In `test/JarSystem.Tests/WorldV2/WorldV2ApplicationServiceTests.cs`:

- Replaced positional `Assert.Collection` assertions with lookup by stable demand ID.
- Kept both assertions that the direct and template-created demand each retain liquid identity `coolant`.
- Left production ordering and runtime behavior unchanged.

This keeps the test focused on its stated contract: non-default liquid identity survives both ApplicationService entry paths.

## Commit

- Test correction: `c2e936a2a1dd6a901ab9d5465c6df997e26329d9`
- Work-record index update: `c8c799780062f741e44a5ccdc4c817cb8caa983d`

## Validation

- Failure reproduced from the archived CI log with exact expected/actual IDs.
- New-head workflow: Run `45867857842`, verify job completed `SUCCESS` on `f4f2151db445afc794d34d8d7d1932dd0d3b40dd`.
- This closes the Core verification item for the corrected head. It does not establish Space integration or desktop GUI acceptance.

## Remaining

- Obtain a new verification result for the corrected Core head.
- Then validate the exact Core API/bridge pin consumed by Space and run the Space static, build, headless, and GUI acceptance stages.

# 9l — Preserve exact rule provenance in concrete plan operations

## Why this slice is required

The symbolic rule candidate already carries `RuleId` and `RuleVersion`, but the concrete `SolverOperation` previously dropped both fields. A materializer could therefore create a physically bound path that described the state transition but could not prove which exact rule definition authorized it.

That loss is unacceptable for the symbolic-to-concrete boundary: rule-set fingerprints say which catalog was analyzed, while each operation must retain the exact candidate selected from that catalog.

## Contract added

- `SolverOperation` carries optional `RuleId` and `RuleVersion`. Optionality preserves compatibility with legacy solver paths.
- `WorldPlanOperation` freezes both values when `WorldOrderSolvePlan.FromMaterializedPaths` copies a solver path.
- A symbolic rule node rejects candidates without a non-empty RuleId and positive RuleVersion.
- `WorldOrderResourceManifest.ValidateStep` rejects partial or malformed provenance: both fields must be absent (legacy path) or both present and valid.
- The values survive the immutable plan projection; source-path mutation cannot rewrite the stored plan.

## Deliberate scope boundary

This change does not yet create concrete paths from symbolic programs. It closes a data-loss hole that would otherwise make the forthcoming materializer incapable of preserving exact rule identity. Physical Jar binding, schedule witness replay, operation snapshots, and ordered staging simulation remain separate steps.

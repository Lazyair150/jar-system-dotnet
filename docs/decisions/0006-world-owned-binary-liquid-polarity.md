# ADR-0006: World-owned binary liquid identity and Jar polarity

- Status: Accepted; implementation in progress on PR #63 (exact-head CI pending)
- Date: 2026-11-25
- Decision owners: Jar Core domain contract
- Related audit: `docs/architecture/2026-11-core-multi-angle-audit.md`

## Context

A World has exactly two liquid identities. They are consumables for selected Jar operations and are mutually exclusive inside an individual Jar. Empty Jar is the third derived state.

The existing implementation violates the intended abstraction:
- `JarPolarityResolver` maps the literal names `酒` and `蜜`.
- `WorkshopCapability.PolarityMode` asks whether those literal names are banned.
- `WorldOptions.Validate` rejects a world that renames or reorders the two liquids.
- Solver/executor/rulebook code enumerates `Constants.LIQUIDS`.
- `Jar` already carries a per-instance `Vocabulary`, but polarity resolution bypasses it.

Space's names `燃料` and `冷却液` must therefore be legal world identities without changing the Core algorithm.

## Decision

1. Liquid identity is world data. It is an opaque, stable key used in inventory dictionaries, operation costs, order requirements, production rates, Ban entries, events and snapshots.
2. Polarity is a separate binary structural property. Each World explicitly assigns exactly one of its two liquid identities to Cathode and the other to Anode. The assignment is not inferred from display names or list order.
3. A World vocabulary is immutable and owns this mapping. No process-global mutable registry is allowed.
4. A Jar resolves its polarity through the exact vocabulary with which it was created. Deep clone, pool rent/recycle, restore and snapshot validation must preserve/check vocabulary identity and mapping.
5. Workshop Ban governs only the workshop's free-liquid staging account (pay/receive/transfer admission). It does not derive a Jar-operating polarity mode and cannot block MATCH, Jar-internal consumption, or physical Jar migration. If a future workshop-specific Jar-operation polarity policy is required, it must be a separate explicit capability, never inferred from Ban.
6. Solver and executor iterate the vocabulary supplied by the World/solver context. Static default constants may remain only as a backwards-compatible default-world fixture, never as the production algorithm's liquid domain.
7. World configuration validates: exactly two unique liquid identities; exactly two mapping entries; keys equal the liquid identity set; values contain Cathode and Anode exactly once.
8. Snapshot/fingerprint formats serialize the identity and polarity assignment explicitly in a stable canonical order. Mapping changes are content changes and must invalidate/revise the World fingerprint.
9. Unknown liquid identities or incomplete mappings fail closed at World assembly/restore boundaries; they must not be silently dropped or assigned a polarity by fallback.

## Candidate API shape

The exact public type names may be refined during implementation, but preserve these semantics:

- `LiquidPolarity` enum in a dependency-safe configuration/value layer: `Cathode`, `Anode`.
- `WorldOptions.LiquidPolarities: Dictionary<string, LiquidPolarity>`.
- `WorldV2Options.LiquidPolarities: Dictionary<string, LiquidPolarity>`; the World V2 factory passes this exact mapping into its immutable Vocabulary.
- `Vocabulary` captures a read-only identity-to-polarity mapping and exposes `TryGetPolarity` / `ResolvePolarity`.
- `Jar.Polarity` resolves through its captured vocabulary.
- `WorkshopCapability` receives the World liquid identity vocabulary for staging-account validation/enumeration; Ban is not translated into Jar polarity compatibility.
- Pure solver facts either carry the resolved polarity or evaluate it through the same immutable Workshop/World rule context. They must not call a static name resolver.

## Space mapping

Space will author its own mapping in the Space world profile:
- `燃料` → Anode
- `冷却液` → Cathode

These names and this particular assignment are Space content. They must not be embedded in Jar Core source.

## Compatibility and migration

- Default World retains the existing behavior: `酒` → Cathode, `蜜` → Anode.
- Existing JSON with omitted mapping may be accepted only by a versioned legacy migration path for the exact legacy default vocabulary. It must not silently infer mappings for arbitrary renamed identities.
- World fingerprints/schema versions must change when the new mapping becomes part of the canonical contract.
- Old checkpoints are either migrated deterministically using their original exact default vocabulary or rejected with a typed compatibility error. No partial restore.
- Different Worlds may use different identities and even opposite assignments concurrently; no shared static mutable state.

## Required proof

- Rename-isomorphism: replace both liquid identities with arbitrary new unique IDs while preserving polarity assignments and all corresponding keyed data. Solve results, operation topology, resource accounting and final state remain structurally equivalent after identity substitution.
- Polarity-swap: swap the two assignments and all corresponding identity-labelled data; results transform consistently rather than retaining name-based behavior.
- Ban-scope: banning a staging liquid blocks free-liquid storage/use/transfer only; MATCH, POUR_OUT, Jar-internal RESHAPE consumption and ordinary Jar migration are not rejected merely because the Jar contains the banned liquid.
- Multi-world isolation: two simultaneous worlds with different names/mappings never resolve each other's liquid identities.
- Pool/clone/restore: no Jar can be rented from a pool under a mismatched vocabulary.
- Core/Space bridge: exact mapping survives config serialization, fingerprinting, DTO/snapshot and reload.

## Consequences

This is a cross-cutting contract change, not a local alias patch. It touches config, vocabulary, Jar, workshop capability/rules, solver projections, execution, world snapshot/checkpoint, tests, bridge schema and Space authored options. It must be delivered as one coherent migration with compatibility tests; intermediate partial states must not be merged.


## Implementation checkpoint (2026-11-25)

- Implemented explicit `LiquidPolarity` and immutable `Vocabulary` mapping; `Jar.Polarity` resolves through its captured vocabulary.
- Both legacy `WorldOptions` and World V2 `WorldV2Options` now carry `liquidPolarities`, validate exactly one Cathode and one Anode, and serialize the mapping. World V2 factory passes the mapping into `Vocabulary`; World V2 canonical configuration fingerprint automatically includes the serialized property.
- Context-free `JarPolarityResolver` overloads and `JarFacts` default inference have been removed. Production solver facts must receive polarity from the active World/RuleContext.
- Workshop Ban no longer defines Jar-operation polarity. It remains a staging admission policy; the legacy `WorkshopPolarityMode` is retained only as an Unrestricted transition-contract projection.
- Added tests for custom identity mapping, list-order independence, liquid rename isomorphism at the polarity projection, mapping swap, staging-only Ban, World V2 custom mapping factory construction, JSON round-trip and configuration fingerprint sensitivity.
- Core exact-head CI remains pending; additional test and static-scan failures may require further changes. Do not treat this checkpoint as complete until the complete suite passes.

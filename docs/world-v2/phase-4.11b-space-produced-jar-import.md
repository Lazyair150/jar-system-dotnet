# Phase 4.11b — idempotent discrete Jar production ingress

## Domain boundary

Space owns energy-stone pickups, parser selection, parser attributes, typed input queues, and per-shape parsing progress. When Space has completed whole empty Jars, it submits only the resulting shape/count batch to Jar.

This is deliberately separate from `UpdateProducerRates`:

- `UpdateProducerRates` changes an existing time-driven Jar producer's rates and preserves its fixed-point accumulators.
- `ImportProducedJars` imports already-completed empty Jars from an external production domain into the Jar central warehouse.
- The energy-stone parser must not be represented as a time-driven Jar producer. Its input is typed, finite Space inventory and its progress is owned by Space.

Jar receives no energy-stone type, pickup ID, volume, parser identity, percentage, or Space equipment data. It receives a stable operation ID plus positive whole counts keyed by configured Jar shape.

## Command contract

`WorldV2ApplicationService.ImportProducedJars(operationId, jarCounts)`:

- executes under the Host exclusive-operation gate, serialized with world advancement and other mutations;
- requires a non-empty operation ID (maximum 128 characters) and a non-empty shape/count map;
- validates all shapes against the current world's shape vocabulary and all counts as positive integers before mutation;
- creates only empty Jars using the current world's vocabulary;
- imports into the central warehouse, never directly into Master/Slave or a workbench staging area;
- applies the existing central warehouse capacity: accepted Jars are stored, overflow is discarded and counted;
- writes production/discard resource-ledger facts in the same idempotent operation batch;
- uses the resource ledger's persisted operation-ID set, which is already part of the execution checkpoint, so retry after an ambiguous response or checkpoint restore cannot duplicate the batch;
- fingerprints the canonical shape/count payload. Reusing one operation ID with a different payload is rejected; key ordering does not affect identity.

The batch is prevalidated and all accepted Jar entities are constructed before the ledger operation is committed. The warehouse mutation after that commit is limited to storing those preconstructed entities and applying the precomputed discard count.

## Receipt semantics

First application returns submitted, stored, and discarded counts. A repeated identical operation returns `WasDuplicate=true` and performs no mutation; its zero count fields mean "no new mutation on this call", not that the original batch was empty. Reusing the ID with different counts is a domain rejection.

## Ownership and lifecycle

The Bridge exposes the typed Core result. The Godot Runtime converts the shape/count dictionary at the boundary, and the Space ship host forwards the batch. The command is rejected by the Runtime after fault or disposal.

The next Space-side parser implementation will retain its own pickup identities, remaining stone volumes, per-shape fractional progress, and pending output batch IDs in Space battle state. It will call this import only after whole-Jar outputs are determined. The parser's pre-battle percentage remains part of its versioned parser definition and frozen loadout; it is not an output-allocation percentage among shapes.

## Regression coverage

- multi-shape import creates only empty Jars in the central warehouse;
- duplicate operation IDs do not duplicate inventory;
- reordered shape keys produce the same payload identity;
- reusing an ID with changed payload is rejected;
- warehouse overflow is discarded and accounted for in the resource ledger;
- unknown shapes are rejected before any inventory or ledger mutation;
- import deduplication survives execution checkpoint JSON restore.

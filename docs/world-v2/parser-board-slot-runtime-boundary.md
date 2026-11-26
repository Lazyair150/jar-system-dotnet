# Parser Board Slot Runtime Boundary

Status: architecture contract and implementation audit, 2026-11-24.

This document records the current parser ownership boundary before any multi-parser or parser-rule ingress work. It is intentionally not a claim that Jar Core executes parser processing.

## 1. Ownership: Parser is a Space production device

The ore parser converts finite energy-stone pickup volume into completed empty Jars. It is not a Jar Core time-driven Producer and must not be represented by `LiquidProduction`, `ProducerModule`, or a synthetic workshop rule.

The current pipeline is:

1. Space encounter/drop logic creates a typed energy-stone pickup with a stable pickup ID, configured Jar-shape type, and finite volume.
2. The ship acquires the pickup. Space passes its exact identity and payload to the selected parser runtime.
3. The parser keeps an independent FIFO input queue, fixed-point processing remainder, and sub-Jar progress accumulator for every configured shape.
4. Parser throughput consumes only queued finite volume. It never manufactures input from elapsed time alone.
5. A whole-Jar threshold creates a pending output batch with a stable operation ID and per-shape Jar counts.
6. Space submits the batch through `WorldV2ShipHost.ImportProducedJars`.
7. Jar Core creates empty Jars in the central warehouse. Core owns warehouse capacity, overflow discard, resource-ledger operation-ID deduplication, and all downstream warehouse-to-workshop/order behavior.
8. Space acknowledges its pending batch only after Core confirms the import. An ambiguous response is retried with the same operation ID.

Consequences:

- Parser output must enter the central warehouse, never directly into Master or Slave.
- Parser processing does not bypass Jar warehouse capacity or downstream Core rules.
- A failed import must leave the parser batch pending; retry is safe because the Core resource ledger persists operation IDs.
- The parser's finite-input queues/progress are Space-owned state. Core's warehouse and operation ledger are Core-owned state.

## 2. Exact parser instance identity

The parser Definition is an immutable catalog template. Each installed parser is a distinct player-owned instance with its own progression revision and growth attributes.

A battle parser runtime identity must therefore include:
- the battle's `shipWorldId` namespace; and
- the exact installed `ownedInstanceId` from the frozen Board Assignment.

The Definition ID/version is not an instance identity. Two copies of one Definition must not share parser queues, fractional progress, batch sequence, or checkpoint identity.

The current production path freezes the parser Owned Instance snapshot inside `oreParserProfile.ownedInstance`. Space must validate its item kind and exact Definition ID/version before deriving the runtime identity. Definition-only identity is allowed only for explicit development fixtures; it is not the production Hangar path.

Every pending output operation ID must be namespaced by that runtime identity so separate parser instances cannot collide in the Core resource ledger.

## 3. Existing parser numerical contract

The authored parser Definition currently provides:
- `productionPercentage`: a 0–100 scalar multiplying this parser's own per-shape processing rate;
- `shapeProcessingRatePerSecond[shape]`: progress-volume units consumed per logical second before the percentage factor;
- `progressPerJarByShape[shape]`: finite consumed volume required to emit one Jar of that shape.

For each shape, the parser maintains separate:
- FIFO pickup-volume queue;
- fractional rate remainder;
- accumulated progress below one whole-Jar threshold.

The parser advances using the exact whole-millisecond delta authorized by `WorldV2ShipHost`. It processes before Jar advances by that same delta. It is not the generic Core Producer rate model.

The encounter drop profile supplies type/volume distribution, while encounter defeat cadence is a forecast input for balance evaluation. Neither is parser-owned growth. Parser growth currently composes only through the explicit Space subsystem attributes:
- `shapeProcessingRateMultiplier`;
- `progressPerJarMultiplier`;
- `productionPercentageBonus`.

These values must be frozen into the effective parser profile before battle. No parser growth is inferred from workshop capacities, liquid stock, or generic Core modifiers.

## 4. Checkpoint and failure boundary

A coherent battle save must keep these states aligned:
- frozen loadout identity/fingerprint, including exact parser Owned Instance snapshot;
- parser runtime queues, seen-pickup identity ledger, per-shape progress and rate remainders, pending output batches, and next batch sequence;
- pending world pickup nodes, next pickup sequence, and enemy-drop RNG state;
- Jar Core execution checkpoint, including the resource-ledger import receipts;
- Host-owned fractional-millisecond remainder;
- Space run/stage state.

Restore must validate the exact loadout and parser instance identity before publishing any parser state. Parser state restore is staged and atomic. A failed parser step or failed Jar import faults the battle rather than silently dropping input/output.

## 5. Why Core does not yet consume Parser Rule Contributions

The current Core `WorldV2BoardRuleProfileOptions` has a real runtime consumer only for `scope=workshop`. It verifies that such contributions originate from exact workshop Board Slots and builds the corresponding workshop RuleContext.

Parser processing is owned by the Space parser runtime described above. Passing a `scope=parser` contribution into the current Core Board profile would be rejected because Core has no parser execution consumer. Relaxing that validation or silently filtering the contribution would be incorrect.

Therefore:
- workshop contributions continue through Core's `boardRuleProfile`;
- parser-owned runtime parameters must be resolved by the Space parser subsystem before it freezes the parser runtime profile;
- parser contribution data must not be inserted into a workshop RuleContext;
- if a future shared transport envelope carries multiple consumer scopes, each scope needs an explicit consumer and must be rejected until that consumer exists.

The Space Core-facing resolver now rejects authored contributions from Parser, Skill, and Production Device slots before loadout freeze, and rejects any resolved contribution whose scope is not `workshop`. It does not forward unsupported component scopes to Core. Before parser contributions are authored for production, Space must split consumer-specific profiles or introduce a typed parser-runtime composition step. Do not broaden Core's workshop DTO to accept data it cannot execute.

## 6. Multi-parser is a separate runtime feature

The current Board/runtime projection intentionally requires exactly one decomposition slot. Space battle runtime currently owns one `SpaceOreParserRuntime`, and the frozen loadout contains one parser profile. This is a runtime limitation, not a restriction on the abstract Board layout.

Supporting multiple parser slots requires all of the following before the count gate can be relaxed:

1. A runtime collection keyed by exact `slotId + ownedInstanceId`, with exact Definition/version and progression revision frozen per parser.
2. A deterministic input-routing policy assigning each acquired stone to exactly one eligible parser. Broadcasting one pickup to multiple parsers would duplicate finite input.
3. A defined policy for shape support, queue ordering, work sharing, and behavior when one parser is disabled, full, or incompatible.
4. Independent per-parser queues, progress, remainders, pending batches, operation-ID namespaces, and checkpoint validation.
5. A single deterministic update order using the same authorized simulation delta for all parser instances before the Jar Host advances.
6. A settlement policy for parser output batches. All accepted batches still enter the central Jar warehouse; no parser may write directly into a workshop.
7. Restore validation proving every queued pickup and pending batch belongs to exactly one installed parser instance and no pickup is assigned twice.
8. Probes for duplicate Definition copies, different growth profiles, routing determinism, partial processing, retry after ambiguous import, checkpoint restore, parser removal/replacement, and warehouse overflow.

Do not implement multi-parser by changing `parser_count == 1`, copying the single parser state, or assigning one pickup to every parser. Those changes would create resource duplication and ambiguous ownership.

## 7. Acceptance cases before Parser scope is considered complete

- Two Owned Instances of one Definition have different parser runtime IDs and independent progress.
- A checkpoint from one parser instance is rejected by another instance, even if both use the same Definition/version.
- A pickup ID is accepted once; same ID/same payload is idempotent; same ID/different payload is rejected.
- Each shape consumes only its own queued finite volume and accumulates progress independently.
- Production percentage scales processing rate, not output allocation among shapes.
- Incomplete progress and unprocessed input survive checkpoint/restore exactly.
- A completed output batch is imported to the central warehouse and never injected directly into Master/Slave.
- A lost receipt followed by retry creates one warehouse import only.
- Warehouse capacity/overflow remains Core-owned and is visible in Core's authoritative result.
- Parser advancement and Jar advancement use the same Host-authorized whole-millisecond delta.
- Parser-specific contributions cannot enter workshop scope or be ignored by Core.
- Multi-parser assignments are rejected by the current runtime adapter until the complete routing/scheduling/settlement model exists.

## 8. Implementation order

1. Bind the existing single-parser runtime identity to the exact Owned Instance ID (preserving an explicit Definition-only development fallback).
2. Add identity-isolation tests and verify checkpoint restore uses the new identity.
3. Audit the parser runtime's current single-instance path and its actual integration call sites; retain the discrete warehouse import boundary.
4. Split Board contribution resolution by runtime consumer before enabling parser/skill/device contributions.
5. Only then design the multi-parser router/scheduler and its persistence model.
6. Keep parser runtime in Space unless a future architecture explicitly transfers finite pickup processing ownership to Core. Core remains the authority for produced-Jar warehouse admission and all Jar-world semantics.


### Space-side fail-closed ingress refinement (2026-11-24)

The Space Core-facing resolver now rejects any authored Rule Contribution from Parser, Skill, or Production Device slots before loadout freeze, and rejects any resolved contribution whose scope is not `workshop`. Such data no longer reaches Core ingress to fail only during World construction. Consumer-specific profiles remain future work.

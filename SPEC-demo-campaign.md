# Capability Map: Demo Campaign and Procedural Levels

> Date: 2026-11-03
> Status: proposed; implementation starts only after this capability map is reviewed.

## Objective

Turn the current six level files into a coherent tutorial campaign whose authored intent, difficulty
curve, runtime projection, resource provenance, and future procedural generation all share one contract.

The player should always be able to answer:

1. What is this level teaching?
2. What is the next immediate objective?
3. Why did this resource appear or move?
4. Which choices are still available before advancing the world?

## Current Evidence

- The original design intent is recorded in `docs/表达器设计原则.md`: manual ticks, finite choices,
  one mechanic per level, authored slack, and no difficulty based on hidden stat inflation.
- Level files already carry `name`, `hint`, `turns`, `goal`, initial master/slave, and a `WorldOptions`.
- Order deployment is explicit: a level may author an `orders` list of instances, and `LevelCatalog`
  rejects an unknown template, an unreadable entry, or an empty list at startup. **Fixed since this map
  was written**: level 6 used to say "two identical orders" while its world had one template, so the
  runtime deployed a single order. It now authors two instances of the same template, and
  `test/JarSystem.Tests/LevelOrderInstancesTests.cs` pins both spellings (`"模板名"` and
  `{ "template": "名字" }`) to the same list.
- Resource provenance is implemented in the kernel as a read-only projection:
  `src/JarSystem.Core/Production/ResourceLedger.cs` records `production`/`stock`/`flow`/`discard` from the
  tick pipeline and `transfer`/`return` from the order pipeline. Every record carries the tick, the cause,
  the kind, the amount, the source, the destination, and a human reason. Recording never mutates the
  world, so the replay bytes are unchanged by the ledger's existence.
- The page still does not *show* that chain: `GET /api/state` exposes state only, and no endpoint or frame
  serializes ledger entries. That gap is the remaining work in the `level-ui` slice.
- There is no deterministic level generator or machine-checkable generator constraint set.

## Assumptions to Confirm

1. The curated demo remains six levels; procedural generation extends the campaign after the tutorial.
2. Manual tick remains the only normal time-advance action. Auto tick remains optional.
3. The six curated levels keep their original mechanic order:
   reshape -> slave/liquid -> bans and master switch -> four operation types -> selective transfer ->
   resource contention.
4. A level may author an explicit list of order instances independently of the template catalog.
5. Procedural generation is deterministic from `archetype + seed + rules version`; it cannot create
   content that has not passed the same validators as curated levels.
6. Presentation may explain and animate facts, but may never create, infer, or compensate for resources.
7. The existing no-build vanilla JS presentation is retained for this iteration.

## Proposed Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| `level-schema` | Authored level envelope, order schedule, teaching metadata, budget metadata, validation | — |
| `resource-provenance` | Stable causes and read-model events for production, warehouse stock, flow, transfer, return, discard | `level-schema` |
| `level-runtime` | Deploy orders by schedule, expose objective/progress/next-step read model, restart and switch levels | `level-schema` |
| `level-ui` | Present lesson, objective, resource provenance, progress, and real-time movement from kernel facts | `resource-provenance`, `level-runtime` |
| `procedural-campaign` | Deterministic archetype selection and constrained variation, winnability checks, seed/version record | `level-schema`, `level-runtime` |

Build order:

```text
level-schema
  -> resource-provenance
  -> level-runtime
  -> level-ui
  -> procedural-campaign
```

## Proposed Curated Curve

| Level | Single lesson | Authored pressure | Success proof | Planned slack |
|---|---|---|---|---|
| 1 学会改形 | Basic-to-additional reshape with one visible cost liquid | One dependable source; no hidden liquid production | One fixed A order | ≥ 1 tick |
| 2 从台与装液 | Slave is a temporary tool position; liquid can enter a jar | One order needs slave capability | One fixed B order | ≥ 1 tick |
| 3 禁令与换台 | Bans prevent use, not storage; changing master moves authored resources | Two valid-looking benches, only one viable per order | One fixed A order | ≥ 1 tick |
| 4 四种活 | Match, pour in, empty reshape, full-jar self-cost reshape | Three pieces, modest capacity pressure | One fixed A order | ≥ 2 ticks |
| 5 工具罐转移 | Transfer intended jars and a catalyst jar; catalyst and any unused liquid return | Slave dependency plus visible return path | One fixed B order | ≥ 1 tick |
| 6 抢资源 | Order identity and execution order change which order can succeed | Two identical order instances compete for one scarce resource | One of two fixed orders | ≥ 1 tick |

Difficulty increases by introducing a new causal concept, not by silently reducing resources or
inflating turn limits. A level may be longer without being mechanically harder.

## No Mysterious Resources Contract

Every resource delta shown by the UI must reference exactly one kernel cause:

| Cause | Example | Required presentation |
|---|---|---|
| `production` | Shape line produced one jar | Producer, shape, rate, accumulator |
| `stock` | Level started with 2 honey in warehouse | Initial stock and that self-production is zero |
| `flow` | Warehouse moved jar/liquid to master | Source warehouse, destination master, amount, capacity |
| `transfer` | Master moved selected resources to slave | Master, slave, selected amount |
| `return` | Slave returned tool jar or free liquid | Slave, master, amount, reason `delivered/failed/cancelled` |
| `discard` | Capacity overflow | Source, amount, reason, cumulative discarded total |

Acceptance rules:

- A production card cannot show an active rate without a corresponding registered production line.
- Zero self-production remains a valid displayed fact; existing warehouse stock may still flow.
- An animation is drawn only from an observed before/after fact or an explicit provenance event.
- A reload, reconnect, or reduced-motion mode can omit animation but must retain textual provenance.

## Order Schedule Contract

The level envelope no longer derives order count from the number of templates: it authors instances
explicitly, and the field shape is settled (implementation: `LevelCatalog.ReadOrderInstances`). Both of
these spellings are accepted and produce the identical order list, so hand-written levels can stay terse
while per-instance metadata keeps a place to live:

```json
{ "orders": ["一只三角罐", "一只三角罐"] }
```
```json
{ "orders": [{ "template": "一只三角罐" }, { "template": "一只三角罐" }] }
```

Omitting `orders` keeps the older rule (one order per template) so the five levels that predate the
field do not change behaviour by accident. Requirements, all enforced before play starts:

- stable authored order index;
- explicit template reference;
- optional per-instance objective/reward metadata;
- deployment fails before play if a template is missing;
- restart and replay produce the same order list.

## Difficulty and Generation Gates

Every curated or generated level must pass:

1. World/schema validity and unique IDs.
2. At least one deterministic winning run.
3. Best baseline-bot run leaves the authored minimum slack.
4. Exactly one newly introduced mechanic for tutorial levels 1–3 and 5; level 4 is the explicit
   recapitulation level.
5. Every required shape/liquid has a declarative source path: production, initial stock, or a valid
   transformation.
6. No production source outside the declared resource budget.
7. Order schedule, goal, turns, and winnability are consistent.
8. The same seed and generator version reproduce byte-identical level JSON.

Procedural generation should vary non-teaching dimensions first: names, quantities, bench order, and
resource placement. The teaching mechanic and required proof remain archetype-owned until a later
curriculum expansion.

## Presentation Synchronization Contract

- `state.revision`/logical tick remains the synchronization authority.
- Real-time animation receives a stable world epoch, level index, tick, event sequence, and cause.
- Late events from an old world or previous level are discarded by the page.
- The page may coalesce visual updates, but it must not coalesce away the final authoritative state or
  textual resource ledger.
- Keyboard and screen-reader users receive the same objective, progress, and resource-cause information
  without relying on animation or color.

## Verification Commands

```powershell
dotnet run --project test\JarSystem.Tests\JarSystem.Tests.csproj -c Release
$env:CI='1'
重构报告\verify.bat
node tools\web_smoke.js http://127.0.0.1:5299 play.html
```

The complete gate must end with both `VERDICT=OK` and `QUALITY-OK`. Contract, schema, replay, and
determinism artifacts may move only as an intentional part of the relevant module.

## Non-goals

- Changing the core jar/liquid rules to make levels easier.
- Replacing manual ticks with an automatic game loop.
- Introducing a frontend framework or external runtime dependency for this pass.
- Generating arbitrary unsolvable levels at runtime.
- Treating animation as evidence that a resource moved.

## Open Questions

1. Should procedural generation ship as an offline authoring command first, or as an in-page
   “generate next level” action after the six curated levels?
2. Should unused shapes/liquids remain visible as zero-rate cards, or should tutorial levels collapse
   them into a single “本关未使用” summary?
3. Should level 5 teach only catalyst-jar return, or also deliberately demonstrate free liquid produced
   by a catalyst and returned to the master?

Proposed defaults: offline generator first; collapse unused resources into a summary; make level 5
demonstrate both catalyst-jar and free-liquid return.

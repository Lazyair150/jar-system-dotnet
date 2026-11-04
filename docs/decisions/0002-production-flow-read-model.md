# ADR-0002: Separate production, warehouse flow, and presentation projections

## Status

Accepted

## Date

2026-11-03

## Context

The game page exposed producer rates and liquid production rates, but not the warehouse-to-master
flow settings. A player could see "liquid production = 0" while inventory still moved into the master
workbench. Jars had a similar ambiguity: their configured rates were real, but rates outside the four
quick presets were not visible. The page therefore described neither all active mechanisms nor the
causal chain accurately.

The source review also found that enabling a producer/shape pair that was not in the original producer
rate map updated the status projection and slot count without attaching a source to the actual
`ShapeProductionLine`. That made a displayed mechanism exist only in the projection.

## Decision

1. Treat production, inventory flow, and presentation as three separate concepts:
   - `ProducerModule` and `LiquidProduction` create resources in `Warehouse`;
   - `Warehouse.FlowToMaster` only moves existing inventory;
   - `StateSerializer` projects both facts without inferring causes in the client.
2. Extend `producerStatus` with read-only `slots` (`used`, `limit`) and `flow`
   (`jars`, `liquidPerLiquid`). These keys are part of the public state contract and move together
   with the snapshot, schema, TypeScript declarations, and replay bytes.
3. Render one card per configured shape line, not one duplicate visual card per producer/shape pair.
   Show total rate, accumulator, source rates, actual non-preset rates, and the meaning of each slot.
4. Keep zero-source shape lines as state projection entities but skip their tick work; summarize them
   compactly instead of rendering empty controls.
5. Put the read-only production renderer in `production-view.js`. It may not open sockets, issue
   commands, or fetch state.
6. When a producer enables a previously unlisted shape, attach that source to the real
   `ShapeProductionLine` or reject the operation. Status-only activation is forbidden.

## Alternatives Considered

### Infer flow in the browser from stock changes

Rejected. The browser would need to duplicate rules about caps, ordering, and capacities, and would
show guesses rather than kernel facts.

### Add a separate top-level `production` DTO

Rejected for this change. `producerStatus` is already the production read model. Extending it keeps
the causal grouping and avoids a second status tree with overlapping ownership.

### Hide zero-rate liquid cards

Rejected. Zero production is a meaningful control state. The missing information is not the zero; it
is the separate amount available for flow. Both remain visible.

### Keep all eight empty shape cards visible

Rejected. Six zero-source shape lines add visual noise and look configurable while offering no
control. They remain in the backend projection and are summarized as inactive.

## Consequences

- The state contract gains four scalar keys under `producerStatus`.
- Replay state bytes and the text contract snapshot move intentionally.
- Players can distinguish “this round produced nothing” from “the warehouse moved existing stock.”
- A production source can no longer claim an active slot without a corresponding line attachment.
- The presentation controller becomes smaller, while the production view gains one explicit test
  boundary.
- Liquid name externalization and full host content parity remain separate architectural decisions.

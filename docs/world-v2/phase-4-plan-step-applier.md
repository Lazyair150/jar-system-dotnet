# Phase 4.5a — World plan step applier

## Goal

Apply exactly one `WorldPlanStep` to a live `Workshop`. This phase does not create an order session, advance logical time, move jars between master/slave workshops, or settle order completion.

## Identity and indexing

`WorldPlanStep.Operation.JarIndex` is an index into that step's own `Before` projection. It is not a live workshop index and is not a jar ID.

The applier resolves:

```text
jarId = step.Before[step.Operation.JarIndex].Id
liveJar = exactly one Workshop.Jars item whose Id == jarId
```

Missing IDs, duplicate live IDs, invalid indexes, and mismatched before-state are rejected before mutation.

## Step protocol

1. Validate the operation and index.
2. Validate the target jar identity and the planned before/after transition.
3. Validate the exact `Consumed` map for the operation.
4. Capture the live free-liquid ledger.
5. Invoke the existing domain operation:
   - `MATCH`: mark the jar.
   - `POUR_IN`: `Workshop.PourIn`.
   - `POUR_OUT`: `Workshop.PourOut`.
   - `RESHAPE`: `Workshop.TryReshape` with the planned empty/full mode and named cost liquid.
6. Verify the target jar's resulting shape, liquid, and mark.
7. Verify the actual free-liquid delta.

A rejected result means no mutation occurred. If a domain operation reports success but violates the immutable plan's postcondition or liquid accounting, the applier throws `InvalidOperationException`: this is an invariant breach after a domain mutation, not an ordinary plan rejection.

## Liquid accounting

| Operation | Free-liquid delta |
|---|---|
| MATCH | no change |
| POUR_IN(l) | l: -1 |
| POUR_OUT(l) | l: +1 |
| Empty RESHAPE(cost) | cost: -1 |
| Full RESHAPE(jarLiquid) | no free-pool change; liquid is consumed from the jar |

`Consumed` remains the solver's free-pool debit description. It is intentionally empty for `POUR_OUT` and full-jar `RESHAPE`.

## Boundaries

- The applier does not choose another jar or rewrite the plan.
- It does not clear marks; order/phase lifecycle owns mark reset.
- It does not perform master-to-slave transfer.
- It does not release reservations; execution lifecycle owns that.
- It does not roll back a previously applied step.
- It does not make the whole multi-step plan transactional. A later step failure leaves earlier successful steps applied, consistent with the world rule that consumed resources are not implicitly restored.

## Tests

The focused tests cover MATCH, POUR_IN, POUR_OUT, empty reshape, full reshape, stale before-state rejection, and rejection of a false `Consumed` ledger before mutation.

Local verification must be run by checking out this branch and executing `重构报告\\verify.bat`. No CI or local test result is claimed by this document.

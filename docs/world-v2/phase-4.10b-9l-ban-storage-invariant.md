# Phase 4.10b 9l — banned free-liquid staging invariant

> **Semantic correction (2026-11-25):** This is a historical phase note. Its former claim that Ban blocks Jar operations is superseded by ADR-0006. Current Ban governs only free-liquid staging admission, capacity and transfer. It never determines Jar polarity, blocks Jar-internal consumption, or blocks physical Jar migration.

## Domain contract

A workshop ban has two distinct but consistent effects:

1. **Free-liquid staging/storage:** banned liquid cannot be received into or remain in the workshop's free-liquid account. A capability change that would ban a non-zero stored balance is rejected atomically.
2. **Jar operation:** Ban has no effect on Jar operations. POUR_OUT, full-Jar RESHAPE self-consumption and other valid Jar transitions are governed by their own shape/rule preconditions, not by whether the internal liquid identity is banned from staging.
3. **Physical Jar transfer:** moving a Jar between Master/Slave or during workshop replacement is not a Jar operation. Internal liquid does not become free liquid and does not block the transfer. After transfer, the receiving workshop may hold and operate the Jar under ordinary Jar rules; Ban still applies only if its internal liquid is later offered to that workshop's free-liquid staging.

The storage invariant does not create a special compartment for banned liquids. There is one free-liquid account, and a banned liquid cannot be stored there.

## Enforcement points

- `LiquidRules.CanStore` remains the shared receive/storage policy.
- `Workshop.ReplaceCapability` validates the proposed ban against existing non-zero free-liquid balances before changing capability.
- `Workshop.ValidateRuntimeStateSnapshot` rejects snapshots that contain a banned free-liquid balance before restoring any state.
- `WorldInvariants` reports `BANNED-LIQUID-STORED` if an invalid state is observed.
- The 9l ordered replay separately applies Master ban and Slave ban/reject to free-liquid staging; JarState handoff and Jar-internal operations remain independent.

## Regression coverage

- Capability replacement is rejected atomically when the new ban conflicts with stored free liquid; world state and command log remain unchanged.
- Runtime restore rejects banned free-liquid storage without partial mutation.
- Workshop replacement and B-class handoff transfer full Jars without a Ban gate; the receiving workshop may operate them, while any later staging admission of their liquid is still subject to Ban.
- B-class replay treats the Slave's intrinsic ban as a free-liquid receive/staging restriction even when no additional reject list is supplied.
- A banned Jar-internal liquid can still be consumed by full-Jar RESHAPE; if POUR_OUT produces that liquid, staging rejects it and the receipt records it as discarded.

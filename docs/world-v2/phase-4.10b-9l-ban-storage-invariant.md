# Phase 4.10b 9l — banned free-liquid storage invariant

## Domain contract

A workshop ban has two distinct but consistent effects:

1. **Free-liquid staging/storage:** banned liquid cannot be received into or remain in the workshop's free-liquid account. A capability change that would ban a non-zero stored balance is rejected atomically.
2. **Jar operation:** a Jar containing a banned liquid cannot be operated at that workshop. Full-Jar self-consuming reshape uses the explicit `CheckJarOperation` gate; it does not debit the free-liquid account.
3. **Physical Jar transfer:** moving a Jar between Master/Slave or during workshop replacement is not a Jar operation. Internal liquid does not become free liquid and does not block the transfer. After transfer, the receiving workshop's own operation ban applies.

The storage invariant does not create a special compartment for banned liquids. There is one free-liquid account, and a banned liquid cannot be stored there.

## Enforcement points

- `LiquidRules.CanStore` remains the shared receive/storage policy.
- `Workshop.ReplaceCapability` validates the proposed ban against existing non-zero free-liquid balances before changing capability.
- `Workshop.ValidateRuntimeStateSnapshot` rejects snapshots that contain a banned free-liquid balance before restoring any state.
- `WorldInvariants` reports `BANNED-LIQUID-STORED` if an invalid state is observed.
- The 9l ordered replay separately applies Master ban and Slave ban/reject to free-liquid staging; JarState handoff remains independent.

## Regression coverage

- Capability replacement is rejected atomically when the new ban conflicts with stored free liquid; world state and command log remain unchanged.
- Runtime restore rejects banned free-liquid storage without partial mutation.
- Workshop replacement still transfers a full Jar containing a liquid banned by the receiving workshop; the receiving workshop then refuses to operate that Jar.
- B-class replay treats the Slave's intrinsic ban as a free-liquid receive/use restriction even when no additional reject list is supplied.

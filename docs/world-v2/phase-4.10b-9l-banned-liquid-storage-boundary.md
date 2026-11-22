# 9l — Banned free-liquid storage and Jar transfer are separate boundaries

## Workshop ban contract

A workshop ban prevents the workshop from receiving/storing the banned liquid in its free-liquid account and from using that liquid. This is enforced by the shared `LiquidRules` value object:

- `CanPay` rejects banned liquid before checking available balance.
- `CanStore` rejects banned liquid before checking capacity.
- `Workshop.AddLiquid` uses `CanStoreLiquid` and is all-or-nothing.
- `Workshop.TryReceiveLiquid` retains partial-capacity receipt for allowed liquids, but accepts zero units of a banned liquid.
- A capability replacement is rejected atomically if its new ban would leave an existing non-zero free-liquid balance banned.
- Runtime and rollback snapshot validation reject a state that stores a banned free liquid.
- `WorldInvariants` reports `BANNED-LIQUID-STORED` if such an invalid state is observed.

An already-held free-liquid balance is not silently deleted. Instead, changing the capability to ban that liquid is rejected until the balance is resolved through the appropriate resource lifecycle.

## Jar contents remain a distinct physical resource

A Jar containing liquid `L` may still be transferred between workbenches even if the source or destination bans `L`. Jar transfer changes ownership/location; it does not pour, consume, store, or otherwise operate on the Jar's internal liquid.

After transfer, the receiving workshop governs subsequent Jar operations:

- A workshop that bans `L` rejects operations that use/release the Jar's internal `L`, including `PourOut` and full-Jar self-consuming reshape.
- A workshop that does not ban `L` may perform those operations when all other rules permit them.
- The same operation-time rule applies to ordinary and order-scoped execution.

This does not permit banned free liquid to cross into a receiving workshop's free-liquid account. Master-to-Slave liquid handoff and workshop-replacement liquid migration continue to apply the receiver's ban: the receiver rejects or discards banned free liquid. The Jar itself remains transferable.

## Regression coverage

- `LiquidRulesTests`: ban is enforced on both receive/storage and use; zero/unlimited capacity does not bypass ban.
- `LiquidGrantTests`: live Workshop and pure LiquidRules answers remain aligned.
- `WorkshopCapabilityTests`: a capability change that would ban a non-zero existing free-liquid balance is rejected without changing world state or command log; a Jar whose internal liquid becomes banned remains owned and transferable.
- `WorkshopOrderLiquidScopeTests`: a banned Jar is blocked at the current workshop, can be transferred to a non-banning Slave and operated there, and can also be transferred to a banning Slave without being operated there.
- `WorkshopReplacementTests`: Master replacement transfers a full Jar into a target workshop that bans its internal liquid; the transfer succeeds, but the target cannot subsequently operate the Jar.
- `WorkshopRulebookTests`: exhaustive rulebook/executor parity fixtures only seed free liquids that the configured workshop is allowed to store.

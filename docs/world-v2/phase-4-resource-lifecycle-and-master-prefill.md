> **历史阶段快照 / 非当前规范（2026-11-24）**  
> 本文记录 PR #28 阶段的 Master Prefill / 资源生命周期设计，PR 编号、分支状态和阶段结论均为历史信息。  
> 它可用于追溯当时的资源结算与事务设计，但不能覆盖后续 World V2 的 Board Assignment、实例归属、Jar/液体账户边界及单向主从流语义。  
> 当前规范以 [电路系统模型](circuit-system-model-and-bounded-resource-routing.md)、[规则转换契约实施记录](rule-transition-contract-implementation-log.md) 和 [订单漏斗契约](order-funnel-and-workshop-replacement-contract.md) 为准。

# World V2 — Master prefill and liquid lifecycle closure

## Status

This document records the implemented resource contract at the end of the Jar System lifecycle phase. It is the hand-off boundary for the next Space integration semantics phase.

The current implementation is on PR #28. The PR remains Draft/Open and is intentionally not merged here.

## Ownership model

- The ship liquid inventory is the durable world-level free-liquid stock.
- A master workshop's prefill balance is temporary, role-bound working stock. It is not permanent inventory owned by that physical workshop instance.
- An order's master/slave liquid staging accounts are temporary execution scopes, each bounded by the corresponding workshop's staging capacity and ban rules.
- Liquid contained in a jar is part of that jar's state, not free liquid in a staging account.
- The slave workshop has no independent ship-prefill source. During a B-class order, its only free-liquid intake is the one-way master-to-slave transfer performed by that order's execution session.

## Master replacement transaction

For a successful master replacement:

1. The target must be a spare workshop.
2. Before migration starts, the system opens a prefill transaction against the target master's staging capacity.
3. The ship inventory reserves the capacity needed to roll this prefill back. Production cannot consume that reserved return capacity while replacement is pending.
4. The migration runs its reshape and transfer phases. The roster continues to point at the old master until the replacement commit boundary.
5. The roster commits the target as the new master.
6. The target prefill transaction commits.
7. Only after the role change and target prefill commit, the retired master's remaining prefill is drained and returned to ship inventory.
8. Any amount that cannot fit in ship inventory is discarded and recorded separately.

Cancellation or migration failure before the roster commit rolls back the target prefill and leaves the old master's prefill untouched. An uncertain rollback, prefill commit, or retired-prefill return is a terminal resource uncertainty and freezes the Runtime; it is not silently retried.

The return operation records all liquid kinds in one ledger batch. A unique operation ID is used so the ledger batch itself is idempotent. The resource mutation and ledger are not a distributed transaction: if an external ledger write fails after inventory mutation, the Runtime faults and reports the uncertainty rather than claiming rollback.

## Order admission and execution

- Admission checks a fresh world snapshot and reserves the immutable plan's jars, liquid budget, workshop staging peaks, and (for B orders) the single slave role.
- Initial liquid demand is funded from the current master's prefill first. Only the remainder is withdrawn from ship inventory.
- The admission lease records the exact source allocation. Repeated evaluation or restoration must not withdraw the same prefill or ship liquid a second time.
- POUR_IN and empty-jar reshaping consume the order's free-liquid scope. A full-jar reshape consumes liquid already inside that jar.
- POUR_OUT produces free liquid into the current workshop's order scope first. Ban or capacity rejection discards the rejected amount; it does not spill into another account.
- A-class orders use the master only and do not create slave staging or a slave-use event.
- B-class orders use the slave only after the session's one-way transfer boundary. The slave never draws free liquid directly from ship inventory.
- At terminal settlement, only free liquid remaining in the order scopes is returned to ship inventory, subject to ship capacity. Overflow is discarded and recorded.
- Liquid that has been poured into a delivered jar remains inside that jar; it is not included in the free-liquid settlement receipt.

## Replacement and candidate safety

A workshop leaving the master role must not carry its old master-prefill balance into the spare roster. This is enforced by draining and returning that balance only after successful role commit. A retired workshop with an active order account or capacity reservation cannot be drained as if it were idle prefill.

The current Host snapshot exposes spare workshop candidates and their physical HasResources state. That field is only a presentation hint; it is not a complete role-eligibility contract. Any future candidate UI must rely on role-specific eligibility computed by the domain/application boundary, including residual staging state, rather than inferring eligibility from Workshop.HasResources alone.

## Conservation/accounting interpretation

A liquid quantity can be in exactly one of these relevant locations at a time:

- ship free-liquid inventory;
- master prefill;
- master order scope;
- slave order scope after B transfer;
- a jar;
- explicitly discarded quantity.

Transfers change location but do not count as consumption. A liquid is consumed as free liquid only when a plan operation consumes it; if it is incorporated into a jar, the jar becomes its owner. Settlement is a return of remaining free liquid, not a refund of liquid already incorporated into delivered jars.

The ledger distinguishes:
- stock / production entering ship inventory;
- flow from ship inventory into master prefill;
- order-level consumption/production and inter-workshop transfer;
- return to ship inventory;
- discard caused by bans or capacity limits.

Ledger entries are an audit trail, not the source of truth for live balances. The inventory and staging accounts remain authoritative.

## Regression coverage

The focused regression suite covers:
- prefill limited by target master capacity;
- admission consuming prefill before ship stock;
- sequential A orders consuming a shared prefill balance without duplicate ship withdrawal;
- delivered jar liquid remaining in the jar;
- repeated master replacement returning only the unconsumed prefill;
- retired staging cleanup and checkpoint capture after replacement;
- cancellation/rollback preserving the old master's prefill;
- ship return capacity reservation while target prefill is pending;
- B-class slave intake remaining a master-transfer-only path.

The CI suite is currently green on the latest PR head. The implementation phase does not require another manual Godot acceptance pass.

## Explicitly out of scope for Jar

Jar System does not decide:
- when the Space game should request a workshop replacement;
- whether a player action, enemy drop, mission objective, or resource shortage should trigger replacement;
- how a Space combat encounter pauses, resumes, fails, or rewards the player;
- damage, health, enemy, score, reward, or wallet semantics;
- whether the game uses Jar as a combat mechanic, an industrial side activity, or a separate progression layer.

Those belong to the Space domain and are the subject of the next semantics phase.

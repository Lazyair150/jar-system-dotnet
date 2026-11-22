# 9l — Workshop ban is evaluated at the Jar operation boundary

## Contract

A workshop ban is an operation-eligibility rule owned by the workshop that currently operates a Jar. It is not a prohibition on moving the Jar between workshop inventories.

For a Jar containing liquid `L`:

- If the current workshop bans `L`, operations that use or release the Jar's contents are rejected. This includes `PourOut` and full-jar self-consuming reshape (`ReshapeFull`).
- If the current workshop does not ban `L`, the corresponding operation may proceed when its other rules permit it.
- The same rule applies to ordinary workshop execution and order-scoped execution. The order staging account does not redefine the workshop's operation policy.
- A Master-to-Slave Jar handoff is ownership/resource transfer, not a Jar operation. It must not run the source or destination workshop's operation-ban check merely because the Jar contains a banned liquid.
- Workshop replacement resource migration is also transfer, not a Jar operation. Its existing resource-migration policy remains separate from operation eligibility.
- After transfer, any subsequent operation is evaluated against the receiving workshop's own capability and modifiers. A receiving workshop that bans the Jar's liquid may hold the Jar, but cannot operate it in a way that uses/releases that liquid.

## Boundary matrix

| Action | Apply source workshop Jar-operation ban? | Apply destination workshop Jar-operation ban? |
|---|---:|---:|
| Operate Jar before handoff | Yes | N/A |
| Transfer Jar Master → Slave | No | No |
| Operate Jar after handoff | N/A | Yes |
| Transfer Jar during workshop replacement | No | No |
| Operate Jar after replacement | N/A | Yes, against the new current workshop |

This contract is distinct from free-liquid transfer policy. A ban on a liquid in the Master or Slave staging/free-liquid account can still reject that liquid's use or receipt under the corresponding resource-transfer rules. It does not turn a Jar handoff into a chemical operation.

## Regression coverage

`WorkshopOrderLiquidScopeTests` covers:

1. A Master that bans the liquid rejects full-Jar reshape without mutating the Jar.
2. The same Jar can be transferred to a non-banning Slave and reshaped there.
3. A Slave that bans the liquid still accepts the Jar transfer, but rejects subsequent full-Jar reshape.
4. The same behavior is preserved through the order-scoped `OrderLiquidScope` execution path.

The existing `LiquidRules.Check` call in `ReshapeRules.SelfConsume` is intentional: it enforces the current operator's ban at operation time. It must not be removed or relocated into Jar-transfer code.

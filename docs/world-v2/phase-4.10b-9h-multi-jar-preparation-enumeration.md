# World v2 phase 4.10b-9h — Multi-Jar preparation resource enumeration

## Scope

9h extends the 9g branch enumerator with independent Slave preparation Jars.
For each direct-rule assignment, it considers catalogued `PREPARE_POUR_OUT`
operations whose produced free liquid is consumed by at least one selected
requirement. Each selected preparation is assigned a distinct abstract Jar key
(`preparation-resource-{index}`) and is executed on the Slave after the one-time
Master-to-Slave handoff.

This reuses `WorldOrderPreparatoryProgramBuilder` and the ordered boundary
projector. The preparation Jar is therefore an explicit handoff input, not a
virtual liquid source:
- its initial shape and internal liquid become a `JarState` boundary condition;
- its PourOut output enters Slave staging in operation order;
- only later Slave consumers can spend that output;
- Slave rejection and staging-capacity overflow still discard output;
- Master ban applies only if a remaining free-liquid deficit must cross the
  handoff, never to the preparation Jar's internal liquid.

Independent preparation selection is bounded by the number of consumers of the
corresponding liquid. Duplicate preparation use is not permitted to satisfy more
consumption than the branch demands. Every subset is a distinct OR alternative;
each alternative retains its complete AND resource group.

Both node and branch budgets include preparation enumeration. Truncation remains
`Undetermined`, even if partial alternatives have been found.

## Explicit limits

A preparation Jar performs one catalogued PourOut. This phase does not yet support
multi-step operations on one Jar, arbitrary intermediate Jar-state chains,
recursive requirement expansion, physical inventory identity binding, live
availability checks, or resource reservations. It enumerates one-step independent
resource-producing Jars, not a general-purpose planning search.

## Verification coverage

Added tests cover:
- an independent Jar's PourOut satisfying another requirement Jar's free-liquid
  demand without requiring banned Master staging liquid;
- the preparation Jar remaining an explicit JarState boundary condition;
- conservative branch-budget exhaustion.

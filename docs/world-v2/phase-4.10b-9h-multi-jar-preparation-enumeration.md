# World v2 phase 4.10b-9h — Multi-Jar preparation resource enumeration

## Scope

9h extends the 9g enumerator with multiple independent Slave preparation Jars. For each direct-rule assignment, it considers catalogued PREPARE_POUR_OUT operations whose produced free liquid is consumed by the selected requirements.

Every selected preparation is assigned a distinct abstract Jar key (preparation-resource-{index}), different from every requirement-{index} key and every other preparation key. Each Jar performs exactly one rule operation. The selected preparation Jars execute on the Slave after the single Master-to-Slave handoff.

The preparation Jars are explicit handoff inputs, not virtual liquid sources:
- their initial shape and internal liquid become JarState boundary conditions;
- each PourOut output enters Slave staging in operation order;
- later operations on other Jars may consume the produced free liquid;
- Slave rejection and staging-capacity overflow still discard output;
- Master ban applies only if a remaining free-liquid deficit must cross the handoff, never to Jar-internal liquid.

Independent preparation selection is bounded by the number of matching free-liquid consumers. A preparation Jar is never reused, and no requirement Jar is also a preparation Jar. Every subset is a distinct OR alternative; each alternative retains its complete AND resource group.

Both node and branch budgets include preparation enumeration. Truncation remains Undetermined, even if partial alternatives have been found.

## Explicit limits

A preparation Jar performs one catalogued PourOut. The phase does not support multi-step operations on one Jar, arbitrary intermediate Jar-state chains, recursive requirement expansion, physical inventory identity binding, live availability checks, or resource reservations.

## Verification coverage

Tests cover:
- one independent Jar's PourOut satisfying another requirement Jar's free-liquid demand without requiring banned Master staging liquid;
- two independent Jars jointly satisfying aggregated same-liquid demand without reusing either Jar;
- node-budget exhaustion remaining Undetermined;
- the requirement Jar and preparation Jars having distinct JarState boundary identities;
- conservative branch-budget exhaustion.

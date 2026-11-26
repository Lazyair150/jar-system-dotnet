# World v2 phase 4.10b-9g — Independent preparation Jar enumeration

## Corrected domain contract

A Jar's operation mark is an order-lifecycle lock: one Jar may participate in at most one rule operation during one order implementation. A successful rule operation marks the Jar. Revision changes do not clear the mark, and the Master-to-Slave handoff preserves it.

Therefore a preparation Jar and a final requirement Jar must be different identities. The old 9g same-Jar preparation branch was invalid and is removed.

## Scope

9g enumerates direct Slave requirement candidates and optional independent PREPARE_POUR_OUT resource Jars. A selected preparation Jar:
- has its own abstract Jar identity, distinct from every requirement Jar;
- executes exactly one PourOut operation;
- produces free liquid into Slave staging;
- may fund later requirement operations on other Jars;
- is handed over with the initial Jar inventory before the one Slave-side operation.

No preparation output is reused as a second operation on the same Jar. Free-liquid production may still be consumed by other Jars in operation order, subject to staging capacity and rejection.

Every preparation choice is a distinct OR alternative containing one AND resource group. Branch and node budgets remain conservative: truncated enumeration is Undetermined.

## Explicit limits

This is not a recursive solver. It enumerates one-operation independent resource-producing Jars. It does not support repeated operations on one Jar, arbitrary intermediate Jar-state chains, physical inventory identity binding, or resource reservations.

## Verification

Coverage asserts that:
- the preparation Jar and requirement Jar are distinct boundary JarState inputs;
- an independent Slave preparation Jar can satisfy a later requirement Jar's liquid demand without requiring banned Master staging liquid;
- no same-Jar preparation alternative is generated;
- preparation branch budget exhaustion is Undetermined.

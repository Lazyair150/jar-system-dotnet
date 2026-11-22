# World v2 phase 4.10b-9i — One-operation-per-Jar lifecycle correction

## Status

The previous 9i design that allowed an ordered chain of transformations on one Jar was invalid and is withdrawn. Its PourOut -> PourIn -> requirement test and all same-Jar preparation-to-requirement paths are removed.

## Binding invariant

During one order implementation, each Jar identity may participate in at most one rule operation:
- the input Jar must be unmarked;
- a successful rule operation produces a marked Jar;
- changing revision, shape, or liquid never clears the mark;
- Master-to-Slave handoff preserves the mark and does not grant another operation;
- MATCH is also a rule/lifecycle operation and cannot reuse a Jar already operated during the same order;
- a preparation Jar must be distinct from every final requirement Jar and every other preparation Jar.

This is enforced at multiple layers:
- WorldOrderSymbolicJarVersion carries IsMarked;
- WorldOrderSymbolicOperationNode.Rule requires an unmarked input and marked output;
- WorldOrderSymbolicOperationProgram rejects more than one rule node for a Jar identity;
- WorldOrderPreparatoryProgramBuilder rejects repeated preparation Jar keys and preparation/requirement identity collisions;
- the Slave branch enumerator only emits independent preparation Jar assignments.

Revision remains a state-version identifier only. It is not an operation allowance.

## Valid multi-Jar cooperation

Different Jars may cooperate through role-local staging:
- Jar A performs one PourOut and produces free liquid;
- Jar B performs one operation consuming that liquid;
- Jar C performs one other operation.

The liquid dependency is between operations on different Jar identities. Master-to-Slave liquid transfer still occurs only through the one batch handoff, and the existing Master-ban / Slave-rejection scopes remain unchanged.

## Verification

Regressions cover independent preparation and requirement Jars, duplicate preparation identity rejection, same-Jar preparation/requirement rejection, marked outputs, and mark preservation across handoff. The former multi-step same-Jar chain regression is deleted.

# World v2 phase 4.10b-9g — Slave preparatory branch enumeration

## Scope

9g extends 9f's direct Slave candidate Cartesian product with catalogued
`PREPARE_POUR_OUT` alternatives. For each selected direct requirement candidate,
the enumerator emits the no-preparation branch and each compatible preparation
branch. A preparation is compatible only when it empties a Jar of the same shape
that the direct requirement candidate then consumes.

The preparation is assigned to the exact abstract Jar key
`requirement-{index}` and is built by the existing
`WorldOrderPreparatoryProgramBuilder`. It therefore inherits the established
ordering contract:

- Slave preparation occurs strictly after the unique Master-to-Slave handoff.
- The Jar identity is preserved while its revision advances.
- The handoff carries the Jar's pre-preparation state; the Slave does not create
  an initial Jar or borrow a Master-side account.
- Preparation output enters the Slave's staging account before later operations.
- Rejected or capacity-overflowed output is discarded by the ordered boundary replay.
- Master staging bans are evaluated only if a free-liquid boundary from Master is
  actually required. They do not ban Jar internal liquid or locally produced Slave
  liquid.

Every preparation choice is a distinct OR alternative containing one AND resource
group. Branch and node budgets remain conservative: any truncated enumeration is
`Undetermined`, including when partial groups are available.

## Explicit limits

This is deliberately not a general recursive solver. It supports at most one
catalogued PourOut preparation per selected requirement Jar immediately before its
direct rule candidate. It does not yet enumerate arbitrary standalone Jar identities,
multiple preparation steps on one Jar, multi-Jar production chains, or recursive
intermediate Jar-state transformations. It also does not bind abstract Jar keys to
physical inventory or reserve resources.

## Verification coverage

The 9g tests add coverage that:
- a same-Jar Slave PourOut branch can produce and consume its liquid locally, avoiding
  a banned Master free-liquid boundary;
- the direct and prepared variants are separate branches;
- preparation branch budget exhaustion is reported as Undetermined.

The existing 9f coverage continues to exercise OR-of-AND grouping, Master ban scope,
local production, opaque profiles, and direct branch budget handling.

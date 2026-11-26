# 2026-11-27 Board Assignment schema 2 ingress compatibility

## Incident and confirmed root cause

Space production Hangar emits a Board Assignment Snapshot with `schemaVersion = 2`. Its assignment entries include `areaId` and `subAreaId`, because the frozen Board distinguishes functional child areas such as `production/decomposition`.

The Core ingress contract previously required `WorldV2BoardAssignmentSnapshotOptions.SchemaVersion == 1`. The nested Board Definition already accepted schema 1 or 2, which created an incomplete compatibility fix: schema 2 was accepted for the nested Board but rejected for the outer Assignment Snapshot. The single composite error text incorrectly described this as an identity mismatch.

The exact Space producer is `SpaceHangarBoardAssignment.build_snapshot()`:
- writes outer `schemaVersion: 2`;
- writes `boardId`, `definitionVersion`, nested `boardDefinition`, and ordered `assignments`;
- assignment entries contain `slotId`, `areaId`, `subAreaId`, `componentKind`, owned instance/definition identity and source revision, plus role/control area where applicable;
- sorts assignments by stable slot ID before hashing.

The Core consumer is `WorldV2BoardAssignmentSnapshotOptions.Validate()`. It previously required outer schema 1 and its canonical fingerprint writer omitted the area hierarchy fields.

## Changes

1. Accept outer Assignment Snapshot schema 1 and 2. Schema 1 remains backward compatible.
2. Add `AreaId` and `SubAreaId` to the assignment-entry DTO with the Space JSON field names.
3. For schema 2, require assignment area/sub-area to exactly match the frozen Board Slot.
4. Include `areaId` and `subAreaId` in the schema 2 canonical fingerprint in the same property order as Space's producer. Keep the schema 1 fingerprint shape unchanged.
5. Keep the stable SlotId ordering requirement and all exact owned-instance/definition/revision checks.
6. Replace the composite identity error with independent diagnostics for assignment schema, BoardId, definition version, assignment fingerprint, nested Board identity/schema, and missing display name.

## Regression tests

Added coverage for:
- schema 2 Assignment Snapshot with schema 2 frozen Board and matching production/decomposition hierarchy;
- schema 2 assignment whose area hierarchy differs from its frozen slot;
- unsupported outer Assignment Snapshot schema 3 reporting a precise diagnostic.

Existing schema 1 tests remain unchanged to protect compatibility.

## Source revisions

- Branch: `architecture/core-liquid-identity-consolidation`
- Changes committed as:
  - `7b3c7cb91250b6da9d34b60bb04ceada8454fd9f` — schema 2 DTO, validation and fingerprint support.
  - `ec7b4da5d746347d1c9597476c65c4e3d7744691` — schema 2 acceptance and hierarchy mismatch regression tests.
  - `84645bab724a9e71cdcafe54733e67d59a5740a9` — actionable independent identity diagnostics.
  - `15e533691010a24cd8b6a4adcdf6dd1b6d8292ee` — unsupported schema diagnostic regression test.

## Validation status

- Source-level contract comparison against Space's producer: completed.
- xUnit / Core verification on the final commit: pending; no success is claimed yet.
- Space cross-repository Bridge build against the final Core SHA: pending.
- Godot desktop Hangar-to-combat acceptance: pending.

## Release gate

Do not mark this incident resolved until:
1. Core verification passes on the final exact commit.
2. Space builds the Bridge and game against that exact SHA.
3. A regression probe serializes the actual Space schema 2 payload and Core accepts it.
4. The desktop game enters combat from Hangar without a World V2 configuration fault.

# 2026-11-27 World liquid polarity propagation fix

## Incident

After the Hangar Board Assignment schema-v2 ingress was corrected, the desktop game reached:

- `[WorldV2Runtime] initialized: frozen battle loadout`
- `[WorldV2ShipHost] Jar runtime active; external Space clock enabled`

The next failure occurred while materializing the selected Core-owned order template:

`A non-default World must explicitly assign one Cathode and one Anode liquid identity. (Parameter 'liquids')`

This is later than World initialization and unrelated to Board Assignment, Hangar selection, or Godot scene startup.

## Root cause

`WorldV2Context` already owns the complete immutable identity mapping:

- `context.Shapes`
- `context.Liquids`
- `context.LiquidPolarities`

`Vocabulary` intentionally allows implicit legacy polarity only for the exact default `Constants.LIQUIDS` pair. A non-default World must pass its explicit identity-to-polarity mapping.

Three application operations reconstructed Vocabulary from only shape/liquid names and silently dropped the third part of the World vocabulary:

1. `ImportProducedJars`: `new Vocabulary(context.Shapes, context.Liquids)`
2. `AddJarDemand`: `new Vocabulary(context.Resources.Producers.Generators.Keys, context.Resources.Inventory.LiquidWarehouse.Keys)`
3. `AddJarDemandFromTemplate`: `new Vocabulary(context.Shapes, context.Liquids)`

The third path is the exact stack behind the user's desktop fault. The other two were the same latent defect in adjacent application operations.

## Fix

All three call sites now pass `context.LiquidPolarities` to `Vocabulary`. The direct AddJarDemand path retains resource-account identities as its shape/liquid input, but uses the Context's explicit World mapping; it does not infer polarity from names or dictionary order.

No validation was weakened and no default mapping was added for non-default Worlds.

## Regression coverage

Added `ApplicationOperationsPreserveExplicitNonDefaultLiquidPolarities` to `WorldV2ApplicationServiceTests`. It creates a World with:

- `coolant → Cathode`
- `fuel → Anode`

Then exercises all three affected operations in one integration-level Core test:

- direct `AddJarDemand` using `coolant`;
- Core-owned `AddJarDemandFromTemplate` using `coolant`;
- `ImportProducedJars` using the same World vocabulary.

The test asserts both demands are materialized and the produced Jar is stored.

## Verification

- Source fix commit: `fe0b068c9e80cc232b76c91111d42fa257c227e9`.
- Regression test commit: `b593f4a2effcd7091f80e1a31d703072c2d5f6b4`.
- First CI run `45859139265` failed at build analysis only: xUnit2013 rejected a collection-count assertion in the new test. The runtime code and Bridge builds passed; the run did not reach a clean xUnit gate.
- Test assertion was changed to `Assert.Collection` in commit `93f050fabb201c37a4bc1601f45e6c0aaff33cdd`.
- Exact branch head after analyzer correction: `93f050fabb201c37a4bc1601f45e6c0aaff33cdd`.
- CI for the corrected head: pending. No pass is claimed yet.
- Desktop confirmation: pending.

## Architecture rule

A World vocabulary is the indivisible tuple:

`(Shape identities, Liquid identities, Liquid identity → Polarity mapping)`

Any application operation that creates a `Vocabulary`, `Jar`, `Requirement`, or order specification from an existing World must propagate the exact World-owned mapping. It must not reconstruct polarity from identity strings, ordering, or legacy defaults.

The World Context is authoritative. Resource-account keys are identity projections, not a replacement for the World vocabulary.

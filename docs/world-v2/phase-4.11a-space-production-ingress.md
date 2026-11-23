# Phase 4.11a — external producer-rate ingress

## Purpose

Expose the existing World v2 producer-rate mechanism through the ApplicationService and Godot Bridge so an external simulation domain can drive Jar production without constructing or mutating Core objects.

This is a generic production boundary. Jar receives only a producer identity and a complete rate table keyed by configured Jar shapes. It does not know about energy stones, enemies, ship equipment, parser types, or Space gameplay.

## Command contract

`UpdateProducerRates(producerName, rates)`:

- runs inside `WorldV2HostService.ExecuteExclusive`, serialized with world advancement, replacement, and disposal;
- requires an already configured producer;
- requires the submitted shape keys to exactly match that producer's configured shape topology;
- validates every shape and finite non-negative rate against the Core producer limit before mutation;
- validates the projected producer-slot count before mutation;
- applies rate reductions before rate increases, so a valid batch cannot fail halfway because a slot was still occupied;
- does not add/remove producers or shapes and does not reset any shape's fractional production accumulator.

The complete shape table is intentional. A caller cannot accidentally omit a shape and silently leave an old rate running.

## Checkpoint behavior

Producer rates and per-shape fixed-point production accumulators already belong to `ResourceAreaRuntimeStateSnapshot`. The new command changes only the configured runtime rates. It does not alter the checkpoint schema or configuration fingerprint.

A checkpoint restore therefore reconstructs the configured producer topology and then restores the exact dynamic rates and accumulated fractional production state.

## Space integration boundary

This API is for an external system that genuinely controls an existing time-driven Jar producer. It is not the energy-stone parser ingress. The Space energy-stone path is a finite typed-input parser that owns pickup consumption and per-shape fractional progress; after it completes whole empty Jars, it must use the separate discrete `ImportProducedJars` command documented in phase 4.11b. Do not reinterpret stone input or parser percentage as a dynamic time-based producer rate.

The API deliberately does not define energy-stone types, sizes, parser attributes, parser scheduling, stone depletion, or ship liquid production. Those are Space-side concerns and are not represented by this rate table.

## Regression coverage

- Runtime rate changes affect the next World v2 advance.
- A batch with a mismatched shape topology is rejected without changing any configured rate.
- A fractional production accumulator survives a rate change.
- Checkpoint restore restores the prior rate and fractional accumulator, after which the same rate change produces the same result.

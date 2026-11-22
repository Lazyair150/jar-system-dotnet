# World v2 phase 4.10b-9i — Preparatory Jar state chains

## Scope

9i extends the preparatory program builder from one preparation per Jar to an
explicit ordered chain of transformations on one stable Jar identity.

Each `WorldOrderPreparatoryOperationAssignment` now carries an `Order` within
its Jar chain. Different Jars may have independent chains. For each Jar:

- order values must be unique and non-negative;
- every operation must be a catalogued `PREPARE_*` candidate;
- every operation in one chain executes at the same workshop role;
- each operation's source shape/liquid must equal the previous operation's
  target shape/liquid;
- the final chain state must equal the associated requirement candidate's
  input state when the chain uses a `requirement-{index}` Jar key;
- each operation consumes one Jar revision and produces the next revision;
- every consecutive Jar revision is connected by an explicit JarState dependency.

For a Slave chain, the entire Jar identity crosses the single Master-to-Slave
handoff once. The first Slave preparation consumes the post-handoff revision;
later preparations consume the previous preparation's output revision. The
requirement operation consumes the final chain revision. No Slave operation
can run before handoff or draw an initial Jar from Slave inventory.

Master chains remain entirely before the handoff. Master preparation of a
Slave requirement Jar feeds the handoff input state, and the handoff remains
the immediate producer of the post-handoff Jar version.

Free-liquid dependencies remain separate from Jar-state dependencies. PourOut
production enters the executing role's staging account; later operations may
consume it only after production in the same role. This builder does not merge
Master and Slave staging balances and does not bypass Master ban or Slave
rejection rules. The ordered resource simulator and boundary projector remain
responsible for resource accounting.

## Explicit limits

This phase provides the correct program representation and builder for an
explicitly supplied chain. It does not yet enumerate arbitrary chains from the
rule catalog. The current candidate profile still catalogs the existing
one-step PourOut preparation slice; expanding the preparation catalog to
PourIn, empty/full reshape, and other legal state transitions, then searching
bounded state paths, is the next phase.

It also does not bind abstract Jar keys to physical inventory IDs or reserve
live resources. Those remain execution-admission responsibilities.

## Verification

Added regressions cover:

- a single Slave Jar executing PourOut then PourIn before its final requirement;
- stable Jar identity with monotonically increasing revisions;
- explicit JarState dependency edges between every adjacent operation;
- staging production and consumption replayed in order;
- rejection of a disconnected chain whose next input state does not match the
  previous output state.

Existing tests continue to cover the one-time handoff, A-class behavior,
Master-side preparation, and rejection of invalid preparation assignments.

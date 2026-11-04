# ADR-0005: Local UI state and kernel commands are separate interaction planes

## Status

Accepted

## Context

The game page has rich local interaction: pickers, filters, folded logs, selection highlights and
animations. Some of those flows eventually commit a world choice, while others never affect the
deterministic world. Treating every click as a backend command would make the protocol noisy and let
presentation state leak into replay. Treating world mutations as local state would break authority,
audit and multi-client consistency.

## Decision

1. Local-only presentation state stays in the page and sends no JSON.
2. A confirmed choice that changes world/process/time state is sent as a typed WebSocket command.
3. Kernel facts are received through `state`, `steps`, `frame`, `world` and HTTP queries.
4. `state` is authoritative. `frame` only invalidates cached presentation.
5. The page may show pending/optimistic visuals, but must reconcile or revert on server response.
6. State-changing commands are not automatically retried until stable request correlation and
   idempotency semantics exist.
7. Protocol additions update dispatcher, generated types, guards and tests together.

## Consequences

- Picker, tabs, folds, filters and decorative animation remain fast and independent of the server.
- World choices remain deterministic, replayable and visible to every client.
- Reconnect flows synchronize first rather than replaying stale intentions.
- Rich UI can grow without inventing fake kernel commands.

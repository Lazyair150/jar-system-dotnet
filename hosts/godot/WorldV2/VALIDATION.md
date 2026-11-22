# World v2 Godot Host Runtime Acceptance

The Release build is necessary but does not prove that the Godot scene starts or that the world can be initialized at runtime. Complete this checklist with the Godot 4.7.1 .NET editor.

## 1. Open the standalone project

1. Check out the `world-v2/phase-4.10b-8-versioned-world-checkpoint` branch of `jar-system-dotnet` (the current PR branch, based on the Godot host project).
2. Open Godot 4.7.1 **.NET**.
3. Import `hosts/godot/WorldV2/project.godot` as a separate project.
4. Wait for the C# solution restore/build to finish.
5. Run the project.

Do not open or modify the legacy `hosts/godot/Jar` project for this check.

## 2. Startup acceptance

Expected on startup:

- The main scene opens without a script or resource load error.
- `WorldV2HostNode._Ready()` creates a `WorldV2BridgeSession`.
- The demo `WorldV2Options` passes World v2 validation and creates a world.
- The UI changes from its initialization placeholder to a world snapshot.
- The initial snapshot shows the configured master workshop `主台` and slave workshop `从台`.

If startup fails, capture the first Godot error and full exception text. Do not treat a visible window alone as success.

## 3. Clock and control acceptance

Perform these checks in order:

1. Leave the project running for several seconds. The displayed world-time value should increase.
2. Click **暂停**. The status should become paused. World time may continue to increase because it is the incoming wall-clock time; logical time must stop increasing.
3. While paused, wait several seconds and confirm logical time remains unchanged.
4. Click **恢复**. Logical time should resume increasing.
5. Click **2 倍速**. Over a comparable wall-clock interval, logical time should advance approximately twice as fast as at 1x.
6. Click **1 倍速**. Logical time should return to the normal rate.

Small differences are expected because the UI displays integer milliseconds and the engine is frame-driven. Do not expect exact wall-clock equality.

## 4. Lifetime acceptance

1. Close the running project normally.
2. Confirm there is no disposal exception in the Godot output.
3. Run it again and confirm a fresh world can be created.

## 5. Architecture checks

During this test, the following boundaries must remain true:

- Godot sends wall-clock delta to the Bridge; it does not multiply by the speed ratio.
- Pause and speed changes are delegated to the Bridge/Core WorldClock.
- UI reads a detached `WorldV2SnapshotDto`; it does not access `WorldV2Context`, Host services, or mutable domain objects.
- The old `hosts/godot/Jar` module is not loaded or referenced by the new project.

## 6. Report format

Record each item as PASS or FAIL. For a failure, include:

- the exact step number;
- the first error message and stack trace;
- whether the project window opened;
- the last displayed world/logical time values.

A Release build success must not be substituted for this runtime acceptance.


## 7. Execution checkpoint host acceptance

The demo scene exposes two controls for exercising the JSON checkpoint path:

1. Start the Godot project and wait for world/logical time to advance.
2. Click **保存检查点**. The Host captures the complete root execution checkpoint and encodes the versioned JSON envelope.
3. Let the world continue for several seconds; note the increased world/logical time.
4. Click **恢复检查点**. The Host decodes the saved JSON, prepares a new Context, atomically replaces the current Context, and refreshes the UI snapshot.
5. Confirm the displayed world/logical time returns to the values captured at save time, with the same master/slave workshop roles.
6. Confirm no exception or runtime fault appears in the Godot output.

The demo keeps the JSON string in process memory only. It is an interactive Host lifecycle check, not a disk-save feature; closing/restarting Godot discards this temporary string. The automated .NET B-order checkpoint test separately covers the more demanding pre-pump and post-pump restore boundaries, then resumes the order to completion and checks that funding/transfer/delivery are not repeated.

## 8. Acceptance boundary

A passing CI build and .NET test suite prepare the code for Host testing; they do not replace this Godot runtime test. Enter the Host testing stage when the latest PR HEAD passes CI. Mark Host acceptance complete only after the above checklist is run in Godot 4.7.1 .NET.


## 9. Workshop-individual replacement acceptance

The demo world contains two empty spare workshop individuals. The UI exposes independent target selectors for replacing the master-position individual and the slave-position individual; these are role positions, not UI focus controls.

1. Start the world and confirm both spare individuals appear in the replacement selectors.
2. Start a master replacement using one spare. Confirm the replacement status advances through two phases while world time continues. During the replacement interval, jar inflow to the master is suspended by Core; production continues in the resource area.
3. Wait for completion. Confirm the selected individual now occupies the master position and the displaced master individual appears in the spare list.
4. Start a slave replacement using an empty spare individual. Confirm it completes after one phase and the new individual occupies the slave position.
5. Confirm a spare containing resources is not offered as a slave target. Core remains the final authority and rejects invalid/stale targets even if the UI list is outdated.
6. Start another master replacement and click **取消当前替换** before completion. Confirm the old master remains in its role, the target remains spare, and the UI reports cancellation. Note that order interruption already committed at replacement start is not undone.
7. Confirm the replacement controls are disabled while a replacement is active or the Runtime is faulted, and that the UI shows terminal success/failure details.

A successful UI command only means the Core accepted the replacement process. Host acceptance requires observing the actual role change and the post-replacement spare roster in the live snapshot.

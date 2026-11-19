# CuSaveManager Changelog

> 中文更新日志：见 [changes.md](changes.md)

## 1.3.4

Fixed the incompatibility with Casualties: Together 5.5.x where a client got stuck on the species selection screen after joining a lobby. 1.3.3 unpatched Together's prefix on `PreRunScript.StartRun` and blocked connected clients with "You can't start the game, server can."; from Together 5.5 on, that prefix (`CharacterSelectWait.OnStartOrLoadClicked`) is exactly what handles the client's Join / Ready up, so clients could neither ready up nor join a running game. The prefix is no longer removed: inside a lobby, whenever Together has `CharacterSelectWait`, `StartRun` is passed through to Together entirely, which also means the host's new run now goes through Together's ready lobby.

Single-player with Together installed but no session running now uses a bypass guard attached to Together's own `StartRun` / `LoadRun` prefixes, so starting and loading go straight through the vanilla flow without hitting Together's single-player block.

The multiplayer auto-rollback death threshold `MultiplayerDeathThreshold` now defaults to 0 (trigger only when everyone is dead) instead of 1; an existing value of 1 in the config is changed to 0 once on the first launch after updating. Set it back to 1–16 by hand if you want "roll back after N deaths". A death-triggered countdown is cancelled automatically when the death condition no longer holds (e.g. a teammate was revived). While a death is being handled or a rollback is in progress, `PlayerCamera.ToMainMenu` is intercepted before Together, so Together cannot unregister network objects and send everyone out of the room first.

Multiplayer rollback fixes: when `ServerMain.LateSpawnLocation` is suppressed during the rollback window, `PlayerCheckpoint.ReclaimGear` is now still called (1.3.3 skipped the whole method, so remote players did not get their checkpoint gear back); phase one's return to the main menu no longer writes positions (it wrote the pre-rollback state into the save that had just been restored); rolling back to a layer-transition save (`LEVEL=false` in `mp_rules.json`) no longer applies the previous layer's exit coordinates from `PLRPOS`; the rollback countdown alert is no longer sent back to the P2P host itself.

Connected clients no longer apply their local new-run world engine settings when entering a world, and QoL's seed intervention is turned off for them, so world generation follows the host. On returning to the main menu the seed state received from the host is cleared, and the room's position is no longer written into the local save.

Adapted to `PLRCHARS` (per-player character table) added to saves by Together 5.5: it is rewritten together with `PLRPOS` when persistent IDs are remapped on rollback. While a special game mode is active (`GamemodeManager.HasGamemode()`, e.g. BattleRoyale), timed backups and death auto-rollback are skipped.

Manual save, timed backup and save-and-exit are refused while the player is dead or a rollback is in progress, so a dead state cannot end up in a backup; in multiplayer, timed backups are skipped when the team is at the layer exit according to Together's own check (`CheckIfEnoughPeopleAreAtLayerFinish`). A dead character's coordinates are no longer recorded as the save position, and writes triggered by QoL's background autosave are skipped.

When loading a single-player slot, `biome` is written back to `save.sv` only if the slot carries a position and QoL is not installed, instead of unconditionally.

Fixed-seed world generation now saves and restores each generation phase's own random state on every coroutine step, so it is not affected by other random draws during generation. In multiplayer, Together's outer handshake coroutine is not wrapped, so the random state just synchronised with the host is not overwritten.

Mouse hover and drag are blocked while the save panel is open, so they no longer pass through to what is underneath. The update check's version comparison now handles tags with `-rc.1` / `+build` suffixes and always compares the first three parts. The English button text `Save && Exit` is now `Save & Exit`.

Added optional integration with `Together.ModHooks` / `TogetherPanelApi`: when Together provides them the official hooks are used; otherwise (including the current Steam build 5.5.3) the patch path is kept.

This version also includes the remaining fixes from a full code review; see [docs/README.md](docs/README.md) (in Chinese) for the item-by-item list.

## 1.3.3

Multiplayer loading no longer unpatches Together's `PreRunScript.LoadRun` prefix. It now gates on "is a session running": while connected, Together runs its full character-selection and per-player load flow; this mod only takes over in single-player (multiplayer mod loaded, no session). `StartRun` keeps the old takeover (Together's prefix there only hard-blocks single-player and opens an extra Ready lobby when connected), so starting a new run feels unchanged.

Corrected a wrong assumption about HarmonyX: a prefix's return value only aggregates into "run the original or not" and does **not** stop later prefixes. So StartRun cannot be released with `return true` and must stay detached; single-player LoadRun now shows one extra "You can't play singleplayer" notice from the multiplayer mod, but the world is already being loaded by this mod.

Added a pre-world-load character fallback (`Together.WorldMP.LoadVanillaGeneratedWorld`): on the host it writes the saved character to both `PreRunScript.characterSelected` and `ScavPlayer.LocalPlayer.selectedCharacter`, so Together's `PlayerCamera.Awake` can no longer overwrite it back to Experiment. The character is read only from the local player's own `mp_save/<persistentId>`; the old "first subdirectory that has a save" fallback is no longer used for identity, so another player's species cannot be picked up.

Added a load-patch priority self-check: the prefix execution order for `LoadRun` / `StartRun` is printed at startup, with a warning if this mod ends up behind the multiplayer mod.

Added a `character` field to the sidecar as a second source: when the main save says 0 but the sidecar says otherwise, the sidecar wins, but only if both belong to the same run (matching RunId), so a stale sidecar cannot override a newer run.

## 1.3.2

Fixed rollback resetting the character to Experiment (Milky / Dune included).

Fixed the "current save" card showing "no save" while in-game and right after loading.

Added flow logging for Debug builds (load / rollback / save / slot switch / seed; not included in Release).

## 1.3.1

Fixed the loaded character not following the save (Milky / Dune reset to Experiment).

## 1.3.0

Adapted to the 1.0 release build of the game and Together 5.4.0.

Multiplayer bridge migrated from KrokMP to the Together Interop contract layer: session, channel, roster and type registry all go through reflection contracts, with no compile-time reference to the multiplayer mod assembly.

Multiplayer save detection now keys off "a session is running": Together only switches the game save directory to `mp_save` while `Net.IsRunning`. The old check keyed off "the multiplayer mod is loaded", so single-player sessions with Together installed were misdetected as multiplayer and failed to pack a directory that does not exist.

Fixed-world seed injection now hooks the `Together.LastBeforeGenerationState` constructor, seed broadcast takes over `ServerMain.Server_AnnounceSeed`, and rollback position write-back waits for world generation to finish.

Declared a soft dependency on QoL Unknown so load order can no longer make every QoL member miss on the first launch.

Added Debug-only logging; release builds contain no debug strings.

## 1.2.1

Dedicated Server compatible: host coordinates from the sidecar are skipped on rollback (a dedicated server has no local player).

Fixed multiplayer rollback position-packet timing: now waits for world generation to finish (`Util.IsWorldGenerated`) before writing positions once, replacing fixed-frame retries that could be overwritten by a LateSpawn coroutine running later than the rollback lifecycle.

Removed the redundant PlaceBody broadcast silencer (KrokMP now guards with `has_spawn_location` itself).

Build: target framework net472 → net48; added a direct `KrokoshaCasualtiesMP.dll` reference (rollback coroutine switched from reflection to a strong reference); LangVersion unified to `latest` for compatibility with the .NET 8 SDK used by CI.

## 1.2.0

Fixed anti-rad drugs (and other trap-named items) carried in the inventory always disappearing when entering the next layer.

Fixed items in oversized custom containers (e.g. Super Galaxy Belt / Material Pouch) being lost or scattered on the ground after loading a save.

## 1.1.8 & 1.1.9

Fixed "Save & Exit" not creating a slot in the save / rollback list.

## 1.1.7

Fixed loading a save made at a layer bottom (when the "Go to Next Floor" button is showing) skipping an entire floor.

## 1.1.6

**Fixed the layer number not advancing (single-player / KrokMP-installed solo).**

- Root cause: the game advances layers via save→load. `SaveSystem.SaveGame` writes `biome = biomeDepth + 1` (the next layer) and `TryLoadGame` sets `biomeDepth = biome`, so saving at the bottom of a layer and continuing loads the next layer. CuSaveManager's "persist current save for continue" overwrote the `biome` field with the live 0-based `biomeDepth`, erasing the `+1`, so reloading returned to the same layer (the layer number never changed). This affected single-player (and KrokMP installed but not hosting), where the layer transition is a `SaveGame`→`LoadGame` cycle — matching "reach the bottom, choose next layer, still the same layer". It existed since 1.1.3 and disappeared when the mod was removed.
- Fix: when saving at a layer boundary (player at the bottom = advancing), the game's `biome = biomeDepth + 1` is now preserved instead of being normalized back to the current layer; biome normalization only runs for mid-layer saves (resume in place). The redundant biome rewrite on the main-menu Continue path (which has no in-world player position) was removed so it can no longer undo the advance. The same boundary guard was applied to the multiplayer `mp_save` normalization.

## 1.1.5

Determinism hardening for this mod's own fixed-world engine (self): added reseeding at all synchronous execution points inside the worldgen coroutines.

- Why: Harmony prefixes on the coroutine worldgen phases (`IEnumerator`, e.g. `WorldGenerateTerrain`/`WorldPlaceEntities`/`WorldGenerateStructures`) run only once when the coroutine object is created (before the first `yield`). The actual random draws happen on later frames after `yield`, by which time `UnityEngine.Random` may have been disturbed by other systems. So phase-level reseeding alone is unreliable and terrain/entities may not reproduce from the seed.
- Added per-method, just-in-time reseeding of the **synchronous** sub-methods called inside those coroutines: the `FastNoiseLite` constructor (terrain/cave noise, stepped by `_noiseGenStep`), `DistributeEntities` (enemies/traps/crates/corpses, keyed by name hash + params + call index), `PlaceLiquids`, `GenerateLifePods`/`GenerateDropCapsules`/`GenerateCollapsedPods`, and `ApplyLayerModifiers` (coexists with the load/rollback restore patch).
- Counter/seed resets on layer advance / regen / clear: `ContinueRun`, `RegenerateWorld`, `Clear`, plus noise-counter reset at the start of `GenerateWorld`/`WorldGenerateTerrain`.
- Loot determinism: `TraderScript.GenerateInventory` and `Openable.OnUse` (save/restore `Random.state` around them to avoid polluting the global stream).
- **Fixed "stuck layer" with the self engine when advancing layers (multiplayer host)**: on a layer transition the game increments `totalTraveled` in memory before regenerating, but the on-disk save.sv still holds the previous layer's value. `MpWorldSeedInjector` read only the disk value, so every new layer was seeded with the previous layer's `totalTraveled` and regenerated an identical-looking map. It now takes the max of in-memory and disk values (disk for initial load, in-memory for live layer transitions), consistent with `SeededWorldPatcher.LayerSeed`.
- The save/load-exact path (native save + restore patches) is unchanged.

## 1.1.4

Fixes three issues that appeared when running alongside the KrokMP multiplayer mod with no QoL installed:

- **Map mode always defaulted**: after this mod took over `PreRunScript.StartRun`, it dropped the `WorldGeneration.runSettings = <chosen preset>` write that KrokMP's own patch performed, so a host's chosen map mode (Desolate / Unchipped / Custom) was overwritten by the `normal` preset in `WorldGeneration.Awake`. The write is now restored in the takeover.
- **Same seed every restart (map identical to last time)**: with the default `PreferredEngine=qol` and QoL absent, the old logic fell through to this mod's deterministic world engine and force-injected a fixed seed; `EnsureFreshSeed` also returned early when already active, reusing the previous run's seed. The default now hands world generation back to KrokMP (random maps) whenever KrokMP is present; the fixed-world engine is opt-in (pick "This mod" in the panel). `EnsureFreshSeed` now re-rolls the seed for every fresh run.
- **Cannot advance to the next layer**: the deterministic engine recomputed the seed from the save's stale `totalTraveled` when advancing, regenerating a world identical to the current layer, so "walk to the bottom and choose next layer" kept the same layer. Fixed by handing layer progression back to KrokMP per the item above.

## 1.1.2

- Sidebar of the main panel now has an "About" tab, in the same SkinSync-style centered title + link buttons + name buttons layout, showing version / repo / latest release / author / dependencies.
- Settings → "Misc" section gains a UI language switch (auto / Chinese / English); written to `I18n.PreferredLanguage` in the config file and persisted across restarts. Language detection now uses a three-tier fallback (config → game Locale keyword match → PlayerPrefs).
- Aligned the semi-transparent background with the other sidebar-embedded mods (HwAssistive previously rendered noticeably darker; brought back to the saveManager baseline), so switching sidebar tabs no longer flickers between two shades of black.

## 1.1.1

- Fixed the game re-handing out starting supplies (emergency light, or lantern + dogfood + waterbottle + trashbag) every time you reload or roll back to layer 1. The game's `WorldGeneration.WorldPlacePlayer` has no `loadedRun` guard, so it reissues whatever `runSettings["startingsupplies"]` says on every layer-1 generation. Added a "Skip starting supplies on load" toggle (Settings → Misc, on by default); turn it off to restore vanilla behaviour.

## 1.1.0

- The main panel now scales uniformly to the screen resolution, so the oversized design no longer overflows; margins are kept, dragging works, and hit-testing is corrected for the scale.
- Game difficulty (RunSettings) is restored from the save on load/rollback, fixing custom difficulty reverting to default after reloading in both singleplayer and multiplayer.
- Fixed the top-right X close icon rendering wrong when the panel is scaled.

## 1.0.9

- Fixed `ImGuiImeRecovery` clearing `FocusControl` while external IMGUI (e.g. KrokMP connection form) had keyboard focus, blocking IP/username input; focus is now cleared only when this panel closes via `RequestClear()`.

## 1.0.8

- Fixed hotkeys (this mod and CuHotbar) stopping after Chinese IME input in panel text fields (nickname, seed, etc.). Closing the panel clears the stuck IMGUI keyboard focus.

## 1.0.7

A systematic pass around the "deterministic world + rollback" core:

- **Pick a world engine**: choose `QoL (preferred)` or `This mod` in the panel. With QoL it defers world determinism to QoL; with this mod its own engine takes over and temporarily disables QoL's world involvement. When QoL is not installed, the `QoL` button is disabled and only this mod's engine is available.
- **Multiplayer support**: a multiplayer save is packed as a slot from KrokMP's whole `mp_save` directory; rollback uses the host's two-stage reload (return to main menu, then auto-reload), and everyone reconnects into the backup point.
- **Unified logging**: all logs go through one sink; enable "Show mod logs in game console" in settings to view them in the console opened with the ` key.
- **Update check**: compares against the latest GitHub release on startup and shows a red notice at the top-left when a new version exists; can be turned off in settings.

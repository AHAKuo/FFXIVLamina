# TODO

## A) Lamina core — V1 implemented (2026-10-15)

### Done
- [x] Models: profiles, pass groups (normal + final), passes, condition stacks (16 kinds), layers (Vitrine, Glamourer, Penumbra Adjust, Honorific, Moodles), ApplyMask.
- [x] Runtime: `VirtualDesign` with provenance and content hash, `DesignDraft` merge rules (unset = passthrough), `GameStateSnapshot`.
- [x] Pipeline: StateTracker → ConditionEvaluator (hold / linger) → PassCompositor → DesignDiffer → Applier → RedrawScheduler → AppliedStateStore.
- [x] Clean slate: `RemoveAllTemporaryModSettings` with Lamina's negative key, then full mod state, then one redraw.
- [x] IPC bridges: Glamourer, Penumbra (required), Honorific, Moodles (optional), availability tracking.
- [x] Cleanup / Revert on toggle off, unload, logout, profile switch.
- [x] JsonStore: atomic writes, versioned files, migrations scaffold, backups, broken-file isolation, debounced saves.
- [x] Import / export (JSON and `LAMINA1:` clipboard strings).
- [x] Commands: `/lam`, `/lam on|off`, `/lam profile <name>`, `/lam reapply`, `/lam redraw`, `/lam why`, `/vit`.
- [x] UI: Passes (groups / passes / editor with Add Condition + Add Layer mini menus, Macro Override, preview), Profiles, Settings, About, debug overlay.
- [x] 209 unit tests (persistence, compose, state, engine, bridges fakes, vitrine).

### In-game verification (build order step 1 and 9 of the TDD)
- [ ] Glamourer accepts the built state JObject; `Lock` holds against automation on zone / job / gearset change (`StateFinalized` re-assert path).
- [ ] Penumbra: Lamina collection found, temp settings written with the negative key, `RemoveAllTemporaryModSettings` clears only Lamina's, one redraw per real change, no redraw when nothing changed.
- [ ] Honorific `SetCharacterTitle` JSON accepted; Moodles `*ByPtrV2` calls work with the local player address.
- [ ] Condition sources: housing own / FC house detection, `GetPosture()` poses, walk / run speed thresholds, sanctuary flag.
- [ ] Jumping / casting don't flicker (hold / linger defaults); zone change while mounted defers the redraw; toggle off restores the character; unassigning the Lamina collection stops mod changes.
- [ ] Vitrine: mod manager adapter returns metadata (else meta.json fallback), changed-item value types classify correctly, images load.

## B) Vitrine — V1 implemented
- [x] Mod catalog with classification, incremental scan, cache.
- [x] Auto designs per mod (deterministic ids), baselines, variants, `designs.json`.
- [x] `/vit` window: Mods (card grid + details), Collections, Settings, About.

## C) Release and register in ahadevtools
- [x] Add `images/icon.png` (512x512).
- [ ] `dotnet build -c Release` in `Lamina/`, commit, push.
- [ ] Tag and release: `git tag -a v0.1.0 -m "Lamina v0.1.0"`, `git push origin v0.1.0`, then
      `gh release create v0.1.0 Lamina/bin/Release/Lamina/latest.zip Lamina/bin/Release/Lamina/Lamina.json --title "Lamina v0.1.0" --notes "Initial release."`
- [ ] Add `"AHAKuo/FFXIVLamina"` to `ahadevtools/plugins.json` and a row to its README table.
- [ ] Regenerate: `cd ahadevtools && GITHUB_TOKEN=$(gh auth token) python scripts/build_pluginmaster.py`, commit, push.
- [ ] Verify `https://raw.githubusercontent.com/AHAKuo/ahadevtools/main/pluginmaster.json` lists Lamina and it shows in `/xlplugins`.

## D) Split Vitrine into its own plugin (after the engine is verified in game)
Lamina only uses Vitrine for the Vitrine Design layer (design id → mod states), the design/catalog pickers in the Lamina UI, a designs-changed signal, and hosting the `/vit` window. Vitrine only needs a data directory, a collection + temp key for "Preview on character", and a re-evaluate callback from Lamina. The split is cheap:
- [ ] Give Vitrine its own plugin project / config directory (today it borrows Lamina's `vitrine/` folder via `IVitrineHost.DataDirectory`).
- [ ] Expose a small Vitrine IPC: list designs, get a design's mod states, designs-changed event, open window.
- [ ] Replace Lamina's project reference with an optional `VitrineBridge` (like Honorific / Moodles): layer type greyed out when Vitrine is not installed, existing layers skipped with "plugin missing".
- [ ] Decide where Vitrine's preview writes go without Lamina (user-chosen collection, or Penumbra per-player temporary settings with Vitrine's own key).
- [ ] Register the new plugin in `ahadevtools` and release both.

## Later (TDD section 14)
- [ ] Moodles presets: expand to statuses so cleanup can remove them (needs preset-info IPC in the bridge).
- [ ] Other targets (characters / players).
- [ ] Location picker for `PlaceNameIds` (field exists, no UI picker yet).

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
- [x] `dotnet build -c Release` in `Lamina/`, commit, push.
- [x] Tag and release (v0.1.0 released 2026-10-15): `git tag -a v0.1.0 -m "Lamina v0.1.0"`, `git push origin v0.1.0`, then
      `gh release create v0.1.0 Lamina/bin/Release/Lamina/latest.zip Lamina/bin/Release/Lamina/Lamina.json --title "Lamina v0.1.0" --notes "Initial release."`
- [x] Add `"AHAKuo/FFXIVLamina"` to `ahadevtools/plugins.json` and a row to its README table.
- [x] Regenerate: `cd ahadevtools && GITHUB_TOKEN=$(gh auth token) python scripts/build_pluginmaster.py`, commit, push.
- [x] Verify `https://raw.githubusercontent.com/AHAKuo/ahadevtools/main/pluginmaster.json` lists Lamina and it shows in `/xlplugins`.

## D) Split Vitrine into its own plugin (in progress 2026-10-15)
Lamina only uses Vitrine for the Vitrine Design layer (design id → mod states), the design/catalog pickers in the Lamina UI, a designs-changed signal, and hosting the `/vit` window. Vitrine only needs a data directory, a collection + temp key for "Preview on character", and a re-evaluate callback from Lamina. The split is cheap:
- [x] Give Vitrine its own plugin project / config directory (repo `AHAKuo/FFXIVVitrine`, folder `../vitrine`).
- [x] Expose a small Vitrine IPC: list designs, get a design's mod states, designs-changed event, open window, preview (`vitrine/docs/IPC.md`).
- [x] Replace Lamina's project reference with an optional `VitrineBridge` (like Honorific / Moodles): layer type greyed out when Vitrine is not installed, existing layers skipped with "plugin missing".
- [x] Vitrine's preview writes go to Penumbra per-player temporary settings with Vitrine's own key.
- [ ] Register the new plugin in `ahadevtools` and release both.

## E) Vitrine designs that also equip (Apply Action)
Vitrine is adding an **Apply Action** to each design (baseline and variants): which equipment slots the design applies
to and which exact affected piece goes in each slot. Lamina should honour it.
- [ ] Read `applyAction` from the Vitrine design JSON (IPC minor bump; see `vitrine/docs/IPC.md`).
- [ ] Vitrine Design layer: besides enabling the mod, contribute the selected pieces to the virtual design's equipment
      slots (item ids resolved by name through the Item sheet if Vitrine sends names), so one layer both enables the
      mod and dresses the character. Unselected slots stay passthrough.
- [ ] Layer editor: show the design's Apply Action summary and a per-layer toggle "Also equip the selected pieces"
      (default on when the design has an Apply Action); Macro Override masks still apply to those slots.
- [ ] Preview / `/lam why`: provenance names the Vitrine design for equipped pieces.

## F) Native layer types: Equipment Changes and Appearance Changes
Purpose: stop creating throwaway Glamourer designs for one-field tweaks ("only hair colour white at the end of the
cascade", "no feet while sitting"). These layers write straight into the virtual design, so they cascade and mask like
any other layer and Lamina applies them in the same single batch.
- [ ] **Equipment Changes layer** (`kind: "Equipment"`): a list of per-slot edits, each one of
      - *Visibility*: hat / visor / weapon / viera ears toggles (MetaIndex);
      - *Dyes*: dye 1 / dye 2 per slot (Stains) from the game's Stain sheet, with "keep item, change dye only";
      - *Gear*: an item from the game's Item sheet per slot (searchable picker by name, slot filtered; weapons pair
        main hand / off hand), with "Nothing" (remove the piece, e.g. feet while sitting) and crest on / off.
      Unset slots stay passthrough.
- [ ] **Appearance Changes layer** (`kind: "Appearance"`): per-field edits of CustomizeIndex values (hair style, hair
      colour, highlights on / off + colour, eye colour right / left for odd eyes, eye shape, height, face, skin colour,
      lip colour, facial features, tail / ears, bust, face paint...) with pickers that show only values valid for the
      character's current clan / gender (GameCatalog + the actor's state from Glamourer), plus advanced RGB
      parameters (skin, hair, highlights, eyes, lips) where the user wants exact colours. Unset fields passthrough.
- [ ] Models + JSON kinds, resolver → DesignDraft fields with provenance, Macro Override masks apply, Preview and
      `/lam why` name the layer, Add Layer mini menu entries, tests for cascade order (e.g. Glamourer design then an
      Appearance layer overriding only hair colour; a final "Sitting" group with an Equipment layer removing feet).

## G) Penumbra (Additive) layer and Penumbra defaults
Today a Penumbra Adjust layer replaces the whole mod entry when it merges (`Mods[dir] = v`), so a later pass cannot
tweak one option group of a mod an earlier pass enabled without restating every group.
- [ ] **Penumbra (Additive) layer** (`kind: "PenumbraAdditive"`): per mod, only the option groups listed are changed;
      everything else is inherited from the mod state already in the running draft (the earlier pass's Eve body
      settings), or, when the mod is not in the draft yet, from the mod's effective settings in the player's collection
      (`GetCurrentModSettings`) with enabled = true. Priority and enabled are optional overrides. Example: a "Humid"
      pass that only sets Eve's skin group to "Glossy" to simulate sweat. Merge rule documented in TDD 5.7; provenance
      per option group so `/lam why` shows which pass set the gloss.
- [ ] **Defaults when adding a mod**: when a mod is added to a Penumbra Adjust or Additive layer, prefill its settings
      from Penumbra (the collection's current settings via `GetCurrentModSettings`, else each group's default option)
      so the user starts from what Penumbra would do and only changes what they mean to. A "Reset to Penumbra
      defaults" button per mod.
- [ ] Settings editor shows, per group, whether the value is "inherited" (dimmed) or "set by this layer"; Additive
      layers only serialise the groups the user touched.

## H) Stretch: File Redirect layer, then Texture Overlay (decals)
Decals on bodies or gear mean producing a texture, not toggling a setting. Two stages:
- [ ] **File Redirect layer** (`kind: "FileRedirect"`): game path → file on disk, registered as a Penumbra temporary mod
      (`AddTemporaryMod` with Lamina's tag, removed on cleanup / `RemoveTemporaryMod`). Any one-file swap without a
      Penumbra mod. Foundation for the next stage.
- [ ] **Texture Overlay layer** (`kind: "TextureOverlay"`): a PNG decal + target (body skin diffuse, or a gear item's
      material texture) + UV position / scale / rotation. Lamina resolves the texture currently in use on the character
      (Penumbra resolve / resource tree APIs, so body mods like Eve or Bibo+ are respected), composites the decal, writes
      a `.tex` into the cache keyed by content hash (rebuilt only when inputs change), and redirects it through the File
      Redirect machinery. Caveats to design for: UV layouts differ per body (vanilla / Eve / Bibo+) so the editor needs
      a UV template preview; seams distort; Dawntrail skin uses diffuse + mask + normal + index (colour decals in the
      diffuse first, other maps later); encode cost must never land on the apply path.

## Later (TDD section 14)
- [ ] Moodles presets: expand to statuses so cleanup can remove them (needs preset-info IPC in the bridge).
- [ ] Other targets (characters / players).
- [ ] Location picker for `PlaceNameIds` (field exists, no UI picker yet).

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
- [x] Unit tests (persistence, compose, state, engine, bridges fakes, vitrine): 209 after v0.2.0.

### In-game verification (build order step 1 and 9 of the TDD)
- [x] Glamourer accepts the built state JObject (verified 2026-10-15). Note: Glamourer's own automation conflicts with
      Lamina; `Lock` does not reliably hold against it. Glamourer automation must be disabled (or Glamourer not driving
      the character) while Lamina is on. Document this in the README.
- [x] Penumbra (verified 2026-10-15): Lamina collection found (when missing Lamina waits for it to be created and
      applies nothing), temp settings written with the negative key so `RemoveAllTemporaryModSettings` clears only
      Lamina's, one redraw per real change, no redraw when nothing changed.
- [ ] Create the Lamina collection from the plugin when it is missing: a button in Settings / the status banner.
      Penumbra's IPC (`Penumbra.Api/Api/Collection.cs`) can list and assign collections but has no create call, so
      this needs either a new IPC on the Penumbra side or writing the collection file into Penumbra's config folder
      and asking it to reload. Until then the status banner should tell the user to create "Lamina" by hand.
- [x] Honorific `SetCharacterTitle` JSON accepted (verified 2026-10-15).
- [ ] Moodles `*ByPtrV2` calls work with the local player address.
- [ ] Zone change while mounted defers the redraw; toggle off restores the character; unassigning the Lamina collection
      stops mod changes.
- Condition sources and flicker: see section B.

## B) Conditions — in-game verification per kind
Each kind has its own box so this list can grow without blocking section A. Tick a kind once every field it exposes
has been exercised at least once; note partial coverage inline.
- [ ] Time (ET / LT clock, ranges that wrap midnight).
- [ ] Calendar (date range, weekdays, seasonal event).
- [ ] Weather (weather ids, groups: Clear / Overcast / Wet / Stormy / Windy / Hot / Cold / Arcane).
- [ ] Biome — desert verified 2026-10-15; other biomes open.
- [ ] Location (territory, region, place name, world, sanctuary flag, indoors, inn room, home world).
- [ ] Duty (content types, specific content ids, in FATE).
- [ ] Housing — own house verified 2026-10-15; FC house, apartment, chambers, other, district / ward / plot, inside open.
- [ ] Social (solo / party / alliance, size min / max, named players).
- [ ] Character (race, clan, gender, named characters).
- [ ] Job — job ids verified 2026-10-15; roles, disciplines, gearset ids, level range, weapon drawn open.
- [ ] Online status (OnlineStatus ids, e.g. Role-playing).
- [ ] Status effect (status ids, min stacks).
- [ ] Movement (idle, walking, running, sprinting, jumping, falling, mounted, pillion, flying, swimming, diving,
      being moved; walk / run speed thresholds).
- [ ] Body state (dead, in combat, casting, wet, weapon drawn, transformed, disguised, loading, low HP %).
- [ ] Emote — sitting pose verified 2026-10-15; emote ids, looping only, ground sitting / dozing / standing poses open.
- [ ] Activity (crafting, gathering, fishing, performing, NPC dialogue, cutscene, GPose, trading, mini game,
      retainer / market, minion summoned).
- [ ] Hold / linger: jumping and casting don't flicker with the defaults.

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
- [x] Register the new plugin in `ahadevtools` and release both (Vitrine v0.1.0, 2026-10-15). Vitrine's own work is tracked in `../vitrine/TODO.md`.

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

## F) Native layer types: Equipment Changes and Appearance Changes — done (2026-10-15, v0.2.0)
Purpose: stop creating throwaway Glamourer designs for one-field tweaks ("only hair colour white at the end of the
cascade", "no feet while sitting"). These layers write straight into the virtual design, so they cascade and mask like
any other layer and Lamina applies them in the same single batch.
- [x] **Equipment Changes layer** (`kind: "Equipment"`): per slot an item from the Item sheet (searchable, slot filtered,
      "Nothing" for gear slots using Glamourer's custom ids), dye 1 / dye 2 from the Stain sheet with "keep item, change
      dye only", crest on / off, plus hat / visor / weapon / viera ears visibility. Unset slots pass through.
- [x] **Appearance Changes layer** (`kind: "Appearance"`): CustomizeIndex edits with pickers built from CharaMakeType /
      HairMakeType / CharaMakeCustomize and the human.cmp colour table for the character's clan + gender (icons for faces,
      hair, tails, face paints, facial features; colour swatches; shape lists; percentages; flags), wetness, and the
      advanced parameters (RGB / RGBA / percentage / value). Unset fields pass through.
- [x] Models + JSON kinds, resolver → DesignDraft fields with provenance, Macro Override masks apply, Preview and
      `/lam why` name the layer, Add Layer entries, tests for cascade order and masks.
- [x] Body Type is not offered and never sent (values other than 1 are NPC models and crash the game); Race follows Clan.
- [ ] Verify in game: "Nothing" ids are accepted by ApplyState for every gear slot; icon pickers render; colour swatches
      match the character creator; weapons pair main / off hand sensibly (no "Nothing" offered for weapons).
- [x] **Facewear (Glasses) slot in the Equipment layer** (v0.2.1): `EquipmentLayer.Bonus` holds Glamourer bonus item
      ids (`BonusItems`: Glasses row | 1<<49; "Nothing" = 3<<48 | 1<<24, verified against the user's design files),
      names from the `Glasses` sheet, editor row without dyes / crest, Preview shows the name.
- [x] Verify in game (2026-10-15): facewear "Nothing" clears the slot; a chosen pair shows up; Macro Override masks include it.

## G) Penumbra Adjust: per-setting "applies" checkboxes and Penumbra defaults — done (2026-10-15, v0.2.0)
One Penumbra layer, not two. Mod entries now merge per part (enabled flag, priority, each option group), so a later
pass can tweak one option group of a mod an earlier pass enabled without restating every group.
- [x] **Per-setting "applies" checkbox**: enabled flag, priority and every option group have one. Checked = this layer
      sets the value; unchecked = inherited from the mod state already in the running draft, else from the mod's
      permanent settings in the Lamina collection (`GetCurrentModSettings`, temporary settings ignored) at apply time.
      Buttons per mod: **Apply all** (every group ticked, every multi-select option on), **Disable all** (every group
      ticked, every option off), **Reset to Penumbra** (every group ticked with the collection's values), **Inherit all**
      (every group unticked); multi-select groups also have all / none.
- [x] Merge rule (TDD 5.7): `ModContribution` merges per part with provenance per part; `/lam why` and Preview show
      which pass set each group. Profile files bump to v2 (no rewrite: v1 entries already state the flag and the groups
      they list; their unlisted groups are now inherited from the collection instead of reset to the mod default, which
      is the same thing unless the mod was configured in the Lamina collection by hand).
- [x] **Defaults when adding a mod**: picking a mod prefills enabled + priority + every group from the collection's
      current settings (else first option of single-select groups); "Reset to Penumbra" re-prefills ticked groups.
- [x] Editor shows unticked groups dimmed with their inherited value; only ticked groups are serialised.
- [ ] Verify in game: inherited values track the collection after "Re-read collection"; a "Humid" pass that only ticks
      the skin group leaves the body group of an earlier pass untouched.

## H) Decal layer — done, verified in game (2026-10-16)
A PNG projected onto the player's skin: click the character to place it, Size / Rotation / Opacity sliders, baked into the
skin diffuse the clicked spot samples and redirected with one Penumbra temporary mod (`Lamina.Decal`, priority 9000).
Design: texture-space projection in the character's rest pose (`Lamina/Decals/Bake`), live skin meshes from the draw
object's own models posed through XivLiveMesh (racial deformer `human.pbd`, shape keys, attribute visibility) and picked
with the game camera's ray (`Lamina/Decals/Mesh`, `XivLiveMesh/` vendored from Proteus, AGPL), bake cache + BC7 through
Penumbra's converter + temp mod (`Lamina/Decals/Publish`), one card in the layer editor with pick mode and a debug mesh
overlay (`Lamina/UI`). 411 unit tests.

### Verified in game
- Placement on body-mod skin (Eve), gear-provided skin (Midlander-authored tops on a Miqo'te, deformer on), freely named
  outfit skin materials (classified by `skin.shpk`), several skin textures on one body (each baked separately), the face.
- Hidden geometry (Eve's attribute-gated neck/seam parts, piercings) no longer catches clicks or paint.
- Bake / convert / redirect / redraw loop, cache hits, temp mod removal on layer removal, profile switch and unload.

### Known limits
- A click must land on true skin: a spot covered by a cloth mesh that reuses the skin texture region (sheer tops) can
  paint under the cloth. The hover label names the surface and part before the click.
- Mirrored UV bodies show the decal on both sides (warned in the card). Diffuse only (no normal / mask relief).
- `atr_lod` is treated as always enabled (LOD 0 detail parts); if an outfit's main skin ever misses the pick, check the
  census line in the log first.
- Mare: the redirected file lives in `<config>/decals/cache`; not checked whether sync picks temporary-mod files up.

## I) Sub-passes
A pass can contain sub-passes that are gated by their parent: a sub-pass is only evaluated while the parent pass
matches, and its layers stack on top of the parent's.
- [ ] Model: `Pass.SubPasses` (list of `Pass`, same shape: name, enabled, conditions, layers, macro override); nesting
      depth 1 to start (sub-passes have no sub-passes), JSON round trip, profile import / export warnings.
- [ ] Evaluation: parent conditions gate the whole subtree; a sub-pass adds its layers after the parent's when its own
      conditions match (same hold / linger rules); the evaluation report lists matched sub-passes under their parent.
- [ ] Composition: parent layers first, then each matching sub-pass in order; the parent's macro override applies to the
      subtree unless the sub-pass sets its own.
- [ ] UI: sub-passes shown indented under the parent in the Passes tab (add / reorder / move out to a top-level pass);
      the debug overlay shows parent → sub-pass; "Add sub-pass" on a pass.

## Later (TDD section 14)
- [ ] Moodles presets: expand to statuses so cleanup can remove them (needs preset-info IPC in the bridge).
- [ ] Other targets (characters / players).
- [ ] Location picker for `PlaceNameIds` (field exists, no UI picker yet).

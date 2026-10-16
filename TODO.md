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

## J) 2026-10-16 batch (five features, unverified in game; on branch `wip/integration`)
- [x] **Collapsible layer cards**: arrow / click on the header folds a card; `Layer.Collapsed` is saved; collapse-all / expand-all
      icons on the Add Layer line. Collapsing never invalidates the engine.
- [x] **Layer duplicate / copy / paste / move**: per-card Duplicate (inserted below), Copy (`LAMINALAYER1:` clipboard string, also
      kept in memory), Paste layer (next to Add Layer), Move / copy to another pass popup (profile → group → pass).
      `ProfileStore.DuplicateLayer / CopyLayerTo / MoveLayerTo`, `ProfileTransfer.ImportLayer`. The Profiles-tab importer rejects a
      layer string with a message pointing at Paste layer.
- [x] **Vitrine hair / face / tail as appearance** (Vitrine IPC 1.4 `customizations`, Vitrine repo change UNCOMMITTED in
      `../vitrine`, needs a Vitrine release): the Vitrine Design layer sets Hairstyle / Face / TailShape for the player's clan +
      gender; `VitrineDesignLayer.ApplyCustomizations` (default on) toggles it; masks apply.
- [x] **Save as Glamourer design**: pass and group editors get "Save as Glamourer design…" (name, include mod associations);
      `GlamourerDesignWriter` writes the FileVersion 3 layout, `IGlamourerBridge.AddDesign`. Mods with inherited (null) enable
      state are left out and reported. Honorific / Moodles / decals are not part of a Glamourer design.
- [x] **Fallback to the game state when no rule applies**: `LaminaConfig.NoRuleFallback` (GameState default, Automation). An
      empty design reverts with Glamourer `RevertState` (game) instead of automation; when a new design drops fields the previous
      one set, the Applier reverts first and then applies the new state in the same frame so dropped fields fall back; cleanup
      paths use the same setting. Settings → "When no rule sets a field".
- [ ] Verify in game: Glamourer accepts the written design JSON (`AddDesign`) and honours its mod settings; revert-then-apply in
      one frame does not flicker the kept fields (if it does: apply the dropped fields with the baseline's values instead);
      the hair value from a Vitrine hair mod shows after both plugins are rebuilt; the layer header fits at the default window
      size; ImGui clipboard paste works.

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

### Known issues (fix later)
- [x] **Decals can break when a virtual design is first applied** — fixed 2026-10-16 (unverified in game): decals are no longer
      published in the same frame as a mods redraw. The apply marks them pending; `DecalFollowUp` publishes after the player's
      draw object was rebuilt (Penumbra `GameObjectRedrawn`, or the 1 s draw-object poll that also catches zone changes / login /
      gear swaps) and every model reports LoadState 7 (5 s timeout). A publish first computes a situation key (mods hash +
      draw-object key + decal specs + image stamps): equal to the installed one → nothing; known from the 16-entry `RedirectLedger`
      → the remembered redirect map is re-added without baking; otherwise the slow path (bake cache by content key). The second
      redraw is only requested when the redirect map actually changed.
- [ ] Verify in game: first apply of a decal design shows the decal without `/lam reapply`; rule change with the same skin causes
      one redraw only; a rule set that leaves and returns reuses its map; zone change keeps the decal; the LoadState 7 check is
      sufficient (slots that populate late would be missed; the two-tick stability check and the 5 s timeout are the fallbacks).
- [ ] Residual edge: a game-driven reload inside the 1–2 s window in which `Prepare` has the temp mod out of the collection can
      leave the character without decals until the next trigger.
- [x] Settings → Decals: "Clean up decal leftovers" (forget non-installed situations, delete unreferenced bakes / temp files,
      prune unreferenced images, remove a stray temp mod when nothing is applied) and "Clear all decal data" (unconditional:
      temp mod, whole cache, every image; layers keep their image name and show it as missing). Decal images are now stored by
      content hash (re-picking the same file reuses it) and pruned on start, after a pick and after a layer removal.
- [ ] Old known issue text kept for reference:
- [ ] **Decals can break when a virtual design is first applied**; `/lam reapply` once the draw has finished fixes them.
      Likely cause: the apply publishes mods and decals in the same frame, so the publisher snapshots the skin meshes and
      textures of the outfit still on screen, bakes against those, and the redraw that follows loads the new outfit with a
      different skin model / texture. Fix idea: when a mods redraw was sent and decals are applied, forget the decal hash and
      re-publish once after `GameObjectRedrawn` for the player (the mesh provider's cache key already tracks model
      LoadState, so a short delay or a LoadState check keeps it from baking against half-loaded models).

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

## K) Customize+ and Simple Heels layer types
Two more optional-plugin layers, same shape as Honorific / Moodles: an IPC bridge with availability tracking, the layer
greyed out in Add Layer when the plugin is missing, existing layers skipped with "plugin missing", cleanup on toggle off /
unload / logout / profile switch. Both plugins are vendored as submodules (`CustomizePlus/`, `SimpleHeels/`); IPC surface
read from that source 2026-10-16 (Customize+ API 6.x in `CustomizePlus/CustomizePlus/Api/`, EzIPC names prefixed
`CustomizePlus.`; Simple Heels API 2.5 in `SimpleHeels/SimpleHeels/ApiProvider.cs`, names prefixed `SimpleHeels.`).

### Customize+ — has profiles (its "presets"), so the layer picks one
- [ ] **Customize+ layer** (`kind: "CustomizePlus"`): picker over the user's profiles from `Profile.GetList` (tuple: UniqueId,
      Name, VirtualPath, Characters, Priority, IsEnabled; "somewhat heavy", cache it and refresh on `Profile.OnUpdate`
      (object index, profile id; Guid.Empty = none)). Store the profile id + name; show "missing" when the id is gone.
- [ ] Apply route = **temporary profile** (same as Mare, nothing persisted, sits above the user's normal profiles):
      `Profile.GetByUniqueId(Guid) → (int, string? json)` gives `IPCCharacterProfile { Bones: { name → { Translation,
      Rotation, Scaling, ChildScaling, ChildScaleIndependent, Propagate* } } }`; Lamina merges the bones of every matching layer
      (later pass wins per bone, Macro Override masks apply) and calls `Profile.SetTemporaryProfileOnCharacter(ushort
      objectIndex, string json) → (int, Guid?)`; cleanup / empty design calls `Profile.DeleteTemporaryProfileOnCharacter(ushort)`
      (also `DeleteTemporaryProfileByUniqueId(Guid)`). Error codes are ints (0 = success). Check `General.GetApiVersion`
      breaking == 6 and `General.IsValid` before use. `Profile.GetActiveProfileIdOnCharacter(ushort) → (int, Guid?)` for
      `/lam why`.
- [ ] Optional second mode, persistent: `Profile.EnableByUniqueId / DisableByUniqueId / SetPriorityByUniqueId(Guid, int)` and
      per-template `Profile.GetTemplates(Guid) → (int, List<(UniqueId, Name, Bones, IsEnabled)>)`,
      `EnableTemplateByUniqueId / DisableTemplateByUniqueId(Guid profile, Guid template)`. These write the user's Customize+
      config, so only offer them as an explicit "toggle saved profile" layer if at all; the temp-profile route is the default.
- [ ] Bones only: no clan / gender scaling or other knobs are exposed. Preview shows profile name + bone count.

### Simple Heels — no presets over IPC, the layer is raw offsets
- [ ] **Simple Heels layer** (`kind: "SimpleHeels"`): height offset (Y) and optionally full temp offset (X, Y, Z, rotation,
      pitch, roll) plus per-emote offsets; later passes override; Macro Override masks apply.
- [ ] IPC: `SimpleHeels.RegisterPlayer(int objectIndex, string json)` / `UnregisterPlayer(int)` (empty json also unregisters).
      The json is `IpcCharacterConfig`: `DefaultOffset` (float Y), `EmoteConfigs` [{ Emote {EmoteModeId, CPoseState},
      LinkedEmotes, Offset xyz, Rotation, RelativeOffset }], `TempOffset` {X,Y,Z,R,Pitch,Roll}, `MinionPosition`, `EmotePosition`,
      `Tags` {string→string}, `CustomVoiceId`, `PluginVersion`. Honoured for the local player (`TryGetCharacterConfig` checks
      `IpcAssignedData` first). `SimpleHeels.ApiVersion → (2, 5)`; `SimpleHeels.Ready` fires on load.
- [ ] Caveat 1: while Lamina has a config registered, Simple Heels ignores the player's own per-footwear heel configs and uses
      only the registered `DefaultOffset` (HeelsConfig / sitting offsets are not part of the IPC json). So the layer should
      read the current resolved config (`SimpleHeels.GetLocalPlayer → string json`, and the `SimpleHeels.LocalChanged(string)`
      event, both built with allowIpc = false so they keep reporting the user's real per-shoe offset) and register
      `DefaultOffset = resolved + Lamina delta`, re-registering when `LocalChanged` fires (debounced 250 ms upstream).
      Offer "absolute" vs "add to current" in the editor.
- [ ] Caveat 2: Mare syncs from `GetLocalPlayer` / `LocalChanged`, which report the user's own config, so other Mare users will
      not see the Lamina offset. Document it on the card.
- [ ] Also available: `SimpleHeels.RegisterEmoteOverride(int objectIndex, uint emoteModeId, byte cPose)` / `ClearEmoteOverride(int)`
      (force which emote config is active); `SetTag / GetTag / RemoveTag(int, string[, string])` + `TagChanged` event (free
      key/value store other plugins read). Not needed for v1.

### Shared
- [ ] Bridges `ICustomizePlusBridge` / `ISimpleHeelsBridge` with fakes, availability tracking, JSON kinds, models, Add Layer
      entries, tests for cascade order, masks and cleanup.
- [ ] Verify in game: the temporary Customize+ profile shows and clears and wins over the user's assigned profile; the heel
      offset shows and clears; per-shoe offsets come back after unregister; neither plugin's own automation fights Lamina
      (document like the Glamourer note in section A if it does).

## Later (TDD section 14)
- [ ] Moodles presets: expand to statuses so cleanup can remove them (needs preset-info IPC in the bridge).
- [ ] Other targets (characters / players).
- [ ] Location picker for `PlaceNameIds` (field exists, no UI picker yet).

# TODO

## A) Finish Lamina

### Data models
- [ ] `VirtualDesign` struct (TDD.md): resolved Glamourer, Penumbra, Honorific and Moodles state.
- [ ] `RuleCascade` struct (TDD.md): ordered rulesets that layer into one VirtualDesign.
- [ ] Ruleset / condition model (what triggers a ruleset: character, zone, job, state, etc.).
- [ ] Plugin configuration (profiles, rulesets, virtual designs, saved to Dalamud config).

### Backend
- [ ] Glamourer IPC (Glamourer.Api): list designs, apply layered designs.
- [ ] Penumbra IPC (Penumbra.Api): resolve collection, set mod enable/disable/priority, trigger a single redraw.
- [ ] Honorific IPC (see `Honorific/IpcProvider.cs`): set/clear titles.
- [ ] Moodles IPC (see `Moodles/Moodles/IPCProcessor.cs`): apply/remove statuses and presets.
- [ ] Resolver: evaluate rulesets → merge into one VirtualDesign → apply all four in one pass → one redraw.
- [ ] Cleanup / revert when a virtual design is swapped or disabled.

### Frontend
- [ ] Main window (`/lamina`): profiles, rulesets, virtual designs.
- [ ] Virtual design editor (pick Glamourer designs, Penumbra mods, Honorific title, Moodles).
- [ ] Ruleset editor (conditions and ordering).
- [ ] Settings tab (per-integration enable toggles).

## B) LazyLamina

### Data models
- [ ] Mod catalog: Penumbra mods indexed by what each one affects (VFX, SFX, animation, gear, etc.).
- [ ] Auto-generated virtual designs per mod group, with variants/presets (toggles on/off).

### Backend
- [ ] Scan Penumbra mods and classify by affected game content.
- [ ] Generate virtual designs from the catalog; expose them to Lamina rulesets.
- [ ] Enable/disable/priority handling for mods used by a virtual design, with cleanup on removal.

### Frontend
- [ ] LazyLamina menu: mods auto-organized by category, no manual Penumbra interaction needed.
- [ ] Variant/preset editor per mod.

## C) Release and register in ahadevtools
- [ ] Add `images/icon.png` (512x512) so the IconUrl in `Lamina/Lamina.csproj` resolves.
- [ ] `dotnet build -c Release` in `Lamina/`, commit, push.
- [ ] Tag and release: `git tag -a v0.1.0 -m "Lamina v0.1.0"`, `git push origin v0.1.0`, then
      `gh release create v0.1.0 Lamina/bin/Release/Lamina/latest.zip Lamina/bin/Release/Lamina/Lamina.json --title "Lamina v0.1.0" --notes "Initial release."`
- [ ] Add `"AHAKuo/FFXIVLamina"` to `ahadevtools/plugins.json` and a row to its README table.
- [ ] Regenerate: `cd ahadevtools && GITHUB_TOKEN=$(gh auth token) python scripts/build_pluginmaster.py`, commit, push.
- [ ] Verify `https://raw.githubusercontent.com/AHAKuo/ahadevtools/main/pluginmaster.json` lists Lamina and it shows in `/xlplugins`.

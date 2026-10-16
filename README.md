## About
Lamina is a FFXIV dalamud plugin that takes your appearance automation to the next level by introducing the concept of virtual designs. Layer multiple designs by rulesets and apply a final virtual look with only one redraw. It's better to read the example to get it instead of explaining.

## Example
You are RP'ing as multiple characters, and each of them has a special look in different possible situations, and not only that, they also have special titles, buffs or debuffs, and even specific mods activated at a time (VFX, SFX, Animation, etc...). You want to switch between them in one click and also see them swap between all these modes super quick.
If that sounds like something you want, then Lamina is for you.

## Installation
Lamina is one of ahadev's plugins and installs through Dalamud's plugin installer from a custom repository.

1. In game, type `/xlsettings` to open the Dalamud settings and go to the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL into the empty field, click the **+** button, then **Save and Close**:
   ```
   https://raw.githubusercontent.com/AHAKuo/ahadevtools/main/pluginmaster.json
   ```
3. Type `/xlplugins` to open the plugin installer, search for **Lamina** and click **Install**.
4. Make sure Glamourer and Penumbra are installed and enabled, then type `/lam` to open Lamina.

Updates arrive through the same installer like any other plugin. The repository also lists ahadev's other plugins (Vitrine among them), so you only add it once.

## Requires
- Glamourer.
- Penumbra.

## Supports
- Honorific.
- Moodles.

## Features
- Penumbra mods enable/disable state handling based on virtual designs (auto-resolved).
- Layering of Glamourer designs.
- Native layers without a Glamourer design: **Equipment Changes** (item per slot incl. "Nothing", facewear, dyes, crest, hat / visor / weapon / viera ears visibility) and **Appearance Changes** (hair, colours, face, shapes, height, wetness, advanced RGB parameters), with pickers that only show values valid for your clan and gender.
- Penumbra Adjust with per-setting "applies" checkboxes: a pass can change one option group of a mod an earlier pass enabled; everything unticked is inherited from the earlier pass or the Lamina collection.
- Application of Honorific titles.
- Application of Moodles states.
- Vitrine integration (optional): use designs from the separate Vitrine plugin as layers, with a per-layer slot mask to combine pieces of several designs and a "clear other slots" switch.
- Equipment Cleanse layer: empties every gear slot (weapons stay) so the outfit layers after it start from a bare body.
- Decal layer: a PNG or JPG placed on your character's skin by clicking it in the game world, with size, rotation and opacity; it is baked into the body or face texture and shown through a temporary Penumbra mod in the Lamina collection.

## Virtual Designs
A Virtual Design is a design created by combining multiple different rulesets and combining the resulting Glamourer, Honorific, Moodles, and Penumbra statuses into one final design. This design is applied in one pass with efficient handling to prevent slow flickers or heavy condition swapping. The character is drawn ONCE after the virtual design is determined.

## Works with Vitrine (separate plugin, optional)
Vitrine is ahadev's standalone mod browser: it organizes your Penumbra mods by what they change, shows their display photos, lets you try a mod on your character (mod + a full set of its affected items) and turns each mod into a design with your baseline settings. Lamina does not need it to run. When Vitrine is installed, Lamina finds it automatically and its designs become a layer type ("Vitrine Design") you can use in any pass: the layer carries the design's mod settings (overridable per pass) and the pieces the design equips (its affected slots). "Override slots" narrows those pieces per layer, so one pass can take the body from one Vitrine design and the boots from another; the mods themselves are always applied in full. "Clear other slots" (the design's own flag, overridable per layer) empties every gear slot the layer does not fill so an earlier outfit does not bleed through. Needs Vitrine 0.3 or newer for pieces, 0.4 for the clear flag. Repository: https://github.com/AHAKuo/FFXIVVitrine

## Third-party code
- `XivLiveMesh/` is vendored from [Proteus](https://github.com/Solona/Proteus) by Solona, licensed under the AGPL-3.0-or-later (the same license as Lamina; see `XivLiveMesh/LICENSE` and `XivLiveMesh/NOTICE.md`). It poses a worn model's vertices onto the live character on the CPU (racial bone deformer, then skinning), captures the game camera and picks a point under the mouse. Lamina's decal placement is built on it. Its `PbdFile.cs` was ported in Proteus from [Meddle](https://github.com/PassiveModding/Meddle) (AGPL-3.0). It references FFXIVClientStructs (MIT) through Dalamud and does not redistribute it.

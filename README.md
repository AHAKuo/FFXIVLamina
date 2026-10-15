## About
Lamina is a FFXIV dalamud plugin that takes your appearance automation to the next level by introducing the concept of virtual designs. Layer multiple designs by rulesets and apply a final virtual look with only one redraw. It's better to read the example to get it instead of explaining.

## Example
You are RP'ing as multiple characters, and each of them has a special look in different possible situations, and not only that, they also have special titles, buffs or debuffs, and even specific mods activated at a time (VFX, SFX, Animation, etc...). You want to switch between them in one click and also see them swap between all these modes super quick.
If that sounds like something you want, then Lamina is for you.

## Requires
- Glamourer.
- Penumbra.

## Supports
- Honorific.
- Moodles.

## Features
- Penumbra mods enable/disable state handling based on virtual designs (auto-resolved).
- Layering of Glamourer designs.
- Native layers without a Glamourer design: **Equipment Changes** (item per slot incl. "Nothing", dyes, crest, hat / visor / weapon / viera ears visibility) and **Appearance Changes** (hair, colours, face, shapes, height, wetness, advanced RGB parameters), with pickers that only show values valid for your clan and gender.
- Penumbra Adjust with per-setting "applies" checkboxes: a pass can change one option group of a mod an earlier pass enabled; everything unticked is inherited from the earlier pass or the Lamina collection.
- Application of Honorific titles.
- Application of Moodles states.
- Vitrine integration (optional): use designs from the separate Vitrine plugin as layers.

## Virtual Designs
A Virtual Design is a design created by combining multiple different rulesets and combining the resulting Glamourer, Honorific, Moodles, and Penumbra statuses into one final design. This design is applied in one pass with efficient handling to prevent slow flickers or heavy condition swapping. The character is drawn ONCE after the virtual design is determined.

## Works with Vitrine (separate plugin, optional)
Vitrine is ahadev's standalone mod browser: it organizes your Penumbra mods by what they change, shows their display photos, lets you try a mod on your character (mod + a full set of its affected items) and turns each mod into a design with your baseline settings. Lamina does not need it to run. When Vitrine is installed, Lamina finds it automatically and its designs become a layer type ("Vitrine Design") you can use in any pass. Repository: https://github.com/AHAKuo/FFXIVVitrine

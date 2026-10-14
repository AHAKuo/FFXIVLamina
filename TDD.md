## Prelude
Yes, I'm a game dev, and TDD is very special to me to prep so here it is.

---

# Lamina: Technical Design Document

> **Status:** always v1. Lamina is never "finished": it gets built, tested in game, and adjusted.
> **Source of truth for visuals:** `FFXIV - Lamina.canvas` in the Obsidian vault. This document is the written version of that canvas.
> **Repo:** one solution (`Lamina.slnx`), two projects: `Lamina/` (core) and `Vitrine/` (module).

---

## Table of contents
1. [Overview](#1-overview)
2. [Glossary](#2-glossary)
3. [Goals and non-goals](#3-goals-and-non-goals)
4. [Architecture](#4-architecture)
5. [Core rules (how evaluation works)](#5-core-rules-how-evaluation-works)
6. [Interface](#6-interface)
7. [Backend: runtime pipeline and services](#7-backend-runtime-pipeline-and-services)
8. [Data models](#8-data-models)
9. [VirtualDesign field reference](#9-virtualdesign-field-reference)
10. [Persistence (JSON)](#10-persistence-json)
11. [Integrations (verified against bundled API sources)](#11-integrations-verified-against-bundled-api-sources)
12. [Vitrine](#12-vitrine)
13. [Condition catalog](#13-condition-catalog)
14. [First build scope and build order](#14-first-build-scope-and-build-order)
15. [Risks and design notes](#15-risks-and-design-notes)
16. [Open items](#16-open-items)

---

## 1. Overview

Lamina is a Dalamud plugin for FFXIV that automates a character's appearance through **virtual designs**. Instead of several rules each applying their own change (and redrawing the character every time), Lamina:

1. Reads the game state.
2. Finds which rules (passes) currently match.
3. Layers everything those passes want (Glamourer appearance, Penumbra mods, Honorific title, Moodles) into **one** immutable result: the `VirtualDesign`.
4. Applies that result in **one batch** with **at most one redraw**.

This removes the flicker and redraw spam of rule-by-rule tools (the reason Lamina exists, as a replacement for Dynamic Bridge style automation).

**Requires:** Glamourer, Penumbra.
**Supports (optional):** Honorific, Moodles.

---

## 2. Glossary

| Term | Meaning |
|---|---|
| **Profile** | A complete Lamina setup (its pass groups and passes) that ends with its **Targets**. One active profile per target. |
| **Target** | Who receives the applications. First build: the local player only. |
| **Pass Group** | A container of passes with its **own** condition stack. Normal groups act like a switch (first true wins). Groups marked **Final** are layered on top by priority. |
| **Pass** | One row in a pass group: a condition stack plus a list of layers. |
| **Condition stack** | A tree of conditions (All / Any / Not) that decides if a pass or group is true. |
| **Layer** | One contribution inside a pass: a Glamourer design, a Penumbra Adjust, a Vitrine design, an Honorific title, Moodles. |
| **Pass.FinalLayer** | All layers of one pass smashed into one (runtime only). |
| **VirtualDesign** | The final, immutable result for a target after all passes and final groups are merged. Everything that will be sent to Glamourer, Penumbra, Honorific and Moodles. |
| **Clean slate** | Before every apply, Lamina removes all of its own mod changes in its collection, then writes the new state. |
| **Evaluation direction** | Setting: top to bottom (default, bottom wins) or bottom to top. |
| **Vitrine** | The second system inside Lamina: an auto organized, read focused view of Penumbra mods that also produces Vitrine designs. |
| **RuleCascade** | Earlier working name for the ordered chain of rules that layer into one VirtualDesign. In this document that chain is **Profile → PassGroup → Pass → Layers**, walked by the `PassCompositor`. |

---

## 3. Goals and non-goals

### Goals
- One apply, one redraw per actual change. No flicker.
- Rules that layer like image layers, with predictable "who wins" rules.
- Lamina owns its own Penumbra collection and never touches the user's other collections.
- Everything the user builds is saved as human readable JSON.
- Clean exit: turning Lamina off leaves the character exactly as Glamourer and Penumbra would have it without Lamina.

### Non-goals (for now)
- Applying to other players (theoretical, see Targets).
- Reorganizing the user's Penumbra mod folders (Vitrine is a view, not a file manager).
- Replacing Glamourer's design editor. Lamina references Glamourer designs, it does not edit them.

---

## 4. Architecture

**One plugin, two systems.** Lamina and Vitrine ship together, in one repo, installed together. Vitrine lives behind an internal interface so it can become its own plugin later without rewriting Lamina.

```
                         ┌──────────────────────────────┐
                         │            Lamina            │
                         │   (plugin entry, /lam)       │
                         └──────────────┬───────────────┘
                 ┌──────────────────────┴───────────────────────┐
                 ▼                                              ▼
   ┌──────────────────────────┐   internal API      ┌──────────────────────────┐
   │      Lamina (core)       │ ◄─────────────────► │         Vitrine          │
   │ profiles, passes, layers │  Read | Set (on/off)│ mod catalog, Vitrine     │
   │ pipeline, apply, JSON    │                     │ designs, /vit            │
   └────────────┬─────────────┘                     └────────────┬─────────────┘
                │ IPC                                            │ IPC
     ┌──────────┼──────────────┬──────────────┐                  ▼
     ▼          ▼              ▼              ▼              Penumbra (read)
 Glamourer   Penumbra      Honorific       Moodles
 (required)  (required)    (optional)      (optional)
```

### Projects
| Project | Responsibility |
|---|---|
| `Lamina` | Plugin entry, UI (`/lam`), runtime pipeline, models, persistence, IPC bridges. |
| `Vitrine` | Mod catalog (reading layer), Vitrine designs, Vitrine UI (`/vit`), write helpers scoped to the Lamina collection. |
| `Glamourer.Api`, `Penumbra.Api`, `Honorific`, `Moodles` | Submodules used as API references / IPC subscribers. |

---

## 5. Core rules (how evaluation works)

### 5.1 Profiles and targets
- A **Profile** is a complete Lamina setup: its pass groups, passes and layers.
- Each profile ends with its **Targets**: who receives the applications (mostly gear, plus whatever else the layers say).
- **One active profile per target.** Enabling a profile disables any other active profile on the same target.
- **First build:** the only target kind is the local player, added by default.
- Other characters / players are theoretical. Only part of the state is readable for other players (race, job, statuses when nearby). Condition flags, gearset, UI state, online status and similar only exist for the local player.
- Profiles can clone / copy / move passes and pass groups between each other.
- Switching profiles runs Cleanup first, then one apply.

### 5.2 Pass groups
Each pass group has its **own condition stack**.

- **Normal groups** are checked in list order. The **first** true group wins and the check **stops** there. No other normal group is checked. Only the winning group's passes are evaluated.
- **Final pass groups** (toggle on the group) are evaluated after that. Every final group whose condition stack is true is included **on top**, sorted by its **Priority** field.
- Example of a final group: *"Remove a piece of equipment while sitting"*. Condition: sitting. It layers on top of whichever normal group is active.

```
final design = merge( winning normal group , active final groups by priority )
```

### 5.3 Passes and layers
- A **Pass** = name, enabled toggle, condition stack, list of layers, optional Macro Override, preview.
- A pass can hold **many layers** (a Glamourer design + a title + Moodles...), so one pass can cover several needs at once.
- Inside a pass, layers stack in the evaluation direction and are smashed into **`Pass.FinalLayer`**.
- Inside a group, every **matching** pass contributes its FinalLayer. Passes further down the chain override earlier ones.
- **Evaluation direction** (setting): top to bottom with **bottom winning** by default ("falling toward the ground"), or bottom to top.

> **UI note:** list position means opposite things in two places. For **pass groups** the top has priority (first match wins). For **passes** the bottom wins (everything matching stacks, later overrides). Each list shows a hint: "first match wins" / "lower rows override".

### 5.4 Conditions
- Conditions are added through an **Add Condition** mini menu (keeps the UI clean).
- A condition stack is a tree: groups with `All` / `Any` and `Negate`, leaves are conditions. An empty stack is **true**.
- Every condition can have **Hold** (must be true for X seconds before it counts) and **Linger** (stays true for Y seconds after it stops). Defaults come from Settings. This prevents flicker from states like jumping, casting, sprinting, combat.
- Full list of condition types: see [Condition catalog](#13-condition-catalog).

### 5.5 Layer types
Added through an **Add Layer** mini menu.

| Layer | What it does |
|---|---|
| **Vitrine Design** | A design created from Vitrine's auto mod list. Usually a full mod with its basal settings, plus optional settings override. |
| **Glamourer** | A Glamourer design (by Guid). Its **mod associations carry over as is** and Lamina applies them. |
| **Penumbra Adjust** | Direct control of Penumbra mods in the Lamina collection: enable / disable / priority / option settings. |
| **Honorific** | A title (or explicit Clear). |
| **Moodles** | Moodles to add and to remove. |

### 5.6 Macro Override (ApplyMask)
Optional per pass, for Glamourer and Vitrine layers only: restricts what the pass may change, e.g. **Only Equipment** or **Only Customization**, or per slot / per field. The player can still use Glamourer's own per design rules; since Lamina builds the final virtual design anyway, it can control this again.

### 5.7 Merge rules
| Part | Rule |
|---|---|
| Direction | Setting. Default top to bottom, bottom wins. Applies to layers in a pass and passes in a group. |
| Equipment / Customization / Meta | Per field override. **Unset = passthrough** (keep what is underneath). |
| Mods | Per mod override on top of a clean (cleared) collection. Mods no layer touches stay at default. |
| Honorific | Replaces the whole title; Clear allowed. *(proposed)* |
| Moodles | Additive, with explicit removes. *(proposed)* |
| ApplyMask | Filters a layer **before** it merges. |

### 5.8 Clean slate and opt out
- Lamina owns its collection. Before any pass, evaluation starts from a clean slate: every mod at default.
- Physically, the clear happens in the **Applier**, in the same frame as writing the new mod state and right before the single redraw. If the new design equals the last applied one, nothing is cleared and nothing is redrawn.
- Since the collection is reset every apply, Lamina never needs to remember which mods it turned on. Nothing stale can leak between designs.
- **Opt out of mods:** the player unassigns their character from the Lamina collection in Penumbra. Lamina respects it and never re-assigns on its own. (This opts out of mods only; Glamourer / Honorific / Moodles still apply. The master toggle turns everything off.)

### 5.9 Evaluation walkthrough (pseudo code)

```csharp
VirtualDesign? Evaluate(Profile profile, Target target, GameStateSnapshot s)
{
    var draft = DesignDraft.Empty;                      // clean slate, all fields unset

    // 1. Normal groups: first true wins, then stop
    var active = profile.PassGroups
        .Where(g => g.Enabled && !g.IsFinal)
        .FirstOrDefault(g => conditions.Eval(g.Conditions, s));

    if (active != null)
        foreach (var pass in Ordered(active.Passes))    // evaluation direction
            if (pass.Enabled && conditions.Eval(pass.Conditions, s))
                draft.MergeOnTop(pass.BuildFinalLayer()); // layers smashed, mask applied

    // 2. Final groups: every active one, by priority
    foreach (var g in profile.PassGroups
                 .Where(g => g.Enabled && g.IsFinal && conditions.Eval(g.Conditions, s))
                 .OrderBy(g => g.Priority))
        foreach (var pass in Ordered(g.Passes))
            if (pass.Enabled && conditions.Eval(pass.Conditions, s))
                draft.MergeOnTop(pass.BuildFinalLayer());

    return draft.Freeze(provenance: true);             // immutable VirtualDesign
}
```

---

## 6. Interface

### 6.1 Lamina window (`/lam`)
Toolbar: **Passes · Profiles · Settings · About**

#### Passes tab
```
Pass Groups (Default, Home, Duty, ...)          ← list, first true wins
 └─ Pass Group
     ├─ Group conditions (+ Add Condition mini menu)
     ├─ Final Pass toggle  → enables Priority field
     └─ Passes (vertical list)                   ← lower rows override
         └─ Pass
             ├─ Conditions (top to bottom)  (+ Add Condition mini menu)
             ├─ Layers                      (+ Add Layer mini menu)
             │    Vitrine Design | Glamourer | Penumbra Adjust | Honorific | Moodles
             ├─ Macro Override (optional): Only Equipment / Only Customization / per slot
             └─ Virtual Design preview: implicit preview of what this condition tree produces
```
- Pass groups are non layerable condition stacks where only one normal group can be true at a time, like a switch. When one is true, only the condition stacks in it are evaluated.
- **Final Pass (toggle):** if true, the group runs at the very end and gets a priority field. If final pass groups exist, the final design is built from the winning group plus all active final groups in sequence by priority.
- **Preview:** shows what the selected pass / group would produce right now, using provenance to show which pass set each field.
- When conditions match, the final layer is collected (like merging layers in Photoshop) and the final design is applied in one draw. **Never draw after a single condition passes.**

#### Profiles tab
- List of profiles. Each is a complete Lamina setup ending with its Targets.
- One active profile per target.
- First build: target = your own character, set by default.
- Clone / copy / move passes and groups between profiles.

#### Settings tab
| Setting | Default | Notes |
|---|---|---|
| Master toggle | on | Off = full Cleanup / Revert. |
| Evaluation direction | Top to bottom, bottom wins | Or bottom to top. |
| Hold Glamourer state against automation | on | See [11.2](#112-glamourer). Originally "Disable Glamourer automation"; Glamourer exposes no API to switch automation off, so Lamina locks its state instead. |
| Integrations | detected | Glamourer + Penumbra required; Honorific + Moodles optional. Live status shown. |
| Penumbra collection | "Lamina" | Assigned to targets on first setup. If the player unassigns it, that is an opt out; Lamina never re-assigns on its own. |
| Default hold / linger | e.g. 0.5 s / 1 s | Per condition override possible. |
| Redraw policy | defer when unsafe | Defer during combat, cutscenes, loading, mounted / flying, GPose. |
| Debug overlay | off | Active group, matched passes, last apply. |
| Data | | Import / export, backups. |

#### About tab
About the plugin. By ahadev.

### 6.2 Vitrine window (`/vit`)
Toolbar: **Mods · Collections · Settings · About**
- **Mods:** a large card arrangement of all your mods. Provides applicable designs directly from what a mod affects. If a mod has configurable settings, they can be adjusted through its default settings in the same place. Designs appear as cards and contain defaults; the user can set those defaults as the baseline.
- **Collections:** displays collections and who is using them right now (informational; Lamina only writes to its own collection).
- **Settings:** how cards appear, default views, where to cache mod photos and info.
- **About:** About the plugin. By ahadev.

---

## 7. Backend: runtime pipeline and services

### 7.1 Pipeline
```
StateTracker → ConditionEvaluator → PassGroupSelector → PassCompositor
      → DesignDiffer → Applier → RedrawScheduler → AppliedStateStore
```

| # | Service | Responsibility |
|---|---|---|
| 1 | **StateTracker** | Listens to Dalamud (territory change, condition flag changes, job / gearset change, login / logout) and polls slow things (Eorzea time, weather) about once a second. Builds a new `GameStateSnapshot` per target **only when something relevant changed**. First build: local player only. |
| 2 | **ConditionEvaluator** | Evaluates condition stacks against the snapshot. Owns **hold / linger** timers. Caches results per snapshot. |
| 3 | **PassGroupSelector** | Checks normal groups in list order and **stops at the first true one**. Then counts every active final group, sorted by priority. |
| 4 | **PassCompositor** | Starts from a clean slate. Walks the selected passes in the evaluation direction; each matching pass smashes its layers (through its Macro Override mask) into `Pass.FinalLayer`; final layers merge in order (unset = passthrough); final groups merge by priority. Freezes one immutable `VirtualDesign` per target, with provenance. |
| 5 | **DesignDiffer** | Compares with `AppliedState`. Identical: do nothing. Otherwise computes the delta per integration and whether a redraw is needed (only Penumbra changes need one). |
| 6 | **Applier** | Pushes the delta per target in one batch: **Penumbra** (skipped if the target opted out): remove all of Lamina's temporary settings in one call, then write the full mod state of the new design (Vitrine designs, Glamourer mod associations, Penumbra Adjust layers). **Glamourer** state (locked with Lamina's key). **Honorific** title. **Moodles**. All in the same frame, before the single redraw. |
| 7 | **RedrawScheduler** | At most **one** redraw per apply, only if mods changed. Defers while unsafe (combat, cutscene, loading, mounted / flying) and coalesces requests. |
| 8 | **AppliedStateStore** | Remembers the last design hash, Moodles Lamina added, title Lamina set. Lets the Differ skip no-op applies and lets Cleanup undo only Lamina's own changes. No mod list needed (clean slate). |

### 7.2 Support services
| Service | Responsibility |
|---|---|
| **IPC Bridges** | `GlamourerIpc` (required), `PenumbraIpc` (required), `HonorificIpc` (optional), `MoodlesIpc` (optional), `Vitrine` (internal module behind an interface). Detect availability and API version on load and whenever plugins load / unload; layer types whose plugin is missing are disabled and flagged in Settings. |
| **JsonStore** | Loads and saves everything as JSON. See [Persistence](#10-persistence-json). |
| **Cleanup / Revert** | Runs on toggle off, unload, logout, profile switch: remove Lamina's Penumbra temporary settings, unassign the Lamina collection if Lamina assigned it this session, unlock and revert Glamourer state (to automation), clear the Honorific title, remove Lamina's Moodles. Then one redraw. |
| **Commands** | `/lam` open window · `/lam on` / `off` · `/lam profile <name>` · `/lam reapply` · `/lam why` (print active group, matched passes, which pass set each field). `/vit` opens Vitrine. |

### 7.3 Event sources (Dalamud)
- `IClientState`: login / logout, territory changed, local player, class job.
- `ICondition`: `ConditionFlag` changes (mounted, in combat, swimming, diving, flying, bound by duty, occupied, cutscene, etc.).
- `IFramework.Update`: throttled polling (ET, weather, emote, movement).
- `IDataManager` (Lumina sheets): `TerritoryType`, `Weather`, `ClassJob`, `Emote`, `OnlineStatus`, `Status`, `ContentFinderCondition`, `PlaceName`.
- FFXIVClientStructs for things without a service: weather manager, gearset module, emote / mode state, Eorzea time.
- Penumbra / Glamourer events (see 11) to detect external changes.

> Exact flag and struct names must be checked against the current Dalamud / ClientStructs versions when implementing each condition.

---

## 8. Data models

Legend: **saved** = serialized to JSON, mutable classes edited by the UI. **runtime** = memory only, immutable records.
Every referenceable object has a `Guid Id`. References are always by Id, never by name.

### 8.1 Saved models

```csharp
class LaminaConfig                        // → config.json
{
    int Version;
    bool Enabled;                         // master toggle
    EvaluationDirection Direction;        // TopToBottom (bottom wins) | BottomToTop
    bool HoldGlamourerState = true;       // lock Glamourer state against automation
    string CollectionName = "Lamina";
    float DefaultHold, DefaultLinger;     // seconds
    RedrawPolicy RedrawPolicy;
    bool DebugOverlay;
}

class BiomeMap                            // → biomes.json (user overrides only)
{
    Dictionary<uint, string> TerritoryToBiome;
}

class Profile                             // → profiles/<Id>.json
{
    Guid Id; string Name; bool Enabled;
    List<PassGroup> PassGroups;           // ordered, first true normal group wins
    List<Target> Targets;                 // rule: one active profile per target
}

class Target
{
    TargetKind Kind;                      // LocalPlayer | Character | (later) OtherPlayer
    string? Name; uint? WorldId;          // when Kind = Character
}

class PassGroup
{
    Guid Id; string Name; bool Enabled;
    ConditionStack Conditions;            // its own
    bool IsFinal; int Priority;           // Priority used only when IsFinal
    List<Pass> Passes;                    // ordered
}

class Pass
{
    Guid Id; string Name; bool Enabled;
    ConditionStack Conditions;
    List<Layer> Layers;                   // ordered, smashed into FinalLayer
    ApplyMask? MacroOverride;
}

class ConditionStack { ConditionNode Root; }   // empty = true
abstract class ConditionNode { bool Negate; }
class ConditionGroup : ConditionNode { GroupOp Op; List<ConditionNode> Children; } // All | Any
abstract class Condition : ConditionNode       // polymorphic by "kind"
{
    Guid Id; bool Enabled;
    float? HoldSeconds, LingerSeconds;
}

abstract class Layer                       // polymorphic by "kind"
{
    Guid Id; bool Enabled;
}
class VitrineDesignLayer  : Layer { Guid VitrineDesignId; Dictionary<string, List<string>>? SettingsOverride; }
class GlamourerLayer      : Layer { Guid DesignId; }   // mod associations carried over as is
class PenumbraAdjustLayer : Layer { List<ModState> Mods; }
class HonorificLayer      : Layer { bool Clear; string? Title; bool IsPrefix; Vector3? Color; Vector3? Glow; }
class MoodlesLayer        : Layer { List<Guid> Add; List<Guid> Remove; }   // Moodles or presets

class ModState
{
    string ModDirectory;
    ModToggle State;                       // Enable | Disable
    int? Priority;
    Dictionary<string, List<string>>? Settings;   // option group → selected options
}

class ApplyMask                            // Macro Override
{
    EquipSlotFlags Equipment;              // per slot
    CustomizeFlags Customization;          // per field
    bool Meta, Mods;
    // presets: OnlyEquipment, OnlyCustomization
}
```

### 8.2 Condition kinds (one subclass each)
| Kind | Fields |
|---|---|
| Time | `Clock` (ET / LT / ST), `From`, `To` |
| Calendar | `DateFrom`, `DateTo`, `Weekdays[]`, `Event?` |
| Weather | `WeatherIds[]`, `Groups[]` |
| Biome | `Biomes[]` (resolved through BiomeMap) |
| Location | `TerritoryIds[]`, `RegionIds[]`, `PlaceNameIds[]`, `Sanctuary?`, `Indoors?`, `WorldIds[]` |
| Duty | `ContentTypes[]`, `ContentIds[]`, `InFate?` |
| Housing | `Where` (Any / OwnHouse / FcHouse / Apartment / Chambers / Other), `Districts[]`, `Ward?`, `Plot?` |
| Social | `PartyState` (Solo / Party / Alliance), `MinSize`, `MaxSize`, `Players[]` (name@world) |
| Character | `Races[]`, `Clans[]`, `BodyTypes[]`, `Characters[]` |
| Job | `JobIds[]`, `Roles[]`, `Disciplines[]`, `GearsetIds[]`, `LevelMin`, `LevelMax`, `WeaponDrawn?` |
| OnlineStatus | `StatusIds[]` |
| StatusEffect | `StatusIds[]`, `MinStacks?` |
| Movement | `States[]` (Idle, Walking, Running, Jumping, Mounted, Flying, Swimming, Diving...) |
| BodyState | `States[]` (Dead, InCombat, Casting, Wet, Transformed...), `HpBelowPercent?` |
| Emote | `EmoteIds[]`, `LoopingOnly`, `Poses[]` (Sitting, GroundSitting, Dozing...) |
| Activity | `Activities[]` (Crafting, Gathering, Fishing, Cutscene, GPose...) |

### 8.3 Runtime models
```csharp
record GameStateSnapshot(...);   // everything the catalog reads + timestamp, one per target
record LayerDraft(...);          // Pass.FinalLayer: same shape as VirtualDesign, all fields optional
record VirtualDesign(...);       // see section 9
class AppliedState               // last design hash, Moodles added, title set, pending redraw
```
- `GameStateSnapshot`: territory, weather, ET, job, gearset, condition flags, emote, online status, statuses, party...
- `Pass.FinalLayer`: computed, never saved.
- `AppliedState`: not saved. The collection is reset on every apply, so nothing needs recovering after a crash.

---

## 9. VirtualDesign field reference

An **immutable struct produced at the end of a pass group** evaluation (winning group + final groups). It contains everything that will be sent to Glamourer, Penumbra, Honorific and Moodles for one target. **Every field is optional: unset = passthrough.**

### Equipment
- **Slots:** Main Hand, Off Hand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Right Ring, Left Ring.
- **Bonus slot:** Facewear (glasses).
- **Per slot:** Item, Dye 1, Dye 2, apply flag for the item and for each dye.
- **FC crests:** Head, Body, Off Hand (on / off).

### Customization
Race, Clan, Body type, Model (non human models), Height, Face, Hairstyle, Highlights on / off, Skin color, Hair color, Highlights color, Eye color (right), Eye color (left / heterochromia), Small irises, Eye shape, Eyebrows, Nose, Jaw, Mouth, Lip color + lipstick, Facial features (7 toggles + legacy tattoo), Feature / tattoo color, Face paint, Face paint reversed, Face paint color, Muscle tone or ear size (race dependent), Tail / ear shape, Bust size. Each field has its own apply flag.

### Advanced (Glamourer)
- **Advanced customizations:** free RGB for skin, hair, highlights, eyes, lips, face paint, feature color, plus muscle slider.
- **Advanced dyes:** per slot material color table rows (diffuse, specular, emissive, gloss).

### Meta toggles
Hat visible · Visor toggled · Weapon visible · Viera ears visible · Wetness.

### Mods (Penumbra)
- Target: the **Lamina** collection only. Cleared before every apply; untouched mods stay at default.
- Per mod: mod directory, enabled / disabled, priority, option settings (group → selected options).
- **Sources:** Vitrine designs, Glamourer design mod associations (carried over as is), Penumbra Adjust layers.

### Honorific
Title text, prefix or suffix, color, glow color, or explicit Clear.

### Moodles
Set of Moodles to apply (presets expanded), set of Moodles to remove.

### Metadata
- **Provenance:** which group / pass / layer set each field (powers Preview and `/lam why`).
- Content hash (for DesignDiffer).
- `RequiresRedraw` (computed).
- ProfileId, BuiltAt.

### Future layers (not committed)
- Customize+ profile (body scaling).
- SimpleHeels offset (should follow shoe changes).

> The Glamourer field list was written from Glamourer's feature set; confirm exact fields against `GetDesignJObject` / `GetState` output before building the struct.

---

## 10. Persistence (JSON)

### 10.1 File layout
```
pluginConfigs/Lamina/
  config.json            LaminaConfig
  biomes.json            BiomeMap overrides
  profiles/
    <profile-id>.json    one Profile, everything nested
  backups/
    <timestamp>/         rotating copies
  vitrine/
    mods.json            ModInformation cache
    photos/              mod preview cache
```

### 10.2 JsonStore
- **Load** on plugin start: read, validate, migrate, then hand the models to the UI and pipeline.
- **Save** debounced, about 1 s after the last edit.
- **Atomic writes:** write to a temp file, then rename. A crash never leaves half a file.
- **One file per profile:** a broken file only breaks that profile.
- Keeps the last N backups.

### 10.3 Format rules
- Every file has a `"version"` field; migrations run step by step (v1 → v2 → v3).
- `Condition` and `Layer` are polymorphic: saved with a `"kind"` field and read back through a converter (Newtonsoft ships with Dalamud; System.Text.Json with `JsonDerivedType` also works; pick one and stay with it).
- Enums are saved as **strings**, so reordering an enum never corrupts files.
- Every referenceable object has a Guid `"id"`.
- **References by Id, never by name:** Glamourer design Guid, Vitrine design Id, Moodle Guid, mod directory. A missing reference shows a broken link warning and that layer is skipped.
- Unknown kinds (from a newer version) are kept, not deleted, and shown as unsupported.

### 10.4 Import / export
- Export a profile, pass group or single pass as a JSON file or a base64 clipboard string.
- On import: fresh Ids, targets reset to the local player, missing references flagged.

### 10.5 Saved vs not saved
| Saved | Never saved |
|---|---|
| LaminaConfig, Profiles (everything inside), BiomeMap overrides, Vitrine cache | GameStateSnapshot, Pass.FinalLayer, VirtualDesign, AppliedState |

### 10.6 Example: `profiles/<id>.json`
```json
{
  "version": 1,
  "id": "6f1c…",
  "name": "Main",
  "enabled": true,
  "targets": [ { "kind": "LocalPlayer" } ],
  "passGroups": [ {
    "id": "…", "name": "Home",
    "isFinal": false, "priority": 0,
    "conditions": { "op": "All", "children": [
      { "kind": "Housing", "where": "OwnHouse" } ] },
    "passes": [ {
      "id": "…", "name": "Rainy night",
      "conditions": { "op": "All", "children": [
        { "kind": "Weather", "groups": ["Wet"] },
        { "kind": "Time", "clock": "ET", "from": "18:00", "to": "06:00" } ] },
      "layers": [
        { "kind": "Glamourer", "designId": "…" },
        { "kind": "Honorific", "title": "Rain Walker" }
      ]
    } ]
  } ]
}
```

---

## 11. Integrations (verified against bundled API sources)

Checked against `Penumbra.Api/` and `Glamourer.Api/` in this repo on 2026-10-14.

### 11.1 Penumbra
| Need | API | Notes |
|---|---|---|
| Find the Lamina collection | `GetCollections()` / `GetCollectionsByIdentifier(name)` | No API to create a permanent collection was found, so first run asks the user to create (or pick) the "Lamina" collection in Penumbra. |
| Assign to target (first setup) | `SetCollectionForObject(objectIndex, collectionId, allowCreateNew, ...)` | Only on first setup. |
| Detect opt out | `GetCollectionForObject(objectIndex)` | If the effective / individual collection is not Lamina, skip mods for that target. |
| Write mod state | `SetTemporaryModSettings(collectionId, modDirectory, modName, inherit, enabled, priority, options, source, key)` | `source` = "Lamina". `options` = group name → enabled options. |
| **Clean slate in one call** | `RemoveAllTemporaryModSettings(collectionId, key)` | ✅ Verified. Use a **negative key**: an identification lock that lets the user still edit, but lets Lamina remove only its own settings. |
| Redraw | `RedrawObject(objectIndex, RedrawType)` | One per apply, scheduled. `GameObjectRedrawn` event available. |
| Mod catalog (Vitrine) | `GetModList()`, `GetChangedItems(modDirectory, modName)`, `ModAdded` / `ModDeleted` / `ModMoved` events | Affected items per mod for Vitrine classification. |

### 11.2 Glamourer
| Need | API | Notes |
|---|---|---|
| List designs (layer picker) | `GetDesignList()` / `GetDesignListExtended()` | |
| Read a design to merge it | `GetDesignJObject(designId)` | Layering = read each GlamourerLayer's JObject, merge per field, apply once. Also the source of the design's **mod associations**. |
| Apply the merged result | `ApplyState(state, objectIndex, key, flags)` | Flags: `Equipment`, `Customization`, `Lock`. Apply **once per change**. |
| Read current state | `GetState(objectIndex, key)` | For preview / baseline. |
| Undo on cleanup | `UnlockState(objectIndex, key)` then `RevertToAutomation(objectIndex, key, flags)` | Gives the character back to whatever Glamourer automation would show. |
| Detect automation interfering | `StateFinalized` event (`ReapplyAutomation`, `RevertAutomation` types) | Re-assert Lamina's state if automation reapplies over it. |

> ⚠️ **Finding: Glamourer has no IPC to turn automation off.** The design decision "a default on setting kills Glamourer automation and keeps it off" cannot be implemented as written. Replacement: apply with `ApplyFlag.Lock` and Lamina's key so the state is held, listen to `StateFinalized` for automation reapplies and re-assert, and on cleanup unlock + `RevertToAutomation`. Whether a lock fully blocks automation (zone change, job change, gearset change) must be tested in game. Consequence: `GlamourerAutomationWasOn` is no longer needed in the config.

> ⚠️ To verify: whether `ApplyState` with a merged state applies the design's mod associations itself. Lamina applies associations through Penumbra on its own, so Glamourer should **not** also apply them (double application).

### 11.3 Honorific (later)
Set / clear title through its IPC (`Honorific/IpcProvider.cs`).

### 11.4 Moodles (later)
Apply / remove statuses and presets through its IPC (`Moodles/.../IPCProcessor.cs`). Track what Lamina added so Cleanup removes only those.

---

## 12. Vitrine

A system inside Lamina (may become its own plugin later) to view all Penumbra mods neatly and control them, using a reading layer similar to SQL views. It doesn't change how Penumbra itself is organized.

### 12.1 Backend
**READ**
- All mods with their settings and statuses, **excluding orphans** → feeds the **Reading Layer (SQL like)** → `ModInformation` structs.
- Collections and their assignments. Mostly informational: Lamina works with its own dedicated collection, but Vitrine still shows which collections are active and who is using them.

**WRITE**
- Writes **only** to the "Lamina" collection.
- Enable / Disable and Mod Settings, as **temporary settings** (shares Lamina's key, so the clean slate removes them too).

### 12.2 `ModInformation` (struct)
**Front box**
- Name, Author, Link, Description, Type
- Affected Items (struct): Category | item id or name
- Tags, Version

**Collections**
- Enabled / disabled
- Settings (option group → options)
- Priority
- `CurrentActiveCollection`: which collection currently activates this mod (temporarily or not)

### 12.3 Vitrine designs
- Auto created from mods that change in game things, grouped by what each mod affects.
- A Vitrine design is usually a full mod with its basal settings. Users can set defaults as the baseline and create variants / presets with toggles on or off.
- Used in Lamina through the **Vitrine Design** layer, referenced by Id.

---

## 13. Condition catalog

All FFXIV states Lamina can react to. First build subset is in section 14.

### World
- **Time:** Eorzea Time (ET), Local Time (LT), Server Time (ST, UTC); day phase (Dawn, Day, Dusk, Night); Eorzea calendar moon / sun.
- **Calendar:** real date range, weekday, seasonal events (Heavensturn, Valentione's Day, Little Ladies' Day, Hatching-tide, Moonfire Faire, Make It Rain, the Rising, All Saints' Wake, Starlight Celebration), custom dates.
- **Weather:** Clear Skies, Fair Skies, Clouds, Fog, Wind, Gales, Rain, Showers, Thunder, Thunderstorms, Dust Storms, Sandstorms, Hot Spells, Heat Waves, Snow, Blizzards, Gloom, Umbral Wind, Umbral Static, Moon Dust, Astromagnetic Storm, other zone specific weather. Groups: Clear, Overcast, Wet, Stormy, Windy, Hot, Cold, Arcane. Upcoming weather (forecast is deterministic).
- **Biome (Lamina defined):** user editable Territory → Biome map. Desert, Snow / Tundra, Forest, Jungle / Tropical, Coastal / Island, Highland / Mountain, Underwater, Urban, Lunar / Space, Interior, Otherworldly. Ships with defaults.

### Place
- **Location:** territory / zone, sub area, region, city state, sanctuary (rested area), indoors / outdoors, inn room, world / data center, home vs visiting.
- **Content / Duty:** in any duty; dungeon, trial, raid (normal / savage / ultimate), alliance raid, guildhest, quest battle, treasure dungeon; deep dungeons; field operations (Eureka, Bozja / Zadnor, Occult Crescent); variant / criterion; PvP (Crystalline Conflict, Frontline, Rival Wings); in a FATE; specific duty; Island Sanctuary, Diadem, Cosmic Exploration, Ocean Fishing; Gold Saucer, Masked Carnivale.
- **Housing:** any ward (Mist, Lavender Beds, Goblet, Shirogane, Empyreum), inside a house (yours / FC / someone else's), apartment, private chambers, specific ward + plot, own property only.
- **Social:** solo / party / alliance, party size, cross world party, specific player nearby or in party, targeted by another player, in duty queue.

### Character
- **Character:** name@world, race, clan, body type, appearance differs from base.
- **Job and gear:** job / class, role, discipline group (DoW, DoM, DoH, DoL), gearset, level range, item equipped in slot, glamour plate, weapon drawn / sheathed.
- **Online status:** Online, Busy, Away from Keyboard, **Role-playing**, Looking for Party, Looking to Meld Materia, New Adventurer, Returner, Mentor (Battle / Trade / PvP), Camera Mode, Viewing Cutscene.
- **Status effects:** by Status ID (Well Fed, Medicated, Sprint, Weakness, Brink of Death, Transcendent, Hidden, job buffs and stances), stacks, remaining time.

### State
- **Movement:** idle, walking, running, sprinting, jumping, falling, mounted, riding pillion, flying, swimming, diving, moved by an event.
- **Body state:** dead / KO, low HP threshold, in combat, casting, wet, weapon drawn, transformed, disguised, loading / between areas.
- **Emote and pose:** any emote, specific emote, looping vs one shot, sitting, ground sitting, dozing, idle pose (/cpose), fashion accessory out, holding a prop.
- **Activity:** crafting, gathering, fishing, performing, NPC dialogue, cutscene, GPose, trading, mini games (Triple Triad, Mahjong, Chocobo Racing, Lord of Verminion), retainer bell / market board, chocobo companion, minion summoned.

---

## 14. First build scope and build order

### First build
- **Core:** profiles (local player target), pass groups + final pass groups, passes, condition stacks, layers, `Pass.FinalLayer`, `VirtualDesign`, clean slate reset, single redraw, Cleanup / Revert, JSON saving, `/lam` commands.
- **Conditions:** Time (ET / LT / ST), Weather, Location (zone, region, city, inn), Housing, Job / Role / Gearset, Online Status (Role-playing), simple states: Mounted, Flying, Swimming, Diving, In Combat, Dead, Sitting.
- **Layers:** Glamourer, Penumbra Adjust.
- **Settings:** evaluation direction, hold Glamourer state, collection name, hold / linger.

### Later
- Vitrine (system + Vitrine Design layer).
- Honorific and Moodles layers.
- Biome, Calendar, Social, Status effects, full Emote and Activity sets.
- Debug overlay, import / export.
- Other targets (characters / players).

### Build order
1. **Spike plugin** (throwaway): find the Lamina collection, write temp settings with a negative key, `RemoveAllTemporaryModSettings`, one redraw. Apply a merged Glamourer state with `Lock`, then change zone / job / gearset and watch whether automation breaks through (`StateFinalized`).
2. **Models + JsonStore** (config, profile with groups / passes / conditions / layers, polymorphic "kind" converter, atomic save).
3. **StateTracker + GameStateSnapshot** for the first build condition set.
4. **ConditionEvaluator** with hold / linger.
5. **PassGroupSelector + PassCompositor** (unit testable without the game: snapshot in, VirtualDesign out).
6. **DesignDiffer + Applier + RedrawScheduler + AppliedStateStore**.
7. **Cleanup / Revert** and master toggle.
8. **UI:** Passes tab (groups, passes, mini menus, preview), Profiles, Settings, About.
9. Test in game, adjust, repeat (always v1).

### Testing notes
- Steps 2, 4 and 5 are pure logic: test them with hand built snapshots (e.g. "own house + rain + night" → expected VirtualDesign).
- In game checklist: no redraw when nothing changed; one redraw per real change; jumping / casting don't flicker; zone change while mounted defers the redraw; toggle off restores the character; unassigning the Lamina collection stops mod changes.

---

## 15. Risks and design notes

- **Redraw cost:** Glamourer changes apply live, but Penumbra changes need a redraw. Redrawing mid combat, mounted, swimming or in a cutscene is jarring. The RedrawScheduler defers until safe.
- **Flicker from transient states:** jumping, casting, sprinting, combat toggle often. Hold / linger exist for this.
- **Clear only on change:** the clean slate is logical before every evaluation, but the physical clear only happens when the Differ finds a real change. Otherwise Penumbra would reload files every tick.
- **Stable vs transient conditions:** location, job, gearset, time, weather are stable; movement, emote, casting are transient. The UI could later split them into "Context" and "Moment" conditions.
- **Other players:** most of the catalog only exists for the local player, so other targets can only ever use a reduced condition set.
- **Glamourer automation:** see the finding in 11.2. If locking does not hold, the fallback is a clear warning in Settings telling the player to disable automation for the character in Glamourer.
- **Opt out scope:** unassigning the Lamina collection only stops mods. The master toggle stops everything.

---

## 16. Open items
1. **No group active:** nothing applies (passthrough)? Proposed yes.
2. **Honorific and Moodles merging:** replace or add up? Proposed: Honorific replaces, Moodles add with explicit removes.
3. **Lamina turned off:** restore Glamourer automation? Proposed yes, now concretely `UnlockState` + `RevertToAutomation` (see 11.2).

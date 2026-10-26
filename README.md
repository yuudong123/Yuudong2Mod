# Yuudong2Mod

A personal all-in-one RimWorld 1.7 mod. It replaces about 40 separate quality-of-life and balance mods with a single mod. Each feature can be switched on and off on its own. Many of the original mods were no longer maintained or did not work on RimWorld 1.7, which runs on .NET 10 (Waylith).

Player-facing documentation (Korean): [`Yuudong2Mod/README.md`](Yuudong2Mod/README.md)

This project was built with an AI coding agent (Claude Code) working under my own agent harness toolkit. I chose the features, set the constraints and did the in-game testing. The agent ported, rewrote, verified and debugged the code.

> **What is in this repository.** Only my own code is published. Code and assets copied as-is from other authors' mods are excluded via [`.gitignore`](.gitignore) and are not redistributed:
>
> - Better Workbench Management source
> - DragonianMix source and content
> - Random's Gene Assistant source
> - Gloomy Furniture Fix and MoreGloomyWalls defs and textures
> - the meat whitelist
> - all textures
>
> Because of that, the published source does not build on its own. The original mods and their authors are credited in the [player documentation](Yuudong2Mod/README.md#포팅한-모드).

## What it does

Features are grouped in the settings screen; each one is a toggle.

| Group | Examples |
| --- | --- |
| Colony | In-place material replacement without deconstructing (Replace Stuff rewrite). Wall swap that keeps rooms and roofs intact. Power-on-demand workbenches and TVs. Shelves with no slowdown that accept everything a stockpile does. Custom map sizes. Meat consolidation. Room-aware food choice. Realistic room sizes. |
| Interface | Message placement below the colonist bar. Health tab sorting. Damage numbers. Letter stack cleaner. Smart Speed (speed multipliers, ultrafast button). Drag-select for work priorities and trade counts. TPS display. Techprint ownership highlight in trade. |
| Pawn | Hair under hats with per-headgear rules. Hidden bionic eyes. Durable clothes. Uncapped skill levels (EndlessGrowth). An RPG-style level-up system with six attributes and tooltips generated from the actual stat effects. |
| Ideology | Specialists can work. Roles untied to memes. No ideology limits. Auto-cast specialist commands. |
| Trade | A caravan arrival spot and a supply drop spot. A map-wide trade beacon. Trade stock boost. Faction insult that triggers a raid. Empire goodwill fix. |
| Compatibility | Fixes for EPOE AI cores, Dragonian genes and healing, Gloomy furniture recipes and work givers, and a thread-safe draw-scale cache for NewRatkin. |

## Engineering highlights

- **Feature isolation.**
  - Every feature is a Harmony patch category. C# def edits run only for applied features.
  - XML patches go through a custom `PatchOperationFeature` that applies an *enabled* or *disabled* operation.
  - Each feature is applied inside its own error boundary. If a game or mod update breaks one transpiler, only that feature is skipped and the error is logged.
- **Rewrites, not wrappers.** Ports are adapted to the 1.7 API rather than just recompiled. A few examples:
  - **Replace Stuff:** only the material-swap part was rewritten. It uses generated frame defs and a weak-reference table instead of patching every reservation method.
  - **RPG level-up:** the stat effects were moved from XML patches to one C# table, so tooltips always match the real effects. Save field names are kept, so existing saves carry over.
  - **Power on demand:** a "recently used" window copes with RimWorld 1.6+ tick intervals.
  - **Better Workbench "count away pawns":** rewritten on top of a registry of pawns that left their home map.
- **Data-driven compatibility.** Hooks into other mods are applied only when that mod is active, found by type name or package id. Nothing hard-depends on them.
- **Code review pass.** A full review by the agent found and fixed:
  - a static `Bill` list that kept old games' maps alive after loading another save
  - one broken patch disabling every later feature
  - a right-click that leaked through to the map
  - numeric settings fields that could not accept decimals

## How it was built

1. **Specify.** I name a mod to port or a behavior to change.
2. **Port or rewrite.** The agent reads the original mod's source and the decompiled 1.7 game code, then ports or rewrites the feature for 1.7.
3. **Verify.** It builds the mod and copies it into my local game folder.
4. **Play-test.** I test in the game and report problems with screenshots or logs. The agent traces the cause from the game log and decompiled code before changing anything, and reports what it could not verify.

## Repository layout

```text
Yuudong2Mod/
  About/, Defs/, Patches/, Languages/   Mod content (own files only)
  Source/Yuudong2Mod/                   C# source, one file per feature area
    Yuudong2Mod.cs                      Startup: per-feature patching with error isolation
    Yuudong2Settings.cs                 Feature list, groups and settings UI
    ReplaceStuff.cs, WallSwap.cs, PowerOnDemand.cs, InterfaceTweaks.cs, Rpg/, ...
```

## Status

- Personal use only.
- Not published on the Steam Workshop.
- No license is granted for my code; all rights reserved.
- Third-party mods belong to their authors.

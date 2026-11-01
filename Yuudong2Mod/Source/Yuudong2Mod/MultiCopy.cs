using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 지을 수 있는 건물·청사진·건설 중인 틀을 여러 개 고르면 같은 재료·양식으로 배치 모양 그대로 한 번에 복제 청사진을 놓는다.
    public static class MultiCopyUtility
    {
        public struct CopySource
        {
            public ThingDef def;
            public ThingDef stuff;
            public Precept_Building precept;
            public ThingStyleDef style;
            public bool styleOverridden;
            public ColorInt? glowerColor;
        }

        private static Designator_MultiCopy designator;
        private static int countedFrame = -1;
        private static int selectedCount;

        public static Designator_MultiCopy Designator => designator ??= new Designator_MultiCopy();

        // 바닐라 복제 버튼이 넘기는 값과 같다. 아직 연구하지 않았거나 지을 수 없게 막힌 건물은 빠진다.
        public static bool TryGetSource(Thing thing, out CopySource source)
        {
            switch (thing)
            {
                case Blueprint_Build blueprint:
                    source = new CopySource
                    {
                        def = blueprint.def.entityDefToBuild as ThingDef,
                        stuff = blueprint.stuffToUse,
                        precept = blueprint.StyleSourcePrecept as Precept_Building,
                        style = blueprint.selectedStyleDef ?? blueprint.StyleDef,
                        styleOverridden = blueprint.styleOverridden,
                        glowerColor = blueprint.glowerColorOverride,
                    };
                    break;
                case Blueprint_Install install when install.ThingToInstall != null:
                    Thing inner = install.ThingToInstall;
                    source = new CopySource
                    {
                        def = inner.def,
                        stuff = inner.Stuff,
                        precept = inner.StyleSourcePrecept as Precept_Building,
                        style = install.StyleDef,
                        styleOverridden = true,
                    };
                    break;
                case Frame frame:
                    source = new CopySource
                    {
                        def = frame.def.entityDefToBuild as ThingDef,
                        stuff = frame.Stuff,
                        precept = frame.StyleSourcePrecept as Precept_Building,
                        style = frame.StyleDef,
                        styleOverridden = true,
                        glowerColor = frame.glowerColorOverride,
                    };
                    break;
                case Building building:
                    CompGlower glower = building.GetComp<CompGlower>();
                    source = new CopySource
                    {
                        def = building.def,
                        stuff = building.Stuff,
                        precept = building.StyleSourcePrecept as Precept_Building,
                        style = building.StyleDef,
                        styleOverridden = true,
                        glowerColor = glower != null && glower.HasGlowColorOverride ? glower.GlowColor : (ColorInt?)null,
                    };
                    break;
                default:
                    source = default;
                    return false;
            }

            ThingDef def = source.def;
            if (def?.building == null || def.building.neverBuildable || BuildCopyCommandUtility.FindAllowedDesignator(def) == null)
            {
                return false;
            }

            source.stuff = def.MadeFromStuff ? source.stuff ?? GenStuff.DefaultStuffFor(def) : null;
            return true;
        }

        public static bool CanCopy(Thing thing)
        {
            return thing.Spawned && TryGetSource(thing, out _);
        }

        public static List<Thing> SelectedCopyable()
        {
            return Find.Selector.SelectedObjectsListForReading.OfType<Thing>().Where(CanCopy).ToList();
        }

        // 고른 물건마다 버튼을 물어 오므로 한 프레임에 한 번만 센다.
        public static int SelectedCopyableCount()
        {
            if (countedFrame != Time.frameCount)
            {
                countedFrame = Time.frameCount;
                selectedCount = SelectedCopyable().Count;
            }

            return selectedCount;
        }

        // Designator_Build.ThingStyleDefNonPreceptSource와 같다.
        public static ThingStyleDef StyleFor(CopySource source)
        {
            return Find.IdeoManager.classicMode && source.styleOverridden ? source.style : Faction.OfPlayer.ideos?.PrimaryIdeo?.GetStyleFor(source.def);
        }
    }

    public class Designator_MultiCopy : Designator_MultiPlace
    {
        private readonly Dictionary<Thing, MultiCopyUtility.CopySource> sources = new Dictionary<Thing, MultiCopyUtility.CopySource>();

        public Designator_MultiCopy()
        {
            icon = TexButton.Copy;
            iconProportions = new Vector2(1f, 1f);
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
            Order = 11f;
            defaultLabel = "Yuudong2.MultiCopy.Label".Translate();
            defaultDesc = "Yuudong2.MultiCopy.Desc".Translate();
        }

        protected override List<Thing> SelectedThings()
        {
            sources.Clear();
            foreach (Thing thing in Find.Selector.SelectedObjectsListForReading.OfType<Thing>())
            {
                if (thing.Spawned && MultiCopyUtility.TryGetSource(thing, out MultiCopyUtility.CopySource source))
                {
                    sources[thing] = source;
                }
            }

            return sources.Keys.ToList();
        }

        protected override bool Repeatable => true;

        protected override ThingDef PlacedDef(Thing thing)
        {
            return sources[thing].def;
        }

        protected override AcceptanceReport CanPlace(Placement placement, List<Placement> all)
        {
            AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(placement.def, placement.center, placement.rot, Map, DebugSettings.godMode,
                stuffDef: sources[placement.thing].stuff);
            return report.Accepted ? InteractionCellsFreeInGroup(placement, all) : report;
        }

        // 청사진과 틀은 다 지었을 때의 모습으로 그린다.
        protected override void DrawGhost(Placement placement, Color color)
        {
            if (placement.thing.def == placement.def)
            {
                base.DrawGhost(placement, color);
                return;
            }

            GhostDrawer.DrawGhostThing(placement.center, placement.rot, placement.def, placement.def.graphic, color, AltitudeLayer.Blueprint,
                stuff: sources[placement.thing].stuff);
        }

        // Designator_Build.DesignateSingleCell과 같다.
        protected override void Place(List<Placement> placements)
        {
            foreach (Placement placement in placements)
            {
                MultiCopyUtility.CopySource source = sources[placement.thing];
                ThingDef def = placement.def;
                ThingDef stuff = source.stuff;

                foreach (Thing thing in placement.center.GetThingList(Map).ToList())
                {
                    if (thing is Frame frame && def.blueprintDef.replaceTags.NotNullAndContainsAnyElement(frame.def.replaceTags))
                    {
                        frame.Destroy(DestroyMode.Cancel);
                    }
                }

                if (DebugSettings.godMode || def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f)
                {
                    Thing thing = ThingMaker.MakeThing(def, stuff);
                    thing.SetFactionDirect(Faction.OfPlayer);
                    Thing spawned = GenSpawn.Spawn(thing, placement.center, Map, placement.rot);
                    if (source.precept != null && spawned is Building building)
                    {
                        building.StyleSourcePrecept = source.precept;
                    }
                    else if (source.precept == null)
                    {
                        spawned.StyleDef = MultiCopyUtility.StyleFor(source);
                    }

                    CompGlower spawnedGlower = spawned.TryGetComp<CompGlower>();
                    if (source.glowerColor.HasValue && spawnedGlower != null)
                    {
                        spawnedGlower.GlowColor = source.glowerColor.Value;
                    }
                }
                else
                {
                    GenSpawn.WipeExistingThings(placement.center, placement.rot, def.blueprintDef, Map, DestroyMode.Deconstruct);
                    Blueprint_Build blueprint = GenConstruct.PlaceBlueprintForBuild(def, placement.center, Map, placement.rot, Faction.OfPlayer, stuff);
                    blueprint.StyleSourcePrecept = source.precept;
                    if (source.precept == null)
                    {
                        blueprint.StyleDef = MultiCopyUtility.StyleFor(source);
                    }

                    blueprint.glowerColorOverride = source.glowerColor;
                }

                FleckMaker.ThrowMetaPuffs(placement.rect, Map);
                if (def.PlaceWorkers != null)
                {
                    foreach (PlaceWorker placeWorker in def.PlaceWorkers)
                    {
                        placeWorker.PostPlace(Map, def, placement.center, placement.rot);
                    }
                }
            }
        }
    }

    // 건물·틀·청사진 모두 이 메서드를 거친다.
    [HarmonyPatchCategory(Features.MultiCopy)]
    [HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetGizmos))]
    internal static class ThingWithComps_GetGizmos_MultiCopy_Patch
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, ThingWithComps __instance)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            if (Find.Selector.NumSelected >= 2 && !(__instance is Pawn) && MultiCopyUtility.CanCopy(__instance) && MultiCopyUtility.SelectedCopyableCount() >= 2)
            {
                yield return MultiCopyUtility.Designator;
            }
        }
    }
}

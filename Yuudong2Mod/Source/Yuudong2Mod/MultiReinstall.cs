using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 옮길 수 있는 건물을 여러 개 고르면 배치 모양 그대로 한 번에 옮긴다.
    // 새 자리가 같이 옮기는 다른 건물의 지금 자리와 겹쳐도 된다. 그 건물은 막는 순간 철거 대신 떼어 내 바닥에 두고, 나중에 자기 자리로 옮긴다.
    public static class MultiReinstallUtility
    {
        private static Designator_MultiReinstall designator;

        // 옮기는 묶음의 배치 검사 중에만 채운다. 이 건물들은 곧 비켜날 것으로 보고 막는 물건에서 뺀다.
        internal static HashSet<Thing> ignoredForPlacement;

        public static bool CanReinstall(Building building)
        {
            return building.Spawned && building.def.Minifiable && (building.Faction == Faction.OfPlayer || building.def.building.alwaysUninstallable);
        }

        public static List<Building> SelectedReinstallable()
        {
            return Find.Selector.SelectedObjectsListForReading.OfType<Building>().Where(CanReinstall).ToList();
        }

        public static Designator_MultiReinstall Designator => designator ??= new Designator_MultiReinstall();

        // 옮기기 청사진을 막는 건물이 자기도 옮겨 갈 건물이면 철거하지 않고 떼어 낸다.
        public static Job UninstallBlockerJob(Building blocker, Pawn worker, bool forced)
        {
            DesignationManager designations = blocker.Map.designationManager;
            if (designations.DesignationOn(blocker, DesignationDefOf.Uninstall) == null)
            {
                designations.AddDesignation(new Designation(blocker, DesignationDefOf.Uninstall));
            }

            if (worker.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || (worker.workSettings != null && !worker.workSettings.WorkIsActive(WorkTypeDefOf.Construction)))
            {
                return null;
            }

            if (!forced && blocker.IsForbidden(worker))
            {
                return null;
            }

            if (!worker.CanReserveAndReach(blocker, PathEndMode.Touch, worker.NormalMaxDanger(), 1, -1, null, forced))
            {
                return null;
            }

            return JobMaker.MakeJob(JobDefOf.Uninstall, blocker);
        }

        // GenConstruct.InteractionCellStandable과 같되, 비켜날 건물은 막지 않는 것으로 본다.
        internal static AcceptanceReport InteractionCellStandable(ThingDef thingDef, IntVec3 center, Rot4 rot, Map map, Thing thingToIgnore)
        {
            foreach (IntVec3 cell in ThingUtility.InteractionCellsWhenAt(thingDef, center, rot, map).ToList())
            {
                if (!cell.InBounds(map))
                {
                    return new AcceptanceReport("InteractionSpotOutOfBounds".Translate());
                }

                foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing == thingToIgnore || ignoredForPlacement.Contains(thing))
                    {
                        continue;
                    }

                    if (thing.def.passability != Traversability.Standable || thing.def == thingDef)
                    {
                        return new AcceptanceReport("InteractionSpotBlocked".Translate(thing.LabelNoCount, thing).CapitalizeFirst());
                    }

                    BuildableDef toBuild = thing.def.entityDefToBuild;
                    if (toBuild != null && (toBuild.passability != Traversability.Standable || toBuild == thingDef))
                    {
                        return new AcceptanceReport("InteractionSpotWillBeBlocked".Translate(thing.LabelNoCount, thing).CapitalizeFirst());
                    }
                }
            }

            return true;
        }

        // GenConstruct.NotBlockingAnyInteractionCells와 같되, 비켜날 건물의 지금 작업 칸은 따지지 않는다.
        internal static AcceptanceReport NotBlockingAnyInteractionCells(ThingDef def, IntVec3 center, Rot4 rot, Map map)
        {
            CellRect rect = GenAdj.OccupiedRect(center, rot, def.Size);
            foreach (IntVec3 cell in GenAdj.CellsAdjacentCardinal(center, rot, def.Size))
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }

                foreach (Thing thing in cell.GetThingList(map))
                {
                    if (ignoredForPlacement.Contains(thing))
                    {
                        continue;
                    }

                    ThingDef other = thing is Blueprint || thing is Frame ? thing.def.entityDefToBuild as ThingDef : thing.def;
                    if (other == null || !other.HasSingleOrMultipleInteractionCells || (def.passability == Traversability.Standable && def != other))
                    {
                        continue;
                    }

                    if (ThingUtility.InteractionCellsWhenAt(other, thing.Position, thing.Rotation, thing.Map).Any(rect.Contains))
                    {
                        return new AcceptanceReport("WouldBlockInteractionSpot".Translate(def.label, other.label).CapitalizeFirst());
                    }
                }
            }

            return true;
        }
    }

    public class Designator_MultiReinstall : Designator_MultiPlace
    {
        public Designator_MultiReinstall()
        {
            icon = TexCommand.Install;
            iconProportions = new Vector2(1f, 1f);
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
            Order = -10f;
            defaultLabel = "Yuudong2.MultiReinstall.Label".Translate();
            defaultDesc = "Yuudong2.MultiReinstall.Desc".Translate();
        }

        protected override List<Thing> SelectedThings()
        {
            return MultiReinstallUtility.SelectedReinstallable().Cast<Thing>().ToList();
        }

        protected override void Started(List<Thing> group)
        {
            foreach (Thing building in group)
            {
                InstallBlueprintUtility.CancelBlueprintsFor((Building)building);
            }
        }

        protected override AcceptanceReport CanPlaceGroup(List<Placement> placements)
        {
            return placements.Any(p => p.Moves) ? true : new AcceptanceReport("IdenticalThingExists".Translate());
        }

        protected override AcceptanceReport CanPlace(Placement placement, List<Placement> all)
        {
            if (!placement.Moves)
            {
                return true;
            }

            Thing building = placement.thing;
            ThingDef def = placement.def;
            Map map = building.Map;
            var ignored = new HashSet<Thing>(all.Where(p => p.Moves).Select(p => p.thing));
            MultiReinstallUtility.ignoredForPlacement = ignored;
            try
            {
                AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt_NewTemp(def, placement.center, placement.rot, map, godMode: false,
                    thingToIgnore: building, thing: building, ignoreInteractionSpots: true, skipBlockingThing: ignored.Contains);
                if (!report.Accepted)
                {
                    return report;
                }

                report = MultiReinstallUtility.NotBlockingAnyInteractionCells(def, placement.center, placement.rot, map);
                if (!report.Accepted)
                {
                    return report;
                }
            }
            finally
            {
                MultiReinstallUtility.ignoredForPlacement = null;
            }

            return InteractionCellsFreeInGroup(placement, all);
        }

        protected override void Place(List<Placement> placements)
        {
            foreach (Placement placement in placements)
            {
                InstallBlueprintUtility.CancelBlueprintsFor((Building)placement.thing);
            }

            // 자리도 방향도 그대로인 건물은 옮기지 않는다.
            foreach (Placement placement in placements.Where(p => p.Moves))
            {
                GenSpawn.WipeExistingThings(placement.center, placement.rot, placement.def.installBlueprintDef, Map, DestroyMode.Deconstruct);
                GenConstruct.PlaceBlueprintForReinstall((Building)placement.thing, placement.center, Map, placement.rot, Faction.OfPlayer);
                FleckMaker.ThrowMetaPuffs(placement.rect, Map);
            }
        }
    }

    [HarmonyPatchCategory(Features.MultiReinstall)]
    [HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
    internal static class Building_GetGizmos_MultiReinstall_Patch
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building __instance)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            if (Find.Selector.NumSelected >= 2 && MultiReinstallUtility.CanReinstall(__instance) && MultiReinstallUtility.SelectedReinstallable().Count >= 2)
            {
                yield return MultiReinstallUtility.Designator;
            }
        }
    }

    // 배치 검사 중 바닐라 안에서 부르는 작업 칸 검사도 비켜날 건물을 막는 것으로 보지 않게 한다.
    [HarmonyPatchCategory(Features.MultiReinstall)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.InteractionCellStandable))]
    internal static class GenConstruct_InteractionCellStandable_MultiReinstall_Patch
    {
        private static bool Prefix(ThingDef thingDef, IntVec3 center, Rot4 rot, Map map, Thing thingToIgnore, ref AcceptanceReport __result)
        {
            if (MultiReinstallUtility.ignoredForPlacement == null)
            {
                return true;
            }

            __result = MultiReinstallUtility.InteractionCellStandable(thingDef, center, rot, map, thingToIgnore);
            return false;
        }
    }

    // 바닐라는 청사진 자리를 막는 건물을 철거한다. 그 건물에도 옮기기 청사진이 있으면 떼어 내기만 한다.
    [HarmonyPatchCategory(Features.MultiReinstall)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.HandleBlockingThingJob))]
    internal static class GenConstruct_HandleBlockingThingJob_MultiReinstall_Patch
    {
        private static bool Prefix(Thing constructible, Pawn worker, bool forced, ref Job __result)
        {
            if (!(GenConstruct.FirstBlockingThing(constructible, worker) is Building blocker) || InstallBlueprintUtility.ExistingBlueprintFor(blocker) == null)
            {
                return true;
            }

            __result = MultiReinstallUtility.UninstallBlockerJob(blocker, worker, forced);
            return false;
        }
    }

    // 청사진 밑 건물을 교체용으로 철거하는 작업도 옮겨 갈 건물에는 하지 않는다.
    [HarmonyPatchCategory(Features.MultiReinstall)]
    [HarmonyPatch(typeof(WorkGiver_DeconstructForBlueprint), nameof(WorkGiver_DeconstructForBlueprint.HasJobOnThing))]
    internal static class WorkGiver_DeconstructForBlueprint_MultiReinstall_Patch
    {
        private static bool Prefix(Thing t, ref bool __result)
        {
            if (t is Building building && InstallBlueprintUtility.ExistingBlueprintFor(building) != null)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
}

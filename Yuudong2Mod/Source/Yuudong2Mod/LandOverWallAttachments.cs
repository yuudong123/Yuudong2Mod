using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    internal static class LandOverWallAttachments
    {
        // 벽등처럼 벽에 붙는 건물. 앞 칸을 차지하지만 걸어 다닐 수 있고, 착륙한 왕복선에 지워지지도 않는다.
        public static bool IsWallAttachment(Thing thing)
        {
            return thing.def.building != null && thing.def.building.isAttachment;
        }
    }

    // 왕복선 착륙 지점을 고를 때 벽에 붙은 건물이 있는 칸도 받아 준다. 바닐라 GetReportFromCell과 같고, 벽 부착물만 건너뛴다.
    [HarmonyPatchCategory(Features.LandOverWallAttachments)]
    [HarmonyPatch(typeof(RoyalTitlePermitWorker_CallShuttle), "GetReportFromCell")]
    internal static class RoyalTitlePermitWorker_CallShuttle_GetReportFromCell_Patch
    {
        private static bool Prefix(IntVec3 cell, Map map, ThingDef shuttleDef, ref string __result)
        {
            if (!cell.InBounds(map))
            {
                __result = "OutOfBounds".Translate().CapitalizeFirst();
                return false;
            }
            if (cell.Fogged(map))
            {
                __result = "ShuttleCannotLand_Fogged".Translate().CapitalizeFirst();
                return false;
            }
            if (!cell.Walkable(map))
            {
                __result = "ShuttleCannotLand_Unwalkable".Translate().CapitalizeFirst();
                return false;
            }
            if (!cell.GetAffordances(map).Contains(ThingDefOf.Shuttle.terrainAffordanceNeeded))
            {
                __result = "ShuttleCannotLand_Unaffordable".Translate(shuttleDef.label).CapitalizeFirst();
                return false;
            }
            RoofDef roof = cell.GetRoof(map);
            if (roof != null && (roof.isNatural || roof.isThickRoof))
            {
                __result = "MessageTransportPodsDestinationIsInvalid".Translate().CapitalizeFirst();
                return false;
            }
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (LandOverWallAttachments.IsWallAttachment(thing))
                {
                    continue;
                }
                if (thing is IActiveTransporter || thing is Skyfaller || (thing.def.category == ThingCategory.Building && !thing.def.building.isPowerConduit))
                {
                    __result = "BlockedBy".Translate(thing).CapitalizeFirst();
                    return false;
                }
                PlantProperties plant = thing.def.plant;
                if (plant != null && plant.IsTree)
                {
                    __result = "BlockedBy".Translate(thing).CapitalizeFirst();
                    return false;
                }
            }
            __result = null;
            return false;
        }
    }

    // 착륙 신호기가 만드는 착륙장도 벽 부착물 때문에 막히지 않게 한다. 바닐라 RecalculateBlockingThing과 같고, 벽 부착물만 건너뛴다.
    [HarmonyPatchCategory(Features.LandOverWallAttachments)]
    [HarmonyPatch(typeof(ShipLandingArea), nameof(ShipLandingArea.RecalculateBlockingThing))]
    internal static class ShipLandingArea_RecalculateBlockingThing_Patch
    {
        private static readonly AccessTools.FieldRef<ShipLandingArea, Map> mapRef = AccessTools.FieldRefAccess<ShipLandingArea, Map>("map");
        private static readonly AccessTools.FieldRef<ShipLandingArea, Thing> blockingRef = AccessTools.FieldRefAccess<ShipLandingArea, Thing>("firstBlockingThing");
        private static readonly AccessTools.FieldRef<ShipLandingArea, bool> roofRef = AccessTools.FieldRefAccess<ShipLandingArea, bool>("blockedByRoof");
        private static readonly AccessTools.FieldRef<ShipLandingArea, bool> terrainRef = AccessTools.FieldRefAccess<ShipLandingArea, bool>("blockedByTerrainAffordance");

        private static bool Prefix(ShipLandingArea __instance)
        {
            Map map = mapRef(__instance);
            roofRef(__instance) = false;
            foreach (IntVec3 cell in __instance.MyRect)
            {
                if (cell.Roofed(map))
                {
                    roofRef(__instance) = true;
                    break;
                }
                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing is Pawn || LandOverWallAttachments.IsWallAttachment(thing))
                    {
                        continue;
                    }
                    if (thing.def.Fillage != FillCategory.None || thing.def.IsEdifice() || thing is Skyfaller)
                    {
                        blockingRef(__instance) = thing;
                        return false;
                    }
                }
                TerrainAffordanceDef support = __instance.beacons[0].Props.landingAreaTerrainSupport;
                if (support != null && !cell.SupportsStructureType(map, support))
                {
                    terrainRef(__instance) = true;
                    return false;
                }
            }
            blockingRef(__instance) = null;
            return false;
        }
    }
}

using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    public static class FloorInfestationUtility
    {
        // 지어 깐 바닥(위에 덮은 지형), 다리·하부 구조 같은 기반, Floor 태그가 붙은 지형을 바닥으로 본다. 다듬은 돌은 지형 자체가 바뀐 것이라 넣지 않는다.
        public static bool HasFloor(IntVec3 c, Map map)
        {
            TerrainGrid grid = map.terrainGrid;
            return grid.UnderTerrainAt(c) != null || grid.FoundationAt(c) != null || grid.TopTerrainAt(c).IsFloor;
        }
    }

    // 감염 위치 점수: 바닥 칸은 0점이라 감염 시작 위치로 뽑히지 않는다.
    [HarmonyPatchCategory(Features.NoFloorInfestation)]
    [HarmonyPatch(typeof(InfestationCellFinder), "GetScoreAt")]
    internal static class InfestationCellFinder_GetScoreAt_NoFloorInfestation_Patch
    {
        private static void Postfix(IntVec3 cell, Map map, ref float __result)
        {
            if (__result > 0f && FloorInfestationUtility.HasFloor(cell, map))
            {
                __result = 0f;
            }
        }
    }

    // 추가 굴과 군락 번식 위치: 바닥 칸에는 새 군락을 만들지 않는다.
    [HarmonyPatchCategory(Features.NoFloorInfestation)]
    [HarmonyPatch(typeof(CompSpawnerHives), "CanSpawnHiveAt")]
    internal static class CompSpawnerHives_CanSpawnHiveAt_NoFloorInfestation_Patch
    {
        private static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result && FloorInfestationUtility.HasFloor(c, map))
            {
                __result = false;
            }
        }
    }

    // 좋은 위치가 없을 때(퀘스트 감염 등) 맵 가운데 근처에서 고르는 대체 위치도 바닥이 아닌 칸으로 다시 고른다. 없으면 감염이 일어나지 않는다.
    [HarmonyPatchCategory(Features.NoFloorInfestation)]
    [HarmonyPatch(typeof(InfestationUtility), "FindRootTunnelLoc")]
    internal static class InfestationUtility_FindRootTunnelLoc_NoFloorInfestation_Patch
    {
        private static void Postfix(Map map, bool ignoreRoofIfNoGoodCell, ref IntVec3 __result)
        {
            if (!__result.IsValid || !FloorInfestationUtility.HasFloor(__result, map))
            {
                return;
            }

            if (RCellFinder.TryFindRandomCellNearTheCenterOfTheMapWith(c => Valid(c, map, false), map, out IntVec3 cell)
                || (ignoreRoofIfNoGoodCell && RCellFinder.TryFindRandomCellNearTheCenterOfTheMapWith(c => Valid(c, map, true), map, out cell)))
            {
                __result = cell;
                return;
            }

            __result = IntVec3.Invalid;
        }

        private static bool Valid(IntVec3 c, Map map, bool ignoreRoof)
        {
            if (!c.Standable(map) || c.Fogged(map) || FloorInfestationUtility.HasFloor(c, map))
            {
                return false;
            }

            if (ignoreRoof)
            {
                return true;
            }

            int count = GenRadial.NumCellsInRadius(3f);
            for (int i = 0; i < count; i++)
            {
                IntVec3 near = c + GenRadial.RadialPattern[i];
                if (near.InBounds(map) && near.GetRoof(map) is RoofDef roof && roof.isThickRoof)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // 알주머니 자동 공격: Patches/EggSacTargeting.xml이 Odyssey 알주머니의 thingClass를 이것으로 바꾼다.
    // 알주머니는 던진 여왕의 세력을 물려받는다. 곤충 세력 것만 적대라서 자동 조준되고, 야생이나 정착지 여왕의 것은 그대로 둔다.
    public class Building_EggSac : Building, IAttackTarget
    {
        Thing IAttackTarget.Thing => this;

        public float TargetPriorityFactor => 0.4f;

        public LocalTargetInfo TargetCurrentlyAimingAt => LocalTargetInfo.Invalid;

        public bool ThreatDisabled(IAttackTargetSearcher disabledFor)
        {
            return !Spawned;
        }
    }
}

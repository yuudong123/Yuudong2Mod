using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 중력 구동기와 중력장 확장기의 원형 범위를 없앤다. 배의 크기는 하부구조 지탱 수치로만 제한된다.
    public static class GravshipNoRangeUtility
    {
        public const float LinkDistance = 500f;

        // 확장기는 구동기에서 일정 거리 안에 있어야 설치·연결된다. 다른 중력선 설비와 같은 거리(맵 전체)로 맞춘다.
        public static void ApplyDefPatches()
        {
            ThingDef extender = DefDatabase<ThingDef>.GetNamedSilentFail("GravFieldExtender");
            CompProperties_GravshipFacility facility = extender?.GetCompProperties<CompProperties_GravshipFacility>();
            if (facility == null)
            {
                if (ModsConfig.OdysseyActive)
                {
                    Log.Warning("[Yuudong2Mod] 중력선 범위 제거: 중력장 확장기(GravFieldExtender)의 설비 설정을 찾지 못해 확장기 연결 거리를 그대로 둡니다.");
                }

                return;
            }

            if (facility.maxDistance < LinkDistance)
            {
                facility.maxDistance = LinkDistance;
            }
        }
    }

    [HarmonyPatchCategory(Features.GravshipNoRange)]
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.InsideFootprint))]
    internal static class GravshipUtility_InsideFootprint_NoRange_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = ModsConfig.OdysseyActive;
            return false;
        }
    }

    // 범위가 없으므로 범위 원도 그리지 않는다.
    [HarmonyPatchCategory(Features.GravshipNoRange)]
    [HarmonyPatch(typeof(SubstructureGrid), nameof(SubstructureGrid.DrawSubstructureFootprint))]
    internal static class SubstructureGrid_DrawSubstructureFootprint_NoRange_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }
}

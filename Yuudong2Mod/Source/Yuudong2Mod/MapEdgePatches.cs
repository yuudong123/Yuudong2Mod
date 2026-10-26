using HarmonyLib;
using Verse;

namespace Yuudong2Mod
{
    // Change map edge limit 포팅. 기존 설정값(0칸)대로 맵 가장자리 건설·구역 제한과 경계선을 없앤다.
    [HarmonyPatchCategory(Features.NoMapEdgeLimit)]
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.InNoBuildEdgeArea))]
    internal static class GenGrid_InNoBuildEdgeArea_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoMapEdgeLimit)]
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.InNoZoneEdgeArea))]
    internal static class GenGrid_InNoZoneEdgeArea_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoMapEdgeLimit)]
    [HarmonyPatch(typeof(CellRect), nameof(CellRect.InNoBuildEdgeArea))]
    internal static class CellRect_InNoBuildEdgeArea_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoMapEdgeLimit)]
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawNoBuildEdgeLines))]
    internal static class GenDraw_DrawNoBuildEdgeLines_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoMapEdgeLimit)]
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawNoZoneEdgeLines))]
    internal static class GenDraw_DrawNoZoneEdgeLines_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }
}

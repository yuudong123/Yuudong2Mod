using HarmonyLib;
using RimWorld;

namespace Yuudong2Mod
{
    // 착륙 신호기가 착륙장을 이루지 못했다는 "신호기 사용불가" 경고를 띄우지 않는다.
    [HarmonyPatchCategory(Features.NoBeaconAlert)]
    [HarmonyPatch(typeof(Alert_ShuttleLandingBeaconUnusable), nameof(Alert_ShuttleLandingBeaconUnusable.GetReport))]
    internal static class Alert_ShuttleLandingBeaconUnusable_GetReport_Patch
    {
        private static bool Prefix(ref AlertReport __result)
        {
            __result = AlertReport.Inactive;
            return false;
        }
    }
}

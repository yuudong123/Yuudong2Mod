using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Roofed Scanning 포팅. 지하 자원 스캐너와 장거리 광물 스캐너를 지붕 아래에서도 쓸 수 있다.
    // 설치 제한과 "지붕 아래에서 쓸 수 없음" 경고는 Patches/RoofedScanning.xml이 푼다. Odyssey 궤도 탐지기는 작동 중 지붕을 검사하지 않아 XML만으로 된다.
    [HarmonyPatchCategory(Features.RoofedScanning)]
    [HarmonyPatch(typeof(CompScanner), nameof(CompScanner.CanUseNow), MethodType.Getter)]
    internal static class CompScanner_CanUseNow_RoofedScanning_Patch
    {
        private static bool Prefix(CompScanner __instance, CompPowerTrader ___powerComp, CompForbiddable ___forbiddable, ref AcceptanceReport __result)
        {
            ThingWithComps parent = __instance.parent;
            __result = parent.Spawned
                && (___powerComp == null || ___powerComp.PowerOn)
                && (___forbiddable == null || !___forbiddable.Forbidden)
                && parent.Faction == Faction.OfPlayer;
            return false;
        }
    }
}

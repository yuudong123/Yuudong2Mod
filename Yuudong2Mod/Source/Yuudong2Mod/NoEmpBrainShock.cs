using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 뇌 이식물이 EMP에 맞았을 때 붙이는 뇌충격과 EPOE의 Disorientation을 붙이지 않는다.
    [HarmonyPatchCategory(Features.NoEmpBrainShock)]
    [HarmonyPatch(typeof(HediffComp_ReactOnDamage), nameof(HediffComp_ReactOnDamage.Notify_PawnPostApplyDamage))]
    public static class HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage_Patch
    {
        private static bool Prefix(HediffComp_ReactOnDamage __instance, DamageInfo dinfo)
        {
            return !(dinfo.Def == DamageDefOf.EMP && IsBrainShock(__instance.Props.createHediff));
        }

        // 바닐라 뇌충격과 EPOE 보조 AI의 Disorientation만 막는다. 외골격 정지 같은 다른 EMP 반응은 그대로 둔다.
        public static bool IsBrainShock(HediffDef def)
        {
            return def != null && (def.defName == "BrainShock" || def.defName.Contains("Disorientation"));
        }
    }
}

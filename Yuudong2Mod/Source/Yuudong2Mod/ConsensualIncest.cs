using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Allow Consensual Incest 포팅. 원본이 워크숍에서 내려가 동작을 바닐라 기준으로 다시 작성했다.
    // 가족끼리도 다른 폰처럼 연애할 수 있다. 근친 출산 시 근친 유전자가 붙는 바닐라 규칙은 그대로 둔다.
    [HarmonyPatchCategory(Features.ConsensualIncest)]
    [HarmonyPatch(typeof(RelationsUtility), "Incestuous")]
    internal static class RelationsUtility_Incestuous_ConsensualIncest_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    // 혈연 관계마다 곱하는 연애 확률 배율(부모 0.03, 조부모 0.25 등)을 되돌린다.
    [HarmonyPatchCategory(Features.ConsensualIncest)]
    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.SecondaryRomanceChanceFactor))]
    internal static class Pawn_RelationsTracker_SecondaryRomanceChanceFactor_ConsensualIncest_Patch
    {
        private static void Postfix(Pawn ___pawn, Pawn otherPawn, ref float __result)
        {
            float factor = 1f;
            foreach (PawnRelationDef relation in ___pawn.GetRelations(otherPawn))
            {
                factor *= relation.romanceChanceFactor;
            }

            if (factor > 0f && factor != 1f)
            {
                __result /= factor;
            }
        }
    }

    // 가족과 사귀는 폰을 보고 다른 폰이 깎는 호감도를 없앤다.
    [HarmonyPatchCategory(Features.ConsensualIncest)]
    [HarmonyPatch(typeof(LovePartnerRelationUtility), nameof(LovePartnerRelationUtility.IncestOpinionOffsetFor))]
    internal static class LovePartnerRelationUtility_IncestOpinionOffsetFor_ConsensualIncest_Patch
    {
        private static bool Prefix(ref float __result)
        {
            __result = 0f;
            return false;
        }
    }
}

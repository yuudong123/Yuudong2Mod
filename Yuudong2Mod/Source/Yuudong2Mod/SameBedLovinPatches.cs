using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    [HarmonyPatchCategory(Features.SameBedLovin)]
    [HarmonyPatch(typeof(LovePartnerRelationUtility), nameof(LovePartnerRelationUtility.GetPartnerInMyBed))]
    internal static class LovePartnerRelationUtility_GetPartnerInMyBed_SameBedPatch
    {
        private static void Postfix(Pawn pawn, ref Pawn __result)
        {
            if (__result != null || pawn == null || pawn.DestroyedOrNull())
            {
                return;
            }

            if (!IsAdult(pawn))
            {
                return;
            }

            Building_Bed bed = pawn.CurrentBed();
            if (bed == null || bed.SleepingSlotsCount <= 1)
            {
                return;
            }

            foreach (Pawn occupant in bed.CurOccupants)
            {
                if (occupant != pawn && occupant != null && !occupant.Destroyed && !occupant.Dead && IsAdult(occupant))
                {
                    __result = occupant;
                    return;
                }
            }
        }

        // 연인 관계 없이 고르는 상대는 둘 다 성인 단계이고 18세 이상일 때만 허용한다.
        private static bool IsAdult(Pawn pawn)
        {
            return pawn.ageTracker != null && pawn.DevelopmentalStage.Adult() && pawn.ageTracker.AgeBiologicalYearsFloat >= 18f;
        }
    }
}

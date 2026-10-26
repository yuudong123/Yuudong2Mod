using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    [HarmonyPatchCategory(Features.AlwaysAcceptProposal)]
    [HarmonyPatch(typeof(InteractionWorker_MarriageProposal), nameof(InteractionWorker_MarriageProposal.AcceptanceChance))]
    internal static class InteractionWorker_MarriageProposal_AcceptanceChance_NoRejectionPatch
    {
        private static void Postfix(ref float __result)
        {
            __result = 1f;
        }
    }
}

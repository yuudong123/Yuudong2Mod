using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Dragonian to Super Fast Healing Patch 포팅.
    // 상처 회복 유전자에 의해 덮여 있는 드래곤본 유전자를 불러올 때 다시 활성화한다.
    [HarmonyPatchCategory(Features.DragonianHealing)]
    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.ExposeData))]
    internal static class Pawn_GeneTracker_ExposeData_DragonianHealing_Patch
    {
        private static void Postfix(Pawn_GeneTracker __instance)
        {
            if (Scribe.mode != LoadSaveMode.PostLoadInit || __instance?.GenesListForReading == null)
            {
                return;
            }

            foreach (Gene gene in __instance.GenesListForReading)
            {
                if (gene?.overriddenByGene == null || !IsDragonborn(gene.def))
                {
                    continue;
                }

                GeneDef overridingDef = gene.overriddenByGene.def;
                if (IsWoundHealingGene(overridingDef) && !gene.def.ConflictsWith(overridingDef))
                {
                    gene.OverrideBy(null);
                }
            }
        }

        private static bool IsDragonborn(GeneDef def)
        {
            return def != null && (def.defName == "Dragonian_Dragonborn" || def.defName == "Dragonian_Dragonborn_Black");
        }

        private static bool IsWoundHealingGene(GeneDef def)
        {
            return def != null
                && (def.defName == "WoundHealing_Slow" || def.defName == "WoundHealing_Fast" || def.defName == "WoundHealing_SuperFast");
        }
    }
}

using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    internal static class DragonianCompatibilityUtility
    {
        private const string DragonianPackageId = "RooAndGloomy.DragonianRaceMod";
        private static readonly string[] DragonbornGeneDefNames = { "Dragonian_Dragonborn", "Dragonian_Dragonborn_Black" };

        public static void ApplyDefPatches()
        {
            if (!DragonianActive)
            {
                return;
            }

            foreach (string defName in DragonbornGeneDefNames)
            {
                DefDatabase<GeneDef>.GetNamedSilentFail(defName)?.exclusionTags?.Clear();
            }
        }

        public static bool IsDragonborn(GeneDef def)
        {
            return def != null && Array.IndexOf(DragonbornGeneDefNames, def.defName) >= 0;
        }

        private static bool DragonianActive
        {
            get
            {
                return ModsConfig.IsActive(DragonianPackageId) || ModsConfig.IsActive(DragonianPackageId.ToLowerInvariant());
            }
        }
    }

    // 제한을 없애기 전에 저장된 세이브에는 드래곤본 유전자가 다른 유전자를 억제한 상태가 남아 있다. 더는 충돌하지 않는 억제를 불러올 때 푼다.
    [HarmonyPatchCategory(Features.DragonianGenes)]
    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.ExposeData))]
    internal static class Pawn_GeneTracker_ExposeData_DragonianGenes_Patch
    {
        private static void Postfix(Pawn_GeneTracker __instance)
        {
            if (Scribe.mode != LoadSaveMode.PostLoadInit || __instance?.GenesListForReading == null)
            {
                return;
            }

            foreach (Gene gene in __instance.GenesListForReading)
            {
                Gene by = gene?.overriddenByGene;
                if (by != null && by.def != gene.def && (DragonianCompatibilityUtility.IsDragonborn(by.def) || DragonianCompatibilityUtility.IsDragonborn(gene.def)) && !gene.def.ConflictsWith(by.def))
                {
                    gene.OverrideBy(null);
                }
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 바닐라는 인공 부품을 단 부위(또는 그 아래 부위)에는 임플란트를 달 수 없다. 초월공학 눈에 전술 각막을 다는 식으로 같이 달 수 있게 한다.
    // 부위가 없거나, 같은 임플란트가 이미 있거나, 서로 막는 태그가 있는 경우는 바닐라대로 막는다.
    // EPOE 생체 최적화는 능률만 올리는 살아 있는 조직용이라 인공 부품에는 그대로 막는다.
    [HarmonyPatchCategory(Features.ImplantsOnAddedParts)]
    [HarmonyPatch(typeof(Recipe_InstallImplant), nameof(Recipe_InstallImplant.GetPartsToApplyOn))]
    internal static class Recipe_InstallImplant_GetPartsToApplyOn_Patch
    {
        private static readonly HashSet<string> NaturalOnlyHediffs = new HashSet<string> { "EPOE_OrganicOptimized" };

        private static bool Prefix(Pawn pawn, RecipeDef recipe, ref IEnumerable<BodyPartRecord> __result)
        {
            HediffSet hediffSet = pawn.health.hediffSet;
            bool naturalOnly = recipe.addsHediff != null && NaturalOnlyHediffs.Contains(recipe.addsHediff.defName);
            __result = MedicalRecipesUtility.GetFixedPartsToApplyOn(recipe, pawn, record =>
            {
                if (!hediffSet.GetNotMissingParts().Contains(record))
                {
                    return false;
                }

                if (naturalOnly && hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(record))
                {
                    return false;
                }

                List<Hediff> hediffs = hediffSet.hediffs;
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (hediffs[i].Part == record && (hediffs[i].def == recipe.addsHediff || !recipe.CompatibleWithHediff(hediffs[i].def)))
                    {
                        return false;
                    }
                }

                return true;
            });
            return false;
        }
    }
}

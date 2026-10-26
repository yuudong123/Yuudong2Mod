using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(InteractionWorker_Breakup), nameof(InteractionWorker_Breakup.RandomSelectionWeight))]
    internal static class InteractionWorker_Breakup_RandomSelectionWeight_Patch
    {
        private static bool Prefix(ref float __result)
        {
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(InteractionWorker_Breakup), nameof(InteractionWorker_Breakup.Interacted))]
    internal static class InteractionWorker_Breakup_Interacted_Patch
    {
        private static bool Prefix(
            out string letterText,
            out string letterLabel,
            out LetterDef letterDef,
            out LookTargets lookTargets)
        {
            letterText = null;
            letterLabel = null;
            letterDef = null;
            lookTargets = null;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(InteractionWorker_MarriageProposal), nameof(InteractionWorker_MarriageProposal.Interacted))]
    internal static class InteractionWorker_MarriageProposal_Interacted_NoBreakupOnRejection_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && value == 0.4f)
                {
                    CodeInstruction replacement = new CodeInstruction(OpCodes.Ldc_R4, -1f);
                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    yield return replacement;
                    continue;
                }

                yield return instruction;
            }
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch]
    internal static class InteractionWorker_RomanceAttempt_BreakLoverAndFianceRelations_Patch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(InteractionWorker_RomanceAttempt), "BreakLoverAndFianceRelations");
        }

        private static bool Prefix(out List<Pawn> oldLoversAndFiances)
        {
            oldLoversAndFiances = new List<Pawn>();
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(SpouseRelationUtility), nameof(SpouseRelationUtility.RemoveSpousesAsForbiddenByIdeo))]
    internal static class SpouseRelationUtility_RemoveSpousesAsForbiddenByIdeo_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(SpouseRelationUtility), nameof(SpouseRelationUtility.DoDivorce))]
    internal static class SpouseRelationUtility_DoDivorce_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }

    [HarmonyPatchCategory(Features.NoBreakup)]
    [HarmonyPatch(typeof(LovePartnerRelationUtility), nameof(LovePartnerRelationUtility.ChangeSpouseRelationsToExSpouse))]
    internal static class LovePartnerRelationUtility_ChangeSpouseRelationsToExSpouse_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }
}

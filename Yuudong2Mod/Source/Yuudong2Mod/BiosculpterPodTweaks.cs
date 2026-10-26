using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Faster Biosculpter Pod 포팅. 기존에 쓰던 설정값(모든 주기 1일, 생체조율 없음)을 고정으로 적용한다.
    internal static class BiosculpterPodTweaks
    {
        private const float CycleDays = 1f;
        private const int VanillaBiotunedDurationTicks = 4800000;
        public const int BiotunedDurationTicks = 0;

        public static void ApplyDefPatches()
        {
            ThingDef pod = DefDatabase<ThingDef>.GetNamedSilentFail("BiosculpterPod");
            if (pod?.comps == null)
            {
                return;
            }

            foreach (CompProperties comp in pod.comps)
            {
                if (comp is CompProperties_BiosculpterPod_BaseCycle cycle)
                {
                    cycle.durationDays = CycleDays;
                }
            }
        }

        public static IEnumerable<CodeInstruction> ReplaceBiotunedDuration(IEnumerable<CodeInstruction> instructions, string target)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int value && value == VanillaBiotunedDurationTicks)
                {
                    instruction.operand = BiotunedDurationTicks;
                    replaced++;
                }

                yield return instruction;
            }

            if (replaced == 0)
            {
                Log.Warning("[Yuudong2Mod] " + target + "에서 생체조율 기간 상수를 찾지 못했습니다.");
            }
        }
    }

    [HarmonyPatchCategory(Features.FasterBiosculpter)]
    [HarmonyPatch(typeof(CompBiosculpterPod), nameof(CompBiosculpterPod.SetBiotuned))]
    internal static class CompBiosculpterPod_SetBiotuned_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return BiosculpterPodTweaks.ReplaceBiotunedDuration(instructions, "CompBiosculpterPod.SetBiotuned");
        }
    }

    [HarmonyPatchCategory(Features.FasterBiosculpter)]
    [HarmonyPatch(typeof(CompBiosculpterPod), nameof(CompBiosculpterPod.CompTick))]
    internal static class CompBiosculpterPod_CompTick_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return BiosculpterPodTweaks.ReplaceBiotunedDuration(instructions, "CompBiosculpterPod.CompTick");
        }
    }
}

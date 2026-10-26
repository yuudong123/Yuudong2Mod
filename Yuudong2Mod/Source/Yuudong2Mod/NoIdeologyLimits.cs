using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Ducks' No Limits - Ideology 포팅.
    // 밈 개수, 교리 개수, 의식 개수 제한을 풀고, 모든 교리를 편집 화면에서 지울 수 있게 한다.
    internal static class NoIdeologyLimitsUtility
    {
        private const int Unlimited = 1000;

        public static FactionDef currentFaction;

        public static IntRange MemeCountRange()
        {
            return new IntRange(0, Unlimited);
        }

        public static int RitualLimit()
        {
            return Unlimited;
        }

        public static void ApplyDefPatches()
        {
            foreach (PreceptDef precept in DefDatabase<PreceptDef>.AllDefsListForReading)
            {
                precept.ignoreLimitsInEditMode = true;
                precept.canRemoveInUI = true;
            }
        }

        public static bool AppliesToCurrentFaction()
        {
            return !Yuudong2Settings.Current.ideologyLimitsOnlyPlayer || (currentFaction != null && currentFaction.isPlayer);
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Ideology.OnlyPlayer".Translate(), ref settings.ideologyLimitsOnlyPlayer, "Yuudong2.Ideology.OnlyPlayer.Desc".Translate());
            listing.End();
        }
    }

    // 원본은 IdeoFoundation.MemeCountRangeAbsolute(static readonly) 값을 직접 바꿨다.
    // 1.7 런타임에서는 static readonly 값이 상수처럼 굳을 수 있어서, 그 값을 읽는 밈 선택 창 쪽을 바꾼다.
    [HarmonyPatchCategory(Features.NoIdeologyLimits)]
    [HarmonyPatch(typeof(Dialog_ChooseMemes), "MemeCountRangeAbsolute", MethodType.Getter)]
    internal static class Dialog_ChooseMemes_MemeCountRangeAbsolute_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo field = AccessTools.Field(typeof(IdeoFoundation), nameof(IdeoFoundation.MemeCountRangeAbsolute));
            MethodInfo replacement = AccessTools.Method(typeof(NoIdeologyLimitsUtility), nameof(NoIdeologyLimitsUtility.MemeCountRange));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Ldsfld || instruction.opcode == OpCodes.Ldsflda) && instruction.operand as FieldInfo == field)
                {
                    if (instruction.opcode == OpCodes.Ldsflda)
                    {
                        // 주소를 읽는 경우에는 값을 지역 변수에 담아 주소를 넘긴다.
                        yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Dialog_ChooseMemes_MemeCountRangeAbsolute_Patch), nameof(Box)));
                        replaced++;
                        continue;
                    }

                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }

                yield return instruction;
            }

            if (replaced == 0)
            {
                Log.Warning("[Yuudong2Mod] 밈 선택 창에서 밈 개수 제한을 찾지 못했습니다.");
            }
        }

        private static IntRange boxed;

        private static ref IntRange Box(IntRange value)
        {
            boxed = value;
            return ref boxed;
        }
    }

    [HarmonyPatchCategory(Features.NoIdeologyLimits)]
    [HarmonyPatch(typeof(IdeoFoundation), nameof(IdeoFoundation.CanAdd))]
    internal static class IdeoFoundation_CanAdd_Patch
    {
        private static void Postfix(ref AcceptanceReport __result, IdeoFoundation __instance, PreceptDef precept, bool checkDuplicates)
        {
            if (__result.Accepted || !NoIdeologyLimitsUtility.AppliesToCurrentFaction())
            {
                return;
            }

            List<Precept> precepts = __instance.ideo.PreceptsListForReading;
            if (precept.takeNameFrom != null && !precepts.Any(p => p?.def == precept.takeNameFrom))
            {
                return;
            }

            if (checkDuplicates)
            {
                foreach (Precept existing in precepts)
                {
                    if (precept == existing.def)
                    {
                        if (!precept.allowDuplicates)
                        {
                            return;
                        }
                    }
                    else if (!precept.issue.allowMultiplePrecepts && precept.issue == existing.def.issue)
                    {
                        return;
                    }
                }
            }

            __result = true;
        }
    }

    [HarmonyPatchCategory(Features.NoIdeologyLimits)]
    [HarmonyPatch(typeof(IdeoFoundation), nameof(IdeoFoundation.CanAddForFaction))]
    internal static class IdeoFoundation_CanAddForFaction_Patch
    {
        private static void Prefix(FactionDef forFaction)
        {
            NoIdeologyLimitsUtility.currentFaction = forFaction;
        }

        private static void Finalizer()
        {
            NoIdeologyLimitsUtility.currentFaction = null;
        }
    }

    [HarmonyPatchCategory(Features.NoIdeologyLimits)]
    [HarmonyPatch(typeof(IdeoUIUtility), "AddPrecept")]
    internal static class IdeoUIUtility_AddPrecept_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                OpCode next = list[i + 1].opcode;
                bool isGreaterOrEqualBranch = next == OpCodes.Bge || next == OpCodes.Bge_S || next == OpCodes.Bge_Un || next == OpCodes.Bge_Un_S;
                if (list[i].opcode == OpCodes.Ldc_I4_6 && isGreaterOrEqualBranch)
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = AccessTools.Method(typeof(NoIdeologyLimitsUtility), nameof(NoIdeologyLimitsUtility.RitualLimit));
                    replaced++;
                }
            }

            if (replaced == 0)
            {
                Log.Warning("[Yuudong2Mod] 교리 추가 메뉴에서 의식 개수 제한을 찾지 못했습니다.");
            }

            return list;
        }
    }

    [HarmonyPatchCategory(Features.NoIdeologyLimits)]
    [HarmonyPatch(typeof(IdeoUIUtility), "CanAddRitualPattern")]
    internal static class IdeoUIUtility_CanAddRitualPattern_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }
}

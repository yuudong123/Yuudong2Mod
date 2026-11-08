using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // EndlessGrowth 포팅. 기술 레벨 20 제한을 없애고(또는 원하는 최대치로 바꾸고) 경험치·품질·작업 지시 범위를 그에 맞춘다.
    // 패치 내용이 설정값에 따라 달라지므로 세부 설정을 바꾸면 재시작해야 한다.
    internal static class EndlessGrowthUtility
    {
        public static int maxPawnLevel;
        private static readonly List<System.WeakReference<Bill>> bills = new List<System.WeakReference<Bill>>();

        public static readonly SimpleCurve XpForLevelUpCurve = new SimpleCurve
        {
            new CurvePoint(0f, 1000f),
            new CurvePoint(9f, 10000f),
            new CurvePoint(19f, 30000f),
            new CurvePoint(29f, 60000f),
            new CurvePoint(39f, 100000f),
            new CurvePoint(49f, 150000f),
            new CurvePoint(59f, 220000f),
            new CurvePoint(69f, 300000f),
            new CurvePoint(79f, 400000f),
            new CurvePoint(89f, 520000f),
            new CurvePoint(99f, 650000f),
        };

        public static readonly SimpleCurve QualityCurve = new SimpleCurve
        {
            new CurvePoint(0f, 0.7f),
            new CurvePoint(2f, 1.5f),
            new CurvePoint(3f, 1.8f),
            new CurvePoint(4f, 2f),
            new CurvePoint(8f, 2.8f),
            new CurvePoint(12f, 3.4f),
            new CurvePoint(20f, 4.2f),
            new CurvePoint(30f, 5f),
            new CurvePoint(40f, 5.7f),
            new CurvePoint(50f, 6f),
            new CurvePoint(100f, 10f),
        };

        private static int MaxLevelSetting => Yuudong2Settings.Current.endlessMaxLevel;

        private static string maxLevelBuffer;
        private static string billGapBuffer;

        public static void CheckMaxLevel(int level)
        {
            if (level <= maxPawnLevel)
            {
                return;
            }

            int previousBillMax = MaxLevelForBill();
            maxPawnLevel = level;
            bills.RemoveAll(r => !r.TryGetTarget(out Bill _));
            foreach (System.WeakReference<Bill> reference in bills)
            {
                if (reference.TryGetTarget(out Bill bill) && bill.allowedSkillRange.max == previousBillMax)
                {
                    bill.allowedSkillRange.max = MaxLevelForBill();
                }
            }
        }

        public static int MaxLevelForBill()
        {
            if (MaxLevelSetting != -1)
            {
                return Math.Max(20, MaxLevelSetting);
            }

            return Math.Max(100, maxPawnLevel + Yuudong2Settings.Current.endlessBillGap);
        }

        public static void RegisterBill(Bill bill)
        {
            bill.allowedSkillRange = new IntRange(0, MaxLevelForBill());
            if (bills.Count >= 1024)
            {
                bills.RemoveAll(r => !r.TryGetTarget(out Bill _));
            }

            bills.Add(new System.WeakReference<Bill>(bill));
        }

        // 레벨을 20으로 자르는 부분을 최대치로 바꾸거나(최대치 지정) Clamp를 Max로 바꾼다(무한).
        public static IEnumerable<CodeInstruction> FixClampOrMakeMax(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)20));
            if (MaxLevelSetting != -1)
            {
                matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4, MaxLevelSetting));
                return matcher.InstructionEnumeration();
            }

            matcher.RemoveInstruction();
            matcher.MatchStartForward(new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(int), typeof(int), typeof(int) })));
            matcher.SetInstruction(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(int), typeof(int) })));
            return matcher.InstructionEnumeration();
        }

        public static void IntervalDecay(SkillRecord record, float multiplier)
        {
            if (record.levelInt > 20)
            {
                record.Learn(-12f * multiplier);
            }
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.Endless.Restart".Translate());
            listing.Gap();
            listing.TextFieldNumericLabeled("Yuudong2.Endless.MaxLevel".Translate() + " ", ref s.endlessMaxLevel, ref maxLevelBuffer, -1f, 1E+09f);
            listing.Gap();
            listing.CheckboxLabeled("Yuudong2.Endless.UnlimitedPrice".Translate(), ref s.endlessUnlimitedPrice);
            listing.Gap();
            listing.Label("Yuudong2.Endless.BillGap".Translate());
            listing.TextFieldNumericLabeled("", ref s.endlessBillGap, ref billGapBuffer, 20f, 1E+09f);
            listing.Gap();
            listing.CheckboxLabeled("Yuudong2.Endless.CraftNotifications".Translate(), ref s.endlessCraftNotifications);
            listing.Gap();
            listing.CheckboxLabeled("Yuudong2.Endless.SpeedCap".Translate(), ref s.endlessSpeedCap, "Yuudong2.Endless.SpeedCap.Desc".Translate());
            listing.End();
        }

        public static void PatchModCompatibility(Harmony harmony)
        {
            if (ModLister.HasActiveModWithName("Mad Skills"))
            {
                harmony.Patch(AccessTools.Method(AccessTools.TypeByName("RTMadSkills.Patch_SkillRecordInterval"), "VanillaMultiplier"),
                    postfix: new HarmonyMethod(typeof(EndlessGrowthUtility), nameof(MadSkillsPostfix)));
            }

            if (ModLister.HasActiveModWithName("Vanilla Traits Expanded"))
            {
                harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VanillaTraitsExpanded.GenerateQualityCreatedByPawn_Patch"), "Postfix"),
                    transpiler: new HarmonyMethod(typeof(EndlessGrowthUtility), nameof(VanillaTraitsExpandedTranspiler)));
            }
        }

        private static void MadSkillsPostfix(int __0, ref float __result)
        {
            if (__0 > 20 && __result == 0f)
            {
                __result = -12f;
            }
        }

        private static IEnumerable<CodeInstruction> VanillaTraitsExpandedTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldc_I4_5), new CodeMatch(OpCodes.Stind_I1));
            for (int i = 0; i < 3; i++)
            {
                matcher.SetOpcodeAndAdvance(OpCodes.Nop);
            }

            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(Bill), MethodType.Constructor)]
    internal static class Bill_Constructor_EndlessGrowth_Patch
    {
        private static void Postfix(Bill __instance)
        {
            EndlessGrowthUtility.RegisterBill(__instance);
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(Bill), MethodType.Constructor, typeof(RecipeDef), typeof(Precept_ThingStyle))]
    internal static class Bill_ConstructorWithRecipe_EndlessGrowth_Patch
    {
        private static void Postfix(Bill __instance)
        {
            EndlessGrowthUtility.RegisterBill(__instance);
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(Dialog_BillConfig), nameof(Dialog_BillConfig.DoWindowContents))]
    internal static class Dialog_BillConfig_DoWindowContents_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)20));
            matcher.RemoveInstruction();
            matcher.Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EndlessGrowthUtility), nameof(EndlessGrowthUtility.MaxLevelForBill))));
            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(QualityUtility), nameof(QualityUtility.GenerateQualityCreatedByPawn), typeof(int), typeof(bool))]
    internal static class QualityUtility_GenerateQualityCreatedByPawn_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            // 레벨별 switch를 지우고 곡선으로 품질 기준값을 계산한다.
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Switch));
            while (!(matcher.Opcode == OpCodes.Ldc_R4 && matcher.Operand is float value && value == 4.2f))
            {
                matcher.RemoveInstruction();
            }

            while (matcher.Opcode != OpCodes.Ldloc_0)
            {
                matcher.RemoveInstruction();
            }

            matcher.Insert(
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(EndlessGrowthUtility), nameof(EndlessGrowthUtility.QualityCurve))),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Conv_R4),
                new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(SimpleCurve), nameof(SimpleCurve.Evaluate))),
                new CodeInstruction(OpCodes.Stloc_0));

            // 품질 상한(전설 = 6)까지 오를 수 있게 한다.
            for (int i = 0; i < 3; i++)
            {
                matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_5));
                matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4_6));
            }

            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(QualityUtility), nameof(QualityUtility.SendCraftNotification))]
    internal static class QualityUtility_SendCraftNotification_EndlessGrowth_Patch
    {
        private static bool Prefix()
        {
            return Yuudong2Settings.Current.endlessCraftNotifications;
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.ExposeData))]
    internal static class SkillRecord_ExposeData_EndlessGrowth_Patch
    {
        private static void Postfix(SkillRecord __instance)
        {
            if (Yuudong2Settings.Current.endlessMaxLevel == -1)
            {
                EndlessGrowthUtility.CheckMaxLevel(__instance.levelInt);
            }
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevelForUI))]
    internal static class SkillRecord_GetLevelForUI_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return EndlessGrowthUtility.FixClampOrMakeMax(instructions);
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevel))]
    internal static class SkillRecord_GetLevel_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return EndlessGrowthUtility.FixClampOrMakeMax(instructions);
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Level), MethodType.Setter)]
    internal static class SkillRecord_Level_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return EndlessGrowthUtility.FixClampOrMakeMax(instructions);
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Interval))]
    internal static class SkillRecord_Interval_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchEndForward(new CodeMatch(OpCodes.Switch));
            matcher.Advance(1);
            matcher.Insert(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EndlessGrowthUtility), nameof(EndlessGrowthUtility.IntervalDecay))));
            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn))]
    internal static class SkillRecord_Learn_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var levelInt = AccessTools.Field(typeof(SkillRecord), nameof(SkillRecord.levelInt));
            CodeMatch[] levelIs20 = { new CodeMatch(OpCodes.Ldfld, levelInt), new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)20), new CodeMatch(OpCodes.Bne_Un), new CodeMatch(OpCodes.Ldarg_0) };
            CodeMatch[] levelUp = { new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldarg_0), new CodeMatch(OpCodes.Ldfld, levelInt), new CodeMatch(OpCodes.Ldc_I4_1), new CodeMatch(OpCodes.Add), new CodeMatch(OpCodes.Stfld, levelInt) };
            CodeMatch[] levelAtLeast20 = { new CodeMatch(OpCodes.Ldfld, levelInt), new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)20), new CodeMatch(OpCodes.Blt_S), new CodeMatch(OpCodes.Ldarg_0) };

            CodeMatcher matcher = new CodeMatcher(instructions);
            int maxLevel = Yuudong2Settings.Current.endlessMaxLevel;
            if (maxLevel == -1)
            {
                // 레벨 20에서 경험치를 깎는 부분을 지운다.
                matcher.MatchEndForward(levelIs20);
                while (matcher.Opcode != OpCodes.Br)
                {
                    matcher.RemoveInstruction();
                }

                // 레벨이 오를 때마다 최고 레벨을 갱신한다.
                matcher.MatchEndForward(levelUp);
                matcher.Advance(1);
                matcher.Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, levelInt),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EndlessGrowthUtility), nameof(EndlessGrowthUtility.CheckMaxLevel))));

                // 레벨 20 이상에서 레벨업을 막는 부분을 지운다.
                matcher.MatchEndForward(levelAtLeast20);
                while (matcher.Opcode != OpCodes.Br_S)
                {
                    matcher.RemoveInstruction();
                }

                matcher.RemoveInstruction();
            }
            else
            {
                matcher.MatchStartForward(levelIs20);
                matcher.Advance(1);
                matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4, maxLevel));
                matcher.MatchStartForward(levelAtLeast20);
                matcher.Advance(1);
                matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4, maxLevel));
                matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)20));
                matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4, maxLevel));
            }

            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.LevelDescriptor), MethodType.Getter)]
    internal static class SkillRecord_LevelDescriptor_EndlessGrowth_Patch
    {
        private static void Postfix(ref string __result, SkillRecord __instance)
        {
            int level = __instance.GetLevelForUI();
            if (__result == "Unknown" && level > 20 && level < 100)
            {
                __result = ("Skill" + level).Translate();
            }
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.XpRequiredToLevelUpFrom))]
    internal static class SkillRecord_XpRequiredToLevelUpFrom_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(SkillRecord), "XpForLevelUpCurve")));
            matcher.SetInstruction(new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(EndlessGrowthUtility), nameof(EndlessGrowthUtility.XpForLevelUpCurve))));
            return matcher.InstructionEnumeration();
        }
    }

    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(Tradeable), "InitPriceDataIfNeeded")]
    internal static class Tradeable_InitPriceDataIfNeeded_EndlessGrowth_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Tradeable), "pricePlayerBuy")),
                new CodeMatch(OpCodes.Stfld, AccessTools.Field(typeof(Tradeable), "pricePlayerSell")));
            if (Yuudong2Settings.Current.endlessUnlimitedPrice)
            {
                matcher.RemoveInstructions(4);
            }

            return matcher.InstructionEnumeration();
        }
    }

    // 이름이 Speed로 끝나는 스탯(채굴·건설·연구 속도 등)은 기술 레벨이 20을 넘어도 20일 때 값에서 멈춘다.
    // 레벨당 보너스가 끝없이 붙으면 콘텐츠 소모가 너무 빨라진다. 이 설정은 재시작 없이 바로 적용된다.
    [HarmonyPatchCategory(Features.EndlessGrowth)]
    [HarmonyPatch(typeof(SkillNeed_BaseBonus), nameof(SkillNeed_BaseBonus.ValueFor))]
    internal static class SkillNeed_BaseBonus_ValueFor_EndlessGrowth_Patch
    {
        private const int SpeedLevelCap = 20;

        private static HashSet<SkillNeed> speedNeeds;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            System.Reflection.MethodInfo levelGetter = AccessTools.PropertyGetter(typeof(SkillRecord), nameof(SkillRecord.Level));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(levelGetter))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SkillNeed_BaseBonus_ValueFor_EndlessGrowth_Patch), nameof(LevelFor)));
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException("SkillRecord.Level call not found in SkillNeed_BaseBonus.ValueFor");
            }
        }

        private static int LevelFor(SkillRecord record, SkillNeed need)
        {
            int level = record.Level;
            if (level <= SpeedLevelCap || !Yuudong2Settings.Current.endlessSpeedCap)
            {
                return level;
            }

            if (speedNeeds == null)
            {
                speedNeeds = new HashSet<SkillNeed>();
                foreach (StatDef stat in DefDatabase<StatDef>.AllDefsListForReading)
                {
                    if (stat.defName.EndsWith("Speed"))
                    {
                        AddAll(stat.skillNeedFactors);
                        AddAll(stat.skillNeedOffsets);
                    }
                }
            }

            return speedNeeds.Contains(need) ? SpeedLevelCap : level;
        }

        private static void AddAll(List<SkillNeed> needs)
        {
            if (needs != null)
            {
                foreach (SkillNeed need in needs)
                {
                    speedNeeds.Add(need);
                }
            }
        }
    }
}

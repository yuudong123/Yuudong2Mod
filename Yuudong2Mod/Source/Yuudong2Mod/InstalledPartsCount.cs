using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ImprovedWorkbenches;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 신체 부위(생체공학·EPOE 부품 등) 제작 지시서의 "장착 중인 것 포함"이 정착민 몸에 이식된 같은 부품도 센다.
    // 옷과 같은 규칙을 따른다: 이 맵의 정착민은 늘, 맵을 떠난 정착민은 "자리 비운 정착민 것도 세기"를 켰을 때만 센다.
    // 타고난 장기는 상태 이상이 아니어서 세지 않는다. 몸에 붙은 부품에는 품질·내구도 필터를 적용할 수 없어 필터와 상관없이 센다.
    internal static class InstalledPartsCountUtility
    {
        private static HashSet<ThingDef> bodyPartDefs;

        // 몸에서 떼어 내면 이 물건이 나오는 상태 이상이 있으면 이식할 수 있는 신체 부위로 본다.
        public static bool IsBodyPart(ThingDef def)
        {
            if (bodyPartDefs == null)
            {
                bodyPartDefs = new HashSet<ThingDef>();
                foreach (HediffDef hediff in DefDatabase<HediffDef>.AllDefsListForReading)
                {
                    if (hediff.spawnThingOnRemoved != null)
                    {
                        bodyPartDefs.Add(hediff.spawnThingOnRemoved);
                    }
                }
            }

            return def != null && bodyPartDefs.Contains(def);
        }

        public static int CountInstalled(Bill_Production bill, ThingDef product)
        {
            Map map = bill.Map;
            if (map == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                count += CountIn(pawn, product);
            }

            if (Yuudong2Mod.AppliedFeatures.Contains(Features.BetterWorkbench))
            {
                if (Main.Instance.ShouldCountCarriedByNonHumans())
                {
                    count += CountIn(map.mapPawns.SpawnedColonyAnimals, product);
                    count += CountIn(map.mapPawns.SpawnedColonyMechs, product);
                    count += CountIn(map.mapPawns.SpawnedColonySubhumansPlayerControlled, product);
                }

                ExtendedBillData data = Main.Instance.GetExtendedBillDataStorage().GetExtendedDataFor(bill);
                if (data != null && data.CountAway)
                {
                    count += CountIn(AwayPawnRegistry.PawnsAwayFrom(map), product);
                }
            }

            return count;
        }

        private static int CountIn(List<Pawn> pawns, ThingDef product)
        {
            int count = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                count += CountIn(pawns[i], product);
            }

            return count;
        }

        private static int CountIn(Pawn pawn, ThingDef product)
        {
            if (pawn.health?.hediffSet == null)
            {
                return 0;
            }

            int count = 0;
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i].def.spawnThingOnRemoved == product)
                {
                    count++;
                }
            }

            return count;
        }
    }

    [HarmonyPatchCategory(Features.InstalledPartsCount)]
    [HarmonyPatch(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountProducts))]
    internal static class RecipeWorkerCounter_CountProducts_InstalledParts_Patch
    {
        private static void Postfix(RecipeWorkerCounter __instance, Bill_Production bill, ref int __result)
        {
            if (!bill.includeEquipped || __instance.recipe?.products.NullOrEmpty() != false)
            {
                return;
            }

            ThingDef product = __instance.recipe.products[0].thingDef;
            if (InstalledPartsCountUtility.IsBodyPart(product))
            {
                __result += InstalledPartsCountUtility.CountInstalled(bill, product);
            }
        }
    }

    // 바닐라는 무기·옷을 만드는 지시서에만 "장착 중인 것 포함"을 보여 준다. 신체 부위에도 보여 준다.
    [HarmonyPatchCategory(Features.InstalledPartsCount)]
    [HarmonyPatch(typeof(Dialog_BillConfig), nameof(Dialog_BillConfig.DoWindowContents))]
    internal static class Dialog_BillConfig_DoWindowContents_InstalledParts_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo isWeapon = AccessTools.PropertyGetter(typeof(ThingDef), nameof(ThingDef.IsWeapon));
            MethodInfo replacement = AccessTools.Method(typeof(Dialog_BillConfig_DoWindowContents_InstalledParts_Patch), nameof(IsWeaponOrBodyPart));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(isWeapon))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException("ThingDef.IsWeapon call not found in Dialog_BillConfig.DoWindowContents");
            }
        }

        private static bool IsWeaponOrBodyPart(ThingDef def)
        {
            return def.IsWeapon || InstalledPartsCountUtility.IsBodyPart(def);
        }
    }
}

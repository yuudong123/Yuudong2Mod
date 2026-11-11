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
    // 제작 지시서의 "장착 중인 것 포함"이 정착민이 어디에 있든(다른 맵, 캐러밴, 비행 중인 중력부양선, 퀘스트로 빌려준 정착민) 센다.
    // - 무기·옷·소지품: 바닐라는 지시서가 있는 맵에 서 있는 정착민 것만 센다. 나머지 정착민 것을 더한다.
    //   BWM "자리 비운 정착민 것도 세기"는 중력부양선이 내린 맵을 게임이 정착지로 봐서 그 맵에 내린 정착민을 빼먹으므로 이걸로 대신한다.
    // - 신체 부위(생체공학·EPOE 부품 등): 모든 정착민 몸에 이식된 같은 부품을 센다. 바닐라는 몸에 붙은 부품을 아예 세지 않는다.
    //   타고난 장기는 상태 이상이 아니어서 세지 않는다. 몸에 붙은 부품에는 품질·내구도 필터를 적용할 수 없어 필터와 상관없이 센다.
    // 동물·메카노이드 등은 BWM 설정대로 이 맵의 것만 센다.
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

        private static readonly HashSet<Pawn> Colonists = new HashSet<Pawn>();
        private static readonly HashSet<ThingDef> Products = new HashSet<ThingDef>();

        public static int CountEquippedEverywhere(RecipeWorkerCounter counter, Bill_Production bill)
        {
            Map map = bill.Map;
            ThingDef mainProduct = counter.recipe.products[0].thingDef;
            if (map == null)
            {
                return 0;
            }

            // BWM 추가 제품 필터로 여러 종류를 함께 세는 지시서는 그 종류도 센다.
            Products.Clear();
            Products.Add(mainProduct);
            Colonists.Clear();
            Colonists.UnionWith(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists);
            bool bwm = Yuudong2Mod.AppliedFeatures.Contains(Features.BetterWorkbench);
            if (bwm)
            {
                ThingFilter extra = Main.Instance.GetExtendedBillDataStorage().GetExtendedDataFor(bill)?.ProductAdditionalFilter;
                if (extra != null)
                {
                    Products.UnionWith(extra.AllowedThingDefs);
                }

                // 월드 폰 목록에 있어 위 목록에 안 잡히는 정착민(퀘스트로 빌려준 정착민 등)
                List<Pawn> away = AwayPawnRegistry.PawnsAwayFrom(map);
                for (int i = 0; i < away.Count; i++)
                {
                    if (away[i].IsFreeColonist)
                    {
                        Colonists.Add(away[i]);
                    }
                }
            }

            int count = 0;
            foreach (ThingDef product in Products)
            {
                bool bodyPart = IsBodyPart(product);
                foreach (Pawn pawn in Colonists)
                {
                    // 이 맵에 서 있는 정착민의 무기·옷·소지품은 바닐라와 BWM이 이미 셌다.
                    if (!(pawn.Spawned && pawn.Map == map))
                    {
                        count += CountCarried(pawn, counter, bill, product);
                    }

                    if (bodyPart)
                    {
                        count += CountIn(pawn, product);
                    }
                }

                if (bodyPart && bwm && Main.Instance.ShouldCountCarriedByNonHumans())
                {
                    count += CountIn(map.mapPawns.SpawnedColonyAnimals, product);
                    count += CountIn(map.mapPawns.SpawnedColonyMechs, product);
                    count += CountIn(map.mapPawns.SpawnedColonySubhumansPlayerControlled, product);
                }
            }

            return count;
        }

        // 바닐라 "장착 중인 것 포함"과 같은 범위: 든 무기, 입은 옷, 소지품.
        private static int CountCarried(Pawn pawn, RecipeWorkerCounter counter, Bill_Production bill, ThingDef product)
        {
            int count = 0;
            if (pawn.equipment != null)
            {
                count += CountValid(pawn.equipment.AllEquipmentListForReading, counter, bill, product);
            }

            if (pawn.apparel != null)
            {
                count += CountValid(pawn.apparel.WornApparel, counter, bill, product);
            }

            if (pawn.inventory != null)
            {
                ThingOwner held = pawn.inventory.innerContainer;
                for (int i = 0; i < held.Count; i++)
                {
                    Thing thing = held[i].GetInnerIfMinified();
                    if (counter.CountValidThing(thing, bill, product))
                    {
                        count += thing.stackCount;
                    }
                }
            }

            return count;
        }

        private static int CountValid<T>(List<T> things, RecipeWorkerCounter counter, Bill_Production bill, ThingDef product) where T : Thing
        {
            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                if (counter.CountValidThing(things[i], bill, product))
                {
                    count += things[i].stackCount;
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

            __result += InstalledPartsCountUtility.CountEquippedEverywhere(__instance, bill);
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

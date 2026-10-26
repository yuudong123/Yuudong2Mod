using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    [HarmonyPatchCategory(Features.CaravanArrivalSpot)]
    [HarmonyPatch(typeof(IncidentWorker_TraderCaravanArrival), "TryResolveParmsGeneral")]
    internal static class IncidentWorker_TraderCaravanArrival_TryResolveParmsGeneral_Patch
    {
        private static void Prefix(IncidentParms parms)
        {
            if (parms.spawnCenter.IsValid || !(parms.target is Map map))
            {
                return;
            }

            if (ArrivalSpotUtility.TryFindCaravanEntryCell(map, out IntVec3 entryCell))
            {
                parms.spawnCenter = entryCell;
            }
        }
    }

    [HarmonyPatchCategory(Features.CaravanArrivalSpot)]
    [HarmonyPatch(typeof(IncidentWorker_TraderCaravanArrival), "TryExecuteWorker")]
    internal static class IncidentWorker_TraderCaravanArrival_TryExecuteWorker_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomSpotJustOutsideColony),
                new[] { typeof(IntVec3), typeof(Map), typeof(Pawn), typeof(IntVec3).MakeByRefType(), typeof(Predicate<IntVec3>) });
            MethodInfo replacement = AccessTools.Method(typeof(ArrivalSpotUtility), nameof(ArrivalSpotUtility.TryFindCaravanChillSpot));
            return ArrivalSpotTranspilerUtility.ReplaceCall(instructions, original, replacement, "IncidentWorker_TraderCaravanArrival.TryExecuteWorker");
        }
    }

    // NewRatkinPlus 유랑단은 바닐라 상단 인시던트 대신 자체 인시던트를 써서 위 패치가 걸리지 않는다. 같은 방식으로 맞춘다.
    [HarmonyPatchCategory(Features.CaravanArrivalSpot)]
    [HarmonyPatch]
    internal static class RatkinWanderingTrader_TryExecuteWorker_Patch
    {
        private static MethodBase target;

        private static bool Prepare()
        {
            target = AccessTools.Method(AccessTools.TypeByName("NewRatkin.IncidentWorker_RatkinWanderingTrader"), "TryExecuteWorker");
            return target != null;
        }

        private static MethodBase TargetMethod()
        {
            return target;
        }

        private static void Prefix(IncidentParms parms)
        {
            if (parms.spawnCenter.IsValid || !(parms.target is Map map))
            {
                return;
            }

            if (ArrivalSpotUtility.TryFindCaravanEntryCell(map, out IntVec3 entryCell))
            {
                parms.spawnCenter = entryCell;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomSpotJustOutsideColony),
                new[] { typeof(IntVec3), typeof(Map), typeof(Pawn), typeof(IntVec3).MakeByRefType(), typeof(Predicate<IntVec3>) });
            MethodInfo replacement = AccessTools.Method(typeof(ArrivalSpotUtility), nameof(ArrivalSpotUtility.TryFindCaravanChillSpot));
            return ArrivalSpotTranspilerUtility.ReplaceCall(instructions, original, replacement, "NewRatkin.IncidentWorker_RatkinWanderingTrader.TryExecuteWorker");
        }
    }

    // 물자 낙하 지점: 궤도 상선 구매품, 퀘스트 보상, 왕족 지원 물자, 귀환하는 정착민 등
    // 바닐라가 "교역 낙하 지점"(DropCellFinder.TradeDropSpot)을 쓰는 모든 낙하를 이 지점 근처로 보낸다.
    [HarmonyPatchCategory(Features.TradeShipDropSpot)]
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.TradeDropSpot))]
    internal static class DropCellFinder_TradeDropSpot_Patch
    {
        private static bool Prefix(Map map, ref IntVec3 __result)
        {
            if (ArrivalSpotUtility.SuppressDropSpot > 0 || !ArrivalSpotUtility.TryFindDropSpotCell(map, out IntVec3 cell))
            {
                return true;
            }

            __result = cell;
            return false;
        }
    }

    // 적 중앙 낙하 공습도 교역 낙하 지점을 고를 때가 있는데, 이건 바닐라 그대로 둔다.
    [HarmonyPatchCategory(Features.TradeShipDropSpot)]
    [HarmonyPatch(typeof(PawnsArrivalModeWorker_CenterDrop), nameof(PawnsArrivalModeWorker_CenterDrop.TryResolveRaidSpawnCenter))]
    internal static class PawnsArrivalModeWorker_CenterDrop_TryResolveRaidSpawnCenter_Patch
    {
        private static void Prefix()
        {
            ArrivalSpotUtility.SuppressDropSpot++;
        }

        private static void Finalizer()
        {
            ArrivalSpotUtility.SuppressDropSpot--;
        }
    }

    // 교역 낙하 지점을 쓰지 않는 퀘스트 보상(지연 보상, 정체불명 화물 등)도 무작위 위치 대신 이 지점 근처로 떨어뜨린다.
    // 퀘스트가 위치를 직접 정한 경우(dropSpot)는 그대로 둔다.
    [HarmonyPatchCategory(Features.TradeShipDropSpot)]
    [HarmonyPatch(typeof(QuestPart_DropPods), "GetRandomDropSpot")]
    internal static class QuestPart_DropPods_GetRandomDropSpot_Patch
    {
        private static bool Prefix(QuestPart_DropPods __instance, ref IntVec3 __result)
        {
            if (!ArrivalSpotUtility.TryFindDropSpotCell(__instance.mapParent?.Map, out IntVec3 cell))
            {
                return true;
            }

            __result = cell;
            return false;
        }
    }

    internal static class ArrivalSpotTranspilerUtility
    {
        public static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, MethodInfo original, MethodInfo replacement, string target)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand as MethodInfo == original)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }

                yield return instruction;
            }

            if (replaced == 0)
            {
                Log.Warning("[Yuudong2Mod] " + target + "에서 " + original.Name + " 호출을 찾지 못해 지정 지점 패치를 적용하지 못했습니다.");
            }
        }
    }
}

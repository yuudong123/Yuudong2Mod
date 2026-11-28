using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 작업대 재료를 작업대 옆에 내려놓는 단계(바닐라 CollectIngredientsToils, 원자 연료 보충)는 고른 칸에 못 놓으면 칸 고르기로 되돌아간다.
    // 칸 고르기는 그 칸이 왜 막혔는지 모른 채(한도보다 많이 쌓인 더미 등) 같은 칸을 다시 고르고, 이것이 같은 틱 안에서 끝없이 되풀이되어 스택 오버플로로 게임이 꺼진다.
    // 같은 틱에 같은 물건으로 다시 칸을 고르면 앞서 고른 칸은 못 놓은 칸이므로 건너뛰고, 놓을 칸이 남지 않으면 그 작업을 그만둔다.
    public static class IngredientPlaceFixUtility
    {
        private const int MaxRetries = 40;

        // 칸 하나에 못 놓아 다시 고르는 것은 흔하다(더미가 꽉 참). 같은 칸을 이만큼 되풀이하면 무엇이 막는지 한 번 남긴다.
        private const int ReportAfter = 3;

        private static readonly Func<Thing, IEnumerable<IntVec3>> PlaceCells =
            AccessTools.MethodDelegate<Func<Thing, IEnumerable<IntVec3>>>(AccessTools.Method(typeof(Toils_JobTransforms), "IngredientPlaceCellsInOrder"));

        // 바닐라가 못 놓은 칸을 다시 골라 대신 다른 칸을 고른 횟수. 게임 테스트가 본다.
        public static int Interventions;

        private static int lastTick = -1;
        private static Pawn lastPawn;
        private static Thing lastThing;
        private static IntVec3 lastCell = IntVec3.Invalid;
        private static readonly HashSet<IntVec3> failed = new HashSet<IntVec3>();

        public static Action Wrap(Toil toil, Action original, TargetIndex facilityInd, TargetIndex carryItemInd, TargetIndex cellTargetInd)
        {
            return () => Choose(toil, original, facilityInd, carryItemInd, cellTargetInd);
        }

        private static void Choose(Toil toil, Action original, TargetIndex facilityInd, TargetIndex carryItemInd, TargetIndex cellTargetInd)
        {
            Pawn actor = toil.actor;
            Job job = actor.CurJob;
            Thing thing = job.GetTarget(carryItemInd).Thing;
            int tick = Find.TickManager.TicksGame;
            bool retry = tick == lastTick && actor == lastPawn && thing == lastThing && lastCell.IsValid;
            if (!retry)
            {
                failed.Clear();
            }
            else
            {
                failed.Add(lastCell);
            }
            lastTick = tick;
            lastPawn = actor;
            lastThing = thing;
            lastCell = IntVec3.Invalid;

            original();
            IntVec3 cell = job.GetTarget(cellTargetInd).Cell;
            if (failed.Count == ReportAfter && failed.Contains(cell))
            {
                Report(actor, job, thing, cell);
            }
            if (!retry || !failed.Contains(cell))
            {
                lastCell = cell;
                return;
            }

            Interventions++;
            if (failed.Count <= MaxRetries && thing != null && actor.Map != null)
            {
                foreach (IntVec3 c in PlaceCells(job.GetTarget(facilityInd).Thing))
                {
                    if (!failed.Contains(c) && GenSpawn.CanSpawnAt(thing.def, c, actor.Map) && HasRoom(c, thing, actor.Map))
                    {
                        job.SetTarget(cellTargetInd, c);
                        lastCell = c;
                        return;
                    }
                }
            }

            failed.Clear();
            actor.jobs.EndCurrentJob(JobCondition.Incompletable);
        }

        private static void Report(Pawn actor, Job job, Thing thing, IntVec3 cell)
        {
            Map map = actor.Map;
            string things = map == null ? "-" : string.Join(", ", map.thingGrid.ThingsListAt(cell).Select(t => $"{t} ({t.def.category}, {t.stackCount}/{t.def.stackLimit}, stacks with carried: {t.CanStackWith(thing)})"));
            Building edifice = map == null ? null : cell.GetEdifice(map);
            Log.Warning($"[Yuudong2Mod] 작업대 재료 내려놓기: {actor} ({job}) carrying {actor.carryTracker.CarriedThing} ({actor.carryTracker.CarriedThing?.stackCount}/{actor.carryTracker.CarriedThing?.def.stackLimit}), target {thing}, "
                + $"keeps failing at {cell}: edifice {edifice?.ToString() ?? "none"} (max items {(map == null ? 0 : cell.GetMaxItemsAllowedInCell(map))}), things [{things}]. Skipping that cell.");
        }

        // 바닐라 칸 고르기와 같은 기준이되, 한도를 넘겨 쌓인 더미도 꽉 찬 것으로 본다.
        private static bool HasRoom(IntVec3 c, Thing thing, Map map)
        {
            if (c.GetMaxItemsAllowedInCell(map) <= 0)
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAt(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].def.category == ThingCategory.Item && (!list[i].CanStackWith(thing) || list[i].stackCount >= list[i].def.stackLimit))
                {
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatchCategory(Features.IngredientPlaceFix)]
    [HarmonyPatch(typeof(Toils_JobTransforms), nameof(Toils_JobTransforms.SetTargetToIngredientPlaceCell))]
    internal static class Toils_JobTransforms_SetTargetToIngredientPlaceCell_Patch
    {
        private static void Postfix(Toil __result, TargetIndex facilityInd, TargetIndex carryItemInd, TargetIndex cellTargetInd)
        {
            if (__result?.initAction != null)
            {
                __result.initAction = IngredientPlaceFixUtility.Wrap(__result, __result.initAction, facilityInd, carryItemInd, cellTargetInd);
            }
        }
    }
}

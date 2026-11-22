using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Yuudong2Mod
{
    [DefOf]
    public static class MultiHaulDefOf
    {
        public static JobDef Yuudong2_HaulBatch;

        public static JobDef Yuudong2_UnloadHauled;

        static MultiHaulDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(MultiHaulDefOf));
        }
    }

    // 저장 구역으로 물건을 옮길 때, 첫 물건을 손에 든 뒤 근처의 다른 옮길 물건을 들 수 있는 무게까지 소지품에 담아 같이 나른다.
    // 더 담을 물건은 일을 실제로 시작한 뒤 한 번만 찾는다. 작업 탐색 단계에서는 일 종류만 바꿔서 비용이 붙지 않는다.
    public static class MultiHaulUtility
    {
        public const float SearchRadius = 12f;

        public const int MaxExtras = 24;

        private const float MinFreeMass = 0.05f;

        public static bool CanBatch(Pawn pawn)
        {
            return pawn.Faction == Faction.OfPlayer && pawn.inventory != null && (pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid)
                && MassUtility.CanEverCarryAnything(pawn) && !pawn.IsQuestLodger();
        }

        public static bool Usable(Thing thing, Pawn pawn)
        {
            return thing != null && thing.Spawned && thing.Map == pawn.Map && !thing.IsForbidden(pawn);
        }

        // 소지품에 더 담을 수 있는 무게. 입은 옷과 든 무기는 빼고 소지품 무게만 운반 한도에서 뺀다.
        // 맵 안에서는 무게가 이동 속도에 영향을 주지 않는데, 갑옷과 무기만으로 한도가 거의 차는 정착민(작은 종족 등)은
        // 바닐라 여유 무게로 세면 아무것도 더 담지 못해 한 번에 한 묶음씩만 나르게 된다.
        public static float FreeMass(Pawn pawn)
        {
            return Mathf.Max(MassUtility.Capacity(pawn) - MassUtility.InventoryMass(pawn), 0f);
        }

        public static int CountThatFits(Thing thing, float freeMass)
        {
            float unitMass = thing.GetStatValue(StatDefOf.Mass);
            return unitMass <= 0f ? thing.stackCount : Mathf.Min(thing.stackCount, Mathf.FloorToInt(freeMass / unitMass));
        }

        // pawn이 서 있는 곳(첫 물건을 집은 자리) 둘레를 가까운 칸부터 훑어, 같은 방에 있고 더 좋은 저장 칸이 있는 물건을 고른다.
        // 고른 물건과 그 저장 칸은 이 일로 예약해 두어, 저장 공간보다 많이 줍거나 다른 정착민과 겹치지 않게 한다.
        public static void PlanExtras(Pawn pawn, Job job, List<Thing> extras, List<int> counts)
        {
            float free = FreeMass(pawn);
            if (free < MinFreeMass)
            {
                return;
            }
            Map map = pawn.Map;
            Room room = pawn.GetRoom();
            ICollection<Thing> haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            Thing carried = pawn.carryTracker.CarriedThing;
            int cells = GenRadial.NumCellsInRadius(SearchRadius);
            for (int i = 0; i < cells; i++)
            {
                IntVec3 cell = pawn.Position + GenRadial.RadialPattern[i];
                if (!cell.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = cell.GetThingList(map);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing thing = things[j];
                    if (thing.def.category != ThingCategory.Item || thing is Corpse || thing == carried || !haulables.Contains(thing))
                    {
                        continue;
                    }
                    int count = CountThatFits(thing, free);
                    if (count <= 0 || thing.IsForbidden(pawn) || thing.GetRoom() != room || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false))
                    {
                        continue;
                    }
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out IntVec3 storeCell))
                    {
                        continue;
                    }
                    if (!pawn.Reserve(thing, job, 1, -1, null, false))
                    {
                        continue;
                    }
                    pawn.Reserve(storeCell, job, 1, -1, null, false);
                    extras.Add(thing);
                    counts.Add(count);
                    free -= count * thing.GetStatValue(StatDefOf.Mass);
                    if (extras.Count >= MaxExtras || free < MinFreeMass)
                    {
                        return;
                    }
                }
            }
        }

        public static Job UnloadJob(List<Thing> hauled)
        {
            Job job = JobMaker.MakeJob(MultiHaulDefOf.Yuudong2_UnloadHauled);
            job.targetQueueA = new List<LocalTargetInfo>();
            if (hauled != null)
            {
                foreach (Thing thing in hauled)
                {
                    job.targetQueueA.Add(thing);
                }
            }
            return job;
        }

        // 일이 끊겨 소지품에 남은 물건은 바닐라의 소지품 비우기 표시로 넘긴다. 정착민은 생각 트리가, 메카노이드는 다음 운반 때 우리 일이 처리한다.
        public static void FlagLeftovers(Pawn pawn, List<Thing> hauled)
        {
            if (pawn.inventory == null || hauled == null)
            {
                return;
            }
            for (int i = 0; i < hauled.Count; i++)
            {
                if (hauled[i] != null && pawn.inventory.innerContainer.Contains(hauled[i]))
                {
                    pawn.inventory.UnloadEverything = true;
                    return;
                }
            }
        }
    }

    public class JobDriver_HaulBatch : JobDriver_HaulToCell
    {
        private const string CarryToilName = "CarryHauledThingToCell";

        private List<Thing> extras = new List<Thing>();

        private List<int> extraCounts = new List<int>();

        private List<Thing> taken = new List<Thing>();

        private Thing current;

        private int currentCount;

        private bool unloadQueued;

        public int TakenCount => taken.Count;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref extras, "yuudong2Extras", LookMode.Reference);
            Scribe_Collections.Look(ref extraCounts, "yuudong2ExtraCounts", LookMode.Value);
            Scribe_Collections.Look(ref taken, "yuudong2Taken", LookMode.Reference);
            Scribe_References.Look(ref current, "yuudong2Current");
            Scribe_Values.Look(ref currentCount, "yuudong2CurrentCount");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                extras = extras ?? new List<Thing>();
                extraCounts = extraCounts ?? new List<int>();
                taken = taken ?? new List<Thing>();
                if (extraCounts.Count != extras.Count)
                {
                    extras.Clear();
                    extraCounts.Clear();
                }
            }
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(delegate
            {
                if (!unloadQueued)
                {
                    MultiHaulUtility.FlagLeftovers(pawn, taken);
                }
            });

            bool inserted = false;
            foreach (Toil toil in base.MakeNewToils())
            {
                if (!inserted && toil.debugName == CarryToilName)
                {
                    inserted = true;
                    foreach (Toil extraToil in ExtraToils())
                    {
                        yield return extraToil;
                    }
                }
                yield return toil;
            }
            if (!inserted)
            {
                Log.ErrorOnce($"[Yuudong2Mod] 여러 개 운반: 바닐라 운반 일에서 '{CarryToilName}' 단계를 찾지 못해 한 개씩만 옮깁니다. JobDriver_HaulToCell이 바뀌었는지 확인하세요.", 73920411);
            }

            Toil queueUnload = ToilMaker.MakeToil("QueueUnload");
            queueUnload.initAction = QueueUnload;
            queueUnload.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return queueUnload;
        }

        private IEnumerable<Toil> ExtraToils()
        {
            Toil plan = ToilMaker.MakeToil("PlanExtras");
            plan.initAction = delegate
            {
                extras.Clear();
                extraCounts.Clear();
                if (pawn.carryTracker.CarriedThing != null)
                {
                    MultiHaulUtility.PlanExtras(pawn, job, extras, extraCounts);
                }
            };
            plan.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return plan;

            Toil next = ToilMaker.MakeToil("NextExtra");
            next.initAction = delegate
            {
                current = null;
                while (extras.Count > 0 && current == null)
                {
                    Thing thing = extras[0];
                    currentCount = extraCounts[0];
                    extras.RemoveAt(0);
                    extraCounts.RemoveAt(0);
                    if (MultiHaulUtility.Usable(thing, pawn))
                    {
                        current = thing;
                    }
                }
            };
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return next;

            Toil done = Toils_General.Label();
            yield return Toils_Jump.JumpIf(done, () => current == null);

            Toil walk = ToilMaker.MakeToil("GotoExtra");
            walk.initAction = delegate
            {
                pawn.pather.StartPath(current, PathEndMode.ClosestTouch);
            };
            walk.tickAction = delegate
            {
                if (!MultiHaulUtility.Usable(current, pawn))
                {
                    pawn.pather.StopDead();
                    JumpToToil(next);
                }
            };
            walk.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            yield return walk;

            Toil take = ToilMaker.MakeToil("TakeExtra");
            take.initAction = TakeCurrent;
            take.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return take;

            yield return Toils_Jump.Jump(next);
            yield return done;
        }

        private void TakeCurrent()
        {
            Thing thing = current;
            current = null;
            if (!MultiHaulUtility.Usable(thing, pawn) || !pawn.CanReachImmediate(thing, PathEndMode.ClosestTouch))
            {
                return;
            }
            int count = Mathf.Min(currentCount, MultiHaulUtility.CountThatFits(thing, MultiHaulUtility.FreeMass(pawn)));
            if (count <= 0)
            {
                return;
            }
            Thing split = thing.SplitOff(count);
            if (pawn.inventory.innerContainer.TryAdd(split, false))
            {
                taken.Add(split);
                thing.def.soundPickup?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            }
            else if (!split.Spawned && !split.Destroyed)
            {
                GenPlace.TryPlaceThing(split, pawn.Position, pawn.Map, ThingPlaceMode.Near);
            }
        }

        private void QueueUnload()
        {
            taken.RemoveAll(x => x == null || !pawn.inventory.innerContainer.Contains(x));
            if (taken.Count == 0)
            {
                return;
            }
            pawn.jobs.jobQueue.EnqueueFirst(MultiHaulUtility.UnloadJob(taken), JobTag.Misc);
            unloadQueued = true;
        }
    }

    // 소지품에 담아 온 물건을 하나씩 손으로 옮겨 저장 칸에 내려놓는다. 넘겨받은 목록이 없으면 바닐라가 비워야 한다고 표시한 물건을 내려놓는다.
    public class JobDriver_UnloadHauled : JobDriver
    {
        private const TargetIndex ItemInd = TargetIndex.A;

        private const TargetIndex CellInd = TargetIndex.B;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(delegate
            {
                if (pawn.inventory == null || job.targetQueueA == null)
                {
                    return;
                }
                for (int i = 0; i < job.targetQueueA.Count; i++)
                {
                    Thing left = job.targetQueueA[i].Thing;
                    if (left != null && pawn.inventory.innerContainer.Contains(left))
                    {
                        pawn.inventory.UnloadEverything = true;
                        return;
                    }
                }
            });

            Toil next = ToilMaker.MakeToil("NextHauled");
            next.initAction = delegate
            {
                while (true)
                {
                    Thing thing = NextThing(out int count);
                    if (thing == null)
                    {
                        EndJobWith(JobCondition.Succeeded);
                        return;
                    }
                    if (StoreUtility.TryFindStoreCellNearColonyDesperate(thing, pawn, out IntVec3 cell) && pawn.Reserve(cell, job, 1, -1, null, false))
                    {
                        job.SetTarget(ItemInd, thing);
                        job.SetTarget(CellInd, cell);
                        job.count = count;
                        return;
                    }
                    if (!pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, count, out Thing _))
                    {
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                }
            };
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return next;

            yield return Toils_Goto.GotoCell(CellInd, PathEndMode.Touch);

            Toil toHands = ToilMaker.MakeToil("HauledToHands");
            toHands.initAction = delegate
            {
                Thing thing = job.GetTarget(ItemInd).Thing;
                if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
                {
                    JumpToToil(next);
                    return;
                }
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
                {
                    pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, job.count, out thing);
                    thing?.SetForbidden(false, false);
                    JumpToToil(next);
                    return;
                }
                pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer, job.count, out thing);
                if (thing == null)
                {
                    JumpToToil(next);
                    return;
                }
                job.SetTarget(ItemInd, thing);
                thing.SetForbidden(false, false);
            };
            toHands.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return toHands;

            Toil carryToCell = Toils_Haul.CarryHauledThingToCell(CellInd);
            yield return carryToCell;
            yield return Toils_Haul.PlaceHauledThingInCell(CellInd, carryToCell, true);

            Toil release = ToilMaker.MakeToil("ReleaseCell");
            release.initAction = delegate
            {
                LocalTargetInfo cell = job.GetTarget(CellInd);
                if (pawn.Map.reservationManager.ReservedBy(cell, pawn, job))
                {
                    pawn.Map.reservationManager.Release(cell, pawn, job);
                }
            };
            release.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return release;

            yield return Toils_Jump.Jump(next);
        }

        private Thing NextThing(out int count)
        {
            while (!job.targetQueueA.NullOrEmpty())
            {
                Thing thing = job.targetQueueA[0].Thing;
                job.targetQueueA.RemoveAt(0);
                if (thing != null && pawn.inventory.innerContainer.Contains(thing))
                {
                    count = thing.stackCount;
                    return thing;
                }
            }
            if (pawn.inventory.UnloadEverything)
            {
                ThingCount unloadable = pawn.inventory.FirstUnloadableThing;
                count = unloadable.Count;
                return unloadable.Thing;
            }
            count = 0;
            return null;
        }
    }

    [HarmonyPatchCategory(Features.MultiHaul)]
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob))]
    internal static class HaulAIUtility_HaulToStorageJob_MultiHaul_Patch
    {
        private static void Postfix(Pawn p, ref Job __result)
        {
            if (__result == null || __result.def != JobDefOf.HaulToCell || !MultiHaulUtility.CanBatch(p))
            {
                return;
            }
            // 메카노이드 생각 트리에는 소지품 비우기가 없어서, 끊긴 운반의 남은 물건은 다음 운반 일 대신 먼저 내려놓게 한다.
            if (p.RaceProps.IsMechanoid && p.inventory.UnloadEverything)
            {
                __result = MultiHaulUtility.UnloadJob(null);
                return;
            }
            __result.def = MultiHaulDefOf.Yuudong2_HaulBatch;
        }
    }
}

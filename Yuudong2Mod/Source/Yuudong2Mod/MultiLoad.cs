using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Yuudong2Mod
{
    // 수송 포드, 왕복선, 포탈에 짐을 실을 때 첫 물건을 손에 든 뒤, 근처의 다른 실을 물건을 들 수 있는 무게까지 소지품에 담아 한 번에 싣는다.
    // 바닐라 적재 일의 드라이버만 바꿔 끼우므로 일 종류와 "누가 얼마나 싣는 중인지" 세는 바닐라 계산은 그대로 쓴다.
    public static class MultiLoadUtility
    {
        public const float SearchRadius = 20f;

        private static readonly List<LoadBatch> batches = new List<LoadBatch>();

        private static readonly Dictionary<TransferableOneWay, int> tmpClaims = new Dictionary<TransferableOneWay, int>();

        private static Game batchesGame;

        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.MultiHaul);

        public static void ApplyDriverClasses()
        {
            Swap(JobDefOf.HaulToTransporter, typeof(JobDriver_HaulToTransporter), typeof(JobDriver_LoadBatchTransporter));
            Swap(JobDefOf.HaulToPortal, typeof(JobDriver_HaulToPortal), typeof(JobDriver_LoadBatchPortal));
        }

        private static void Swap(JobDef def, Type vanilla, Type batch)
        {
            if (def == null)
            {
                return;
            }
            if (def.driverClass == vanilla)
            {
                def.driverClass = batch;
            }
            else if (def.driverClass != batch)
            {
                Log.Warning($"[Yuudong2Mod] 여러 개 한 번에 적재: {def.defName}의 드라이버를 다른 모드가 {def.driverClass}(으)로 바꿔 두어서, 이 일은 한 개씩 싣습니다.");
            }
        }

        public static List<TransferableOneWay> LeftToLoad(Thing container)
        {
            return container is MapPortal portal ? portal.leftToLoad : container?.TryGetComp<CompTransporter>()?.leftToLoad;
        }

        public static TransferableOneWay Matching(Thing thing, List<TransferableOneWay> left)
        {
            return TransferableUtility.TransferableMatchingDesperate(thing, left, TransferAsOneMode.PodsOrCaravanPacking);
        }

        public static int Need(Thing container, Thing thing)
        {
            TransferableOneWay transferable = Matching(thing, LeftToLoad(container));
            return transferable == null ? 0 : Mathf.Min(transferable.CountToTransfer, thing.stackCount);
        }

        public static void Add(Dictionary<TransferableOneWay, int> claims, TransferableOneWay transferable, int count)
        {
            if (transferable != null && count > 0)
            {
                claims.TryGetValue(transferable, out int had);
                claims[transferable] = had + count;
            }
        }

        public static void Register(LoadBatch batch)
        {
            if (batchesGame != Current.Game)
            {
                batchesGame = Current.Game;
                batches.Clear();
            }
            if (!batches.Contains(batch))
            {
                batches.Add(batch);
            }
        }

        public static void Unregister(LoadBatch batch)
        {
            batches.Remove(batch);
        }

        private static void AddBatchClaims(Pawn except, Thing container, List<TransferableOneWay> left, Dictionary<TransferableOneWay, int> claims)
        {
            for (int i = batches.Count - 1; i >= 0; i--)
            {
                LoadBatch batch = batches[i];
                if (!batch.Running)
                {
                    batches.RemoveAt(i);
                }
                else if (batch.Pawn != except && batch.Container == container)
                {
                    batch.AddClaims(left, claims);
                }
            }
        }

        // 다른 정착민이 이 대상에 싣는 중이거나 실으려고 잡아 둔 수량. 바닐라가 세는 손에 든 몫과 우리가 소지품에 담는 몫을 합친다.
        public static Dictionary<TransferableOneWay, int> ClaimsByOthers(Pawn pawn, Thing container, List<TransferableOneWay> left)
        {
            tmpClaims.Clear();
            IReadOnlyList<Pawn> pawns = container.Map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn other = pawns[i];
                if (other == pawn || !(other.jobs?.curDriver is JobDriver_HaulToContainer driver) || driver.job == null || driver.job.GetTarget(TargetIndex.B).Thing != container)
                {
                    continue;
                }
                int inHand = driver is JobDriver_HaulToTransporter toTransporter ? toTransporter.initialCount
                    : driver is JobDriver_HaulToPortal toPortal ? toPortal.initialCount : 0;
                Add(tmpClaims, Matching(driver.job.GetTarget(TargetIndex.A).Thing, left), inHand);
            }
            AddBatchClaims(pawn, container, left, tmpClaims);
            return tmpClaims;
        }

        // 바닐라가 고른 물건이 다른 정착민의 소지품 몫으로 이미 채워질 예정이면 수량을 줄이거나 다른 물건으로 바꾼다.
        public static void Adjust(Pawn pawn, Thing container, ref ThingCount result)
        {
            if (batches.Count == 0 || container == null || result.Thing == null || result.Thing is Pawn)
            {
                return;
            }
            List<TransferableOneWay> left = LeftToLoad(container);
            if (left == null)
            {
                return;
            }
            Thing thing = result.Thing;
            TransferableOneWay transferable = null;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].things.Contains(thing))
                {
                    transferable = left[i];
                    break;
                }
            }
            if (transferable == null)
            {
                return;
            }
            tmpClaims.Clear();
            AddBatchClaims(pawn, container, left, tmpClaims);
            if (!tmpClaims.ContainsKey(transferable))
            {
                return;
            }
            Dictionary<TransferableOneWay, int> claims = ClaimsByOthers(pawn, container, left);
            claims.TryGetValue(transferable, out int claimed);
            int remaining = transferable.CountToTransfer - claimed;
            if (remaining >= result.Count)
            {
                return;
            }
            result = remaining > 0 ? new ThingCount(thing, remaining) : Alternative(pawn, left, claims);
        }

        private static ThingCount Alternative(Pawn pawn, List<TransferableOneWay> left, Dictionary<TransferableOneWay, int> claims)
        {
            Thing best = null;
            int bestCount = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                TransferableOneWay transferable = left[i];
                claims.TryGetValue(transferable, out int claimed);
                int remaining = transferable.CountToTransfer - claimed;
                if (remaining <= 0)
                {
                    continue;
                }
                for (int j = 0; j < transferable.things.Count; j++)
                {
                    Thing thing = transferable.things[j];
                    if (thing == null || thing is Pawn || !thing.Spawned || thing.Map != pawn.Map || thing.IsForbidden(pawn))
                    {
                        continue;
                    }
                    int distance = thing.Position.DistanceToSquared(pawn.Position);
                    if (distance < bestDistance && pawn.carryTracker.AvailableStackSpace(thing.def) > 0 && pawn.CanReserveAndReach(thing, PathEndMode.Touch, pawn.NormalMaxDanger()))
                    {
                        best = thing;
                        bestCount = Mathf.Min(remaining, thing.stackCount);
                        bestDistance = distance;
                    }
                }
            }
            return best == null ? default(ThingCount) : new ThingCount(best, bestCount);
        }
    }

    public interface ILoadBatchDriver
    {
        LoadBatch Batch { get; }
    }

    // 적재 일 하나가 소지품으로 더 나르는 물건의 계획과 진행 상태.
    public class LoadBatch : IExposable
    {
        private const string CarryToilName = "CarryHauledThingToContainer";

        private const float MinFreeMass = 0.05f;

        private List<Thing> extras = new List<Thing>();

        private List<int> extraCounts = new List<int>();

        private List<Thing> taken = new List<Thing>();

        private Thing current;

        private int currentCount;

        private int deposited;

        private JobDriver_HaulToContainer driver;

        private static readonly List<KeyValuePair<Thing, TransferableOneWay>> tmpCandidates = new List<KeyValuePair<Thing, TransferableOneWay>>();

        public Pawn Pawn => driver?.pawn;

        public Thing Container => driver?.job?.GetTarget(TargetIndex.B).Thing;

        public bool Running => driver != null && driver.pawn?.jobs?.curDriver == driver;

        public int TakenCount => taken.Count;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref extras, "extras", LookMode.Reference);
            Scribe_Collections.Look(ref extraCounts, "extraCounts", LookMode.Value);
            Scribe_Collections.Look(ref taken, "taken", LookMode.Reference);
            Scribe_References.Look(ref current, "current");
            Scribe_Values.Look(ref currentCount, "currentCount");
            Scribe_Values.Look(ref deposited, "deposited");
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

        public void AddClaims(List<TransferableOneWay> left, Dictionary<TransferableOneWay, int> claims)
        {
            for (int i = 0; i < extras.Count; i++)
            {
                MultiLoadUtility.Add(claims, MultiLoadUtility.Matching(extras[i], left), extraCounts[i]);
            }
            if (current != null && !taken.Contains(current))
            {
                MultiLoadUtility.Add(claims, MultiLoadUtility.Matching(current, left), currentCount);
            }
            for (int i = deposited; i < taken.Count; i++)
            {
                if (taken[i] != null)
                {
                    MultiLoadUtility.Add(claims, MultiLoadUtility.Matching(taken[i], left), taken[i].stackCount);
                }
            }
        }

        public IEnumerable<Toil> Wrap(JobDriver_HaulToContainer owner, IEnumerable<Toil> vanilla, int depositTicks, Action<int> setInHandCount)
        {
            driver = owner;
            MultiLoadUtility.Register(this);
            owner.AddFinishAction(delegate
            {
                MultiLoadUtility.Unregister(this);
                MultiHaulUtility.FlagLeftovers(owner.pawn, taken);
            });

            bool inserted = false;
            foreach (Toil toil in vanilla)
            {
                if (!inserted && toil.debugName == CarryToilName)
                {
                    inserted = true;
                    foreach (Toil pickup in PickupToils())
                    {
                        yield return pickup;
                    }
                }
                yield return toil;
            }
            if (!inserted)
            {
                Log.ErrorOnce($"[Yuudong2Mod] 여러 개 한 번에 적재: 바닐라 적재 일에서 '{CarryToilName}' 단계를 찾지 못해 한 개씩만 싣습니다. JobDriver_HaulToContainer가 바뀌었는지 확인하세요.", 73920412);
            }
            foreach (Toil deposit in DepositToils(depositTicks, setInHandCount))
            {
                yield return deposit;
            }
        }

        private void Plan()
        {
            extras.Clear();
            extraCounts.Clear();
            Pawn pawn = Pawn;
            Thing container = Container;
            Thing carried = pawn.carryTracker.CarriedThing;
            if (!MultiLoadUtility.Active || carried == null || carried is Pawn || container == null || !MultiHaulUtility.CanBatch(pawn))
            {
                return;
            }
            List<TransferableOneWay> left = MultiLoadUtility.LeftToLoad(container);
            float free = MassUtility.FreeSpace(pawn);
            if (left.NullOrEmpty() || free < MinFreeMass)
            {
                return;
            }

            Dictionary<TransferableOneWay, int> claims = MultiLoadUtility.ClaimsByOthers(pawn, container, left);
            MultiLoadUtility.Add(claims, MultiLoadUtility.Matching(carried, left), carried.stackCount);
            tmpCandidates.Clear();
            for (int i = 0; i < left.Count; i++)
            {
                TransferableOneWay transferable = left[i];
                claims.TryGetValue(transferable, out int claimed);
                if (transferable.CountToTransfer - claimed <= 0)
                {
                    continue;
                }
                for (int j = 0; j < transferable.things.Count; j++)
                {
                    Thing thing = transferable.things[j];
                    if (thing != null && thing.def.category == ThingCategory.Item && thing.Spawned && thing.Map == pawn.Map
                        && thing.Position.InHorDistOf(pawn.Position, MultiLoadUtility.SearchRadius) && !thing.IsForbidden(pawn))
                    {
                        tmpCandidates.Add(new KeyValuePair<Thing, TransferableOneWay>(thing, transferable));
                    }
                }
            }
            IntVec3 from = pawn.Position;
            tmpCandidates.Sort((a, b) => a.Key.Position.DistanceToSquared(from).CompareTo(b.Key.Position.DistanceToSquared(from)));
            for (int i = 0; i < tmpCandidates.Count && extras.Count < MultiHaulUtility.MaxExtras && free >= MinFreeMass; i++)
            {
                Thing thing = tmpCandidates[i].Key;
                TransferableOneWay transferable = tmpCandidates[i].Value;
                claims.TryGetValue(transferable, out int claimed);
                int count = Mathf.Min(transferable.CountToTransfer - claimed, MultiHaulUtility.CountThatFits(thing, free));
                if (count <= 0 || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, pawn.NormalMaxDanger()) || !pawn.Reserve(thing, driver.job, 1, -1, null, false))
                {
                    continue;
                }
                extras.Add(thing);
                extraCounts.Add(count);
                MultiLoadUtility.Add(claims, transferable, count);
                free -= count * thing.GetStatValue(StatDefOf.Mass);
            }
            tmpCandidates.Clear();
        }

        private IEnumerable<Toil> PickupToils()
        {
            Toil plan = ToilMaker.MakeToil("PlanLoadExtras");
            plan.initAction = Plan;
            plan.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return plan;

            Toil next = ToilMaker.MakeToil("NextLoadExtra");
            next.initAction = delegate
            {
                current = null;
                while (extras.Count > 0 && current == null)
                {
                    Thing thing = extras[0];
                    currentCount = extraCounts[0];
                    extras.RemoveAt(0);
                    extraCounts.RemoveAt(0);
                    if (MultiHaulUtility.Usable(thing, Pawn))
                    {
                        current = thing;
                    }
                }
            };
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return next;

            Toil done = Toils_General.Label();
            yield return Toils_Jump.JumpIf(done, () => current == null);

            Toil walk = ToilMaker.MakeToil("GotoLoadExtra");
            walk.initAction = delegate
            {
                Pawn.pather.StartPath(current, PathEndMode.ClosestTouch);
            };
            walk.tickAction = delegate
            {
                if (!MultiHaulUtility.Usable(current, Pawn))
                {
                    Pawn.pather.StopDead();
                    driver.JumpToToil(next);
                }
            };
            walk.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            yield return walk;

            Toil take = ToilMaker.MakeToil("TakeLoadExtra");
            take.initAction = Take;
            take.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return take;

            yield return Toils_Jump.Jump(next);
            yield return done;
        }

        private void Take()
        {
            Pawn pawn = Pawn;
            Thing thing = current;
            current = null;
            if (!MultiHaulUtility.Usable(thing, pawn) || !pawn.CanReachImmediate(thing, PathEndMode.ClosestTouch))
            {
                return;
            }
            int count = Mathf.Min(currentCount, MultiHaulUtility.CountThatFits(thing, MassUtility.FreeSpace(pawn)));
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

        // 손에 든 물건을 실은 뒤, 소지품에 담아 온 물건을 아직 필요한 만큼만 차례로 싣는다.
        private IEnumerable<Toil> DepositToils(int depositTicks, Action<int> setInHandCount)
        {
            Toil next = ToilMaker.MakeToil("NextLoadDeposit");
            next.initAction = delegate
            {
                setInHandCount(0);
                current = null;
                for (; deposited < taken.Count; deposited++)
                {
                    Thing thing = taken[deposited];
                    if (thing != null && Pawn.inventory.innerContainer.Contains(thing) && MultiLoadUtility.Need(Container, thing) > 0)
                    {
                        current = thing;
                        return;
                    }
                }
            };
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return next;

            Toil done = Toils_General.Label();
            yield return Toils_Jump.JumpIf(done, () => current == null);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            yield return Toils_General.Wait(depositTicks, TargetIndex.B).WithProgressBarToilDelay(TargetIndex.B);

            Toil deposit = ToilMaker.MakeToil("DepositLoadExtra");
            deposit.initAction = Deposit;
            deposit.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deposit;

            yield return Toils_Jump.Jump(next);
            yield return done;
        }

        private void Deposit()
        {
            Pawn pawn = Pawn;
            Thing container = Container;
            Thing thing = current;
            current = null;
            deposited++;
            ThingOwner owner = container?.TryGetInnerInteractableThingOwner();
            if (thing == null || owner == null || !pawn.inventory.innerContainer.Contains(thing))
            {
                return;
            }
            int need = MultiLoadUtility.Need(container, thing);
            int moved = need > 0 ? pawn.inventory.innerContainer.TryTransferToContainer(thing, owner, need) : 0;
            if (moved == 0)
            {
                return;
            }
            (container as INotifyHauledTo)?.Notify_HauledTo(pawn, thing, moved);
            if (container is ThingWithComps withComps)
            {
                for (int i = 0; i < withComps.AllComps.Count; i++)
                {
                    (withComps.AllComps[i] as INotifyHauledTo)?.Notify_HauledTo(pawn, thing, moved);
                }
            }
        }
    }

    public class JobDriver_LoadBatchTransporter : JobDriver_HaulToTransporter, ILoadBatchDriver
    {
        private LoadBatch batch = new LoadBatch();

        public LoadBatch Batch => batch;

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit && batch == null)
            {
                batch = new LoadBatch();
            }
            base.ExposeData();
            Scribe_Deep.Look(ref batch, "yuudong2Batch");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            return batch.Wrap(this, base.MakeNewToils(), Duration, count => initialCount = count);
        }
    }

    public class JobDriver_LoadBatchPortal : JobDriver_HaulToPortal, ILoadBatchDriver
    {
        private LoadBatch batch = new LoadBatch();

        public LoadBatch Batch => batch;

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit && batch == null)
            {
                batch = new LoadBatch();
            }
            base.ExposeData();
            Scribe_Deep.Look(ref batch, "yuudong2Batch");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            return batch.Wrap(this, base.MakeNewToils(), Duration, count => initialCount = count);
        }
    }

    [HarmonyPatchCategory(Features.MultiHaul)]
    [HarmonyPatch(typeof(LoadTransportersJobUtility), nameof(LoadTransportersJobUtility.FindThingToLoad))]
    internal static class LoadTransportersJobUtility_FindThingToLoad_MultiLoad_Patch
    {
        private static void Postfix(Pawn p, CompTransporter transporter, ref ThingCount __result)
        {
            MultiLoadUtility.Adjust(p, transporter?.parent, ref __result);
        }
    }

    [HarmonyPatchCategory(Features.MultiHaul)]
    [HarmonyPatch(typeof(EnterPortalUtility), nameof(EnterPortalUtility.FindThingToLoad))]
    internal static class EnterPortalUtility_FindThingToLoad_MultiLoad_Patch
    {
        private static void Postfix(Pawn p, MapPortal portal, ref ThingCount __result)
        {
            MultiLoadUtility.Adjust(p, portal, ref __result);
        }
    }
}

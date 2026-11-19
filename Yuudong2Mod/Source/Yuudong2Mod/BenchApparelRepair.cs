using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    [DefOf]
    public static class ApparelRepairDefOf
    {
        public static JobDef Yuudong2_RepairApparel;

        static ApparelRepairDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ApparelRepairDefOf));
        }
    }

    [StaticConstructorOnStartup]
    internal static class ApparelRepairTextures
    {
        public static readonly Texture2D Eject = ContentFinder<Texture2D>.Get("UI/Commands/PodEject");
    }

    public static class ApparelRepairUtility
    {
        // 입은 옷의 내구도가 이 비율 밑으로 내려가면 수선하러 간다.
        public const float Threshold = 0.8f;

        // 옷의 제작 작업량 × 깎인 비율을 틱당 이만큼 줄인다.
        public const float WorkPerTick = 2f;

        private const float MinApparelRecipeShare = 0.75f;

        public static readonly List<ThingDef> BenchDefs = new List<ThingDef>();

        private static readonly List<ThingDefCountClass> tmpCost = new List<ThingDefCountClass>();

        private static readonly List<Apparel> tmpCandidates = new List<Apparel>();

        // 재봉대 종류: 만드는 물건이 대부분 옷인 작업대와, 이름에 tailor·sewing이 들어간 작업대(종족 모드의 재봉대).
        public static void ApplyDefPatches()
        {
            Dictionary<ThingDef, int> producing = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, int> apparel = new Dictionary<ThingDef, int>();
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (recipe.recipeUsers == null)
                {
                    continue;
                }
                for (int i = 0; i < recipe.recipeUsers.Count; i++)
                {
                    CountRecipe(recipe.recipeUsers[i], recipe, producing, apparel);
                }
            }

            BenchDefs.Clear();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.thingClass == null || !typeof(Building_WorkTable).IsAssignableFrom(def.thingClass) || !def.hasInteractionCell)
                {
                    continue;
                }
                if (def.recipes != null)
                {
                    for (int i = 0; i < def.recipes.Count; i++)
                    {
                        CountRecipe(def, def.recipes[i], producing, apparel);
                    }
                }

                producing.TryGetValue(def, out int total);
                apparel.TryGetValue(def, out int clothes);
                bool byRecipes = clothes > 0 && clothes >= total * MinApparelRecipeShare;
                bool byName = def.defName.IndexOf("tailor", StringComparison.OrdinalIgnoreCase) >= 0
                    || def.defName.IndexOf("sewing", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!byRecipes && !byName)
                {
                    continue;
                }

                def.comps ??= new List<CompProperties>();
                if (!def.HasComp(typeof(CompApparelRepair)))
                {
                    def.comps.Add(new CompProperties_ApparelRepair());
                }
                BenchDefs.Add(def);
            }
        }

        private static void CountRecipe(ThingDef bench, RecipeDef recipe, Dictionary<ThingDef, int> producing, Dictionary<ThingDef, int> apparel)
        {
            ThingDef product = recipe?.ProducedThingDef;
            if (bench == null || product == null)
            {
                return;
            }
            producing.TryGetValue(bench, out int total);
            producing[bench] = total + 1;
            if (product.IsApparel)
            {
                apparel.TryGetValue(bench, out int clothes);
                apparel[bench] = clothes + 1;
            }
        }

        public static bool IsExcludedMaterial(ThingDef def)
        {
            return def == ThingDefOf.ComponentIndustrial || def == ThingDefOf.ComponentSpacer;
        }

        public static float HitPointsFraction(Thing thing)
        {
            return thing.MaxHitPoints > 0 ? (float)thing.HitPoints / thing.MaxHitPoints : 1f;
        }

        // 제작 비용(재질 포함)에서 부품·고급 부품을 빼고, 깎인 비율만큼 재료마다 올림. 새 리스트를 돌려준다.
        public static List<ThingDefCountClass> RepairCost(Apparel apparel)
        {
            List<ThingDefCountClass> result = new List<ThingDefCountClass>();
            float missing = 1f - HitPointsFraction(apparel);
            if (missing <= 0f)
            {
                return result;
            }

            List<ThingDefCountClass> cost = apparel.def.CostListAdjusted(apparel.Stuff, errorOnNullStuff: false);
            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass entry = cost[i];
                if (entry.thingDef == null || entry.count <= 0 || IsExcludedMaterial(entry.thingDef))
                {
                    continue;
                }

                // 부동소수 오차로 2.0000001이 3이 되지 않게 조금 깎고 올린다.
                int count = Mathf.Max(1, Mathf.CeilToInt(entry.count * missing - 0.0001f));
                result.Add(new ThingDefCountClass(entry.thingDef, count));
            }
            return result;
        }

        // 수선 작업량. 제작 작업량이 없는 옷도 최소 시간은 걸리게 한다.
        public static float RepairWork(Apparel apparel)
        {
            float workToMake = apparel.def.GetStatValueAbstract(StatDefOf.WorkToMake, apparel.Stuff);
            return Mathf.Max(workToMake, 1000f) * (1f - HitPointsFraction(apparel));
        }

        public static int EquipTicks(Apparel apparel)
        {
            return Mathf.Max(30, Mathf.RoundToInt(apparel.GetStatValue(StatDefOf.EquipDelay) * GenTicks.TicksPerRealSecond));
        }

        public static bool NeedsRepair(Pawn pawn, Apparel apparel)
        {
            if (!apparel.def.useHitPoints || HitPointsFraction(apparel) >= Threshold)
            {
                return false;
            }
            if (pawn.apparel.IsLocked(apparel))
            {
                return false;
            }
            tmpCost.Clear();
            tmpCost.AddRange(apparel.def.CostListAdjusted(apparel.Stuff, errorOnNullStuff: false));
            for (int i = 0; i < tmpCost.Count; i++)
            {
                if (tmpCost[i].thingDef != null && tmpCost[i].count > 0 && !IsExcludedMaterial(tmpCost[i].thingDef))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool AnyNeedsRepair(Pawn pawn)
        {
            if (pawn.apparel == null)
            {
                return false;
            }
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (NeedsRepair(pawn, worn[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // 가장 많이 상한 옷부터, 쓸 수 있는 재료로 감당되는 옷만 고른다.
        // available은 재료별로 쓸 수 있는 양, need에는 고른 옷들의 재료 합계가 들어간다.
        public static List<Apparel> PlanRepair(Pawn pawn, Func<ThingDef, int> available, Dictionary<ThingDef, int> need)
        {
            need.Clear();
            List<Apparel> result = new List<Apparel>();
            if (pawn.apparel == null)
            {
                return result;
            }

            tmpCandidates.Clear();
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (NeedsRepair(pawn, worn[i]))
                {
                    tmpCandidates.Add(worn[i]);
                }
            }
            tmpCandidates.Sort((a, b) =>
            {
                int byHp = HitPointsFraction(a).CompareTo(HitPointsFraction(b));
                return byHp != 0 ? byHp : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });

            Dictionary<ThingDef, int> availableCache = new Dictionary<ThingDef, int>();
            for (int i = 0; i < tmpCandidates.Count; i++)
            {
                Apparel apparel = tmpCandidates[i];
                List<ThingDefCountClass> cost = RepairCost(apparel);
                if (cost.Count == 0)
                {
                    continue;
                }

                bool affordable = true;
                for (int j = 0; j < cost.Count; j++)
                {
                    ThingDef def = cost[j].thingDef;
                    if (!availableCache.TryGetValue(def, out int have))
                    {
                        have = available(def);
                        availableCache[def] = have;
                    }
                    need.TryGetValue(def, out int already);
                    if (already + cost[j].count > have)
                    {
                        affordable = false;
                        break;
                    }
                }
                if (!affordable)
                {
                    continue;
                }

                result.Add(apparel);
                for (int j = 0; j < cost.Count; j++)
                {
                    need.TryGetValue(cost[j].thingDef, out int already);
                    need[cost[j].thingDef] = already + cost[j].count;
                }
            }
            tmpCandidates.Clear();
            return result;
        }

        public static bool CanUseMaterial(Pawn pawn, Thing thing)
        {
            return thing.Spawned && !thing.IsForbidden(pawn) && pawn.CanReserve(thing)
                && pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Some);
        }

        // 이 정착민의 옷을 맡아 둔 재봉대.
        public static CompApparelRepair BenchHolding(Pawn pawn)
        {
            Map map = pawn.Map;
            for (int i = 0; i < BenchDefs.Count; i++)
            {
                List<Thing> benches = map.listerThings.ThingsOfDef(BenchDefs[i]);
                for (int j = 0; j < benches.Count; j++)
                {
                    CompApparelRepair comp = benches[j].TryGetComp<CompApparelRepair>();
                    if (comp != null && comp.HasClothesOf(pawn))
                    {
                        return comp;
                    }
                }
            }
            return null;
        }

        // 비어 있고 지금 쓸 수 있는 재봉대 중 갈 수 있는 가장 가까운 것.
        public static CompApparelRepair FindFreeBench(Pawn pawn)
        {
            Map map = pawn.Map;
            List<CompApparelRepair> candidates = new List<CompApparelRepair>();
            for (int i = 0; i < BenchDefs.Count; i++)
            {
                List<Thing> benches = map.listerThings.ThingsOfDef(BenchDefs[i]);
                for (int j = 0; j < benches.Count; j++)
                {
                    Thing bench = benches[j];
                    CompApparelRepair comp = bench.TryGetComp<CompApparelRepair>();
                    if (comp == null || bench.Faction != Faction.OfPlayer)
                    {
                        continue;
                    }
                    comp.ReleaseIfAbandoned();
                    if (comp.State == ApparelRepairState.Idle && comp.Usable && !bench.IsForbidden(pawn) && !bench.IsBurning())
                    {
                        candidates.Add(comp);
                    }
                }
            }

            IntVec3 root = pawn.Position;
            candidates.SortBy(c => c.parent.Position.DistanceToSquared(root));
            for (int i = 0; i < candidates.Count; i++)
            {
                if (pawn.CanReserveAndReach(candidates[i].parent, PathEndMode.InteractionCell, Danger.Some))
                {
                    return candidates[i];
                }
            }
            return null;
        }
    }

    public class CompProperties_ApparelRepair : CompProperties
    {
        public CompProperties_ApparelRepair()
        {
            compClass = typeof(CompApparelRepair);
        }
    }

    public enum ApparelRepairState
    {
        Idle,
        Working,
        Done,
    }

    // 재봉대가 수선 재료와 맡긴 옷을 들고 있게 한다. 수선은 맡긴 정착민이 재봉대 앞에서 일하는 동안 진행된다.
    public class CompApparelRepair : ThingComp, IThingHolder
    {
        private ThingOwner<Thing> innerContainer;

        private ApparelRepairState state = ApparelRepairState.Idle;

        private float workLeft;

        private float workTotal;

        private Pawn user;

        private List<Apparel> repairing = new List<Apparel>();

        private List<Apparel> forcedApparel = new List<Apparel>();

        private static readonly Dictionary<ThingDef, int> tmpNeed = new Dictionary<ThingDef, int>();

        // 옷을 맡겨 둔 동안 바닐라 옷 최적화가 다른 옷을 집어 입지 않게 미루는 여유 시간.
        private const int OptimizeDelayMargin = 2500;

        public CompApparelRepair()
        {
            innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        public ApparelRepairState State => state;

        public Pawn User => user;

        public float Progress => workTotal > 0f ? Mathf.Clamp01(1f - workLeft / workTotal) : 0f;

        public bool Usable => !(parent is IBillGiver giver) || giver.CurrentlyUsableForBills();

        public bool HasClothesOf(Pawn pawn)
        {
            return user == pawn && state != ApparelRepairState.Idle;
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return innerContainer;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref innerContainer, "yuudong2RepairContainer", this);
            Scribe_Values.Look(ref state, "yuudong2RepairState", ApparelRepairState.Idle);
            Scribe_Values.Look(ref workLeft, "yuudong2RepairWorkLeft", 0f);
            Scribe_Values.Look(ref workTotal, "yuudong2RepairWorkTotal", 0f);
            Scribe_References.Look(ref user, "yuudong2RepairUser");
            Scribe_Collections.Look(ref repairing, "yuudong2Repairing", LookMode.Reference);
            Scribe_Collections.Look(ref forcedApparel, "yuudong2RepairForced", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                innerContainer ??= new ThingOwner<Thing>(this, oneStackOnly: false);
                repairing ??= new List<Apparel>();
                forcedApparel ??= new List<Apparel>();
                repairing.RemoveAll(x => x == null);
                forcedApparel.RemoveAll(x => x == null);
                if (state != ApparelRepairState.Idle && (user == null || repairing.Count == 0))
                {
                    ResetToIdle();
                }
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (mode != DestroyMode.WillReplace)
            {
                EjectContents(map);
            }
        }

        public int CountContained(ThingDef def)
        {
            int count = 0;
            for (int i = 0; i < innerContainer.Count; i++)
            {
                Thing thing = innerContainer[i];
                if (thing.def == def && !(thing is Apparel apparel && repairing.Contains(apparel)))
                {
                    count += thing.stackCount;
                }
            }
            return count;
        }

        // 들어 있는 재료로 감당되는 옷을 벗겨 넣고 수선을 시작한다. 시작했으면 true.
        public bool TryStartRepair(Pawn pawn)
        {
            if (state != ApparelRepairState.Idle)
            {
                return false;
            }
            List<Apparel> plan = ApparelRepairUtility.PlanRepair(pawn, CountContained, tmpNeed);
            if (plan.Count == 0)
            {
                return false;
            }

            foreach (KeyValuePair<ThingDef, int> entry in tmpNeed)
            {
                ConsumeMaterial(entry.Key, entry.Value);
            }
            tmpNeed.Clear();

            // 남은 재료는 꺼내 두고, 옷만 넣는다.
            EjectContents(parent.MapHeld);

            repairing.Clear();
            forcedApparel.Clear();
            workTotal = 0f;
            for (int i = 0; i < plan.Count; i++)
            {
                Apparel apparel = plan[i];
                workTotal += ApparelRepairUtility.RepairWork(apparel);
                if (pawn.outfits?.forcedHandler != null && pawn.outfits.forcedHandler.IsForced(apparel))
                {
                    forcedApparel.Add(apparel);
                }
                pawn.apparel.Remove(apparel);
                if (!innerContainer.TryAdd(apparel, canMergeWithExistingStacks: false))
                {
                    GenPlace.TryPlaceThing(apparel, DropCell, parent.Map, ThingPlaceMode.Near);
                    continue;
                }
                repairing.Add(apparel);
            }
            if (repairing.Count == 0)
            {
                ResetToIdle();
                return false;
            }

            workLeft = workTotal;
            user = pawn;
            state = ApparelRepairState.Working;
            DelayApparelOptimize();
            return true;
        }

        // 정착민이 재봉대 앞에서 일한 만큼 진행한다. 전력이 끊긴 전기 재봉대는 멈춘다.
        public void DoWork(int ticks)
        {
            if (state != ApparelRepairState.Working || !Usable)
            {
                return;
            }
            DelayApparelOptimize();
            workLeft -= ticks * ApparelRepairUtility.WorkPerTick;
            if (workLeft <= 0f)
            {
                FinishRepair();
            }
        }

        private void DelayApparelOptimize()
        {
            if (user?.mindState == null)
            {
                return;
            }
            int until = Find.TickManager.TicksGame + Mathf.CeilToInt(workLeft / ApparelRepairUtility.WorkPerTick) + OptimizeDelayMargin;
            user.mindState.nextApparelOptimizeTick = Mathf.Max(user.mindState.nextApparelOptimizeTick, until);
        }

        private void ConsumeMaterial(ThingDef def, int count)
        {
            for (int i = innerContainer.Count - 1; i >= 0 && count > 0; i--)
            {
                Thing thing = innerContainer[i];
                if (thing.def != def)
                {
                    continue;
                }
                int take = Mathf.Min(count, thing.stackCount);
                Thing taken = innerContainer.Take(thing, take);
                taken.Destroy();
                count -= take;
            }
        }

        public void FinishRepair()
        {
            if (state != ApparelRepairState.Working)
            {
                return;
            }
            for (int i = 0; i < repairing.Count; i++)
            {
                repairing[i].HitPoints = repairing[i].MaxHitPoints;
            }
            workLeft = 0f;
            state = ApparelRepairState.Done;
        }

        // 다 고친 옷을 꺼내 입힌다. 못 입는 옷은 앞에 내려놓는다.
        public void RetrieveFor(Pawn pawn)
        {
            if (state != ApparelRepairState.Done || pawn != user)
            {
                return;
            }
            for (int i = 0; i < repairing.Count; i++)
            {
                Apparel apparel = repairing[i];
                if (!innerContainer.Contains(apparel))
                {
                    continue;
                }
                innerContainer.Remove(apparel);
                if (ApparelUtility.HasPartsToWear(pawn, apparel.def) && apparel.PawnCanWear(pawn, ignoreGender: true))
                {
                    pawn.apparel.Wear(apparel, dropReplacedApparel: true);
                    if (forcedApparel.Contains(apparel) && pawn.outfits?.forcedHandler != null)
                    {
                        pawn.outfits.forcedHandler.SetForced(apparel, forced: true);
                    }
                }
                else
                {
                    GenPlace.TryPlaceThing(apparel, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                }
            }
            ResetToIdle();
        }

        // 맡긴 정착민이 죽었거나 이 맵에 없으면 옷을 꺼내 재봉대를 비운다.
        public void ReleaseIfAbandoned()
        {
            if (state != ApparelRepairState.Idle && (user == null || user.Dead || user.Destroyed || user.MapHeld != parent.MapHeld))
            {
                EjectContents(parent.MapHeld);
            }
        }

        private IntVec3 DropCell => parent.Spawned && parent.def.hasInteractionCell ? parent.InteractionCell : parent.PositionHeld;

        // 내용물을 모두 앞에 꺼낸다. 수선 중이던 옷은 진행한 만큼만 고쳐진다.
        public void EjectContents(Map map)
        {
            if (state == ApparelRepairState.Working)
            {
                float progress = Progress;
                for (int i = 0; i < repairing.Count; i++)
                {
                    Apparel apparel = repairing[i];
                    apparel.HitPoints = Mathf.Min(apparel.MaxHitPoints, apparel.HitPoints + Mathf.FloorToInt((apparel.MaxHitPoints - apparel.HitPoints) * progress));
                }
            }
            if (innerContainer.Count > 0 && map != null)
            {
                innerContainer.TryDropAll(DropCell, map, ThingPlaceMode.Near);
            }
            if (state != ApparelRepairState.Idle)
            {
                ResetToIdle();
            }
        }

        private void ResetToIdle()
        {
            state = ApparelRepairState.Idle;
            user = null;
            repairing.Clear();
            forcedApparel.Clear();
            workLeft = 0f;
            workTotal = 0f;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }
            if (parent.Faction != Faction.OfPlayer || innerContainer.Count == 0)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "Yuudong2.ApparelRepair.Eject".Translate(),
                defaultDesc = "Yuudong2.ApparelRepair.EjectDesc".Translate(),
                icon = ApparelRepairTextures.Eject,
                action = () => EjectContents(parent.MapHeld),
            };

            if (DebugSettings.ShowDevGizmos && state == ApparelRepairState.Working)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Finish repair",
                    action = FinishRepair,
                };
            }
        }

        public override string CompInspectStringExtra()
        {
            switch (state)
            {
                case ApparelRepairState.Working:
                    return "Yuudong2.ApparelRepair.Working".Translate(user.LabelShort, repairing.Count, Progress.ToStringPercent());
                case ApparelRepairState.Done:
                    return "Yuudong2.ApparelRepair.Done".Translate(user.LabelShort);
                default:
                    return innerContainer.Count > 0 ? (string)"Yuudong2.ApparelRepair.Contents".Translate(innerContainer.ContentsString) : null;
            }
        }
    }

    // MainColonistBehaviorCore의 우선순위 정렬 노드에 들어간다 (Patches/BenchApparelRepair.xml).
    // 음식(9.5)보다 낮고 작업(최대 9)·수면(8)보다 높다. 아주 피곤하면 수면에 양보한다.
    public class JobGiver_RepairApparel : ThinkNode_JobGiver
    {
        public const float Priority = 9.2f;

        public const float TiredPriority = 7.5f;

        // 수선하는 중에 이보다 높은 우선순위(배고픔 9.5)만 끼어든다.
        public const float InterruptPriority = 9.4f;

        // 재봉대나 재료가 없어 못 간 정착민은 이만큼 지난 뒤에 다시 알아본다.
        private const int RetryInterval = GenDate.TicksPerHour;

        private static readonly Dictionary<int, int> nextTryTick = new Dictionary<int, int>();

        private static readonly Dictionary<ThingDef, int> tmpNeed = new Dictionary<ThingDef, int>();

        private static readonly List<Thing> tmpCandidates = new List<Thing>();

        public override float GetPriority(Pawn pawn)
        {
            if (pawn.apparel == null || !pawn.Spawned || ApparelRepairUtility.BenchDefs.Count == 0)
            {
                return 0f;
            }
            if (ApparelRepairUtility.BenchHolding(pawn) == null && (Waiting(pawn) || !ApparelRepairUtility.AnyNeedsRepair(pawn)))
            {
                return 0f;
            }
            if (pawn.needs?.rest != null && pawn.needs.rest.CurCategory >= RestCategory.VeryTired)
            {
                return TiredPriority;
            }
            return Priority;
        }

        // 다른 세이브에서 남은 값은 간격보다 멀리 있으므로 무시한다.
        private static bool Waiting(Pawn pawn)
        {
            if (!nextTryTick.TryGetValue(pawn.thingIDNumber, out int next))
            {
                return false;
            }
            int left = next - Find.TickManager.TicksGame;
            return left > 0 && left <= RetryInterval;
        }

        public static void Postpone(Pawn pawn)
        {
            nextTryTick[pawn.thingIDNumber] = Find.TickManager.TicksGame + RetryInterval;
        }

        private static Job GiveUp(Pawn pawn)
        {
            Postpone(pawn);
            tmpNeed.Clear();
            return null;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            CompApparelRepair holding = ApparelRepairUtility.BenchHolding(pawn);
            if (holding != null)
            {
                Thing held = holding.parent;
                if (held.IsForbidden(pawn) || held.IsBurning() || !pawn.CanReserveAndReach(held, PathEndMode.InteractionCell, Danger.Some))
                {
                    return null;
                }
                return JobMaker.MakeJob(ApparelRepairDefOf.Yuudong2_RepairApparel, held);
            }

            if (Waiting(pawn) || !ApparelRepairUtility.AnyNeedsRepair(pawn))
            {
                return null;
            }
            CompApparelRepair comp = ApparelRepairUtility.FindFreeBench(pawn);
            if (comp == null)
            {
                return GiveUp(pawn);
            }

            // 맵에서 가져올 수 있는 재료로 감당되는 옷만 고른다.
            Map map = pawn.Map;
            List<Apparel> plan = ApparelRepairUtility.PlanRepair(pawn, def => comp.CountContained(def) + CountAvailable(pawn, map, def), tmpNeed);
            if (plan.Count == 0)
            {
                return GiveUp(pawn);
            }

            Job job = JobMaker.MakeJob(ApparelRepairDefOf.Yuudong2_RepairApparel, comp.parent);
            foreach (KeyValuePair<ThingDef, int> entry in tmpNeed)
            {
                int remaining = entry.Value - comp.CountContained(entry.Key);
                if (remaining <= 0)
                {
                    continue;
                }
                tmpCandidates.Clear();
                List<Thing> things = map.listerThings.ThingsOfDef(entry.Key);
                for (int i = 0; i < things.Count; i++)
                {
                    if (ApparelRepairUtility.CanUseMaterial(pawn, things[i]))
                    {
                        tmpCandidates.Add(things[i]);
                    }
                }
                IntVec3 root = pawn.Position;
                tmpCandidates.SortBy(t => t.Position.DistanceToSquared(root));
                for (int i = 0; i < tmpCandidates.Count && remaining > 0; i++)
                {
                    int take = Math.Min(remaining, tmpCandidates[i].stackCount);
                    job.AddQueuedTarget(TargetIndex.B, tmpCandidates[i]);
                    job.countQueue ??= new List<int>();
                    job.countQueue.Add(take);
                    remaining -= take;
                }
                tmpCandidates.Clear();
                if (remaining > 0)
                {
                    return GiveUp(pawn);
                }
            }
            tmpNeed.Clear();
            return job;
        }

        private static int CountAvailable(Pawn pawn, Map map, ThingDef def)
        {
            int count = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                if (ApparelRepairUtility.CanUseMaterial(pawn, things[i]))
                {
                    count += things[i].stackCount;
                }
            }
            return count;
        }
    }

    // 한 잡으로 단계를 이어 간다: 재료 넣기 → 옷 벗어 맡기기 → 재봉대에서 수선하기 → 다시 입기.
    // 도중에 끊기면 (밥 먹으러 가는 등) JobGiver가 같은 잡을 다시 주고, 재봉대 상태에 따라 지난 단계는 건너뛴다.
    public class JobDriver_RepairApparel : JobDriver
    {
        private const TargetIndex BenchInd = TargetIndex.A;

        private const TargetIndex MaterialInd = TargetIndex.B;

        private const int OverrideCheckInterval = 250;

        private int phaseTicksLeft;

        private int phaseTicksTotal;

        private CompApparelRepair Comp => job.GetTarget(BenchInd).Thing?.TryGetComp<CompApparelRepair>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref phaseTicksLeft, "phaseTicksLeft", 0);
            Scribe_Values.Look(ref phaseTicksTotal, "phaseTicksTotal", 0);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(BenchInd), job, 1, -1, null, errorOnFailed))
            {
                return false;
            }
            pawn.ReserveAsManyAsPossible(job.GetTargetQueue(MaterialInd), job);
            return true;
        }

        public override string GetReport()
        {
            CompApparelRepair comp = Comp;
            if (comp != null && comp.HasClothesOf(pawn))
            {
                return (comp.State == ApparelRepairState.Done ? "Yuudong2.ApparelRepair.ReportDressing" : "Yuudong2.ApparelRepair.ReportRepairing").Translate();
            }
            if (pawn.carryTracker?.CarriedThing != null || !job.GetTargetQueue(MaterialInd).NullOrEmpty())
            {
                return "Yuudong2.ApparelRepair.ReportLoading".Translate();
            }
            return "Yuudong2.ApparelRepair.ReportUndressing".Translate();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(BenchInd);
            this.FailOnBurningImmobile(BenchInd);
            this.FailOn(() => Comp == null || (Comp.State != ApparelRepairState.Idle && !Comp.HasClothesOf(pawn)));

            // 옷을 맡기기 전에 끊기면 넣어 둔 재료를 재봉대 앞에 꺼내 둔다.
            AddFinishAction(_ =>
            {
                CompApparelRepair comp = Comp;
                if (comp != null && comp.State == ApparelRepairState.Idle && comp.parent.Spawned)
                {
                    comp.EjectContents(comp.parent.Map);
                }
            });

            Toil gotoBench = Toils_Goto.GotoThing(BenchInd, PathEndMode.InteractionCell);

            // 1. 재료를 한 더미씩 가져와 넣는다. 한 번에 다 못 드는 더미는 남은 만큼 다시 가지러 간다.
            yield return Toils_Jump.JumpIf(gotoBench, () => job.GetTargetQueue(MaterialInd).NullOrEmpty());
            Toil extract = Toils_JobTransforms.ExtractNextTargetFromQueue(MaterialInd, failIfCountFromQueueTooBig: false);
            yield return extract;
            yield return Toils_Goto.GotoThing(MaterialInd, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(MaterialInd)
                .FailOnSomeonePhysicallyInteracting(MaterialInd);
            yield return Toils_Haul.StartCarryThing(MaterialInd, putRemainderInQueue: true, subtractNumTakenFromJobCount: false, failIfStackCountLessThanJobCount: false, reserve: false);
            yield return Toils_Goto.GotoThing(BenchInd, PathEndMode.InteractionCell);
            Toil deposit = ToilMaker.MakeToil("Yuudong2_DepositRepairMaterial");
            deposit.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried != null)
                {
                    pawn.carryTracker.innerContainer.TryTransferToContainer(carried, Comp.GetDirectlyHeldThings(), carried.stackCount);
                }
            };
            yield return deposit;
            yield return Toils_Jump.JumpIf(extract, () => !job.GetTargetQueue(MaterialInd).NullOrEmpty());

            yield return gotoBench;

            // 2. 상한 옷을 벗어 넣는다. 벗는 데는 옷마다 착용 시간만큼 걸린다.
            Toil undress = ToilMaker.MakeToil("Yuudong2_RepairUndress");
            undress.initAction = () =>
            {
                if (Comp.State != ApparelRepairState.Idle)
                {
                    ReadyForNextToil();
                    return;
                }
                List<Apparel> plan = ApparelRepairUtility.PlanRepair(pawn, Comp.CountContained, new Dictionary<ThingDef, int>());
                if (plan.Count == 0)
                {
                    JobGiver_RepairApparel.Postpone(pawn);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                phaseTicksTotal = 0;
                for (int i = 0; i < plan.Count; i++)
                {
                    phaseTicksTotal += ApparelRepairUtility.EquipTicks(plan[i]);
                }
                phaseTicksLeft = phaseTicksTotal;
                pawn.pather.StopDead();
            };
            undress.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.GetTarget(BenchInd));
                phaseTicksLeft -= delta;
                if (phaseTicksLeft <= 0)
                {
                    if (!Comp.TryStartRepair(pawn))
                    {
                        JobGiver_RepairApparel.Postpone(pawn);
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                    ReadyForNextToil();
                }
            };
            undress.handlingFacing = true;
            undress.defaultCompleteMode = ToilCompleteMode.Never;
            undress.WithProgressBar(BenchInd, () => phaseTicksTotal > 0 ? 1f - (float)phaseTicksLeft / phaseTicksTotal : 0f);
            yield return undress;

            // 3. 재봉대에서 수선한다. 의자가 있으면 앉아서 편안함을 얻는다. 배고프면 밥을 먹으러 간다.
            Toil repair = ToilMaker.MakeToil("Yuudong2_RepairWork");
            int sinceOverrideCheck = 0;
            repair.initAction = () =>
            {
                pawn.pather.StopDead();
                sinceOverrideCheck = 0;
                if (Comp.State == ApparelRepairState.Done)
                {
                    ReadyForNextToil();
                }
            };
            repair.tickIntervalAction = delta =>
            {
                CompApparelRepair comp = Comp;
                pawn.rotationTracker.FaceTarget(job.GetTarget(BenchInd));
                pawn.GainComfortFromCellIfPossible(delta, chairsOnly: true);
                comp.DoWork(delta);
                if (comp.State == ApparelRepairState.Done)
                {
                    ReadyForNextToil();
                    return;
                }
                sinceOverrideCheck += delta;
                if (sinceOverrideCheck >= OverrideCheckInterval)
                {
                    sinceOverrideCheck = 0;
                    pawn.jobs.CheckForJobOverride(JobGiver_RepairApparel.InterruptPriority);
                }
            };
            repair.handlingFacing = true;
            repair.defaultCompleteMode = ToilCompleteMode.Never;
            repair.WithProgressBar(BenchInd, () => Comp.Progress);
            repair.WithEffect(() => DefDatabase<EffecterDef>.GetNamedSilentFail("Tailor"), BenchInd);
            repair.PlaySustainerOrSound(() => DefDatabase<SoundDef>.GetNamedSilentFail("Recipe_Tailor"));
            yield return repair;

            // 4. 다 고친 옷을 꺼내 입는다.
            Toil dress = ToilMaker.MakeToil("Yuudong2_RepairDress");
            dress.initAction = () =>
            {
                if (Comp.State != ApparelRepairState.Done)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                phaseTicksTotal = 0;
                ThingOwner held = Comp.GetDirectlyHeldThings();
                for (int i = 0; i < held.Count; i++)
                {
                    if (held[i] is Apparel apparel)
                    {
                        phaseTicksTotal += ApparelRepairUtility.EquipTicks(apparel);
                    }
                }
                phaseTicksLeft = Mathf.Max(1, phaseTicksTotal);
                pawn.pather.StopDead();
            };
            dress.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.GetTarget(BenchInd));
                phaseTicksLeft -= delta;
                if (phaseTicksLeft <= 0)
                {
                    Comp.RetrieveFor(pawn);
                    ReadyForNextToil();
                }
            };
            dress.handlingFacing = true;
            dress.defaultCompleteMode = ToilCompleteMode.Never;
            dress.WithProgressBar(BenchInd, () => phaseTicksTotal > 0 ? 1f - (float)phaseTicksLeft / phaseTicksTotal : 1f);
            yield return dress;
        }
    }
}

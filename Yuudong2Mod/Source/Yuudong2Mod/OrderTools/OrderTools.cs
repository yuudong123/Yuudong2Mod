using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod.OrderTools
{
    // AllowTool 모드에서 쓰던 기능만 옮긴 것(원본은 UnlimitedHugs의 AllowTool, 퍼블릭 도메인).
    // 지정·작업 종류·작업 Def 이름을 원본과 같게 두어, AllowTool을 빼도 세이브의 긴급 운반 지정과 작업 우선순위가 그대로 남는다.
    // 같은 이름이 겹치지 않도록 AllowTool이 켜져 있으면 이 기능의 Def 폴더를 읽지 않고(LoadFolders.xml) 패치도 걸지 않는다.
    public static class OrderToolsUtility
    {
        public const string AllowToolPackageId = "UnlimitedHugs.AllowTool";

        public const string TexturePath = "Yuudong2/OrderTools/";

        private static DesignationDef haulUrgently;

        private static DesignationDef finishOff;

        private static JobDef finishOffJob;

        private static WorkGiverDef finishOffGiver;

        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.OrderTools);

        public static DesignationDef HaulUrgently => haulUrgently ??= DefDatabase<DesignationDef>.GetNamed("HaulUrgentlyDesignation");

        public static DesignationDef FinishOff => finishOff ??= DefDatabase<DesignationDef>.GetNamed("FinishOffDesignation");

        public static JobDef FinishOffJob => finishOffJob ??= DefDatabase<JobDef>.GetNamed("FinishOffPawn");

        public static WorkGiverDef FinishOffGiver => finishOffGiver ??= DefDatabase<WorkGiverDef>.GetNamed("FinishOff");

        public static bool ShiftHeld => Event.current != null ? Event.current.shift : Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        public static Texture2D Icon(string name)
        {
            return ContentFinder<Texture2D>.Get(TexturePath + name);
        }

        public static bool Has(Thing thing, DesignationDef def)
        {
            return thing.MapHeld != null && thing.MapHeld.designationManager.DesignationOn(thing, def) != null;
        }

        public static void Add(Thing thing, DesignationDef def)
        {
            if (!Has(thing, def))
            {
                thing.Map.designationManager.AddDesignation(new Designation(thing, def));
            }
        }

        public static bool Friendly(Thing thing)
        {
            return thing is Pawn pawn && pawn.Faction != null && (pawn.IsPrisonerOfColony || !pawn.Faction.HostileTo(Faction.OfPlayer));
        }

        public static CellRect VisibleRect()
        {
            return Find.CameraDriver.CurrentViewRect;
        }

        public static void DrawMouseLabel(string text)
        {
            if (text.NullOrEmpty())
            {
                return;
            }

            Vector2 mouse = Event.current.mousePosition;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(mouse.x + 8f, mouse.y + 52f, 260f, 9999f), text);
        }

        // 체크 표시가 붙은 우클릭 메뉴 항목. 누르면 값을 뒤집는다.
        public static FloatMenuOption Checkmark(string label, System.Func<bool> getter, System.Action<bool> setter)
        {
            return new FloatMenuOption(label, () => setter(!getter()), MenuOptionPriority.Default, null, null, 34f, rect =>
            {
                Widgets.CheckboxDraw(rect.x + 10f, rect.y + rect.height / 2f - 12f, getter(), false);
                return false;
            });
        }

        public static void Report(int count, string successKey, string failKey)
        {
            if (count > 0)
            {
                Messages.Message(successKey.Translate(count), MessageTypeDefOf.SilentInput, false);
            }
            else
            {
                Messages.Message(failKey.Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        // 도구가 받아 주는 물건을 맵 전체(또는 조건에 맞는 것만)에서 한 번에 지정한다.
        public static int DesignateAll(Designator designator, Map map, ThingRequestGroup group, System.Predicate<Thing> filter = null)
        {
            int count = 0;
            List<Thing> things = new List<Thing>(map.listerThings.ThingsInGroup(group));
            foreach (Thing thing in things)
            {
                if (thing.def == null || !thing.Spawned || thing.Position.Fogged(map) || (filter != null && !filter(thing)))
                {
                    continue;
                }

                if (designator.CanDesignateThing(thing).Accepted)
                {
                    designator.DesignateThing(thing);
                    count++;
                }
            }

            return count;
        }
    }

    // 세이브마다 저장하는 설정과, 긴급 운반 대상 목록.
    public class GameComponent_OrderTools : GameComponent
    {
        private const int HaulRefreshTicks = 60;

        public static GameComponent_OrderTools Instance;

        public bool huntFinishOff = true;

        public bool huntDesignatedOnly;

        public bool huntUnforbid;

        public int stripSpacingX = 5;

        public int stripSpacingZ = 5;

        private HashSet<int> partyHunters = new HashSet<int>();

        private readonly Dictionary<int, List<Thing>> urgentByMap = new Dictionary<int, List<Thing>>();

        private readonly Dictionary<int, int> urgentTick = new Dictionary<int, int>();

        private readonly List<Designation> stale = new List<Designation>();

        public GameComponent_OrderTools(Game game)
        {
            Instance = this;
        }

        public bool IsPartyHunting(Pawn pawn)
        {
            return partyHunters.Count > 0 && partyHunters.Contains(pawn.thingIDNumber);
        }

        public void SetPartyHunting(Pawn pawn, bool on)
        {
            if (on)
            {
                partyHunters.Add(pawn.thingIDNumber);
            }
            else
            {
                partyHunters.Remove(pawn.thingIDNumber);
            }
        }

        public void ForgetUrgent(Map map)
        {
            urgentTick.Remove(map.uniqueID);
        }

        // 긴급 운반 지정이 붙어 있고 아직 옮길 필요가 있는 물건. 더 옮길 필요가 없어진 물건의 지정은 여기서 지운다.
        public List<Thing> UrgentHaulables(Map map)
        {
            int id = map.uniqueID;
            int tick = Find.TickManager.TicksGame;
            if (!urgentByMap.TryGetValue(id, out List<Thing> list))
            {
                list = new List<Thing>();
                urgentByMap[id] = list;
            }

            if (urgentTick.TryGetValue(id, out int last) && tick - last < HaulRefreshTicks && tick >= last)
            {
                return list;
            }

            urgentTick[id] = tick;
            list.Clear();
            DesignationDef def = OrderToolsUtility.HaulUrgently;
            if (!map.designationManager.AnySpawnedDesignationOfDef(def))
            {
                return list;
            }

            ICollection<Thing> haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            stale.Clear();
            foreach (Designation designation in map.designationManager.SpawnedDesignationsOfDef(def))
            {
                Thing thing = designation.target.Thing;
                if (thing != null && thing.Spawned && haulables.Contains(thing))
                {
                    list.Add(thing);
                }
                else
                {
                    stale.Add(designation);
                }
            }

            foreach (Designation designation in stale)
            {
                map.designationManager.RemoveDesignation(designation);
            }

            stale.Clear();
            return list;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref huntFinishOff, "huntFinishOff", true);
            Scribe_Values.Look(ref huntDesignatedOnly, "huntDesignatedOnly");
            Scribe_Values.Look(ref huntUnforbid, "huntUnforbid");
            Scribe_Values.Look(ref stripSpacingX, "stripSpacingX", 5);
            Scribe_Values.Look(ref stripSpacingZ, "stripSpacingZ", 5);
            Scribe_Collections.Look(ref partyHunters, "partyHunters", LookMode.Value);
            partyHunters ??= new HashSet<int>();
        }
    }

    // 드래그한 칸 안의 물건마다 지정을 거는 도구의 공통 부분.
    public abstract class Designator_Things : Designator
    {
        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public override bool Visible => OrderToolsUtility.Active;

        protected Designator_Things(string key, string icon)
        {
            defaultLabel = ("Yuudong2.OrderTools." + key).Translate();
            defaultDesc = ("Yuudong2.OrderTools." + key + ".Desc").Translate();
            this.icon = OrderToolsUtility.Icon(icon);
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            useMouseIcon = true;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map) || c.Fogged(Map))
            {
                return false;
            }

            List<Thing> things = c.GetThingList(Map);
            for (int i = 0; i < things.Count; i++)
            {
                if (CanDesignateThing(things[i]).Accepted)
                {
                    return true;
                }
            }

            return false;
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            List<Thing> things = new List<Thing>(c.GetThingList(Map));
            foreach (Thing thing in things)
            {
                if (CanDesignateThing(thing).Accepted)
                {
                    DesignateThing(thing);
                }
            }
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
        }
    }

    public class Designator_HaulUrgently : Designator_Things
    {
        protected override DesignationDef Designation => OrderToolsUtility.HaulUrgently;

        public Designator_HaulUrgently() : base("HaulUrgently", "haulUrgently")
        {
            soundSucceeded = SoundDefOf.Designate_Haul;
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.HaulUrgently.Visible".Translate(), () =>
                {
                    CellRect view = OrderToolsUtility.VisibleRect();
                    OrderToolsUtility.Report(OrderToolsUtility.DesignateAll(this, Find.CurrentMap, ThingRequestGroup.HaulableEver, t => !t.def.designateHaulable && view.Contains(t.Position)),
                        "Yuudong2.OrderTools.HaulUrgently.Done", "Yuudong2.OrderTools.HaulUrgently.None");
                });
                yield return new FloatMenuOption("Yuudong2.OrderTools.HaulUrgently.All".Translate(), () =>
                {
                    OrderToolsUtility.Report(OrderToolsUtility.DesignateAll(this, Find.CurrentMap, ThingRequestGroup.HaulableEver, t => !t.def.designateHaulable),
                        "Yuudong2.OrderTools.HaulUrgently.Done", "Yuudong2.OrderTools.HaulUrgently.None");
                });
            }
        }

        public override AcceptanceReport CanDesignateThing(Thing t)
        {
            return t.def != null && t.Spawned && !t.Position.Fogged(t.Map) && (t.def.alwaysHaulable || t.def.EverHaulable)
                && !t.IsInValidBestStorage() && !OrderToolsUtility.Has(t, Designation);
        }

        public override void DesignateThing(Thing t)
        {
            if (t.def.designateHaulable)
            {
                OrderToolsUtility.Add(t, DesignationDefOf.Haul);
            }

            OrderToolsUtility.Add(t, Designation);
            t.SetForbidden(false, false);
            GameComponent_OrderTools.Instance?.ForgetUrgent(t.Map);
        }

        // Shift를 누른 채 지정하면 정착민들이 하던 일을 바로 다시 고른다.
        protected override void FinalizeDesignationSucceeded()
        {
            base.FinalizeDesignationSucceeded();
            if (!OrderToolsUtility.ShiftHeld)
            {
                return;
            }

            foreach (Pawn pawn in Find.CurrentMap.mapPawns.FreeColonistsSpawned)
            {
                pawn.jobs.CheckForJobOverride();
            }
        }
    }

    public class WorkGiver_HaulUrgently : WorkGiver_Scanner
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !OrderToolsUtility.Active || GameComponent_OrderTools.Instance == null || GameComponent_OrderTools.Instance.UrgentHaulables(pawn.Map).Count == 0;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            List<Thing> things = GameComponent_OrderTools.Instance.UrgentHaulables(pawn.Map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].Spawned && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, things[i], false))
                {
                    yield return things[i];
                }
            }
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return HaulAIUtility.HaulToStorageJob(pawn, t, forced);
        }
    }

    public class Designator_FinishOff : Designator_Things
    {
        public const int MeleeSkillRequired = 6;

        protected override DesignationDef Designation => OrderToolsUtility.FinishOff;

        public Designator_FinishOff() : base("FinishOff", "finishOff")
        {
            soundSucceeded = SoundDefOf.Designate_Hunt;
            defaultDesc += "\n\n" + "Yuudong2.OrderTools.FinishOff.Skill".Translate(MeleeSkillRequired);
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.FinishOff.All".Translate(), () =>
                {
                    Map map = Find.CurrentMap;
                    int count = 0;
                    bool friendly = false;
                    foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (ValidTarget(pawn) && !pawn.Position.Fogged(map) && !OrderToolsUtility.Has(pawn, Designation))
                        {
                            OrderToolsUtility.Add(pawn, Designation);
                            count++;
                            friendly |= OrderToolsUtility.Friendly(pawn);
                        }
                    }

                    if (friendly)
                    {
                        Messages.Message("Yuudong2.OrderTools.FinishOff.Allies".Translate(count), MessageTypeDefOf.CautionInput, false);
                    }

                    OrderToolsUtility.Report(count, "Yuudong2.OrderTools.FinishOff.Done", "Yuudong2.OrderTools.FinishOff.None");
                });
            }
        }

        public static bool ValidTarget(Thing t)
        {
            return t is Pawn pawn && pawn.def != null && !pawn.Dead && pawn.Downed;
        }

        // 아군(포로 포함)은 Shift를 누르고 있을 때만 지정한다.
        public static AcceptanceReport FriendlyAllowed(Thing t)
        {
            return !OrderToolsUtility.Friendly(t) || OrderToolsUtility.ShiftHeld ? AcceptanceReport.WasAccepted : new AcceptanceReport("Yuudong2.OrderTools.FinishOff.Friendly".Translate());
        }

        public static AcceptanceReport SkillAllows(Pawn pawn, Pawn target)
        {
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                return new AcceptanceReport("IsIncapableOfViolenceShort".Translate());
            }

            bool animal = target?.RaceProps != null && target.RaceProps.Animal;
            if (!animal && (pawn.skills == null || pawn.skills.GetSkill(SkillDefOf.Melee).Level < MeleeSkillRequired))
            {
                return new AcceptanceReport("Yuudong2.OrderTools.FinishOff.PawnSkill".Translate(MeleeSkillRequired));
            }

            return AcceptanceReport.WasAccepted;
        }

        public override AcceptanceReport CanDesignateThing(Thing t)
        {
            if (!ValidTarget(t) || OrderToolsUtility.Has(t, Designation))
            {
                return false;
            }

            return FriendlyAllowed(t);
        }

        public override void DesignateThing(Thing t)
        {
            if (CanDesignateThing(t).Accepted)
            {
                OrderToolsUtility.Add(t, Designation);
            }
        }
    }

    public class WorkGiver_FinishOff : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Pawn);

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public static WorkGiver_FinishOff Create()
        {
            return new WorkGiver_FinishOff { def = OrderToolsUtility.FinishOffGiver };
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !OrderToolsUtility.Active || !pawn.Map.designationManager.AnySpawnedDesignationOfDef(OrderToolsUtility.FinishOff);
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Designation designation in pawn.Map.designationManager.SpawnedDesignationsOfDef(OrderToolsUtility.FinishOff))
            {
                if (Designator_FinishOff.ValidTarget(designation.target.Thing))
                {
                    yield return designation.target.Thing;
                }
            }
        }

        public Job JobInRange(Pawn pawn, float range)
        {
            float squared = range * range;
            foreach (Thing target in PotentialWorkThingsGlobal(pawn).ToList())
            {
                if (pawn.Position.DistanceToSquared(target.Position) < squared)
                {
                    Job job = JobOnThing(pawn, target);
                    if (job != null)
                    {
                        return job;
                    }
                }
            }

            return null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!Designator_FinishOff.ValidTarget(t) || !pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.Deadly))
            {
                return null;
            }

            AcceptanceReport friendly = Designator_FinishOff.FriendlyAllowed(t);
            if (forced && !friendly.Accepted)
            {
                JobFailReason.Is(friendly.Reason);
                return null;
            }

            AcceptanceReport skill = Designator_FinishOff.SkillAllows(pawn, t as Pawn);
            if (!skill.Accepted)
            {
                JobFailReason.Is(skill.Reason);
                return null;
            }

            if (!forced && !OrderToolsUtility.Has(t, OrderToolsUtility.FinishOff))
            {
                return null;
            }

            Verb verb = pawn.meleeVerbs?.TryGetMeleeVerb(t);
            if (verb == null)
            {
                return null;
            }

            Job job = JobMaker.MakeJob(OrderToolsUtility.FinishOffJob, t);
            job.verbToUse = verb;
            job.killIncappedTarget = true;
            return job;
        }
    }

    public class JobDriver_FinishOff : JobDriver
    {
        private const int PrepareTicks = 60;

        private const float NextTargetRange = 8f;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            if (job.playerForced && TargetThingA != null && TargetThingA.Spawned)
            {
                OrderToolsUtility.Add(TargetThingA, OrderToolsUtility.FinishOff);
            }

            AddFailCondition(() => !(TargetThingA is Pawn victim) || !victim.Spawned || victim.Dead || !victim.Downed || !OrderToolsUtility.Has(victim, OrderToolsUtility.FinishOff));
            yield return Toils_Misc.ThrowColonistAttackingMote(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(PrepareTicks).WithProgressBarToilDelay(TargetIndex.A);

            Toil strike = ToilMaker.MakeToil("FinishOff");
            strike.initAction = delegate
            {
                if (!(TargetThingA is Pawn victim) || job.verbToUse == null)
                {
                    return;
                }

                job.verbToUse.TryStartCastOn(victim);
                SocialImpact(victim);
                Execute(pawn, victim);
                if (job.playerForced)
                {
                    return;
                }

                Job next = WorkGiver_FinishOff.Create().JobInRange(pawn, NextTargetRange);
                if (next != null)
                {
                    pawn.jobs.jobQueue.EnqueueFirst(next);
                }
            };
            strike.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return strike;
        }

        private void Execute(Pawn slayer, Pawn victim)
        {
            IntVec3 position = victim.Position;
            Map map = Map;
            int blood = Mathf.Max(GenMath.RoundRandom(victim.BodySize * 8f), 1);
            for (int i = 0; i < blood; i++)
            {
                victim.health.DropBloodFilth();
            }

            BodyPartRecord brain = victim.RaceProps.body.GetPartsWithTag(BodyPartTagDefOf.ConsciousnessSource).FirstOrDefault();
            int amount = brain != null ? Mathf.Clamp((int)victim.health.hediffSet.GetPartHealth(brain) - 1, 1, 20) : 20;
            DamageInfo damage = new DamageInfo(DamageDefOf.ExecutionCut, amount, -1f, -1f, slayer, brain);
            victim.TakeDamage(damage);
            if (!victim.Dead)
            {
                victim.Kill(damage);
            }

            foreach (IntVec3 offset in GenAdj.AdjacentCellsAndInside)
            {
                IntVec3 cell = position + offset;
                if (!cell.InBounds(map))
                {
                    continue;
                }

                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].def.selectable && things[i].IsForbidden(Faction.OfPlayer))
                    {
                        things[i].SetForbidden(false, false);
                    }
                }
            }
        }

        private void SocialImpact(Pawn victim)
        {
            bool prisoner = victim.IsPrisonerOfColony;
            if (OrderToolsUtility.Friendly(victim))
            {
                ThoughtUtility.GiveThoughtsForPawnExecuted(victim, pawn, PawnExecutionKind.GenericBrutal);
            }

            if (victim.RaceProps != null && victim.RaceProps.Animal)
            {
                pawn.records.Increment(RecordDefOf.AnimalsSlaughtered);
            }

            if (prisoner)
            {
                TaleRecorder.RecordTale(TaleDefOf.ExecutedPrisoner, pawn, victim);
            }
        }
    }

    // 정착민을 고르고 쓰러진 생물을 우클릭했을 때의 "마무리" 항목. 소집 중에도 나온다.
    public class FloatMenuOptionProvider_FinishOff : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => true;

        protected override bool AppliesInt(FloatMenuContext context)
        {
            return OrderToolsUtility.Active;
        }

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            Pawn selected = context.FirstSelectedPawn;
            if (!Designator_FinishOff.ValidTarget(clickedPawn) || selected == clickedPawn)
            {
                return null;
            }

            JobFailReason.Clear();
            WorkGiver_FinishOff giver = WorkGiver_FinishOff.Create();
            Job job = giver.JobOnThing(selected, clickedPawn, true);
            string label = "Yuudong2.OrderTools.FinishOff.Menu".Translate(clickedPawn.LabelShort);
            if (job == null)
            {
                return new FloatMenuOption(JobFailReason.HaveReason ? label + " (" + JobFailReason.Reason + ")" : label, null);
            }

            return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(label, () => selected.jobs.TryTakeOrderedJobPrioritizedWork(job, giver, clickedPawn.Position)), selected, clickedPawn);
        }
    }

    // 지금 선택한 것과 같은 종류(재료 포함)만 드래그로 골라 선택한다. 한 칸만 누르면 그 물건을 새 기준으로 삼는다.
    public class Designator_SelectSimilar : Designator_Things
    {
        private const int SelectionLimit = 200;

        private readonly HashSet<(ThingDef, ThingDef)> constraints = new HashSet<(ThingDef, ThingDef)>();

        private string constraintLabel = string.Empty;

        public Designator_SelectSimilar() : base("SelectSimilar", "selectSimilar")
        {
            soundSucceeded = SoundDefOf.ThingSelected;
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.SelectSimilar.Map".Translate(), () => SelectOnMap(null));
                yield return new FloatMenuOption("Yuudong2.OrderTools.SelectSimilar.Visible".Translate(), () =>
                {
                    CellRect view = OrderToolsUtility.VisibleRect();
                    SelectOnMap(t => view.Contains(t.Position));
                });
                yield return new FloatMenuOption("Yuudong2.OrderTools.SelectSimilar.Home".Translate(), () =>
                {
                    Area home = Find.CurrentMap.areaManager.Home;
                    SelectOnMap(t => home[t.Position]);
                });
            }
        }

        public override void Selected()
        {
            base.Selected();
            Reindex();
        }

        public void Reindex()
        {
            constraints.Clear();
            List<string> labels = new List<string>();
            foreach (object obj in Find.Selector.SelectedObjects)
            {
                if (obj is Thing thing && thing.def != null && thing.def.selectable && constraints.Add((thing.def, thing.Stuff)))
                {
                    labels.Add(thing.Stuff == null ? thing.def.LabelCap.ToString() : thing.def.LabelCap + " (" + thing.Stuff.LabelCap + ")");
                }
            }

            constraintLabel = labels.Count <= 4 ? string.Join(", ", labels) : string.Join(", ", labels.Take(4)) + " " + "Yuudong2.OrderTools.SelectSimilar.More".Translate(labels.Count - 4);
        }

        private static bool Selectable(Thing thing)
        {
            return thing.def != null && thing.def.selectable && thing.def.label != null && thing.Spawned && (!thing.Position.Fogged(thing.Map) || DebugSettings.godMode);
        }

        private bool Matches(Thing thing)
        {
            return constraints.Count == 0 || constraints.Contains((thing.def, thing.Stuff));
        }

        public override AcceptanceReport CanDesignateThing(Thing t)
        {
            return Selectable(t);
        }

        public override void DesignateThing(Thing t)
        {
            TrySelect(t);
        }

        public bool TrySelect(Thing thing)
        {
            Selector selector = Find.Selector;
            if (!Selectable(thing) || selector.IsSelected(thing) || selector.NumSelected >= SelectionLimit)
            {
                return false;
            }

            selector.Select(thing, false, false);
            return true;
        }

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            List<IntVec3> list = cells.Where(c => c.InBounds(Map)).ToList();
            int selected = 0;
            if (list.Count == 1)
            {
                if (!OrderToolsUtility.ShiftHeld)
                {
                    Find.Selector.ClearSelection();
                }

                foreach (Thing thing in list[0].GetThingList(Map).ToList())
                {
                    if (TrySelect(thing))
                    {
                        selected++;
                        break;
                    }
                }
            }
            else
            {
                Reindex();
                foreach (IntVec3 cell in list)
                {
                    if (cell.Fogged(Map) && !DebugSettings.godMode)
                    {
                        continue;
                    }

                    foreach (Thing thing in cell.GetThingList(Map).ToList())
                    {
                        if (Matches(thing) && TrySelect(thing))
                        {
                            selected++;
                        }
                    }
                }
            }

            Reindex();
            Finalize(selected > 0);
            if (Find.Selector.NumSelected > 0 && Find.MainTabsRoot.OpenTab == MainButtonDefOf.Architect)
            {
                Find.MainTabsRoot.EscapeCurrentTab(false);
            }
        }

        private void SelectOnMap(System.Predicate<Thing> filter)
        {
            Map map = Find.CurrentMap;
            Reindex();
            if (constraints.Count == 0)
            {
                Messages.Message("Yuudong2.OrderTools.SelectSimilar.Nothing".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Thing> candidates = new List<Thing>();
            foreach ((ThingDef def, ThingDef stuff) in constraints)
            {
                foreach (Thing thing in map.listerThings.ThingsOfDef(def))
                {
                    if (thing.Stuff == stuff && Selectable(thing) && !Find.Selector.IsSelected(thing) && (filter == null || filter(thing)))
                    {
                        candidates.Add(thing);
                    }
                }
            }

            IntVec3 center = Find.CameraDriver.MapPosition;
            candidates.SortBy(t => t.Position.DistanceToSquared(center));
            int count = 0;
            foreach (Thing thing in candidates)
            {
                if (TrySelect(thing))
                {
                    count++;
                }
            }

            if (count < candidates.Count)
            {
                Messages.Message("Yuudong2.OrderTools.SelectSimilar.Part".Translate(count, candidates.Count), MessageTypeDefOf.SilentInput, false);
            }
            else
            {
                Messages.Message("Yuudong2.OrderTools.SelectSimilar.Done".Translate(count), MessageTypeDefOf.SilentInput, false);
            }
        }

        public override void DrawMouseAttachments()
        {
            base.DrawMouseAttachments();
            string text = Find.Selector.NumSelected >= SelectionLimit ? "Yuudong2.OrderTools.SelectSimilar.Limit".Translate().ToString()
                : constraints.Count == 0 ? "Yuudong2.OrderTools.SelectSimilar.Pick".Translate().ToString()
                : "Yuudong2.OrderTools.SelectSimilar.Now".Translate(constraintLabel).ToString();
            OrderToolsUtility.DrawMouseLabel(text);
        }
    }

    // 물건을 선택했을 때 뜨는 "비슷한 것 선택" 버튼. 누르면 드래그 도구를 집어 든다.
    public class Designator_SelectSimilarReverse : Designator_SelectSimilar
    {
        public override AcceptanceReport CanDesignateThing(Thing t)
        {
            return t.def != null && t.def.selectable && t.def.label != null && t.Spawned && !(t is Pawn);
        }

        public override void DesignateThing(Thing t)
        {
        }

        protected override void FinalizeDesignationSucceeded()
        {
            base.FinalizeDesignationSucceeded();
            Find.DesignatorManager.Select(new Designator_SelectSimilar());
        }
    }

    // 채굴 지정을 격자 모양으로 깔아 숨은 광석을 찾는다. 간격은 도구를 우클릭해서 바꾼다.
    public class Designator_StripMine : Designator
    {
        private static readonly int[] Spacings = { 2, 3, 4, 5, 6, 8, 10 };

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public override bool DragDrawMeasurements => true;

        public override bool Visible => OrderToolsUtility.Active;

        protected override DesignationDef Designation => DesignationDefOf.Mine;

        public Designator_StripMine()
        {
            defaultLabel = "Yuudong2.OrderTools.StripMine".Translate();
            defaultDesc = "Yuudong2.OrderTools.StripMine.Desc".Translate();
            icon = OrderToolsUtility.Icon("stripMine");
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_Mine;
            useMouseIcon = true;
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
                if (settings == null)
                {
                    yield break;
                }

                foreach (int spacing in Spacings)
                {
                    int chosen = spacing;
                    string mark = settings.stripSpacingX == chosen && settings.stripSpacingZ == chosen ? " ✔" : string.Empty;
                    yield return new FloatMenuOption("Yuudong2.OrderTools.StripMine.Spacing".Translate(chosen) + mark, () =>
                    {
                        settings.stripSpacingX = chosen;
                        settings.stripSpacingZ = chosen;
                    });
                }
            }
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            return c.InBounds(Map);
        }

        private static bool Mineable(Map map, IntVec3 c)
        {
            return c.Fogged(map) || (c.GetFirstMineable(map)?.def.mineable ?? false);
        }

        // 드래그한 사각형의 왼쪽 아래 모서리에서 시작하는 격자 선 위의 칸.
        private static IEnumerable<IntVec3> GridCells(List<IntVec3> cells)
        {
            if (cells.Count == 0)
            {
                yield break;
            }

            GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
            int stepX = (settings?.stripSpacingX ?? 5) + 1;
            int stepZ = (settings?.stripSpacingZ ?? 5) + 1;
            int minX = int.MaxValue;
            int minZ = int.MaxValue;
            for (int i = 0; i < cells.Count; i++)
            {
                minX = Mathf.Min(minX, cells[i].x);
                minZ = Mathf.Min(minZ, cells[i].z);
            }

            for (int i = 0; i < cells.Count; i++)
            {
                if ((cells[i].x - minX) % stepX == 0 || (cells[i].z - minZ) % stepZ == 0)
                {
                    yield return cells[i];
                }
            }
        }

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            Map map = Map;
            int count = 0;
            foreach (IntVec3 cell in GridCells(cells.Where(c => c.InBounds(map)).ToList()))
            {
                if (Mineable(map, cell) && map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) == null)
                {
                    map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.Mine));
                    count++;
                }
            }

            Finalize(count > 0);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
        }

        public override void RenderHighlight(List<IntVec3> dragCells)
        {
            Map map = Map;
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            foreach (IntVec3 cell in GridCells(dragCells))
            {
                if (cell.InBounds(map) && Mineable(map, cell))
                {
                    Graphics.DrawMesh(MeshPool.plane10, new Vector3(cell.x + 0.5f, y, cell.z + 0.5f), Quaternion.identity, DesignatorUtility.DragHighlightCellMat, 0);
                }
            }
        }
    }

    // 소집 사냥: 소집된 정착민이 온순한 야생 동물을 알아서 쏜다. 소집을 풀면 꺼진다.
    public class Command_PartyHunt : Command_Toggle
    {
        public Command_PartyHunt(Pawn pawn)
        {
            GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
            icon = OrderToolsUtility.Icon("partyHunt");
            defaultLabel = "Yuudong2.OrderTools.PartyHunt".Translate();
            defaultDesc = "Yuudong2.OrderTools.PartyHunt.Desc".Translate();
            isActive = () => settings.IsPartyHunting(pawn);
            toggleAction = () => settings.SetPartyHunting(pawn, !settings.IsPartyHunting(pawn));
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                Disable("IsIncapableOfViolenceShort".Translate().CapitalizeFirst());
            }
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
                yield return OrderToolsUtility.Checkmark("Yuudong2.OrderTools.PartyHunt.FinishOff".Translate(), () => settings.huntFinishOff, v => settings.huntFinishOff = v);
                yield return OrderToolsUtility.Checkmark("Yuudong2.OrderTools.PartyHunt.DesignatedOnly".Translate(), () => settings.huntDesignatedOnly, v => settings.huntDesignatedOnly = v);
                yield return OrderToolsUtility.Checkmark("Yuudong2.OrderTools.PartyHunt.Unforbid".Translate(), () => settings.huntUnforbid, v => settings.huntUnforbid = v);
            }
        }
    }

    public static class PartyHuntUtility
    {
        private const float PartyRange = 20f;

        private static readonly AccessTools.FieldRef<Pawn_DraftController, AutoUndrafter> AutoUndrafterField = AccessTools.FieldRefAccess<Pawn_DraftController, AutoUndrafter>("autoUndrafter");

        private static bool CanFinish(Pawn pawn)
        {
            return !pawn.WorkTagIsDisabled(WorkTags.ManualDumb);
        }

        public static void Tick(JobDriver_Wait driver)
        {
            GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
            Pawn pawn = driver.pawn;
            if (settings == null || !settings.IsPartyHunting(pawn) || pawn.Faction == null || driver.job.def != JobDefOf.Wait_Combat
                || pawn.WorkTagIsDisabled(WorkTags.Violent) || pawn.stances.FullBodyBusy)
            {
                return;
            }

            Verb verb = pawn.TryGetAttackVerb(null, !pawn.IsColonist);
            if (verb != null && pawn.drafter.FireAtWill)
            {
                float range = verb.verbProps.IsMeleeAttack ? 2f : verb.verbProps.range;
                Pawn prey = FindTarget(pawn, verb.verbProps.minRange, range, settings,
                    t => !OrderToolsUtility.Has(t, DesignationDefOf.Tame) && (!t.Downed || (CanFinish(pawn) && !settings.huntFinishOff)));
                if (prey != null)
                {
                    pawn.TryStartAttack(prey);
                    AutoUndrafterField(pawn.drafter).Notify_Drafted();
                }
            }

            if (pawn.stances.FullBodyBusy || !settings.huntFinishOff || !CanFinish(pawn) || AnyPartyMemberBusy(pawn, settings))
            {
                return;
            }

            Pawn downed = FindTarget(pawn, 0f, PartyRange, settings, t => t.Downed && !OrderToolsUtility.Has(t, OrderToolsUtility.FinishOff));
            if (downed != null)
            {
                OrderToolsUtility.Add(downed, OrderToolsUtility.FinishOff);
            }

            Job job = WorkGiver_FinishOff.Create().JobInRange(pawn, PartyRange);
            if (job != null)
            {
                pawn.jobs.StartJob(job, JobCondition.Ongoing, null, true);
                pawn.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(JobDefOf.Goto, pawn.Position));
            }
        }

        private static bool AnyPartyMemberBusy(Pawn center, GameComponent_OrderTools settings)
        {
            foreach (Pawn pawn in center.Map.mapPawns.FreeColonistsSpawned)
            {
                if (settings.IsPartyHunting(pawn) && pawn.stances.FullBodyBusy && pawn.Position.InHorDistOf(center.Position, PartyRange))
                {
                    return true;
                }
            }

            return false;
        }

        private static Pawn FindTarget(Pawn searcher, float minRange, float maxRange, GameComponent_OrderTools settings, System.Predicate<Pawn> filter)
        {
            float min = minRange * minRange;
            float max = maxRange * maxRange;
            Map map = searcher.Map;
            Pawn best = null;
            int bestDistance = int.MaxValue;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn target = pawns[i];
                if (target.Faction != null || target.RaceProps == null || !target.RaceProps.Animal)
                {
                    continue;
                }

                int distance = (searcher.Position - target.Position).LengthHorizontalSquared;
                if (distance < min || distance > max || distance >= bestDistance || target.Position.Fogged(map))
                {
                    continue;
                }

                if (settings.huntDesignatedOnly && !OrderToolsUtility.Has(target, DesignationDefOf.Hunt))
                {
                    continue;
                }

                if (filter(target) && AttackTargetFinder.CanSee(searcher, target))
                {
                    best = target;
                    bestDistance = distance;
                }
            }

            return best;
        }
    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(JobDriver_Wait), "CheckForAutoAttack")]
    internal static class JobDriver_Wait_CheckForAutoAttack_PartyHunt_Patch
    {
        private static void Postfix(JobDriver_Wait __instance)
        {
            PartyHuntUtility.Tick(__instance);
        }
    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    internal static class Pawn_DraftController_Drafted_PartyHunt_Patch
    {
        private static void Postfix(Pawn_DraftController __instance, bool value)
        {
            if (!value)
            {
                GameComponent_OrderTools.Instance?.SetPartyHunting(__instance.pawn, false);
            }
        }
    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Pawn_GetGizmos_PartyHunt_Patch
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
        {
            foreach (Gizmo gizmo in values)
            {
                yield return gizmo;
            }

            if (__instance.Drafted && __instance.IsColonistPlayerControlled && GameComponent_OrderTools.Instance != null)
            {
                yield return new Command_PartyHunt(__instance);
            }
        }
    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class Pawn_Kill_PartyHunt_Patch
    {
        private static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
            if (settings != null && settings.huntUnforbid && dinfo?.Instigator is Pawn hunter && settings.IsPartyHunting(hunter))
            {
                __instance.Corpse?.SetForbidden(false, false);
            }
        }
    }

    // 물건을 선택했을 때 뜨는 지정 버튼 목록에 긴급 운반, 마무리, 비슷한 것 선택을 넣는다.
    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(ReverseDesignatorDatabase), "InitDesignators")]
    internal static class ReverseDesignatorDatabase_InitDesignators_OrderTools_Patch
    {
        private static void Postfix(List<Designator> ___desList)
        {
            foreach (Designator designator in new Designator[] { new Designator_HaulUrgently(), new Designator_FinishOff(), new Designator_SelectSimilarReverse() })
            {
                if (Current.Game.Rules.DesignatorAllowed(designator))
                {
                    ___desList.Add(designator);
                }
            }
        }
    }

    // 바닐라 도구의 우클릭 메뉴에 맵 전체 명령을 더한다.
    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Designator), nameof(Designator.RightClickFloatMenuOptions), MethodType.Getter)]
    internal static class Designator_RightClickFloatMenuOptions_OrderTools_Patch
    {
        private static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> values, Designator __instance)
        {
            foreach (FloatMenuOption option in values)
            {
                yield return option;
            }

            Map map = Find.CurrentMap;
            if (map == null)
            {
                yield break;
            }

            if (__instance is Designator_Haul)
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.Haul.All".Translate(), () => OrderToolsUtility.Report(
                    OrderToolsUtility.DesignateAll(__instance, map, ThingRequestGroup.HaulableEver), "Yuudong2.OrderTools.Haul.Done", "Yuudong2.OrderTools.Haul.None"));
            }
            else if (__instance is Designator_Strip)
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.Strip.All".Translate(), () => OrderToolsUtility.Report(
                    OrderToolsUtility.DesignateAll(__instance, map, ThingRequestGroup.Everything, t => (t is Corpse || t is Pawn) && t.Faction != Faction.OfPlayer
                        && (!(t is Corpse corpse) || corpse.InnerPawn?.Faction != Faction.OfPlayer)),
                    "Yuudong2.OrderTools.Strip.Done", "Yuudong2.OrderTools.Strip.None"));
            }
            else if (__instance is Designator_PlantsCut)
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.Cut.Blighted".Translate(), () => OrderToolsUtility.Report(
                    OrderToolsUtility.DesignateAll(__instance, map, ThingRequestGroup.Plant, t => t is Plant plant && plant.Blighted && !OrderToolsUtility.Has(plant, DesignationDefOf.CutPlant)),
                    "Yuudong2.OrderTools.Cut.Done", "Yuudong2.OrderTools.Cut.None"));
            }
            else if (__instance is Designator_Mine)
            {
                yield return new FloatMenuOption("Yuudong2.OrderTools.Mine.Connected".Translate(), () => OrderToolsUtility.Report(
                    MineConnected(map), "Yuudong2.OrderTools.Mine.Done", "Yuudong2.OrderTools.Mine.None"));
            }
        }

        private static Thing OreAt(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map))
            {
                return null;
            }

            Building building = map.edificeGrid[cell];
            return building?.def.building != null && building.def.mineable && building.def.building.isResourceRock ? building : null;
        }

        // 선택한 광석과 이미 채굴 지정된 광석에서 시작해, 같은 종류로 이어진 광석 전부에 채굴 지정을 건다.
        private static int MineConnected(Map map)
        {
            DesignationManager manager = map.designationManager;
            foreach (object obj in Find.Selector.SelectedObjects)
            {
                if (obj is Thing thing && thing.Spawned && thing.Map == map && OreAt(thing.Position, map) != null && manager.DesignationAt(thing.Position, DesignationDefOf.Mine) == null)
                {
                    manager.AddDesignation(new Designation(thing.Position, DesignationDefOf.Mine));
                }
            }

            List<IntVec3> start = manager.SpawnedDesignationsOfDef(DesignationDefOf.Mine).Where(d => !d.target.HasThing).Select(d => d.target.Cell).ToList();
            HashSet<IntVec3> seen = new HashSet<IntVec3>(start);
            Queue<IntVec3> queue = new Queue<IntVec3>(start.Where(c => !c.Fogged(map)));
            int added = 0;
            while (queue.Count > 0)
            {
                IntVec3 from = queue.Dequeue();
                Thing ore = OreAt(from, map);
                if (ore == null)
                {
                    continue;
                }

                foreach (IntVec3 offset in GenAdj.AdjacentCellsAround)
                {
                    IntVec3 to = from + offset;
                    if (seen.Contains(to) || OreAt(to, map)?.def != ore.def)
                    {
                        continue;
                    }

                    seen.Add(to);
                    manager.AddDesignation(new Designation(to, DesignationDefOf.Mine));
                    added++;
                    queue.Enqueue(to);
                }
            }

            return added;
        }
    }

    // 금지·허용 도구의 아이콘을 더 알아보기 쉬운 것으로 바꾼다.
    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Designator_Forbid), MethodType.Constructor)]
    internal static class Designator_Forbid_Icon_Patch
    {
        private static void Postfix(Designator_Forbid __instance)
        {
            __instance.icon = OrderToolsUtility.Icon("forbid");
        }
    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Designator_Unforbid), MethodType.Constructor)]
    internal static class Designator_Unforbid_Icon_Patch
    {
        private static void Postfix(Designator_Unforbid __instance)
        {
            __instance.icon = OrderToolsUtility.Icon("allow");
        }
    }

    // 작업 우선순위는 세이브에 작업 종류의 순서대로만 적힌다. AllowTool이 있던 세이브를 AllowTool 없이 처음 불러오면
    // "운반+"와 "마무리"의 자리가 뒤로 밀리므로, 세이브에 적힌 모드 순서로 옛 자리를 되짚어 값을 제자리에 옮긴다.
    public static class WorkOrderMigration
    {
        public static List<WorkTypeDef> OldOrder(List<string> savedModIds)
        {
            List<string> ids = savedModIds.Select(id => id.ToLowerInvariant().Replace("_steam", string.Empty)).ToList();
            int allowTool = ids.IndexOf(OrderToolsUtility.AllowToolPackageId.ToLowerInvariant());
            if (allowTool < 0)
            {
                return null;
            }

            int Position(WorkTypeDef def)
            {
                if (def.defName == "HaulingUrgent" || def.defName == "FinishingOff")
                {
                    return allowTool;
                }

                int found = ids.IndexOf((def.modContentPack?.PackageId ?? string.Empty).ToLowerInvariant().Replace("_steam", string.Empty));
                return found < 0 ? int.MaxValue : found;
            }

            return DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(Position).ThenBy(d => d.index).ToList();
        }

        public static void Remap(DefMap<WorkTypeDef, int> map, List<WorkTypeDef> order)
        {
            int[] old = new int[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                old[i] = i < map.Count ? map[DefDatabase<WorkTypeDef>.AllDefsListForReading[i]] : 0;
            }

            for (int i = 0; i < order.Count; i++)
            {
                map[order[i]] = old[i];
            }
        }

    }

    [HarmonyPatchCategory(Features.OrderTools)]
    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.ExposeData))]
    internal static class Pawn_WorkSettings_ExposeData_OrderTools_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_WorkSettings, DefMap<WorkTypeDef, int>> Priorities =
            AccessTools.FieldRefAccess<Pawn_WorkSettings, DefMap<WorkTypeDef, int>>("priorities");

        private static List<string> orderSource;

        private static List<WorkTypeDef> oldOrder;

        private static void Postfix(Pawn_WorkSettings __instance)
        {
            if (Scribe.mode != LoadSaveMode.PostLoadInit || Current.ProgramState == ProgramState.Playing)
            {
                return;
            }

            List<string> saved = ScribeMetaHeaderUtility.loadedModIdsList;
            DefMap<WorkTypeDef, int> map = Priorities(__instance);
            if (saved == null || map == null)
            {
                return;
            }

            if (orderSource != saved)
            {
                orderSource = saved;
                oldOrder = WorkOrderMigration.OldOrder(saved);
            }

            if (oldOrder != null)
            {
                WorkOrderMigration.Remap(map, oldOrder);
            }
        }
    }
}

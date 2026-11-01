using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;
using Yuudong2Mod;

// Gene Ripper 포팅. 원본 모드로 지은 건물이 있는 세이브를 그대로 불러올 수 있게 클래스 이름(GeneRipper.*)과
// defName(GeneRipper, CarryToGeneRipper), 저장 필드 이름(ticksRemaining, selectedGene)을 원본과 같게 둔다.
// 동작은 1.7 바닐라 유전자 추출기를 기준으로 다시 짰다.
namespace GeneRipper
{
    [DefOf]
    public static class GeneRipperDefOf
    {
        public static ThingDef GeneRipper;

        static GeneRipperDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(GeneRipperDefOf));
        }
    }

    // 정착민이 아닌 폰(죄수 등)을 유전자 분쇄기로 옮긴다.
    public class WorkGiver_CarryToGeneRipper : WorkGiver_CarryToBuilding
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(GeneRipperDefOf.GeneRipper);

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ModsConfig.BiotechActive;
        }
    }

    // 고른 유전자 하나를 유전자팩으로 뽑는 대신 대상 폰을 죽이는 유전자 추출기.
    [StaticConstructorOnStartup]
    public class Building_GeneRipper : Building_Enterable, IThingHolderWithDrawnPawn
    {
        // 작동 중에는 기본 전력의 4배를 쓴다 (원본과 같음).
        private const float WorkingPowerFactor = 4f;

        private static readonly Texture2D CancelIcon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel");

        private int ticksRemaining;
        private GeneDef selectedGene;

        [Unsaved]
        private CompPowerTrader cachedPowerComp;

        [Unsaved]
        private Texture2D cachedInsertPawnTex;

        [Unsaved]
        private Sustainer sustainerWorking;

        [Unsaved]
        private Effecter progressBar;

        private static int ExtractionTicks => Mathf.Max(1, Yuudong2Settings.Current.geneRipperHours) * GenDate.TicksPerHour;

        private Pawn ContainedPawn => innerContainer.FirstOrDefault() as Pawn;

        public bool PowerOn => PowerTraderComp.PowerOn;

        public override bool IsContentsSuspended => false;

        private CompPowerTrader PowerTraderComp => cachedPowerComp ?? (cachedPowerComp = this.TryGetComp<CompPowerTrader>());

        private Texture2D InsertPawnTex => cachedInsertPawnTex ?? (cachedInsertPawnTex = ContentFinder<Texture2D>.Get("UI/Gizmos/InsertPawn"));

        private IntVec3 DropCell => def.hasInteractionCell ? InteractionCell : Position;

        public float HeldPawnDrawPos_Y => DrawPos.y + 0.03658537f;

        public float HeldPawnBodyAngle => Rotation.AsAngle;

        public PawnPosture HeldPawnPosture => PawnPosture.LayingOnGroundFaceUp;

        public override Vector3 PawnDrawOffset => Vector3.zero;

        public override void PostPostMake()
        {
            if (!ModLister.CheckBiotech("gene ripper"))
            {
                Destroy();
                return;
            }

            base.PostPostMake();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            sustainerWorking = null;
            if (progressBar != null)
            {
                progressBar.Cleanup();
                progressBar = null;
            }

            base.DeSpawn(mode);
        }

        protected override void Tick()
        {
            base.Tick();
            if (this.IsHashIntervalTick(250))
            {
                PowerTraderComp.PowerOutput = -PowerComp.Props.PowerConsumption * (Working ? WorkingPowerFactor : 1f);
            }

            if (Working)
            {
                if (ContainedPawn == null)
                {
                    Reset();
                    return;
                }

                if (PowerOn)
                {
                    TickEffects();
                    ticksRemaining--;
                    if (ticksRemaining <= 0)
                    {
                        Finish();
                    }

                    return;
                }
            }
            else if (selectedPawn != null && selectedPawn.Dead)
            {
                Reset();
            }

            if (progressBar != null)
            {
                progressBar.Cleanup();
                progressBar = null;
            }
        }

        private void TickEffects()
        {
            if (sustainerWorking == null || sustainerWorking.Ended)
            {
                sustainerWorking = SoundDefOf.GeneExtractor_Working.TrySpawnSustainer(SoundInfo.InMap(this, MaintenanceType.PerTick));
            }
            else
            {
                sustainerWorking.Maintain();
            }

            if (progressBar == null)
            {
                progressBar = EffecterDefOf.ProgressBarAlwaysVisible.Spawn();
            }

            progressBar.EffectTick(new TargetInfo(Position + IntVec3.North.RotatedBy(Rotation), Map), TargetInfo.Invalid);
            MoteProgressBar mote = ((SubEffecter_ProgressBar)progressBar.children[0]).mote;
            if (mote != null)
            {
                mote.progress = 1f - Mathf.Clamp01((float)ticksRemaining / ExtractionTicks);
                mote.offsetZ = -0.8f;
            }
        }

        public override AcceptanceReport CanAcceptPawn(Pawn pawn)
        {
            if (!pawn.IsColonist && !pawn.IsSlaveOfColony && !pawn.IsPrisonerOfColony)
            {
                return false;
            }

            if (selectedPawn != null && selectedPawn != pawn)
            {
                return false;
            }

            if (!pawn.RaceProps.Humanlike || pawn.IsQuestLodger())
            {
                return false;
            }

            if (!PowerOn)
            {
                return "NoPower".Translate().CapitalizeFirst();
            }

            if (innerContainer.Count > 0)
            {
                return "Occupied".Translate();
            }

            if (pawn.genes == null || !pawn.genes.GenesListForReading.Any())
            {
                return "PawnHasNoGenes".Translate(pawn.Named("PAWN"));
            }

            if (!pawn.ageTracker.Adult)
            {
                return "Yuudong2.GeneRipper.Child".Translate(pawn.Named("PAWN"));
            }

            if (pawn.health.hediffSet.HasHediff(HediffDefOf.XenogermReplicating))
            {
                return "Yuudong2.GeneRipper.Regrowing".Translate();
            }

            return true;
        }

        public override void TryAcceptPawn(Pawn pawn)
        {
            if (!CanAcceptPawn(pawn))
            {
                return;
            }

            selectedPawn = pawn;
            bool wasSelected = pawn.DeSpawnOrDeselect();
            if (innerContainer.TryAddOrTransfer(pawn))
            {
                startTick = Find.TickManager.TicksGame;
                ticksRemaining = ExtractionTicks;
            }

            if (wasSelected)
            {
                Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
            }
        }

        // 넣기 전에 뽑을 유전자를 고르고, 죽는다는 확인을 받는다.
        protected override void SelectPawn(Pawn pawn)
        {
            Find.WindowStack.Add(new Dialog_SelectGene(pawn, gene =>
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Yuudong2.GeneRipper.KillConfirm".Translate(pawn.NameShortColored, gene.label), () =>
                {
                    selectedGene = gene;
                    base.SelectPawn(pawn);
                }, destructive: true));
            }));
        }

        private void Reset()
        {
            startTick = -1;
            selectedPawn = null;
            selectedGene = null;
            sustainerWorking = null;
            innerContainer.TryDropAll(DropCell, Map, ThingPlaceMode.Near);
        }

        // 중간에 멈추면 진행한 만큼의 확률로 죽고, 살면 진행한 시간에 비례해 유전형질 재생 상태가 된다 (원본과 같음).
        private void Cancel()
        {
            Pawn pawn = ContainedPawn;
            if (pawn != null)
            {
                int elapsed = ExtractionTicks - ticksRemaining;
                if (Rand.Range(0f, ExtractionTicks) < elapsed)
                {
                    startTick = -1;
                    selectedPawn = null;
                    selectedGene = null;
                    sustainerWorking = null;
                    KillOccupant(pawn);
                    return;
                }

                GeneUtility.ExtractXenogerm(pawn, Mathf.Max(1, 60 * elapsed));
            }

            Reset();
        }

        private void Finish()
        {
            Pawn pawn = ContainedPawn;
            GeneDef gene = selectedGene;
            if (pawn == null || gene == null || pawn.genes?.GetGene(gene) == null)
            {
                // 고른 유전자가 그사이 사라졌으면 아무것도 뽑지 않고 내보낸다.
                Reset();
                return;
            }

            startTick = -1;
            selectedPawn = null;
            selectedGene = null;
            sustainerWorking = null;

            Genepack genepack = (Genepack)ThingMaker.MakeThing(ThingDefOf.Genepack);
            genepack.Initialize(new List<GeneDef> { gene });
            GenPlace.TryPlaceThing(genepack, DropCell, Map, ThingPlaceMode.Near);
            KillOccupant(pawn);
            Messages.Message("GeneExtractionComplete".Translate(pawn.Named("PAWN")) + ": " + gene.label.CapitalizeFirst(), new LookTargets(pawn, genepack), MessageTypeDefOf.PositiveEvent);
        }

        private void KillOccupant(Pawn occupant)
        {
            Map map = Map;
            IntVec3 cell = DropCell;
            DamageInfo dinfo = new DamageInfo(DamageDefOf.ExecutionCut, 9999f, 999f, -1f, null, occupant.health.hediffSet.GetBrain());
            dinfo.SetAllowDamagePropagation(false);
            occupant.forceNoDeathNotification = true;
            occupant.Kill(dinfo);
            occupant.forceNoDeathNotification = false;
            ThoughtUtility.GiveThoughtsForPawnExecuted(occupant, null, PawnExecutionKind.GenericBrutal);

            List<Thing> dropped = new List<Thing>();
            innerContainer.TryDropAll(cell, map, ThingPlaceMode.Near, (thing, count) => dropped.Add(thing));
            if (!Rand.Chance(Yuudong2Settings.Current.geneRipperBlendChance))
            {
                return;
            }

            // 갈려 나오면 시체 대신 고기와 영양죽, 피가 남는다. 원본과 달리 입고 있던 옷과 무기는 떨어뜨린다.
            ThingDef meatDef = occupant.RaceProps.meatDef ?? ThingDefOf.Meat_Human;
            foreach (Corpse corpse in dropped.OfType<Corpse>())
            {
                if (corpse.AnythingToStrip())
                {
                    corpse.Strip();
                }

                corpse.Destroy();
            }

            Thing paste = ThingMaker.MakeThing(ThingDefOf.MealNutrientPaste);
            paste.TryGetComp<CompIngredients>()?.RegisterIngredient(meatDef);
            paste.stackCount = Rand.RangeInclusive(5, 10);
            GenPlace.TryPlaceThing(paste, cell, map, ThingPlaceMode.Near);

            Thing meat = ThingMaker.MakeThing(meatDef);
            meat.stackCount = Rand.RangeInclusive(20, 50);
            GenPlace.TryPlaceThing(meat, cell, map, ThingPlaceMode.Near);

            ThingDef blood = occupant.RaceProps.BloodDef ?? ThingDefOf.Filth_Blood;
            for (int i = 0; i < 5; i++)
            {
                if (CellFinder.TryFindRandomCellNear(cell, map, 2, c => c.Standable(map), out IntVec3 filthCell))
                {
                    FilthMaker.TryMakeFilth(filthCell, map, blood);
                }
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(selPawn))
            {
                yield return option;
            }

            if (!selPawn.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly))
            {
                yield return new FloatMenuOption("CannotEnterBuilding".Translate(this) + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                yield break;
            }

            AcceptanceReport report = CanAcceptPawn(selPawn);
            if (report.Accepted)
            {
                yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption("EnterBuilding".Translate(this), () => SelectPawn(selPawn)), selPawn, this);
            }
            else if (SelectedPawn == selPawn && !selPawn.IsPrisonerOfColony)
            {
                yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption("EnterBuilding".Translate(this), () =>
                {
                    selPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterBuilding, this), JobTag.Misc);
                }), selPawn, this);
            }
            else if (!report.Reason.NullOrEmpty())
            {
                yield return new FloatMenuOption("CannotEnterBuilding".Translate(this) + ": " + report.Reason.CapitalizeFirst(), null);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (Working)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandCancelExtraction".Translate(),
                    defaultDesc = "Yuudong2.GeneRipper.CancelDesc".Translate(),
                    icon = CancelIcon,
                    action = Cancel,
                    activateSound = SoundDefOf.Designate_Cancel,
                };
                if (DebugSettings.ShowDevGizmos)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Finish extraction",
                        action = Finish,
                    };
                }

                yield break;
            }

            if (selectedPawn != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandCancelLoad".Translate(),
                    defaultDesc = "CommandCancelLoadDesc".Translate(),
                    icon = CancelIcon,
                    activateSound = SoundDefOf.Designate_Cancel,
                    action = () =>
                    {
                        if (selectedPawn.CurJobDef == JobDefOf.EnterBuilding)
                        {
                            selectedPawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        }

                        Reset();
                    },
                };
                yield break;
            }

            Command_Action insert = new Command_Action
            {
                defaultLabel = "InsertPerson".Translate() + "...",
                defaultDesc = "Yuudong2.GeneRipper.InsertDesc".Translate(),
                icon = InsertPawnTex,
                action = () =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (Pawn pawn in Map.mapPawns.AllPawnsSpawned.Where(p => p.genes != null).OrderBy(p => p.IsColonist ? 0 : 1).ThenBy(p => p.Label))
                    {
                        AcceptanceReport report = CanAcceptPawn(pawn);
                        string label = pawn.LabelShortCap + ", " + pawn.genes.XenotypeLabelCap;
                        if (report.Accepted)
                        {
                            options.Add(new FloatMenuOption(label, () => SelectPawn(pawn), pawn, Color.white));
                        }
                        else if (!report.Reason.NullOrEmpty())
                        {
                            options.Add(new FloatMenuOption(label + ": " + report.Reason, null, pawn, Color.white));
                        }
                    }

                    if (!options.Any())
                    {
                        options.Add(new FloatMenuOption("NoExtractablePawns".Translate(), null));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
            if (!PowerOn)
            {
                insert.Disable("NoPower".Translate().CapitalizeFirst());
            }

            yield return insert;
        }

        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            base.DynamicDrawPhaseAt(phase, drawLoc, flip);
            if (Working && ContainedPawn != null)
            {
                ContainedPawn.Drawer.renderer.DynamicDrawPhaseAt(phase, drawLoc + PawnDrawOffset, null, neverAimWeapon: true);
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (selectedPawn != null && innerContainer.Count == 0)
            {
                text = AddLine(text, "WaitingForPawn".Translate(selectedPawn.Named("PAWN")).Resolve());
            }
            else if (Working && ContainedPawn != null)
            {
                text = AddLine(text, "ExtractingXenogermFrom".Translate(ContainedPawn.Named("PAWN")).Resolve());
                text = AddLine(text, PowerOn ? "DurationLeft".Translate(ticksRemaining.ToStringTicksToPeriod()).Resolve() : "Yuudong2.GeneRipper.Paused".Translate().Resolve());
            }

            if (selectedGene != null)
            {
                text = AddLine(text, "Yuudong2.GeneRipper.Target".Translate(selectedGene.LabelCap).Resolve());
            }

            return text;
        }

        private static string AddLine(string text, string line)
        {
            return text.NullOrEmpty() ? line : text + "\n" + line;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ticksRemaining, "ticksRemaining", 0);
            Scribe_Defs.Look(ref selectedGene, "selectedGene");
        }
    }

    // 대상 폰의 내재·외래 유전자를 보여 주고 하나를 고르게 한다. 그리기는 바닐라 유전자 UI를 쓴다.
    public class Dialog_SelectGene : Window
    {
        private const float GeneSize = 90f;
        private const float GeneGap = 6f;

        private readonly Pawn target;
        private readonly System.Action<GeneDef> accept;
        private readonly List<Gene> endogenes = new List<Gene>();
        private readonly List<Gene> xenogenes = new List<Gene>();
        private Vector2 scrollPosition;
        private float viewHeight;
        private GeneDef selected;

        public override Vector2 InitialSize => new Vector2(736f, 700f);

        public Dialog_SelectGene(Pawn target, System.Action<GeneDef> accept)
        {
            this.target = target;
            this.accept = accept;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;

            // 피부색 유전자가 따로 있으면 바닐라 유전자 창처럼 기본 멜라닌 유전자는 숨긴다.
            bool skinOverride = target.genes.Endogenes.Any(g => g.def.skinColorOverride.HasValue);
            endogenes.AddRange(target.genes.Endogenes.Where(g => g.def.endogeneCategory != EndogeneCategory.Melanin || !skinOverride));
            xenogenes.AddRange(target.genes.Xenogenes);
            endogenes.SortGenes();
            xenogenes.SortGenes();
        }

        public override void PostOpen()
        {
            if (!ModLister.CheckBiotech("genes viewing"))
            {
                Close(false);
                return;
            }

            base.PostOpen();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect header = new Rect(inRect.x + 34f, inRect.y, inRect.width - 34f, 30f);
            Text.Font = GameFont.Medium;
            Widgets.Label(header, "ViewGenes".Translate() + ": " + target.genes.XenotypeLabelCap);
            Text.Font = GameFont.Small;
            GUI.color = XenotypeDef.IconColor;
            GUI.DrawTexture(new Rect(inRect.x, inRect.y, 30f, 30f), target.genes.XenotypeIcon);
            GUI.color = Color.white;

            Rect outRect = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f - CloseButSize.y - 10f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(viewHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0f;
            if (endogenes.Any())
            {
                DrawSection(viewRect.width, "Endogenes", "EndogenesDesc", endogenes, GeneType.Endogene, ref curY);
                curY += 12f;
            }

            DrawSection(viewRect.width, "Xenogenes", "XenogenesDesc", xenogenes, GeneType.Xenogene, ref curY);
            viewHeight = curY;
            Widgets.EndScrollView();

            float buttonY = inRect.yMax - CloseButSize.y;
            if (Widgets.ButtonText(new Rect(inRect.xMax - CloseButSize.x, buttonY, CloseButSize.x, CloseButSize.y), "Cancel".Translate()))
            {
                Close();
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - CloseButSize.x * 2f - 6f, buttonY, CloseButSize.x, CloseButSize.y), "Yuudong2.GeneRipper.Select".Translate(), active: selected != null) && selected != null)
            {
                Close();
                accept(selected);
            }
        }

        private void DrawSection(float width, string labelKey, string descKey, List<Gene> genes, GeneType type, ref float curY)
        {
            Widgets.Label(10f, ref curY, width, labelKey.Translate().CapitalizeFirst(), descKey.Translate());
            if (!genes.Any())
            {
                GUI.color = ColoredText.SubtleGrayColor;
                Widgets.Label(new Rect(10f, curY, width, Text.LineHeight), "(" + "NoXenogermImplanted".Translate() + ")");
                GUI.color = Color.white;
                curY += Text.LineHeight + 12f;
                return;
            }

            int perRow = Mathf.Max(1, (int)((width - 12f + GeneGap) / (GeneSize + GeneGap)));
            int rows = (genes.Count + perRow - 1) / perRow;
            float height = rows * (GeneSize + GeneGap) + 12f;
            Widgets.DrawMenuSection(new Rect(0f, curY, width, height));
            float left = (width - perRow * (GeneSize + GeneGap) + GeneGap) / 2f;
            for (int i = 0; i < genes.Count; i++)
            {
                Rect rect = new Rect(left + i % perRow * (GeneSize + GeneGap), curY + 6f + i / perRow * (GeneSize + GeneGap), GeneSize, GeneSize);
                GeneUIUtility.DrawGene(genes[i], rect, type, doBackground: true, clickable: false);
                if (selected == genes[i].def)
                {
                    Widgets.DrawBox(rect, 2);
                    Widgets.DrawHighlight(rect);
                }

                if (Widgets.ButtonInvisible(rect))
                {
                    selected = genes[i].def;
                }
            }

            curY += height;
        }
    }
}

namespace Yuudong2Mod
{
    internal static class GeneRipperUtility
    {
        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.GeneRipper.Hours".Translate(s.geneRipperHours), -1f, "Yuudong2.GeneRipper.Hours.Desc".Translate());
            s.geneRipperHours = Mathf.RoundToInt(listing.Slider(s.geneRipperHours, 1f, 72f));
            listing.Label("Yuudong2.GeneRipper.Blend".Translate(s.geneRipperBlendChance.ToStringPercent()), -1f, "Yuudong2.GeneRipper.Blend.Desc".Translate());
            s.geneRipperBlendChance = Mathf.Round(listing.Slider(s.geneRipperBlendChance, 0f, 1f) * 20f) / 20f;
            listing.End();
        }
    }
}

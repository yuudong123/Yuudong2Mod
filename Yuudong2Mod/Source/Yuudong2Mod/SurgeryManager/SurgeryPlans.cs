using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Yuudong2Mod.SurgeryManager
{
    // 계획 항목 하나: 이 부위(같은 종류의 부위 전부, 예: 양쪽 눈)에 이 상태 이상(부품·임플란트)이 있어야 한다.
    public class SurgeryPlanEntry : IExposable
    {
        public HediffDef hediff;
        public BodyPartDef part;

        public SurgeryPlanEntry()
        {
        }

        public SurgeryPlanEntry(HediffDef hediff, BodyPartDef part)
        {
            this.hediff = hediff;
            this.part = part;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref hediff, "hediff");
            Scribe_Defs.Look(ref part, "part");
        }
    }

    public class SurgeryPreset : IExposable
    {
        public int id;
        public string label;
        public List<SurgeryPlanEntry> entries = new List<SurgeryPlanEntry>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref label, "label");
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                entries = entries ?? new List<SurgeryPlanEntry>();
                entries.RemoveAll(e => e == null || e.hediff == null || e.part == null);
            }
        }

        public bool Contains(HediffDef hediff, BodyPartDef part)
        {
            return entries.Any(e => e.hediff == hediff && e.part == part);
        }
    }

    // 계획 프리셋과 정착민별 지정, 창 옵션을 세이브에 남긴다. 기능을 꺼도 이 컴포넌트는 데이터만 들고 있다.
    public class GameComponent_SurgeryPlans : GameComponent
    {
        public const int NoPlanId = -1;

        public List<SurgeryPreset> presets = new List<SurgeryPreset>();
        public bool onlyIncomplete;
        public bool onlyWithMaterials = true;
        public bool nanobotsOneAtATime = true;
        public bool sortByProgress;

        private Dictionary<Pawn, int> assignments = new Dictionary<Pawn, int>();
        private int nextId = 1;
        private List<Pawn> tmpPawns;
        private List<int> tmpIds;

        public GameComponent_SurgeryPlans(Game game)
        {
        }

        public static GameComponent_SurgeryPlans Instance => Current.Game?.GetComponent<GameComponent_SurgeryPlans>();

        public override void FinalizeInit()
        {
            if (presets.Count == 0)
            {
                presets.Add(DefaultPresets.Create(NextId()));
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref presets, "presets", LookMode.Deep);
            Scribe_Collections.Look(ref assignments, "assignments", LookMode.Reference, LookMode.Value, ref tmpPawns, ref tmpIds);
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref onlyIncomplete, "onlyIncomplete");
            Scribe_Values.Look(ref onlyWithMaterials, "onlyWithMaterials", true);
            Scribe_Values.Look(ref nanobotsOneAtATime, "nanobotsOneAtATime", true);
            Scribe_Values.Look(ref sortByProgress, "sortByProgress");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                presets = presets ?? new List<SurgeryPreset>();
                presets.RemoveAll(p => p == null);
                assignments = assignments ?? new Dictionary<Pawn, int>();
                assignments.RemoveAll(kv => kv.Key == null);
            }
        }

        public int NextId()
        {
            return nextId++;
        }

        // 지정하지 않은 정착민은 첫 번째 프리셋을 따른다.
        public SurgeryPreset PresetFor(Pawn pawn)
        {
            if (assignments.TryGetValue(pawn, out int id))
            {
                return id == NoPlanId ? null : presets.Find(p => p.id == id) ?? presets.FirstOrDefault();
            }

            return presets.FirstOrDefault();
        }

        public void Assign(Pawn pawn, SurgeryPreset preset)
        {
            assignments[pawn] = preset?.id ?? NoPlanId;
        }

        public void RemovePreset(SurgeryPreset preset)
        {
            presets.Remove(preset);
            List<Pawn> pawns = new List<Pawn>();
            foreach (KeyValuePair<Pawn, int> kv in assignments)
            {
                if (kv.Value == preset.id)
                {
                    pawns.Add(kv.Key);
                }
            }

            foreach (Pawn pawn in pawns)
            {
                assignments.Remove(pawn);
            }
        }
    }

    // 사용자 계획(팔다리 초월공학, 강화 척추, 각막+본능 최적화+생체 최적화 눈, 합성 장기, 갈비뼈 8종, 몸통 6종, 뇌 4종)을 기본 프리셋으로 만든다.
    // 이 게임에 없는 부품(모드가 빠진 경우)은 건너뛴다.
    public static class DefaultPresets
    {
        private static readonly string[][] Entries =
        {
            new[] { "ArchotechArm", "Shoulder" },
            new[] { "ArchotechLeg", "Leg" },
            new[] { "RK_Hediff_ArchotechTail", "RK_BodyPart_Tail" },
            new[] { "AdvancedBionicSpine", "Spine" },
            new[] { "TacticalCorneaImplant", "Eye" },
            new[] { "EPOE_InstinctOptimizedEyes", "Eye" },
            new[] { "EPOE_OrganicOptimized", "Eye" },
            new[] { "AdvancedBionicEar", "Ear" },
            new[] { "AdvancedBionicJaw", "Jaw" },
            new[] { "AestheticNose", "Nose" },
            new[] { "BionicHeart", "Heart" },
            new[] { "SyntheticLung", "Lung" },
            new[] { "SyntheticKidney", "Kidney" },
            new[] { "SyntheticLiver", "Liver" },
            new[] { "NuclearStomach", "Stomach" },
            new[] { "MedicalRib", "Ribcage" },
            new[] { "AdrenalineRib", "Ribcage" },
            new[] { "PainkillerRib", "Ribcage" },
            new[] { "HeaterRib", "Ribcage" },
            new[] { "CoolerRib", "Ribcage" },
            new[] { "CoagulatorRib", "Ribcage" },
            new[] { "RespirationRib", "Ribcage" },
            new[] { "WakeUpRib", "Ribcage" },
            new[] { "EPIA_ProtectiveExoskeleton", "Torso" },
            new[] { "HealingEnhancer", "Torso" },
            new[] { "Coagulator", "Torso" },
            new[] { "ToughskinGland", "Torso" },
            new[] { "AestheticShaper", "Torso" },
            new[] { "LoveEnhancer", "Torso" },
            new[] { "AIPersonaCore", "Brain" },
            new[] { "Neurocalculator", "Brain" },
            new[] { "LearningAssistant", "Brain" },
            new[] { "CircadianAssistant", "Brain" },
        };

        public static SurgeryPreset Create(int id)
        {
            SurgeryPreset preset = new SurgeryPreset { id = id, label = "Yuudong2.Surgery.DefaultPresetName".Translate() };
            foreach (string[] entry in Entries)
            {
                HediffDef hediff = DefDatabase<HediffDef>.GetNamedSilentFail(entry[0]);
                BodyPartDef part = DefDatabase<BodyPartDef>.GetNamedSilentFail(entry[1]);
                if (hediff != null && part != null && SurgeryPlanner.RecipesFor(hediff, part).Count > 0)
                {
                    preset.entries.Add(new SurgeryPlanEntry(hediff, part));
                }
            }

            return preset;
        }
    }
}

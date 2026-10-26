using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod.Rpg
{
    // RPG Style Level Up Mod 포팅.
    // 폰이 스킬 경험치와 처치로 레벨 경험치를 얻고, 레벨업 포인트를 힘·재주·민첩·체력·지능·매력에 나눠 여러 스탯을 올린다.
    // 저장 필드 이름은 원본(FP_RSLUM_*)과 같아서 기존 저장 파일의 레벨과 능력치를 그대로 이어받는다.
    public enum RpgAttribute
    {
        STR,
        DEX,
        AGL,
        CON,
        INT,
        CHA,
    }

    public class CompRpgLevel : ThingComp
    {
        private const int AnimalExpInterval = 600;
        private const int HealInterval = 1200;
        public const int HealThreshold = 200;

        public int level = -1;
        public int exp;
        public int need_exp = 5000;
        public int StatPoint = 1;
        public int[] values = { -40, -40, -40, -40, -40, -40 };
        public RpgAttribute? autoAttribute;

        private int expTicks;
        private int healTicks;

        public static int levelAverage = -1;
        private static int piIndex;

        public int Get(RpgAttribute attribute) => values[(int)attribute];

        public void Set(RpgAttribute attribute, int value) => values[(int)attribute] = value;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (level != -1)
            {
                return;
            }

            Yuudong2Settings s = Yuudong2Settings.Current;
            if (s.rpgFlatStartingStats)
            {
                level = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = 0;
                }
            }
            else
            {
                level++;
                RollStartingStats();
            }

            if (s.rpgLevelScaling == 0)
            {
                levelAverage = 0;
            }

            if (levelAverage == -1)
            {
                levelAverage = ComputeLevelAverage(s.rpgLevelScaling);
            }

            if (level <= 0)
            {
                return;
            }

            // 원본과 같이 원주율 숫자를 차례로 능력치에 더하며 평균 레벨만큼 나눠 준다.
            level = levelAverage;
            int remaining = levelAverage;
            bool done = false;
            do
            {
                for (int i = 0; i < values.Length; i++)
                {
                    int digit = RpgEffects.PiDigits[piIndex];
                    piIndex = (piIndex + 1) % RpgEffects.PiDigits.Length;
                    values[i] += digit;
                    remaining -= digit;
                    if (remaining <= 0)
                    {
                        done = true;
                        break;
                    }
                }
            }
            while (!done && remaining > 0);
        }

        public void RollStartingStats()
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = Rand.Range(s.rpgStartingStatMin, s.rpgStartingStatMax);
            }
        }

        private static int ComputeLevelAverage(int scaling)
        {
            IEnumerable<Pawn> colonists = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists;
            int total = 0;
            int count = 0;
            foreach (Pawn pawn in colonists ?? Enumerable.Empty<Pawn>())
            {
                CompRpgLevel comp = pawn.GetComp<CompRpgLevel>();
                if (comp != null)
                {
                    total += comp.level;
                    count++;
                }
            }

            return count == 0 ? 0 : total / count * scaling / 100;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref level, "FP_RSLUM_level", 0, true);
            Scribe_Values.Look(ref exp, "FP_RSLUM_exp", 0, true);
            Scribe_Values.Look(ref need_exp, "FP_RSLUM_need_exp", 10000, true);
            Scribe_Values.Look(ref StatPoint, "FP_RSLUM_StatPoint", 1, true);
            foreach (RpgAttribute attribute in RpgEffects.Attributes)
            {
                int value = Get(attribute);
                Scribe_Values.Look(ref value, "FP_RSLUM_" + attribute, 0, true);
                Set(attribute, value);
            }

            // 원본은 능력치마다 자동 분배 여부를 따로 저장했다. 하나만 켜질 수 있으므로 첫 번째 것만 쓴다.
            foreach (RpgAttribute attribute in RpgEffects.Attributes)
            {
                bool auto = autoAttribute == attribute;
                Scribe_Values.Look(ref auto, "FP_RSLUM_" + attribute + "auto", false, true);
                if (Scribe.mode == LoadSaveMode.LoadingVars && auto && autoAttribute == null)
                {
                    autoAttribute = attribute;
                }
            }
        }

        public bool CanLevelUp => Yuudong2Settings.Current.rpgMaxLevel == 0 || level < Yuudong2Settings.Current.rpgMaxLevel;

        public void GainExp(int amount)
        {
            exp += amount;
            if (exp > need_exp && CanLevelUp)
            {
                LevelUp();
            }
        }

        public static int NeedExpFor(int level)
        {
            return (int)Math.Ceiling(10000.0 * Math.Log(level + 1) * (1.0 + 0.01 * level));
        }

        public void LevelUp()
        {
            while (exp > need_exp && CanLevelUp)
            {
                level++;
                StatPoint++;
                exp -= need_exp;
                need_exp = NeedExpFor(level);
                if (autoAttribute.HasValue)
                {
                    StatPoint--;
                    Set(autoAttribute.Value, Get(autoAttribute.Value) + 1);
                }
            }
        }

        public override void CompTickInterval(int delta)
        {
            base.CompTickInterval(delta);
            if (!(parent is Pawn pawn) || !pawn.Spawned)
            {
                return;
            }

            if (pawn.RaceProps.Animal)
            {
                expTicks += delta;
                if (expTicks > AnimalExpInterval)
                {
                    expTicks = 0;
                    levelAverage = -1;
                    GainExp(Yuudong2Settings.Current.rpgAnimalExpPer10Seconds);
                }
            }

            if (Get(RpgAttribute.CON) <= HealThreshold)
            {
                return;
            }

            healTicks += delta;
            if (healTicks <= HealInterval || pawn.health == null)
            {
                return;
            }

            healTicks = 0;
            List<Hediff_Injury> injuries = new List<Hediff_Injury>();
            pawn.health.hediffSet.GetHediffs(ref injuries, x => x.CanHealNaturally() || x.CanHealFromTending());
            if (injuries.TryRandomElement(out Hediff_Injury injury))
            {
                injury.Heal(0.01f * (Get(RpgAttribute.CON) - 100));
            }
        }
    }

    // 능력치가 스탯을 바꾸는 방식.
    public enum RpgEffectMode
    {
        Multiply,          // ×(1 + 능력치 × 가중치 %)
        ReduceToHalf,      // ×max(1 - 0.3% × 능력치, 50%)
        YieldBonus,        // ×(1 + min(0.2% × 능력치, 50%))
        BandwidthBonus,    // +능력치 / 10 (정수)
        ComfyMin,          // -0.3°C × max(0, 능력치)
        ComfyMax,          // +0.3°C × max(0, 능력치)
        VacuumResistance,  // +min(3% × 능력치, 50%)
    }

    public class RpgEffect
    {
        public RpgAttribute attribute;
        public string statDefName;
        public RpgEffectMode mode;
        public float weight;
        public StatDef stat;

        public RpgEffect(RpgAttribute attribute, string statDefName, RpgEffectMode mode, float weight = 0f)
        {
            this.attribute = attribute;
            this.statDefName = statDefName;
            this.mode = mode;
            this.weight = weight;
        }

        public float Apply(int value, float statValue)
        {
            switch (mode)
            {
                case RpgEffectMode.Multiply: return statValue * Factor(value);
                case RpgEffectMode.ReduceToHalf: return statValue * Factor(value);
                case RpgEffectMode.YieldBonus: return statValue * Factor(value);
                case RpgEffectMode.BandwidthBonus: return statValue + (int)(0.1 * value);
                case RpgEffectMode.ComfyMin: return statValue - 0.3f * Mathf.Max(0, value);
                case RpgEffectMode.ComfyMax: return statValue + 0.3f * Mathf.Max(0, value);
                case RpgEffectMode.VacuumResistance: return statValue + Mathf.Min(0.5f, 0.03f * value);
                default: return statValue;
            }
        }

        public float Factor(int value)
        {
            switch (mode)
            {
                case RpgEffectMode.Multiply: return 1f + 0.01f * value * weight;
                case RpgEffectMode.ReduceToHalf: return Mathf.Max(1f - 0.003f * value, 0.5f);
                case RpgEffectMode.YieldBonus: return 1f + Mathf.Min(0.002f * value, 0.5f);
                default: return 1f;
            }
        }

        // 현재 능력치에서의 효과를 사람이 읽을 수 있게 쓴다.
        public string DescribeCurrent(int value)
        {
            switch (mode)
            {
                case RpgEffectMode.BandwidthBonus: return "+" + (int)(0.1 * value);
                case RpgEffectMode.ComfyMin: return "-" + (0.3f * Mathf.Max(0, value)).ToStringTemperatureOffset("F1");
                case RpgEffectMode.ComfyMax: return "+" + (0.3f * Mathf.Max(0, value)).ToStringTemperatureOffset("F1");
                case RpgEffectMode.VacuumResistance: return "+" + Mathf.Min(0.5f, 0.03f * value).ToStringPercent();
                default: return "x" + Factor(value).ToStringPercent();
            }
        }

        // 능력치 1포인트당 효과와 한계.
        public string DescribePerPoint()
        {
            switch (mode)
            {
                case RpgEffectMode.Multiply: return "Yuudong2.Rpg.Effect.Multiply".Translate((weight).ToString("0.##"));
                case RpgEffectMode.ReduceToHalf: return "Yuudong2.Rpg.Effect.ReduceToHalf".Translate();
                case RpgEffectMode.YieldBonus: return "Yuudong2.Rpg.Effect.Yield".Translate();
                case RpgEffectMode.BandwidthBonus: return "Yuudong2.Rpg.Effect.Bandwidth".Translate();
                case RpgEffectMode.ComfyMin: return "Yuudong2.Rpg.Effect.ComfyMin".Translate();
                case RpgEffectMode.ComfyMax: return "Yuudong2.Rpg.Effect.ComfyMax".Translate();
                case RpgEffectMode.VacuumResistance: return "Yuudong2.Rpg.Effect.Vacuum".Translate();
                default: return "";
            }
        }
    }

    public class StatPart_Rpg : StatPart
    {
        public RpgEffect effect;

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.HasThing && req.Thing is Pawn pawn && pawn.GetComp<CompRpgLevel>() is CompRpgLevel comp)
            {
                val = effect.Apply(comp.Get(effect.attribute), val);
            }
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (req.HasThing && req.Thing is Pawn pawn && pawn.GetComp<CompRpgLevel>() is CompRpgLevel comp)
            {
                return RpgEffects.AttributeLabel(effect.attribute) + ": " + effect.DescribeCurrent(comp.Get(effect.attribute));
            }

            return null;
        }
    }

    public static class RpgEffects
    {
        public static readonly RpgAttribute[] Attributes = (RpgAttribute[])Enum.GetValues(typeof(RpgAttribute));

        // 1.7 바닐라에서 원본 XML 패치가 실제로 적용하던 가중치.
        public static readonly List<RpgEffect> All = new List<RpgEffect>
        {
            new RpgEffect(RpgAttribute.STR, "MiningSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.STR, "SmoothingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.STR, "ConstructionSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.STR, "PlantWorkSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.STR, "CarryingCapacity", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.STR, "CarryWeight", RpgEffectMode.Multiply, 1f),

            new RpgEffect(RpgAttribute.DEX, "MeleeHitChance", RpgEffectMode.Multiply, 0.2f),
            new RpgEffect(RpgAttribute.DEX, "ShootingAccuracyPawn", RpgEffectMode.Multiply, 0.2f),
            new RpgEffect(RpgAttribute.DEX, "CookSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "ButcheryFleshSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "ButcheryMechanoidSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "DrugCookingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "ForagedNutritionPerDay", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "GeneralLaborSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "MechFormingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "MechRepairSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "SubcoreEncodingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "FishingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "FishingYield", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.DEX, "PilotingAbility", RpgEffectMode.Multiply, 0.5f),

            new RpgEffect(RpgAttribute.AGL, "AimingDelayFactor", RpgEffectMode.ReduceToHalf),
            new RpgEffect(RpgAttribute.AGL, "MeleeDodgeChance", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.AGL, "HuntingStealth", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.AGL, "MoveSpeed", RpgEffectMode.Multiply, 0.5f),

            new RpgEffect(RpgAttribute.CON, "UnskilledLaborSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.CON, "ImmunityGainSpeed", RpgEffectMode.Multiply, 0.2f),
            new RpgEffect(RpgAttribute.CON, "RestRateMultiplier", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.CON, "ComfyTemperatureMin", RpgEffectMode.ComfyMin),
            new RpgEffect(RpgAttribute.CON, "ComfyTemperatureMax", RpgEffectMode.ComfyMax),
            new RpgEffect(RpgAttribute.CON, "IncomingDamageFactor", RpgEffectMode.ReduceToHalf),
            new RpgEffect(RpgAttribute.CON, "VacuumResistance", RpgEffectMode.VacuumResistance),
            new RpgEffect(RpgAttribute.CON, "StonecuttingSpeed", RpgEffectMode.Multiply, 0.5f),

            new RpgEffect(RpgAttribute.INT, "MedicalTendSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "MedicalTendQuality", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.INT, "MedicalOperationSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "MedicalSurgerySuccessChance", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.INT, "DrugSynthesisSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "ResearchSpeed", RpgEffectMode.Multiply, 0.2f),
            new RpgEffect(RpgAttribute.INT, "PlantHarvestYield", RpgEffectMode.YieldBonus),
            new RpgEffect(RpgAttribute.INT, "ButcheryFleshEfficiency", RpgEffectMode.YieldBonus),
            new RpgEffect(RpgAttribute.INT, "ButcheryMechanoidEfficiency", RpgEffectMode.YieldBonus),
            new RpgEffect(RpgAttribute.INT, "MechBandwidth", RpgEffectMode.BandwidthBonus),
            new RpgEffect(RpgAttribute.INT, "MechRemoteRepairDistance", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "MechRemoteShieldDistance", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "WorkSpeedGlobalOffsetMech", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "MeditationPlantGrowthOffset", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.INT, "PsychicEntropyGain", RpgEffectMode.ReduceToHalf),
            new RpgEffect(RpgAttribute.INT, "MeditationFocusGain", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "PsychicEntropyMax", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "ReadingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "HackingSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.INT, "HackingStealth", RpgEffectMode.Multiply, 0.5f),

            new RpgEffect(RpgAttribute.CHA, "NegotiationAbility", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.CHA, "TameAnimalChance", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.CHA, "TrainAnimalChance", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.CHA, "AnimalGatherSpeed", RpgEffectMode.Multiply, 0.5f),
            new RpgEffect(RpgAttribute.CHA, "AnimalGatherYield", RpgEffectMode.Multiply, 0.8f),
            new RpgEffect(RpgAttribute.CHA, "ArrestSuccessChance", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.CHA, "ConversionPower", RpgEffectMode.Multiply, 0.2f),
            new RpgEffect(RpgAttribute.CHA, "SuppressionPower", RpgEffectMode.Multiply, 0.1f),
            new RpgEffect(RpgAttribute.CHA, "PruningSpeed", RpgEffectMode.Multiply, 0.1f),
        };

        public static readonly byte[] PiDigits = BuildPiDigits();

        private static byte[] BuildPiDigits()
        {
            const string digits =
                "1415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679" +
                "8214808651328230664709384460955058223172535940812848111745028410270193852110555964462294895493038196" +
                "4428810975665933446128475648233786783165271201909145648566923460348610454326648213393607260249141273" +
                "7245870066063155881748815209209628292540917153643678925903600113305305488204665213841469519415116094" +
                "3305727036575959195309218611738193261179310511854807446237996274956735188575272489122793818301194912" +
                "9833673362440656643086021394946395224737190702179860943702770539217176293176752384674818467669405132" +
                "0005681271452635608277857713427577896091736371787214684409012249534301465495853710507922796892589235" +
                "4201995611212902196086403441815981362977477130996051870721134999999837297804995105973173281609631859" +
                "5024459455346908302642522308253344685035261931188171010003137838752886587533208381420617177669147303" +
                "5982534904287554687311595628638823537875937519577818577805321712268066130019278766111959092164201989";
            byte[] result = new byte[digits.Length];
            for (int i = 0; i < digits.Length; i++)
            {
                result[i] = (byte)(digits[i] - '0');
            }

            return result;
        }

        public static string AttributeLabel(RpgAttribute attribute)
        {
            return ("Yuudong2.Rpg.Attr." + attribute).Translate();
        }

        public static IEnumerable<RpgEffect> EffectsOf(RpgAttribute attribute)
        {
            return All.Where(e => e.attribute == attribute && e.stat != null);
        }

        // 게임 시작 시 폰 Def에 레벨 컴프와 정보 탭을, 스탯 Def에 능력치 효과를 붙인다.
        public static void Apply()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.race == null || !typeof(Pawn).IsAssignableFrom(def.thingClass))
                {
                    continue;
                }

                if (def.comps == null)
                {
                    def.comps = new List<CompProperties>();
                }

                if (!def.comps.Any(c => c.compClass == typeof(CompRpgLevel)))
                {
                    def.comps.Add(new CompProperties(typeof(CompRpgLevel)));
                }

                if (def.inspectorTabs == null)
                {
                    def.inspectorTabs = new List<Type>();
                }

                if (!def.inspectorTabs.Contains(typeof(ITab_RpgLevel)))
                {
                    def.inspectorTabs.Add(typeof(ITab_RpgLevel));
                    if (def.inspectorTabsResolved == null)
                    {
                        def.inspectorTabsResolved = new List<InspectTabBase>();
                    }

                    def.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(typeof(ITab_RpgLevel)));
                }
            }

            foreach (RpgEffect effect in All)
            {
                StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(effect.statDefName);
                if (stat == null)
                {
                    continue;
                }

                effect.stat = stat;
                if (stat.parts == null)
                {
                    stat.parts = new List<StatPart>();
                }

                stat.parts.Add(new StatPart_Rpg { effect = effect, parentStat = stat });
            }
        }

        public static bool CarryCapacityHandledElsewhere()
        {
            return ModLister.HasActiveModWithName("Combat Extended") || ModLister.HasActiveModWithName("Carry Capacity Fix");
        }
    }

    [HarmonyPatchCategory(Features.RpgLevelUp)]
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn))]
    internal static class SkillRecord_Learn_Rpg_Patch
    {
        private static void Prefix(float xp, Pawn ___pawn)
        {
            if (xp <= 0f || !(___pawn?.GetComp<CompRpgLevel>() is CompRpgLevel comp))
            {
                return;
            }

            comp.GainExp((int)(xp * Yuudong2Settings.Current.rpgColonistExpPercent * (1.0 + 0.01 * comp.Get(RpgAttribute.INT))));
        }
    }

    [HarmonyPatchCategory(Features.RpgLevelUp)]
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.GetDamageFactorFor), typeof(Tool), typeof(Pawn), typeof(HediffComp_VerbGiver))]
    internal static class VerbProperties_GetDamageFactorFor_Rpg_Patch
    {
        private static void Postfix(Pawn attacker, ref float __result)
        {
            if (attacker?.GetComp<CompRpgLevel>() is CompRpgLevel comp)
            {
                __result *= 1f + 0.003f * comp.Get(RpgAttribute.STR);
            }
        }
    }

    [HarmonyPatchCategory(Features.RpgLevelUp)]
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    internal static class MassUtility_Capacity_Rpg_Patch
    {
        private static bool? skip;

        private static void Postfix(Pawn p, ref float __result)
        {
            skip = skip ?? RpgEffects.CarryCapacityHandledElsewhere();
            if (!skip.Value && __result > 0f && p?.GetComp<CompRpgLevel>() is CompRpgLevel comp)
            {
                __result *= 1f + 0.01f * comp.Get(RpgAttribute.STR);
            }
        }
    }

    [HarmonyPatchCategory(Features.RpgLevelUp)]
    [HarmonyPatch(typeof(RecordsUtility), nameof(RecordsUtility.Notify_PawnKilled))]
    internal static class RecordsUtility_Notify_PawnKilled_Rpg_Patch
    {
        private static void Postfix(Pawn killed, Pawn killer)
        {
            if (killer?.GetComp<CompRpgLevel>() is CompRpgLevel comp)
            {
                comp.GainExp((int)killed.kindDef.combatPower * Yuudong2Settings.Current.rpgKillExpMult);
            }
        }
    }
}

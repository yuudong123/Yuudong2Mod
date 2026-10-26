using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Raids For Me 포팅. 통신기 세력 대화에 "모욕하기"를 추가하고, 적대 세력이면 습격을 부른다.
    // 원본 기본 설정값(습격 확률 100%, 호감도 1~15 감소, 1~8시간 뒤 습격, 낮은 확률로 드롭 포드)을 쓴다.
    internal static class InsultFactionUtility
    {
        private const int MaxGoodwillLoss = 15;
        private const int MinRaidDelayHours = 1;
        private const int MaxRaidDelayHours = 8;
        private const float AirDropChance = 0.05f;
        private const float CenterDropChance = 0.25f;

        public static DiaOption MakeOption(Pawn negotiator, Faction faction)
        {
            DiaOption option = new DiaOption("Yuudong2.Insult.Option".Translate(faction.leader.LabelCap, faction.Name));
            option.action = () => Insult(negotiator, faction);
            option.resolveTree = true;
            if (InsultFactionComponent.Instance != null && InsultFactionComponent.Instance.IsOnCooldown(faction))
            {
                option.disabled = true;
            }

            return option;
        }

        private static void Insult(Pawn negotiator, Faction faction)
        {
            Pawn leader = faction.leader;
            faction.TryAffectGoodwillWith(Faction.OfPlayer, -Rand.RangeInclusive(1, MaxGoodwillLoss), true, true);
            MoteMaker.MakeInteractionBubble(negotiator, null, InteractionDefOf.Insult.interactionMote, InteractionDefOf.Insult.GetSymbol(negotiator.Faction, negotiator.Ideo));
            string insultText = new PlayLogEntry_Interaction(InteractionDefOf.Insult, negotiator, leader, new List<RulePackDef>()).ToGameStringFromPOV(leader);

            if (faction.PlayerRelationKind != FactionRelationKind.Hostile)
            {
                Messages.Message(insultText + " " + "Yuudong2.Insult.Ally".Translate(leader.LabelCap, faction.Name), MessageTypeDefOf.NegativeEvent, false);
                return;
            }

            Map map = negotiator.Map;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.forced = true;
            parms.faction = faction;
            parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
            parms.target = map;

            int delayTicks;
            string raidText;
            if (faction.def.techLevel >= TechLevel.Industrial && Rand.Chance(AirDropChance))
            {
                parms.raidArrivalMode = Rand.Chance(CenterDropChance) ? PawnsArrivalModeDefOf.CenterDrop : PawnsArrivalModeDefOf.EdgeDrop;
                delayTicks = 0;
                raidText = "Yuudong2.Insult.RaidDropPod".Translate(leader.LabelCap, faction.Name);
            }
            else
            {
                parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                delayTicks = Rand.RangeInclusive(MinRaidDelayHours, MaxRaidDelayHours - 1) * GenDate.TicksPerHour;
                raidText = "Yuudong2.Insult.RaidDelayed".Translate(leader.LabelCap, faction.Name, delayTicks.ToStringTicksToPeriodVerbose());
            }

            Messages.Message(insultText + " " + raidText, MessageTypeDefOf.NegativeEvent, false);
            Find.Storyteller.incidentQueue.Add(IncidentDefOf.RaidEnemy, Find.TickManager.TicksGame + delayTicks, parms, 240000);
            InsultFactionComponent.Instance?.SetCooldown(faction, Find.TickManager.TicksGame + delayTicks);
        }
    }

    public class InsultFactionComponent : GameComponent
    {
        public static InsultFactionComponent Instance;

        private Dictionary<string, int> cooldownUntilTick = new Dictionary<string, int>();

        public InsultFactionComponent(Game game)
        {
            Instance = this;
        }

        public bool IsOnCooldown(Faction faction)
        {
            return cooldownUntilTick.TryGetValue(faction.GetUniqueLoadID(), out int tick) && tick > Find.TickManager.TicksGame;
        }

        public void SetCooldown(Faction faction, int untilTick)
        {
            cooldownUntilTick[faction.GetUniqueLoadID()] = untilTick;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref cooldownUntilTick, "cooldownUntilTick", LookMode.Value, LookMode.Value);
            if (cooldownUntilTick == null)
            {
                cooldownUntilTick = new Dictionary<string, int>();
            }
        }
    }

    [HarmonyPatchCategory(Features.InsultFaction)]
    [HarmonyPatch(typeof(FactionDialogMaker), nameof(FactionDialogMaker.FactionDialogFor))]
    internal static class FactionDialogMaker_FactionDialogFor_Patch
    {
        private static void Postfix(DiaNode __result, Pawn negotiator, Faction faction)
        {
            if (__result?.options == null || faction?.leader == null || negotiator?.Map == null)
            {
                return;
            }

            __result.options.Insert(__result.options.Count - 1, InsultFactionUtility.MakeOption(negotiator, faction));
        }
    }
}

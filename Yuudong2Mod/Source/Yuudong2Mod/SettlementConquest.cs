using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Yuudong2Mod
{
    [DefOf]
    public static class SettlementConquestDefOf
    {
        public static LetterDef Yuudong2_SettlementDefeated;

        static SettlementConquestDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SettlementConquestDefOf));
        }
    }

    // 정착지 파괴 편지. 그 맵이 열려 있는 동안 우리 정착지로 삼거나, 함께 싸운 동맹에게 넘기거나, 그대로 둘 수 있다.
    public class ChoiceLetter_SettlementDefeated : ChoiceLetter
    {
        public MapParent ruins;

        public Faction ally;

        public Faction defender;

        public string settlementName;

        public Map Map => ruins != null && !ruins.Destroyed ? ruins.Map : null;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                yield return SettleOption();
                if (ally != null)
                {
                    yield return AllyOption();
                }
                DiaOption leave = new DiaOption("Yuudong2.Conquest.Leave".Translate());
                leave.action = delegate
                {
                    Find.LetterStack.RemoveLetter(this);
                };
                leave.resolveTree = true;
                yield return leave;
                yield return Option_Postpone;
                if (lookTargets.IsValid())
                {
                    yield return Option_JumpToLocationAndPostpone;
                }
            }
        }

        public DiaOption SettleOption()
        {
            DiaOption option = new DiaOption("Yuudong2.Conquest.Settle".Translate());
            Map map = Map;
            if (map == null)
            {
                option.Disable("Yuudong2.Conquest.MapClosed".Translate());
                return option;
            }
            AcceptanceReport report = ruins.CanBeSettled;
            if (!report.Accepted)
            {
                option.Disable(report.Reason.NullOrEmpty() ? (string)"CommandSettleFailGeneric".Translate() : report.Reason);
            }
            else if (map.mapPawns.FreeColonistsCount == 0)
            {
                option.Disable("CommandSettleFailNoColonists".Translate());
            }
            option.action = delegate
            {
                SettlementProximityGoodwillUtility.CheckConfirmSettle(map.Tile, delegate
                {
                    SettlementConquestUtility.Settle(this);
                });
            };
            option.resolveTree = true;
            return option;
        }

        public DiaOption AllyOption()
        {
            DiaOption option = new DiaOption("Yuudong2.Conquest.GiveToAlly".Translate(ally.Name));
            if (Map == null)
            {
                option.Disable("Yuudong2.Conquest.MapClosed".Translate());
            }
            else if (ally.defeated || ally.HostileTo(Faction.OfPlayer) || !FactionWarUtility.Active)
            {
                option.Disable("Yuudong2.Conquest.AllyGone".Translate(ally.Name));
            }
            option.action = delegate
            {
                SettlementConquestUtility.GiveToAlly(this);
            };
            option.resolveTree = true;
            return option;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref ruins, "yuudong2Ruins");
            Scribe_References.Look(ref ally, "yuudong2Ally");
            Scribe_References.Look(ref defender, "yuudong2Defender");
            Scribe_Values.Look(ref settlementName, "yuudong2SettlementName");
        }
    }

    public static class SettlementConquestUtility
    {
        // 게임 옵션의 최대 정착지 수는 따지지 않는다. 그 한도는 바닐라의 정착 버튼에서만 검사한다.
        public static Settlement Settle(ChoiceLetter_SettlementDefeated letter)
        {
            Map map = letter.Map;
            if (map == null)
            {
                return null;
            }
            SettleInExistingMapUtility.Settle(map);
            Find.LetterStack.RemoveLetter(letter);
            return map.Parent as Settlement;
        }

        public static Settlement GiveToAlly(ChoiceLetter_SettlementDefeated letter)
        {
            WorldComponent_FactionWar comp = FactionWarUtility.Comp;
            if (letter.Map == null || comp == null)
            {
                return null;
            }
            Settlement settlement = comp.GiveRuinsToAlly(letter.ruins, letter.ally, letter.defender, letter.settlementName);
            if (settlement != null)
            {
                Find.LetterStack.RemoveLetter(letter);
            }
            return settlement;
        }

        // 바닐라가 방금 보낸 파괴 편지를 같은 내용에 선택지를 단 편지로 바꾼다.
        public static void ReplaceDefeatLetter(MapParent ruins, Faction defender, Faction ally, string settlementName, int lettersBefore)
        {
            TaggedString label = "LetterLabelFactionBaseDefeated".Translate();
            TaggedString text = "LetterFactionBaseDefeatedNoRaids".Translate(settlementName);
            List<Letter> letters = Find.LetterStack.LettersListForReading;
            for (int i = letters.Count - 1; i >= lettersBefore && i >= 0; i--)
            {
                if (letters[i] is ChoiceLetter vanilla && vanilla.def == LetterDefOf.PositiveEvent && vanilla.Label == label)
                {
                    text = vanilla.Text;
                    Find.LetterStack.RemoveLetter(vanilla);
                    break;
                }
            }

            text += "\n\n" + "Yuudong2.Conquest.LetterHint".Translate();
            if (ally != null)
            {
                text += " " + "Yuudong2.Conquest.LetterAllyHint".Translate(ally.Name);
            }
            ChoiceLetter_SettlementDefeated letter = (ChoiceLetter_SettlementDefeated)LetterMaker.MakeLetter(label, text,
                SettlementConquestDefOf.Yuudong2_SettlementDefeated, new LookTargets(new GlobalTargetInfo(ruins.Tile)), defender);
            letter.ruins = ruins;
            letter.ally = ally;
            letter.defender = defender;
            letter.settlementName = settlementName;
            Find.LetterStack.ReceiveLetter(letter, null, 0, false);
        }
    }

    [HarmonyPatchCategory(Features.SettlementConquest)]
    [HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
    internal static class SettlementDefeatUtility_CheckDefeated_SettlementConquest_Patch
    {
        internal struct State
        {
            public Map map;
            public Faction defender;
            public Faction ally;
            public string name;
            public int letters;
        }

        private static void Prefix(Settlement factionBase, out State __state)
        {
            __state = default;
            Map map = factionBase.Faction == Faction.OfPlayer ? null : factionBase.Map;
            if (map == null)
            {
                return;
            }
            __state = new State
            {
                map = map,
                defender = factionBase.Faction,
                ally = FactionWarUtility.Active ? FactionWarUtility.Comp?.AssistingAlly(factionBase) : null,
                name = factionBase.Label,
                letters = Find.LetterStack.LettersListForReading.Count,
            };
        }

        private static void Postfix(Settlement factionBase, State __state)
        {
            if (__state.map == null || !factionBase.Destroyed || !(__state.map.Parent is DestroyedSettlement ruins))
            {
                return;
            }
            SettlementConquestUtility.ReplaceDefeatLetter(ruins, __state.defender, __state.ally, __state.name, __state.letters);
        }
    }
}

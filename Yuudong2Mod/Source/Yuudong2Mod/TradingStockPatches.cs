using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Trading Options Continue 포팅. 기존에 쓰던 설정값만 고정으로 적용한다.
    // - 궤도 상선·정착지·상단의 재고와 은: 8배
    // - 랜디 계열 스토리텔러의 궤도 방문 사건 가중치: 4배
    internal static class TradingStockUtility
    {
        private const int StockMultiplier = 8;
        private const float OrbitalVisitorWeightFactor = 4f;

        private static readonly Dictionary<TraderKindDef, bool> ScaledTraders = new Dictionary<TraderKindDef, bool>();

        public static int ScaleCount(TraderKindDef trader, int count)
        {
            return IsScaledTrader(trader) ? count * StockMultiplier : count;
        }

        private static bool IsScaledTrader(TraderKindDef trader)
        {
            if (trader == null)
            {
                return false;
            }

            if (!ScaledTraders.TryGetValue(trader, out bool scaled))
            {
                scaled = trader.orbital || IsSettlementOrCaravanTrader(trader);
                ScaledTraders[trader] = scaled;
            }

            return scaled;
        }

        // 방문객 상인은 원본 설정에서 배율이 기본값(1배)이었으므로 제외한다.
        private static bool IsSettlementOrCaravanTrader(TraderKindDef trader)
        {
            foreach (FactionDef faction in DefDatabase<FactionDef>.AllDefsListForReading)
            {
                if (faction.baseTraderKinds.Contains(trader) || faction.caravanTraderKinds.Contains(trader))
                {
                    return true;
                }

                if (faction.visitorTraderKinds.Contains(trader))
                {
                    return false;
                }
            }

            return false;
        }

        public static void ApplyStorytellerPatches()
        {
            IncidentCategoryDef orbitalVisitor = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail("OrbitalVisitor");
            if (orbitalVisitor == null)
            {
                return;
            }

            foreach (StorytellerDef storyteller in DefDatabase<StorytellerDef>.AllDefsListForReading)
            {
                foreach (StorytellerCompProperties comp in storyteller.comps)
                {
                    if (!(comp is StorytellerCompProperties_RandomMain randomMain) || randomMain.categoryWeights == null)
                    {
                        continue;
                    }

                    IncidentCategoryEntry entry = randomMain.categoryWeights.Find(e => e.category == orbitalVisitor);
                    if (entry != null)
                    {
                        entry.weight *= OrbitalVisitorWeightFactor;
                    }
                }
            }
        }
    }

    [HarmonyPatchCategory(Features.TradeStockBoost)]
    [HarmonyPatch(typeof(StockGenerator), "RandomCountOf")]
    internal static class StockGenerator_RandomCountOf_Patch
    {
        private static void Postfix(StockGenerator __instance, ref int __result)
        {
            __result = TradingStockUtility.ScaleCount(__instance.trader, __result);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Trading Options Continue 포팅.
    // - 궤도 상선·상단·정착지·방문객 상인의 재고와 은에 각각 배율을 곱한다. 기본값은 기존에 쓰던 8배(방문객 1배).
    // - 랜디 계열 스토리텔러의 궤도 방문 사건 가중치: 4배
    internal static class TradingStockUtility
    {
        private const float OrbitalVisitorWeightFactor = 4f;
        private const float MaxStockMultiplier = 20f;

        private enum TraderType
        {
            Other,
            Orbital,
            Caravan,
            Settlement,
            Visitor,
        }

        private static readonly Dictionary<TraderKindDef, TraderType> TraderTypes = new Dictionary<TraderKindDef, TraderType>();

        public static int ScaleCount(TraderKindDef trader, int count)
        {
            float multiplier = Multiplier(trader);
            return multiplier == 1f ? count : GenMath.RoundRandom(count * multiplier);
        }

        private static float Multiplier(TraderKindDef trader)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            switch (TypeOf(trader))
            {
                case TraderType.Orbital:
                    return s.tradeStockOrbital;
                case TraderType.Caravan:
                    return s.tradeStockCaravan;
                case TraderType.Settlement:
                    return s.tradeStockSettlement;
                case TraderType.Visitor:
                    return s.tradeStockVisitor;
                default:
                    return 1f;
            }
        }

        private static TraderType TypeOf(TraderKindDef trader)
        {
            if (trader == null)
            {
                return TraderType.Other;
            }

            if (!TraderTypes.TryGetValue(trader, out TraderType type))
            {
                type = trader.orbital ? TraderType.Orbital : FactionTraderType(trader);
                TraderTypes[trader] = type;
            }

            return type;
        }

        // 여러 목록에 함께 들어 있으면 상단 → 정착지 → 방문객 순으로 본다.
        private static TraderType FactionTraderType(TraderKindDef trader)
        {
            List<FactionDef> factions = DefDatabase<FactionDef>.AllDefsListForReading;
            if (factions.Any(f => f.caravanTraderKinds.Contains(trader)))
            {
                return TraderType.Caravan;
            }

            if (factions.Any(f => f.baseTraderKinds.Contains(trader)))
            {
                return TraderType.Settlement;
            }

            return factions.Any(f => f.visitorTraderKinds.Contains(trader)) ? TraderType.Visitor : TraderType.Other;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.TradeStock.Note".Translate());
            listing.Gap();
            MultiplierRow(listing, "Orbital", ref s.tradeStockOrbital);
            MultiplierRow(listing, "Caravan", ref s.tradeStockCaravan);
            MultiplierRow(listing, "Settlement", ref s.tradeStockSettlement);
            MultiplierRow(listing, "Visitor", ref s.tradeStockVisitor);
            listing.End();
        }

        private static void MultiplierRow(Listing_Standard listing, string key, ref float value)
        {
            listing.Label(("Yuudong2.TradeStock." + key).Translate(value.ToString("0.#")));
            value = Mathf.Round(listing.Slider(value, 0f, MaxStockMultiplier) * 2f) / 2f;
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

using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 거래창에서 기술 청사진 줄에 색을 칠해 이미 쓸 필요가 없는지 보여준다. 유전자 도우미의 유전자팩 표시와 같은 색을 쓴다.
    //  초록: 이미 연구를 마쳤거나, 적용한 청사진과 정착지에 있는 청사진으로 필요한 수를 다 채울 수 있다.
    //  노랑: 일부만 적용했거나 일부만 가지고 있다.
    //  빨강: 하나도 적용하지 않았고 가지고 있지도 않다.
    internal static class TechprintTradeHighlight
    {
        public static void Draw(Rect rect, Tradeable trad)
        {
            ResearchProjectDef project = trad?.ThingDef?.GetCompProperties<CompProperties_Techprint>()?.project;
            if (project == null)
            {
                return;
            }

            int required = project.TechprintCount;
            int applied = project.TechprintsApplied;
            int owned = OwnedOnMaps(trad.ThingDef);
            Color color;
            string tip;
            if (project.IsFinished)
            {
                color = GeneAssistant.SettingsRef.green;
                tip = "Yuudong2.Techprint.Finished".Translate(project.LabelCap);
            }
            else
            {
                int missing = Mathf.Max(0, required - applied);
                color = missing <= owned ? GeneAssistant.SettingsRef.green
                    : applied > 0 || owned > 0 ? GeneAssistant.SettingsRef.yellow
                    : GeneAssistant.SettingsRef.red;
                tip = "Yuudong2.Techprint.Status".Translate(project.LabelCap, applied, required, owned);
            }

            Widgets.DrawBoxSolid(rect, color);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(rect, tip);
        }

        // 플레이어 기지 맵에 놓여 있는 같은 기술 청사진 수 (상인이 가진 것은 맵에 없으므로 세지 않는다).
        private static int OwnedOnMaps(ThingDef def)
        {
            int count = 0;
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome)
                {
                    continue;
                }

                foreach (Thing thing in map.listerThings.ThingsOfDef(def))
                {
                    if (thing.Faction == null || thing.Faction == Faction.OfPlayer)
                    {
                        count += thing.stackCount;
                    }
                }
            }

            return count;
        }
    }

    [HarmonyPatchCategory(Features.TechprintTradeHighlight)]
    [HarmonyPatch(typeof(TradeUI), nameof(TradeUI.DrawTradeableRow))]
    internal static class TradeUI_DrawTradeableRow_Techprint_Patch
    {
        private static void Prefix(Rect rect, Tradeable trad)
        {
            TechprintTradeHighlight.Draw(rect, trad);
        }
    }
}

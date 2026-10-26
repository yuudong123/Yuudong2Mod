using System.Linq;
using HarmonyLib;
using ImprovedWorkbenches;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Better Workbench Management 포팅의 초기화와 세부 설정.
    // 원본 코드는 BetterWorkbench 폴더에 원래 네임스페이스(ImprovedWorkbenches) 그대로 두어 기존 저장 데이터를 이어받는다.
    internal static class BetterWorkbenchUtility
    {
        public static void Initialize(Harmony harmony)
        {
            new Main(harmony);
            AddOriginMapComps();
        }

        // 원본은 XML로 BasePawn에만 컴프를 붙여서, BasePawn을 상속하지 않는 종족 폰은 출신 맵을 기억하지 못했다.
        // 모든 폰 Def에 직접 붙인다.
        private static void AddOriginMapComps()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.race == null || def.thingClass == null || !typeof(Pawn).IsAssignableFrom(def.thingClass))
                {
                    continue;
                }

                if (def.comps == null)
                {
                    def.comps = new System.Collections.Generic.List<CompProperties>();
                }

                if (!def.comps.Any(c => c.compClass == typeof(CompPawnOriginalMap)))
                {
                    def.comps.Add(new CompProperties(typeof(CompPawnOriginalMap)));
                }
            }
        }

        public static void DrawSettings(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("IW.AutoOpenBillTabLabel".Translate(), ref ModSettings_ImprovedWorkbenches._expandBillsTab, "IW.AutoOpenBillTabDesc".Translate());
            listing.CheckboxLabeled("IW.EnableDragToReorder".Translate(), ref ModSettings_ImprovedWorkbenches._enableDragToReorder, "IW.EnableDragToReorderDesc".Translate());
            listing.CheckboxLabeled("IW.MirrorSuspendedStatus".Translate(), ref ModSettings_ImprovedWorkbenches._mirrorSuspendedStatus, "IW.MirrorSuspendedStatusDesc".Translate());
            listing.CheckboxLabeled("IW.DropOnFloorByDefault".Translate(), ref ModSettings_ImprovedWorkbenches._dropOnFloorByDefault, "IW.DropOnFloorByDefaultDesc".Translate());
            listing.CheckboxLabeled("IW.CountOutsideStockpiles".Translate(), ref ModSettings_ImprovedWorkbenches._countOutsideStockpiles, "IW.CountOutsideStockpilesDesc".Translate());
            listing.CheckboxLabeled("IW.CountInventory".Translate(), ref ModSettings_ImprovedWorkbenches._countInventory, "IW.CountInventoryDesc".Translate());
            listing.CheckboxLabeled("IW.CountCarriedByNonHumans".Translate(), ref ModSettings_ImprovedWorkbenches._countCarriedByNonHumans, "IW.CountCarriedByNonHumansDesc".Translate());
            listing.End();
        }

        public static void ExposeSettings()
        {
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._expandBillsTab, "bwmExpandBillsTab", true);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._enableDragToReorder, "bwmEnableDragToReorder", true);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._mirrorSuspendedStatus, "bwmMirrorSuspendedStatus", true);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._dropOnFloorByDefault, "bwmDropOnFloorByDefault", false);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._countOutsideStockpiles, "bwmCountOutsideStockpiles", true);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._countInventory, "bwmCountInventory", false);
            Scribe_Values.Look(ref ModSettings_ImprovedWorkbenches._countCarriedByNonHumans, "bwmCountCarriedByNonHumans", true);
        }
    }
}

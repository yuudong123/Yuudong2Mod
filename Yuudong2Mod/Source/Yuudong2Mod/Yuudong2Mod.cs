using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Yuudong2Mod
{
    [StaticConstructorOnStartup]
    public static class Yuudong2Mod
    {
        // 게임 시작 시 실제로 적용한 기능. 설정을 바꿔도 재시작 전까지는 이 목록이 유지된다.
        public static readonly HashSet<string> AppliedFeatures = new HashSet<string>();

        static Yuudong2Mod()
        {
            Harmony harmony = new Harmony("yuudong2.yuudong2mod");
            Assembly assembly = typeof(Yuudong2Mod).Assembly;
            foreach (FeatureGroup group in Features.Groups)
            {
                foreach (string feature in group.features)
                {
                    if (Features.Enabled(feature) && Try(feature, () => harmony.PatchCategory(assembly, feature)))
                    {
                        AppliedFeatures.Add(feature);
                    }
                }
            }

            WhenEnabled(Features.ShelfNoSlowdown, ShelfPathCostUtility.RemoveShelfPathCosts);
            WhenEnabled(Features.EpoeAiCore, EpoeCompatibilityUtility.ApplyDefPatches);
            WhenEnabled(Features.DragonianGenes, DragonianCompatibilityUtility.ApplyDefPatches);
            WhenEnabled(Features.FasterBiosculpter, BiosculpterPodTweaks.ApplyDefPatches);
            WhenEnabled(Features.OrbitalTraderFrequency, TradingStockUtility.ApplyStorytellerPatches);
            WhenEnabled(Features.PowerOnDemand, PowerOnDemandDefs.Initialize);
            WhenEnabled(Features.NoIdeologyLimits, NoIdeologyLimitsUtility.ApplyDefPatches);
            WhenEnabled(Features.EmpireGoodwillFix, EmpireGoodwillFixUtility.ApplyDefPatches);
            WhenEnabled(Features.MeatOptimization, MeatOptimizationUtility.FinishLoading);
            WhenEnabled(Features.RpgLevelUp, Rpg.RpgEffects.Apply);
            WhenEnabled(Features.RealisticRooms, RealisticRoomsUtility.Apply);
            WhenEnabled(Features.GloomyFurnitureFix, GloomyWorkbenchSync.Apply);
            WhenEnabled(Features.MapWideTradeBeacon, MapWideTradeBeacon.ApplyDefChanges);

            if (AppliedFeatures.Contains(Features.BetterWorkbench))
            {
                Try(Features.BetterWorkbench, () => BetterWorkbenchUtility.Initialize(harmony));
            }

            // 다른 모드가 있을 때만 거는 호환 패치
            if (AppliedFeatures.Contains(Features.AutoCastSpecialistCommands))
            {
                Try(Features.AutoCastSpecialistCommands, () => AutoCastUtility.PatchResearchReinvented(harmony));
            }

            if (AppliedFeatures.Contains(Features.EndlessGrowth))
            {
                Try(Features.EndlessGrowth, () => EndlessGrowthUtility.PatchModCompatibility(harmony));
            }

            if (AppliedFeatures.Contains(Features.GeneAssistant))
            {
                Try(Features.GeneAssistant, () => GeneAssistant.SettingsRef.PatchOtherMods(harmony));
            }
        }

        // 패치가 실패한 기능은 그 뒤 Def 작업도 하지 않는다.
        private static void WhenEnabled(string feature, Action action)
        {
            if (AppliedFeatures.Contains(feature))
            {
                LongEventHandler.ExecuteWhenFinished(() => Try(feature, action));
            }
        }

        // 기능 하나의 실패(게임·다른 모드 업데이트로 패치 위치를 못 찾는 등)가 다른 기능까지 막지 않게 한다.
        private static bool Try(string feature, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[Yuudong2Mod] '" + feature + "' 기능을 적용하지 못해 건너뜁니다: " + e);
                return false;
            }
        }
    }
}

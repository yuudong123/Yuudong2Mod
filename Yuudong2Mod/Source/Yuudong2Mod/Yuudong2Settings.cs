using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 설정에서 켜고 끌 수 있는 기능 목록. 값은 Harmony 패치 카테고리와 번역 키에도 쓰인다.
    public static class Features
    {
        public const string AlwaysAcceptProposal = "AlwaysAcceptProposal";
        public const string NoBreakup = "NoBreakup";
        public const string SameBedLovin = "SameBedLovin";

        public const string ShelfNoSlowdown = "ShelfNoSlowdown";
        public const string ShelfAcceptAll = "ShelfAcceptAll";
        public const string HealthCardSorting = "HealthCardSorting";
        public const string MessagePlacement = "MessagePlacement";
        public const string NoMapEdgeLimit = "NoMapEdgeLimit";
        public const string CustomMapSize = "CustomMapSize";
        public const string PowerOnDemand = "PowerOnDemand";
        public const string ReplaceStuff = "ReplaceStuff";
        public const string GloomyWallReplace = "GloomyWallReplace";

        public const string HairUnderHats = "HairUnderHats";
        public const string DurableClothes = "DurableClothes";
        public const string InfiniteApparelDurability = "InfiniteApparelDurability";

        public const string AllSpecialistsCanWork = "AllSpecialistsCanWork";
        public const string NoIdeologyLimits = "NoIdeologyLimits";
        public const string FasterBiosculpter = "FasterBiosculpter";

        public const string CaravanArrivalSpot = "CaravanArrivalSpot";
        public const string TradeShipDropSpot = "TradeShipDropSpot";
        public const string TradeStockBoost = "TradeStockBoost";
        public const string MapWideTradeBeacon = "MapWideTradeBeacon";
        public const string OrbitalTraderFrequency = "OrbitalTraderFrequency";
        public const string InsultFaction = "InsultFaction";

        public const string NoRandomRelations = "NoRandomRelations";
        public const string RoomFood = "RoomFood";
        public const string MeatOptimization = "MeatOptimization";
        public const string SinglePlantTexture = "SinglePlantTexture";
        public const string DamageIndicators = "DamageIndicators";
        public const string LetterStackCleaner = "LetterStackCleaner";
        public const string SmartSpeed = "SmartSpeed";
        public const string TpsDisplay = "TpsDisplay";
        public const string TechprintTradeHighlight = "TechprintTradeHighlight";
        public const string DragSelect = "DragSelect";
        public const string HiddenBionicEyes = "HiddenBionicEyes";
        public const string EndlessGrowth = "EndlessGrowth";
        public const string RolesUntiedToMemes = "RolesUntiedToMemes";
        public const string AutoCastSpecialistCommands = "AutoCastSpecialistCommands";
        public const string EmpireGoodwillFix = "EmpireGoodwillFix";
        public const string GloomyFurnitureFix = "GloomyFurnitureFix";

        public const string RomanceTweaks = "RomanceTweaks";
        public const string RoofedScanning = "RoofedScanning";
        public const string RealisticRooms = "RealisticRooms";
        public const string BetterWorkbench = "BetterWorkbench";
        public const string RpgLevelUp = "RpgLevelUp";
        public const string GeneAssistant = "GeneAssistant";
        public const string DragonianMix = "DragonianMix";

        public const string EpoeAiCore = "EpoeAiCore";
        public const string DragonianGenes = "DragonianGenes";
        public const string DragonianHealing = "DragonianHealing";
        public const string RatkinDrawScaleFix = "RatkinDrawScaleFix";

        public static readonly FeatureGroup[] Groups =
        {
            new FeatureGroup("Romance", AlwaysAcceptProposal, NoBreakup, SameBedLovin, NoRandomRelations, RomanceTweaks),
            new FeatureGroup("Colony", ShelfNoSlowdown, ShelfAcceptAll, NoMapEdgeLimit, CustomMapSize, PowerOnDemand, ReplaceStuff, GloomyWallReplace, RoomFood, MeatOptimization, SinglePlantTexture, RoofedScanning, RealisticRooms, BetterWorkbench),
            new FeatureGroup("Interface", MessagePlacement, HealthCardSorting, DamageIndicators, LetterStackCleaner, SmartSpeed, TpsDisplay, DragSelect, GeneAssistant, TechprintTradeHighlight),
            new FeatureGroup("Pawn", HairUnderHats, HiddenBionicEyes, DurableClothes, InfiniteApparelDurability, EndlessGrowth, RpgLevelUp),
            new FeatureGroup("Ideology", AllSpecialistsCanWork, RolesUntiedToMemes, NoIdeologyLimits, AutoCastSpecialistCommands, FasterBiosculpter),
            new FeatureGroup("Trade", CaravanArrivalSpot, TradeShipDropSpot, MapWideTradeBeacon, TradeStockBoost, OrbitalTraderFrequency, InsultFaction, EmpireGoodwillFix),
            new FeatureGroup("Compatibility", EpoeAiCore, DragonianGenes, DragonianHealing, DragonianMix, GloomyFurnitureFix, RatkinDrawScaleFix),
        };

        // 세부 설정 창이 있는 기능. 세부 설정은 재시작 없이 바로 적용된다.
        public static readonly HashSet<string> WithDetails = new HashSet<string>
        {
            HairUnderHats, CustomMapSize, NoIdeologyLimits, RoomFood, MeatOptimization, DamageIndicators, LetterStackCleaner,
            SmartSpeed, EndlessGrowth, AutoCastSpecialistCommands, RomanceTweaks, RpgLevelUp, GeneAssistant, RealisticRooms, BetterWorkbench,
        };

        public static bool Enabled(string feature)
        {
            return Yuudong2Settings.Current == null || Yuudong2Settings.Current.IsEnabled(feature);
        }
    }

    public class FeatureGroup
    {
        public readonly string key;
        public readonly string[] features;

        public FeatureGroup(string key, params string[] features)
        {
            this.key = key;
            this.features = features;
        }
    }

    public class Yuudong2Settings : ModSettings
    {
        public static Yuudong2Settings Current;

        // 새 기능이 기본으로 켜지도록 꺼진 기능만 저장한다.
        private HashSet<string> disabledFeatures = new HashSet<string>();

        // 모자 아래 머리카락
        public bool hairUnderUpperHeadgear = true;
        public bool hairUnderFullHeadgear = true;
        public bool beardUnderFullHeadgear;
        public bool headgearAboveHair = true;
        public bool hairOnlyPlayerPawns;
        public float headgearScale = 1f;
        public HashSet<string> hairHidingHeadgear = new HashSet<string>();
        public HashSet<string> hairHiddenUnderHeadgear = new HashSet<string>();

        // 맵 크기 (-1이면 직접 지정)
        public int defaultMapSize = 250;
        public int customMapWidth = 250;
        public int customMapHeight = 250;

        // 이데올로기 제한
        public bool ideologyLimitsOnlyPlayer;

        // 방 안 식사 (Room Food)
        public int roomFoodTableDistance = 100;

        // 피해 표시 (Damage Indicators)
        public bool damageNeutralFaction;
        public bool damagePawnsOnly;
        public bool damagePawnInstigatorOnly = true;

        // 전문가 명령 자동 사용 (Auto-Cast Specialist Commands)
        public bool autoCastFarming = true;
        public bool autoCastMining = true;
        public bool autoCastProduction = true;
        public bool autoCastResearch = true;
        public bool autoCastCooking = true;
        public bool autoCastVoid = true;
        public bool autoCastMajordomo;

        // 고기 통합 (Optimization: Meats). 0 자동, 1 바닐라, 2 날고기, 3 인육, 4 곤충 고기
        public Dictionary<string, int> meatPolicy = DefaultMeatPolicy();
        public bool meatDebug;

        // 편지 정리 (Letter Stack Cleaner)
        public KeyCode letterCleanKey = KeyCode.Delete;
        public bool letterCleanOneAtATime;
        public bool letterCleanOldestFirst;

        // 게임 속도 (Smart Speed)
        public float speedNormal = 1f;
        public float speedFast = 3f;
        public float speedSuperfast = 6f;
        public float speedUltrafast = 15f;
        public SmartSpeedEventMode speedEventMode = SmartSpeedEventMode.Ignore;

        // 무한 성장 (EndlessGrowth)
        public int endlessMaxLevel = -1;
        public bool endlessUnlimitedPrice;
        public int endlessBillGap = 20;
        public bool endlessCraftNotifications;

        // 연애 확률 조절 (Romance Tweaks)
        public float romanceChanceMult = 1f;
        public float romanceSingleMult = 1f;
        public float romanceCheatingMult;
        public float romanceFactionMult;
        public float romanceSuccessMult = 1f;
        public float breakupChanceMult;
        public bool romanceDebug;

        // RPG 레벨업 (RPG Style Level Up Mod)
        public int rpgColonistExpPercent = 40;
        public int rpgAnimalExpPer10Seconds = 1;
        public bool rpgFlatStartingStats = true;
        public int rpgStartingStatMin;
        public int rpgStartingStatMax;
        public int rpgKillExpMult = 50;
        public int rpgMaxLevel = 9999;
        public int rpgLevelScaling;

        // 방 크기 기준 (Realistic Rooms Rewritten). 빡빡함, 평범, 약간 넓음, 꽤나 넓음, 상당히 넓음, 매우 넓음 순서
        public float[] roomSpaceThresholds = (float[])RealisticRoomsUtility.DefaultThresholds.Clone();
        public bool roomFilthTweak = true;

        // 유전자 도우미 (Random's Gene Assistant)
        public bool geneOverrideSorting = true;
        public bool geneSinglesFirst = true;
        public float geneBackgroundSaturation = 0.5f;
        public bool geneAlternateColors;
        public bool geneSellFromGeneBanks;
        public HashSet<string> geneIgnored;

        private static Dictionary<string, int> DefaultMeatPolicy()
        {
            return new Dictionary<string, int> { { "Pig", 1 } };
        }

        public bool IsEnabled(string feature)
        {
            return !disabledFeatures.Contains(feature);
        }

        public void SetEnabled(string feature, bool enabled)
        {
            if (enabled)
            {
                disabledFeatures.Remove(feature);
            }
            else
            {
                disabledFeatures.Add(feature);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref disabledFeatures, "disabledFeatures", LookMode.Value);

            Scribe_Values.Look(ref hairUnderUpperHeadgear, "hairUnderUpperHeadgear", true);
            Scribe_Values.Look(ref hairUnderFullHeadgear, "hairUnderFullHeadgear", true);
            Scribe_Values.Look(ref beardUnderFullHeadgear, "beardUnderFullHeadgear", false);
            Scribe_Values.Look(ref headgearAboveHair, "headgearAboveHair", true);
            Scribe_Values.Look(ref hairOnlyPlayerPawns, "hairOnlyPlayerPawns", false);
            Scribe_Values.Look(ref headgearScale, "headgearScale", 1f);
            Scribe_Collections.Look(ref hairHidingHeadgear, "hairHidingHeadgear", LookMode.Value);
            Scribe_Collections.Look(ref hairHiddenUnderHeadgear, "hairHiddenUnderHeadgear", LookMode.Value);

            Scribe_Values.Look(ref defaultMapSize, "defaultMapSize", 250);
            Scribe_Values.Look(ref customMapWidth, "customMapWidth", 250);
            Scribe_Values.Look(ref customMapHeight, "customMapHeight", 250);

            Scribe_Values.Look(ref ideologyLimitsOnlyPlayer, "ideologyLimitsOnlyPlayer", false);

            Scribe_Values.Look(ref roomFoodTableDistance, "roomFoodTableDistance", 100);

            Scribe_Values.Look(ref damageNeutralFaction, "damageNeutralFaction", false);
            Scribe_Values.Look(ref damagePawnsOnly, "damagePawnsOnly", false);
            Scribe_Values.Look(ref damagePawnInstigatorOnly, "damagePawnInstigatorOnly", true);

            Scribe_Values.Look(ref autoCastFarming, "autoCastFarming", true);
            Scribe_Values.Look(ref autoCastMining, "autoCastMining", true);
            Scribe_Values.Look(ref autoCastProduction, "autoCastProduction", true);
            Scribe_Values.Look(ref autoCastResearch, "autoCastResearch", true);
            Scribe_Values.Look(ref autoCastCooking, "autoCastCooking", true);
            Scribe_Values.Look(ref autoCastVoid, "autoCastVoid", true);
            Scribe_Values.Look(ref autoCastMajordomo, "autoCastMajordomo", false);

            Scribe_Collections.Look(ref meatPolicy, "meatPolicy", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref meatDebug, "meatDebug", false);

            Scribe_Values.Look(ref letterCleanKey, "letterCleanKey", KeyCode.Delete);
            Scribe_Values.Look(ref letterCleanOneAtATime, "letterCleanOneAtATime", false);
            Scribe_Values.Look(ref letterCleanOldestFirst, "letterCleanOldestFirst", false);

            Scribe_Values.Look(ref speedNormal, "speedNormal", 1f);
            Scribe_Values.Look(ref speedFast, "speedFast", 3f);
            Scribe_Values.Look(ref speedSuperfast, "speedSuperfast", 6f);
            Scribe_Values.Look(ref speedUltrafast, "speedUltrafast", 15f);
            Scribe_Values.Look(ref speedEventMode, "speedEventMode", SmartSpeedEventMode.Ignore);

            Scribe_Values.Look(ref endlessMaxLevel, "endlessMaxLevel", -1);
            Scribe_Values.Look(ref endlessUnlimitedPrice, "endlessUnlimitedPrice", false);
            Scribe_Values.Look(ref endlessBillGap, "endlessBillGap", 20);
            Scribe_Values.Look(ref endlessCraftNotifications, "endlessCraftNotifications", false);

            Scribe_Values.Look(ref romanceChanceMult, "romanceChanceMult", 1f);
            Scribe_Values.Look(ref romanceSingleMult, "romanceSingleMult", 1f);
            Scribe_Values.Look(ref romanceCheatingMult, "romanceCheatingMult", 0f);
            Scribe_Values.Look(ref romanceFactionMult, "romanceFactionMult", 0f);
            Scribe_Values.Look(ref romanceSuccessMult, "romanceSuccessMult", 1f);
            Scribe_Values.Look(ref breakupChanceMult, "breakupChanceMult", 0f);
            Scribe_Values.Look(ref romanceDebug, "romanceDebug", false);

            Scribe_Values.Look(ref rpgColonistExpPercent, "rpgColonistExpPercent", 40);
            Scribe_Values.Look(ref rpgAnimalExpPer10Seconds, "rpgAnimalExpPer10Seconds", 1);
            Scribe_Values.Look(ref rpgFlatStartingStats, "rpgFlatStartingStats", true);
            Scribe_Values.Look(ref rpgStartingStatMin, "rpgStartingStatMin", 0);
            Scribe_Values.Look(ref rpgStartingStatMax, "rpgStartingStatMax", 0);
            Scribe_Values.Look(ref rpgKillExpMult, "rpgKillExpMult", 50);
            Scribe_Values.Look(ref rpgMaxLevel, "rpgMaxLevel", 9999);
            Scribe_Values.Look(ref rpgLevelScaling, "rpgLevelScaling", 0);

            for (int i = 0; i < RealisticRoomsUtility.DefaultThresholds.Length; i++)
            {
                float value = roomSpaceThresholds[i];
                Scribe_Values.Look(ref value, "roomSpace" + i, RealisticRoomsUtility.DefaultThresholds[i]);
                roomSpaceThresholds[i] = value;
            }

            Scribe_Values.Look(ref roomFilthTweak, "roomFilthTweak", true);

            BetterWorkbenchUtility.ExposeSettings();

            Scribe_Values.Look(ref geneOverrideSorting, "geneOverrideSorting", true);
            Scribe_Values.Look(ref geneSinglesFirst, "geneSinglesFirst", true);
            Scribe_Values.Look(ref geneBackgroundSaturation, "geneBackgroundSaturation", 0.5f);
            Scribe_Values.Look(ref geneAlternateColors, "geneAlternateColors", false);
            Scribe_Values.Look(ref geneSellFromGeneBanks, "geneSellFromGeneBanks", false);
            Scribe_Collections.Look(ref geneIgnored, "geneIgnored", LookMode.Value);

            if (disabledFeatures == null)
            {
                disabledFeatures = new HashSet<string>();
            }

            if (hairHidingHeadgear == null)
            {
                hairHidingHeadgear = new HashSet<string>();
            }

            if (hairHiddenUnderHeadgear == null)
            {
                hairHiddenUnderHeadgear = new HashSet<string>();
            }

            if (meatPolicy == null)
            {
                meatPolicy = DefaultMeatPolicy();
            }
        }
    }

    public class Yuudong2ModSettingsMod : Mod
    {
        private const float RowHeight = 30f;
        private const float HeaderHeight = 36f;
        private const float DetailsButtonWidth = 110f;

        private static Vector2 scrollPosition;
        private HashSet<string> enabledAtStartup;

        public Yuudong2ModSettingsMod(ModContentPack content) : base(content)
        {
            Yuudong2Settings.Current = GetSettings<Yuudong2Settings>();

            // 암시적 Def 생성 단계에 끼어드는 패치는 Def 로딩 전에 걸어야 해서 여기서 먼저 건다.
            HarmonyLib.Harmony early = new HarmonyLib.Harmony("yuudong2.yuudong2mod.early");
            if (Features.Enabled(Features.ReplaceStuff))
            {
                early.Patch(
                    HarmonyLib.AccessTools.Method(typeof(RimWorld.DefGenerator), nameof(RimWorld.DefGenerator.GenerateImpliedDefs_PreResolve)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(ReplaceStuffUtility), nameof(ReplaceStuffUtility.GenerateFrameDefs)));
            }

            if (Features.Enabled(Features.MeatOptimization))
            {
                early.Patch(
                    HarmonyLib.AccessTools.Method(typeof(RimWorld.ThingDefGenerator_Meat), nameof(RimWorld.ThingDefGenerator_Meat.ImpliedMeatDefs)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(MeatOptimizationUtility), nameof(MeatOptimizationUtility.MergeMeatDefs)));
            }
        }

        public override string SettingsCategory()
        {
            return "Yuudong2Mod";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (enabledAtStartup == null)
            {
                enabledAtStartup = new HashSet<string>(Yuudong2Mod.AppliedFeatures);
            }

            Listing_Standard header = new Listing_Standard();
            header.Begin(inRect);
            header.Label("Yuudong2.Settings.RestartNote".Translate());
            if (NeedsRestart(settings))
            {
                GUI.color = new Color(1f, 0.45f, 0.45f);
                header.Label("Yuudong2.Settings.RestartPending".Translate());
                GUI.color = Color.white;
            }

            header.GapLine();
            float headerHeight = header.CurHeight;
            header.End();

            Rect outRect = new Rect(inRect.x, inRect.y + headerHeight, inRect.width, inRect.height - headerHeight);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, ContentHeight());
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            foreach (FeatureGroup group in Features.Groups)
            {
                Text.Font = GameFont.Medium;
                listing.Label(("Yuudong2.Group." + group.key).Translate());
                Text.Font = GameFont.Small;
                foreach (string feature in group.features)
                {
                    DrawFeatureRow(listing.GetRect(RowHeight), settings, feature);
                }

                listing.Gap();
            }

            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawFeatureRow(Rect row, Yuudong2Settings settings, string feature)
        {
            Rect checkboxRect = row;
            if (Features.WithDetails.Contains(feature))
            {
                checkboxRect.width -= DetailsButtonWidth + 10f;
                Rect buttonRect = new Rect(row.xMax - DetailsButtonWidth, row.y + 2f, DetailsButtonWidth, row.height - 4f);
                if (Widgets.ButtonText(buttonRect, "Yuudong2.Settings.Details".Translate()))
                {
                    Find.WindowStack.Add(new Dialog_FeatureDetails(feature));
                }
            }

            bool enabled = settings.IsEnabled(feature);
            Widgets.CheckboxLabeled(checkboxRect, ("Yuudong2.Feature." + feature).Translate(), ref enabled);
            if (Mouse.IsOver(checkboxRect))
            {
                Widgets.DrawHighlight(checkboxRect);
            }

            TooltipHandler.TipRegion(checkboxRect, ("Yuudong2.Feature." + feature + ".Desc").Translate());
            settings.SetEnabled(feature, enabled);
        }

        private bool NeedsRestart(Yuudong2Settings settings)
        {
            foreach (FeatureGroup group in Features.Groups)
            {
                foreach (string feature in group.features)
                {
                    if (settings.IsEnabled(feature) != enabledAtStartup.Contains(feature))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static float ContentHeight()
        {
            float height = 0f;
            foreach (FeatureGroup group in Features.Groups)
            {
                height += HeaderHeight + group.features.Length * RowHeight + 12f;
            }

            return height;
        }
    }

    // 기능별 세부 설정 창.
    public class Dialog_FeatureDetails : Window
    {
        private readonly string feature;

        public Dialog_FeatureDetails(string feature)
        {
            this.feature = feature;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => feature == Features.HairUnderHats || feature == Features.MeatOptimization ? new Vector2(900f, 720f) : feature == Features.RpgLevelUp ? new Vector2(700f, 720f) : new Vector2(560f, 560f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), ("Yuudong2.Feature." + feature).Translate());
            Text.Font = GameFont.Small;
            Rect body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f - CloseButSize.y - 10f);
            Yuudong2Settings settings = Yuudong2Settings.Current;
            switch (feature)
            {
                case Features.HairUnderHats:
                    HairUnderHatsSettingsUI.Draw(body, settings);
                    break;
                case Features.CustomMapSize:
                    CustomMapSizeUtility.DrawSettings(body, settings);
                    break;
                case Features.NoIdeologyLimits:
                    NoIdeologyLimitsUtility.DrawSettings(body, settings);
                    break;
                case Features.RoomFood:
                    RoomFoodUtility.DrawSettings(body, settings);
                    break;
                case Features.MeatOptimization:
                    MeatOptimizationUtility.DrawSettings(body, settings);
                    break;
                case Features.DamageIndicators:
                    DamageIndicatorsUtility.DrawSettings(body, settings);
                    break;
                case Features.LetterStackCleaner:
                    LetterStackCleanerUtility.DrawSettings(body, settings);
                    break;
                case Features.SmartSpeed:
                    SmartSpeedUtility.DrawSettings(body, settings);
                    break;
                case Features.EndlessGrowth:
                    EndlessGrowthUtility.DrawSettings(body, settings);
                    break;
                case Features.AutoCastSpecialistCommands:
                    AutoCastUtility.DrawSettings(body, settings);
                    break;
                case Features.RomanceTweaks:
                    RomanceTweaksUtility.DrawSettings(body, settings);
                    break;
                case Features.RpgLevelUp:
                    Rpg.RpgSettingsUI.Draw(body, settings);
                    break;
                case Features.GeneAssistant:
                    GeneAssistant.SettingsRef.DrawSettings(body);
                    break;
                case Features.RealisticRooms:
                    RealisticRoomsUtility.DrawSettings(body, settings);
                    break;
                case Features.BetterWorkbench:
                    BetterWorkbenchUtility.DrawSettings(body);
                    break;
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            Yuudong2Settings.Current.Write();
            if (feature == Features.HairUnderHats)
            {
                HairUnderHatsUtility.RefreshAllPawnGraphics();
            }
        }
    }
}

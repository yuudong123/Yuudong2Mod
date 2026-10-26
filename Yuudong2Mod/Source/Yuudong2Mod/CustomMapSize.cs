using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Better Map Sizes 포팅. 시작 맵 크기를 가로·세로로 직접 정하고, 새 게임의 기본 맵 크기를 설정에서 고를 수 있다.
    internal static class CustomMapSizeUtility
    {
        public const int CustomSize = -1;
        private const int FallbackSize = 250;
        private static readonly int[] PresetSizes = { 200, 225, 250, 275, 300, 325 };

        // 새 게임 설정 중 쓰는 값. 설정의 기본값에서 복사해 온다.
        public static int width = FallbackSize;
        public static int height = FallbackSize;
        private static string widthBuffer;
        private static string heightBuffer;

        private static string settingsWidthBuffer;
        private static string settingsHeightBuffer;

        public static IntVec3? pendingSize;
        public static int placeholderSize;
        public static bool startingMapPending;

        public static void LoadFromSettings(Yuudong2Settings settings)
        {
            width = settings.customMapWidth;
            height = settings.customMapHeight;
            widthBuffer = null;
            heightBuffer = null;
        }

        public static string CustomLabel(int w, int h)
        {
            return "Yuudong2.MapSize.CustomLabel".Translate(w, h, w * h);
        }

        public static void DrawCustomOption(Listing_Standard listing)
        {
            listing.Gap(10f);
            listing.Label("Yuudong2.MapSize.Custom".Translate());
            if (listing.RadioButton(CustomLabel(width, height), Find.GameInitData.mapSize == CustomSize))
            {
                Find.GameInitData.mapSize = CustomSize;
            }

            if (Find.GameInitData.mapSize == CustomSize)
            {
                listing.Gap(5f);
                listing.TextFieldNumericLabeled("Yuudong2.MapSize.Width".Translate(), ref width, ref widthBuffer, 1f, 1000f);
                listing.TextFieldNumericLabeled("Yuudong2.MapSize.Height".Translate(), ref height, ref heightBuffer, 1f, 1000f);
            }
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.MapSize.SettingsDesc".Translate());
            listing.Gap(6f);
            foreach (int size in PresetSizes)
            {
                if (listing.RadioButton("MapSizeDesc".Translate(size, size * size), settings.defaultMapSize == size))
                {
                    settings.defaultMapSize = size;
                }
            }

            listing.Gap(10f);
            if (listing.RadioButton(CustomLabel(settings.customMapWidth, settings.customMapHeight), settings.defaultMapSize == CustomSize))
            {
                settings.defaultMapSize = CustomSize;
            }

            if (settings.defaultMapSize == CustomSize)
            {
                listing.Gap(5f);
                listing.TextFieldNumericLabeled("Yuudong2.MapSize.Width".Translate(), ref settings.customMapWidth, ref settingsWidthBuffer, 1f, 1000f);
                listing.TextFieldNumericLabeled("Yuudong2.MapSize.Height".Translate(), ref settings.customMapHeight, ref settingsHeightBuffer, 1f, 1000f);
            }

            listing.End();
        }

        public static void PrepareNewGame()
        {
            pendingSize = null;
            startingMapPending = false;
            GameInitData initData = Find.GameInitData;
            if (initData == null || initData.mapSize != CustomSize)
            {
                return;
            }

            int w = width;
            int h = height;
            if (w <= 0 || h <= 0)
            {
                Log.Warning("[Yuudong2Mod] 잘못된 맵 크기 " + w + "x" + h + "입니다. 250x250을 씁니다.");
                w = FallbackSize;
                h = FallbackSize;
            }

            // 바닐라 코드는 정사각형 크기만 다루므로, 우선 큰 쪽 길이로 진행하고 맵 생성 직전에 바꾼다.
            placeholderSize = Mathf.Max(w, h);
            initData.mapSize = placeholderSize;
            pendingSize = new IntVec3(w, 1, h);
            startingMapPending = true;
        }
    }

    [HarmonyPatchCategory(Features.CustomMapSize)]
    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.PostOpen))]
    internal static class Page_CreateWorldParams_PostOpen_Patch
    {
        private static void Postfix()
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (Find.GameInitData != null)
            {
                Find.GameInitData.mapSize = settings.defaultMapSize;
            }

            CustomMapSizeUtility.LoadFromSettings(settings);
        }
    }

    [HarmonyPatchCategory(Features.CustomMapSize)]
    [HarmonyPatch(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.DoWindowContents))]
    internal static class Dialog_AdvancedGameConfig_DoWindowContents_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo newColumn = AccessTools.Method(typeof(Listing), nameof(Listing.NewColumn));
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand as MethodInfo == newColumn)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Dialog_AdvancedGameConfig_DoWindowContents_Patch), nameof(NewColumnWithCustomSize));
                    replaced = true;
                }

                yield return instruction;
            }

            if (!replaced)
            {
                Log.Warning("[Yuudong2Mod] 고급 설정 창에서 맵 크기 목록 끝을 찾지 못했습니다.");
            }
        }

        private static void NewColumnWithCustomSize(Listing_Standard listing)
        {
            CustomMapSizeUtility.DrawCustomOption(listing);
            listing.NewColumn();
        }
    }

    [HarmonyPatchCategory(Features.CustomMapSize)]
    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    internal static class Game_InitNewGame_Patch
    {
        private static void Prefix()
        {
            CustomMapSizeUtility.PrepareNewGame();
        }

        private static void Postfix()
        {
            if (CustomMapSizeUtility.pendingSize.HasValue && Find.World != null)
            {
                Find.World.info.initialMapSize = CustomMapSizeUtility.pendingSize.Value;
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            CustomMapSizeUtility.pendingSize = null;
            CustomMapSizeUtility.startingMapPending = false;
            return __exception;
        }
    }

    [HarmonyPatchCategory(Features.CustomMapSize)]
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class MapGenerator_GenerateMap_Patch
    {
        private static void Prefix(ref IntVec3 mapSize)
        {
            IntVec3? pending = CustomMapSizeUtility.pendingSize;
            int placeholder = CustomMapSizeUtility.placeholderSize;
            if (CustomMapSizeUtility.startingMapPending && pending.HasValue && mapSize.x == placeholder && mapSize.z == placeholder)
            {
                mapSize = pending.Value;
                CustomMapSizeUtility.startingMapPending = false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Yuudong2Mod
{
    // ---------- Letter Stack Cleaner 포팅: 지정한 키로 화면 오른쪽 편지를 지운다 ----------

    internal static class LetterStackCleanerUtility
    {
        private static readonly KeyCode[] Keys = { KeyCode.Delete, KeyCode.Mouse3, KeyCode.Mouse4, KeyCode.Mouse5, KeyCode.Mouse6 };
        private static float lastCleanTime = -1f;

        public static void CleanLetterStacks()
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (!KeyDown(settings.letterCleanKey) || Time.time <= lastCleanTime)
            {
                return;
            }

            lastCleanTime = Time.time;
            LetterStack stack = Find.LetterStack;
            List<Letter> letters = stack.LettersListForReading.Where(l => l.CanDismissWithRightClick).ToList();
            if (letters.Count == 0)
            {
                return;
            }

            if (settings.letterCleanOneAtATime)
            {
                stack.RemoveLetter(settings.letterCleanOldestFirst ? letters.Last() : letters.First());
            }
            else
            {
                for (int i = letters.Count - 1; i >= 0; i--)
                {
                    stack.RemoveLetter(letters[i]);
                }
            }

            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static bool KeyDown(KeyCode key)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
            {
                return Input.GetMouseButtonDown(key - KeyCode.Mouse0);
            }

            return Input.GetKeyDown(key);
        }

        private static string KeyLabel(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Mouse3: return "Yuudong2.Letter.Mouse4".Translate();
                case KeyCode.Mouse4: return "Yuudong2.Letter.Mouse5".Translate();
                case KeyCode.Mouse5: return "Yuudong2.Letter.Mouse6".Translate();
                case KeyCode.Mouse6: return "Yuudong2.Letter.Mouse7".Translate();
                default: return "Yuudong2.Letter.Delete".Translate();
            }
        }

        // 카메라 OnGUI 메서드 마지막(ret 직전)에 정리 호출을 끼운다.
        public static IEnumerable<CodeInstruction> InsertBeforeReturn(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            CodeInstruction last = list[list.Count - 1];
            CodeInstruction call = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LetterStackCleanerUtility), nameof(CleanLetterStacks)));
            call.MoveLabelsFrom(last);
            list.Insert(list.Count - 1, call);
            return list;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.Letter.Key".Translate());
            if (Widgets.ButtonText(listing.GetRect(28f), KeyLabel(settings.letterCleanKey)))
            {
                Find.WindowStack.Add(new FloatMenu(Keys.Select(k => new FloatMenuOption(KeyLabel(k), () => settings.letterCleanKey = k)).ToList()));
            }

            listing.CheckboxLabeled("Yuudong2.Letter.OneAtATime".Translate(), ref settings.letterCleanOneAtATime);
            if (settings.letterCleanOneAtATime)
            {
                listing.CheckboxLabeled("Yuudong2.Letter.OldestFirst".Translate(), ref settings.letterCleanOldestFirst);
            }

            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.LetterStackCleaner)]
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CameraDriverOnGUI))]
    internal static class CameraDriver_CameraDriverOnGUI_LetterClean_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return LetterStackCleanerUtility.InsertBeforeReturn(instructions);
        }
    }

    [HarmonyPatchCategory(Features.LetterStackCleaner)]
    [HarmonyPatch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.WorldCameraDriverOnGUI))]
    internal static class WorldCameraDriver_WorldCameraDriverOnGUI_LetterClean_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return LetterStackCleanerUtility.InsertBeforeReturn(instructions);
        }
    }

    // ---------- Smart Speed 포팅: 속도별 배율, 이벤트 중 감속 방식, 초고속 버튼 ----------

    public enum SmartSpeedEventMode
    {
        Slow,
        Normal,
        Fast,
        Half,
        Ignore,
    }

    [StaticConstructorOnStartup]
    internal static class SmartSpeedUtility
    {
        public static readonly Texture2D[] SpeedButtonTextures =
        {
            ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Pause"),
            ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Normal"),
            ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Fast"),
            ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Superfast"),
            ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Ultrafast"),
        };

        private static readonly Func<TickManager, bool> nothingHappening = AccessTools.MethodDelegate<Func<TickManager, bool>>(AccessTools.Method(typeof(TickManager), "NothingHappeningInGame"));

        public static float TickRate(TimeSpeed speed, bool ultraSpeedBoost, TickManager manager)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            switch (speed)
            {
                case TimeSpeed.Paused:
                    return 0f;
                case TimeSpeed.Normal:
                    return s.speedNormal;
                case TimeSpeed.Fast:
                    return s.speedFast;
                case TimeSpeed.Superfast:
                    if (Find.Maps.Count == 0)
                    {
                        return s.speedSuperfast * 20f;
                    }

                    return nothingHappening(manager) ? s.speedSuperfast * 2f : s.speedSuperfast;
                case TimeSpeed.Ultrafast:
                    return Find.Maps.Count == 0 || ultraSpeedBoost ? s.speedUltrafast * 10f : s.speedUltrafast;
                default:
                    return -1f;
            }
        }

        public static void DoEventSpeedMenu(Rect timerRect)
        {
            if (!Mouse.IsOver(timerRect) || Event.current.button != 1 || Event.current.type != EventType.MouseDown)
            {
                return;
            }

            Yuudong2Settings s = Yuudong2Settings.Current;
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (SmartSpeedEventMode mode in Enum.GetValues(typeof(SmartSpeedEventMode)))
            {
                SmartSpeedEventMode chosen = mode;
                string label = ("Yuudong2.Speed.Event." + mode).Translate() + (s.speedEventMode == mode ? " V" : "");
                options.Add(new FloatMenuOption(label, () =>
                {
                    s.speedEventMode = chosen;
                    s.Write();
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options, "Yuudong2.Speed.EventSpeed".Translate()));
            Event.current.Use();
        }

        private static float SpeedRow(Listing_Standard listing, string key, float value, float min, float max, float reset, int digits)
        {
            Rect labelRect = listing.GetRect(Text.LineHeight);
            Widgets.Label(labelRect.LeftPart(0.7f), ("Yuudong2.Speed." + key).Translate() + ": " + value);
            TooltipHandler.TipRegion(labelRect, ("Yuudong2.Speed." + key + ".Desc").Translate());
            if (Widgets.ButtonText(labelRect.RightPart(0.25f), "Yuudong2.Speed.Reset".Translate()))
            {
                value = reset;
            }

            value = (float)Math.Round(listing.Slider(value, min, max), digits);
            listing.Gap(6f);
            return value;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.Speed.Note".Translate());
            listing.Gap();
            s.speedNormal = SpeedRow(listing, "Normal", s.speedNormal, 0.1f, 6f, 1f, 2);
            s.speedFast = SpeedRow(listing, "Fast", s.speedFast, 0.1f, 15f, 3f, 2);
            s.speedSuperfast = SpeedRow(listing, "Superfast", s.speedSuperfast, 0.1f, 50f, 6f, 2);
            s.speedUltrafast = SpeedRow(listing, "Ultrafast", s.speedUltrafast, 0.1f, 5000f, 15f, 1);
            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.SmartSpeed)]
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
    internal static class TickManager_TickRateMultiplier_SmartSpeed_Patch
    {
        private static bool Prefix(ref float __result, bool ___UltraSpeedBoost, TickManager __instance)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            TimeSpeed speed = __instance.CurTimeSpeed;
            if (!__instance.slower.ForcedNormalSpeed)
            {
                __result = SmartSpeedUtility.TickRate(speed, ___UltraSpeedBoost, __instance);
                return false;
            }

            switch (s.speedEventMode)
            {
                case SmartSpeedEventMode.Slow:
                    __result = s.speedNormal / 2f;
                    break;
                case SmartSpeedEventMode.Normal:
                    __result = s.speedNormal;
                    break;
                case SmartSpeedEventMode.Fast:
                    __result = s.speedNormal * 2f;
                    break;
                case SmartSpeedEventMode.Half:
                    __result = SmartSpeedUtility.TickRate(speed, ___UltraSpeedBoost, __instance) / 2f;
                    break;
                default:
                    __result = SmartSpeedUtility.TickRate(speed, ___UltraSpeedBoost, __instance);
                    break;
            }

            if (speed == TimeSpeed.Paused)
            {
                __result = 0f;
            }

            return false;
        }
    }

    // 시간 조절 버튼 영역 오른쪽 클릭으로 이벤트 속도를 고르고, 초고속 버튼을 항상 보여준다.
    [HarmonyPatchCategory(Features.SmartSpeed)]
    [HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
    internal static class TimeControls_DoTimeControlsGUI_SmartSpeed_Patch
    {
        private static readonly AccessTools.FieldRef<Vector2> timeButSize = AccessTools.StaticFieldRefAccess<Vector2>(AccessTools.Field(typeof(TimeControls), "TimeButSize"));

        private static void Prefix(ref Rect timerRect)
        {
            SmartSpeedUtility.DoEventSpeedMenu(timerRect);
            timerRect.x -= 14f;
            timeButSize() = new Vector2(28f, 24f);
        }

        private static bool AlwaysTrue()
        {
            return true;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo vanillaButtons = AccessTools.Field(typeof(TexButton), nameof(TexButton.SpeedButtonTextures));
            FieldInfo buttons = AccessTools.Field(typeof(SmartSpeedUtility), nameof(SmartSpeedUtility.SpeedButtonTextures));
            MethodInfo devMode = AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.DevMode));
            List<CodeInstruction> codes = instructions.ToList();
            int found = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                // 원본과 같이 첫 ldloc.3과 뒤 두 명령(초고속 버튼 숨김 조건)을 지운다.
                if (found < 0 && code.opcode == OpCodes.Ldloc_3)
                {
                    found = i;
                    yield return new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(code);
                }
                else if (found >= 0 && (i == found + 1 || i == found + 2))
                {
                    yield return new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(code);
                }
                else if (code.opcode == OpCodes.Ldsfld && code.operand as FieldInfo == vanillaButtons)
                {
                    yield return new CodeInstruction(OpCodes.Ldsfld, buttons).MoveLabelsFrom(code);
                }
                else if (code.opcode == OpCodes.Call && code.operand as MethodInfo == devMode)
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TimeControls_DoTimeControlsGUI_SmartSpeed_Patch), nameof(AlwaysTrue))).MoveLabelsFrom(code);
                }
                else
                {
                    yield return code;
                }
            }

            if (found < 0)
            {
                Log.Warning("[Yuudong2Mod] 시간 조절 버튼 코드에서 초고속 버튼 조건을 찾지 못했습니다.");
            }
        }
    }

    // ---------- DragSelect 포팅: 작업 우선순위, 복사/붙여넣기, 거래 수량 버튼을 끌어서 여러 개 누른다 ----------

    internal static class DragSelectUtility
    {
        public enum DragResult
        {
            Idle = 0,
            LeftPressed = 1,
            RightPressed = 4,
        }

        private const int DraggingMinSqrDistance = 20;
        private const int ClickSuppressFrames = 5;

        private static int lastLeftHash, lastRightHash;
        private static Vector3 leftStart, rightStart;
        private static bool leftDragging, rightDragging;
        private static int lastLeftFrame, lastRightFrame;

        public static DragResult ButtonInvisibleDraggable(Rect rect, bool listenRightButton = true, int? rectHash = null)
        {
            if (!Mouse.IsOver(rect))
            {
                return DragResult.Idle;
            }

            int hash = rectHash ?? rect.GetHashCode();
            if (HandleButton(0, hash, ref leftStart, ref leftDragging, ref lastLeftHash, ref lastLeftFrame))
            {
                return DragResult.LeftPressed;
            }

            if (listenRightButton && HandleButton(1, hash, ref rightStart, ref rightDragging, ref lastRightHash, ref lastRightFrame))
            {
                return DragResult.RightPressed;
            }

            return DragResult.Idle;
        }

        private static bool HandleButton(int button, int hash, ref Vector3 start, ref bool dragging, ref int lastHash, ref int lastFrame)
        {
            bool clicked = false;
            if (Input.GetMouseButtonDown(button))
            {
                start = Input.mousePosition;
                dragging = false;
            }

            if (Input.GetMouseButton(button))
            {
                if (!dragging && (start - Input.mousePosition).sqrMagnitude > DraggingMinSqrDistance)
                {
                    dragging = true;
                }
            }
            else if (!Input.GetMouseButtonUp(button))
            {
                dragging = false;
                lastHash = 0;
            }

            if (Input.GetMouseButtonUp(button) && Time.frameCount - lastFrame > ClickSuppressFrames)
            {
                if (!dragging)
                {
                    clicked = true;
                }

                lastFrame = Time.frameCount;
                dragging = false;
            }

            if ((dragging && lastHash != hash) ^ clicked)
            {
                lastHash = hash;
                return true;
            }

            return false;
        }

        public static bool AnyPress(Rect rect)
        {
            return ButtonInvisibleDraggable(rect) != DragResult.Idle;
        }

        // 원본처럼 Vanilla UI Expanded가 있으면 그쪽 기능과 겹치므로 패치하지 않는다.
        public static bool ShouldPatch()
        {
            try
            {
                return !ModLister.HasActiveModWithName("Vanilla UI Expanded");
            }
            catch (System.Exception)
            {
                return true;
            }
        }
    }

    [HarmonyPatchCategory(Features.DragSelect)]
    [HarmonyPatch(typeof(CopyPasteUI), nameof(CopyPasteUI.DoCopyPasteButtons))]
    internal static class CopyPasteUI_DoCopyPasteButtons_DragSelect_Patch
    {
        private static bool Prepare()
        {
            return DragSelectUtility.ShouldPatch();
        }

        [HarmonyInfix(typeof(Widgets), nameof(Widgets.ButtonImage), new[] { typeof(Rect), typeof(Texture2D), typeof(bool), typeof(string) }, Positions = new[] { -1 })]
        [HarmonyPostfix]
        private static void DraggableButtonImage(Rect butRect, ref bool __result)
        {
            __result = DragSelectUtility.ButtonInvisibleDraggable(butRect) == DragSelectUtility.DragResult.LeftPressed;
        }
    }

    [HarmonyPatchCategory(Features.DragSelect)]
    [HarmonyPatch(typeof(TransferableUIUtility), "DoCountAdjustInterfaceInternal")]
    internal static class TransferableUIUtility_DoCountAdjustInterfaceInternal_DragSelect_Patch
    {
        private static bool Prepare()
        {
            return DragSelectUtility.ShouldPatch();
        }

        [HarmonyInfix(typeof(Widgets), nameof(Widgets.ButtonText), new[] { typeof(Rect), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(TextAnchor?) })]
        [HarmonyPostfix]
        private static void DraggableButtonText(Rect rect, [HarmonyOuter] Transferable trad, ref bool __result)
        {
            __result = DragSelectUtility.ButtonInvisibleDraggable(rect, false, trad.AnyThing.thingIDNumber) == DragSelectUtility.DragResult.LeftPressed;
        }
    }

    [HarmonyPatchCategory(Features.DragSelect)]
    [HarmonyPatch(typeof(WidgetsWork), nameof(WidgetsWork.DrawWorkBoxFor))]
    internal static class WidgetsWork_DrawWorkBoxFor_DragSelect_Patch
    {
        private static bool Prepare()
        {
            return DragSelectUtility.ShouldPatch();
        }

        [HarmonyInfix(typeof(Widgets), nameof(Widgets.ButtonInvisible))]
        [HarmonyPrefix]
        private static bool DraggableButtonInvisible(Rect butRect, [HarmonyOuter] Pawn p, [HarmonyOuter] WorkTypeDef wType, ref bool __result)
        {
            DragSelectUtility.DragResult result = DragSelectUtility.ButtonInvisibleDraggable(butRect);
            __result = result == DragSelectUtility.DragResult.LeftPressed
                || (result == DragSelectUtility.DragResult.RightPressed && p.workSettings.GetPriority(wType) != 0);
            return false;
        }

        // 숫자 우선순위 모드의 입력 영역도 끌기로 누를 수 있게 바꾼다.
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            MethodInfo setAnchor = AccessTools.PropertySetter(typeof(Text), nameof(Text.Anchor));
            int index = list.FindLastIndex(x => x.Calls(setAnchor));
            if (index < 0 || index + 6 > list.Count)
            {
                Log.Warning("[Yuudong2Mod] 작업 탭 숫자 입력 영역을 찾지 못해 끌어서 선택을 일부 적용하지 못했습니다.");
                return list;
            }

            list[index + 1] = new CodeInstruction(OpCodes.Ldloc_0).WithLabels(list[index + 1].labels);
            list.RemoveRange(index + 2, 4);
            list.Insert(index + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DragSelectUtility), nameof(DragSelectUtility.AnyPress))));
            return list;
        }
    }
}

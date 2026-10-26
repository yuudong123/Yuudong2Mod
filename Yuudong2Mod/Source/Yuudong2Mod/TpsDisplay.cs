using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 화면 오른쪽 아래(날씨·온도·영구적 여름 같은 상태 위)에 "TPS : 현재(목표)"를 표시한다.
    // 현재 TPS는 최근 1초 동안 실제로 진행된 틱 수, 목표 TPS는 배속(Smart Speed 설정 포함)에 따른 초당 틱 수다.
    internal static class TpsDisplayUtility
    {
        // 표시 값은 1초마다 한 번만 바꾼다. 그 1초 동안 진행된 틱 수가 현재 TPS다.
        private const float UpdateInterval = 1f;
        private const float RowHeight = 26f;
        private const float Width = 200f;

        private static float windowStartTime = -1f;
        private static int windowStartTicks;
        private static float currentTps;

        private static void Sample()
        {
            float now = Time.realtimeSinceStartup;
            int ticks = Find.TickManager.TicksGame;
            if (windowStartTime < 0f || ticks < windowStartTicks)
            {
                // 처음이거나 저장 파일을 불러와 틱이 되돌아갔다.
                windowStartTime = now;
                windowStartTicks = ticks;
                return;
            }

            float elapsed = now - windowStartTime;
            if (elapsed < UpdateInterval)
            {
                return;
            }

            currentTps = (ticks - windowStartTicks) / elapsed;
            windowStartTime = now;
            windowStartTicks = ticks;
        }

        public static void Draw(float leftX, ref float curBaseY)
        {
            if (Event.current.type == EventType.Repaint)
            {
                Sample();
            }

            float target = 60f * Find.TickManager.TickRateMultiplier;
            Rect rect = new Rect(leftX, curBaseY - RowHeight, Width - 7f, RowHeight);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(rect, "TPS : " + Mathf.RoundToInt(currentTps) + "(" + Mathf.RoundToInt(target) + ")");
            Text.Anchor = TextAnchor.UpperLeft;
            curBaseY -= RowHeight;
        }
    }

    [HarmonyPatchCategory(Features.TpsDisplay)]
    [HarmonyPatch(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI))]
    internal static class GlobalControls_GlobalControlsOnGUI_TpsDisplay_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            FieldInfo showMemoryInfo = AccessTools.Field(typeof(DebugViewSettings), nameof(DebugViewSettings.showMemoryInfo));
            MethodInfo drawTpsCounter = AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DrawTpsCounter));

            // 바닐라 TPS 카운터 호출(leftX, 200, ref curBaseY)에서 두 지역 변수를 읽는 명령을 가져온다.
            int call = codes.FindIndex(c => c.Calls(drawTpsCounter));
            int insertAt = codes.FindIndex(c => c.LoadsField(showMemoryInfo));
            if (call < 3 || insertAt < 0)
            {
                Log.Warning("[Yuudong2Mod] 오른쪽 아래 상태 표시 코드를 찾지 못해 TPS 표시를 넣지 못했습니다.");
                return codes;
            }

            CodeInstruction loadLeftX = new CodeInstruction(codes[call - 3].opcode, codes[call - 3].operand);
            CodeInstruction loadBaseYAddress = new CodeInstruction(codes[call - 1].opcode, codes[call - 1].operand);
            loadLeftX.MoveLabelsFrom(codes[insertAt]);
            codes.InsertRange(insertAt, new[]
            {
                loadLeftX,
                loadBaseYAddress,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TpsDisplayUtility), nameof(TpsDisplayUtility.Draw))),
            });
            return codes;
        }
    }
}

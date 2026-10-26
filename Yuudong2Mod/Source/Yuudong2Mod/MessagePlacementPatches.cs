using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Better Message Placement 포팅. 화면 메시지를 정착민 바 아래로 내린다.
    [HarmonyPatchCategory(Features.MessagePlacement)]
    [HarmonyPatch(typeof(Messages), nameof(Messages.MessagesDoGUI))]
    internal static class Messages_MessagesDoGUI_Patch
    {
        private static readonly FieldInfo Vector2X = AccessTools.Field(typeof(Vector2), nameof(Vector2.x));
        private static readonly FieldInfo Vector2Y = AccessTools.Field(typeof(Vector2), nameof(Vector2.y));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool patchedX = false;
            bool patchedY = false;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode != OpCodes.Ldfld)
                {
                    continue;
                }

                if (!patchedX && instruction.operand as FieldInfo == Vector2X)
                {
                    patchedX = true;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Messages_MessagesDoGUI_Patch), nameof(XOffset)));
                    yield return new CodeInstruction(OpCodes.Add);
                }
                else if (!patchedY && instruction.operand as FieldInfo == Vector2Y)
                {
                    patchedY = true;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Messages_MessagesDoGUI_Patch), nameof(YOffset)));
                    yield return new CodeInstruction(OpCodes.Add);
                }
            }

            if (!patchedX || !patchedY)
            {
                Log.Warning("[Yuudong2Mod] Messages.MessagesDoGUI에서 메시지 위치 좌표를 찾지 못했습니다.");
            }
        }

        private static float XOffset()
        {
            return Messages.MessagesTopLeftStandard.x;
        }

        private static float YOffset()
        {
            float offset = Messages.MessagesTopLeftStandard.y;
            if (Find.CurrentMap == null)
            {
                return offset;
            }

            float scale = Find.ColonistBar.Scale;
            int rows = scale > 0.58f ? 1 : scale > 0.42f ? 2 : 3;
            return offset + (int)(Find.ColonistBar.Size.y + 24f * scale) * rows;
        }
    }
}

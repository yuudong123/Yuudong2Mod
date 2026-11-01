using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    [DefOf]
    public static class SaveHotkeyDefOf
    {
        public static KeyBindingDef Yuudong2_OpenSaveDialog;

        static SaveHotkeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SaveHotkeyDefOf));
        }
    }

    // 단축키(기본 F5, 옵션 > 조작에서 바꿀 수 있음)로 저장 창을 연다. 열려 있으면 닫는다.
    // 바닐라 메뉴의 "저장"과 같은 조건(저장 일시 불가, 각오 모드)을 따른다.
    // 다른 창이 입력을 막고 있으면(거래창 등) 그 창을 그대로 두고 아무것도 하지 않는다.
    [HarmonyPatchCategory(Features.SaveHotkey)]
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class UIRoot_Play_UIRootOnGUI_SaveHotkey_Patch
    {
        private static void Postfix()
        {
            KeyBindingDef key = SaveHotkeyDefOf.Yuudong2_OpenSaveDialog;
            if (key == null || !key.KeyDownEvent)
            {
                return;
            }

            Dialog_SaveFileList_Save open = Find.WindowStack.WindowOfType<Dialog_SaveFileList_Save>();
            if (open != null)
            {
                Event.current.Use();
                open.Close();
                return;
            }

            if (Find.WindowStack.AnyWindowAbsorbingAllInput)
            {
                return;
            }

            Event.current.Use();
            if (GameDataSaveLoader.SavingIsTemporarilyDisabled || Current.Game.Info.permadeathMode)
            {
                Messages.Message("Yuudong2.SaveHotkey.Unavailable".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Find.MainTabsRoot.EscapeCurrentTab(false);
            Find.WindowStack.Add(new Dialog_SaveFileList_Save());
        }
    }
}

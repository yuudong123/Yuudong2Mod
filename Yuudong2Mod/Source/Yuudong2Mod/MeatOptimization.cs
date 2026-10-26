using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 고기 통합에서 제외할 고기와 종족 목록.
    public class MeatWhiteListDef : Def
    {
        public List<string> meats = new List<string>();
        public List<string> races = new List<string>();
    }

    // Optimization: Meats - C# Edition 포팅.
    // 동물마다 따로 생기는 고기를 날고기·인육·곤충 고기 셋으로 합친다. 종족별 정책은 세부 설정에서 고른다.
    internal static class MeatOptimizationUtility
    {
        public const int PolicyAutomatic = 0;
        public const int PolicyVanilla = 1;
        public const int PolicyCow = 2;
        public const int PolicyHuman = 3;
        public const int PolicyInsect = 4;

        private const float RowHeight = 32f;

        private static readonly HashSet<string> whiteListedByMeat = new HashSet<string>();
        private static readonly List<string> debugRecords = new List<string>();
        private static string cowLabel, cowDescription, humanLabel, humanDescription;

        private static List<ThingDef> races;
        private static Vector2 scroll;
        private static string search = "";

        // ThingDefGenerator_Meat.ImpliedMeatDefs 후처리 (암시적 Def 생성 단계)
        public static IEnumerable<ThingDef> MergeMeatDefs(IEnumerable<ThingDef> __result)
        {
            List<ThingDef> meats = __result.ToList();
            ThingDef cow = meats.FirstOrDefault(d => d.defName == "Meat_Cow");
            ThingDef human = meats.FirstOrDefault(d => d.defName == "Meat_Human");
            ThingDef insect = meats.FirstOrDefault(d => d.defName == "Meat_Megaspider");
            if (cow == null || human == null || insect == null)
            {
                Log.Error("[Yuudong2Mod] 기본 고기(소, 사람, 곤충)를 찾지 못해 고기 통합을 건너뜁니다.");
                return meats;
            }

            // 합친 날고기는 "날고기"로 부른다 (원본은 영어 번역으로 raw meat).
            cowLabel = "Yuudong2.Meat.RawMeat".Translate();
            cowDescription = cow.description;
            humanLabel = human.label;
            humanDescription = human.description;

            Dictionary<string, ThingDef> defsByName = (Dictionary<string, ThingDef>)AccessTools.Field(typeof(DefDatabase<ThingDef>), "defsByName").GetValue(null);
            MeatWhiteListDef whiteList = DefDatabase<MeatWhiteListDef>.GetNamedSilentFail("Yuudong2_MeatWhiteList");
            Dictionary<string, int> policy = Yuudong2Settings.Current.meatPolicy;
            List<ThingDef> kept = new List<ThingDef>();

            foreach (ThingDef meat in meats)
            {
                if (meat == cow || meat == human || meat == insect)
                {
                    kept.Add(meat);
                    continue;
                }

                ThingDef source = meat.ingestible.sourceDef;
                string record = meat.defName + " (" + source.defName + ")";
                ThingDef target;
                if (policy.TryGetValue(source.defName, out int value) && value > PolicyAutomatic)
                {
                    target = value == PolicyCow ? cow : value == PolicyHuman ? human : value == PolicyInsect ? insect : null;
                    record += " -> policy " + value;
                }
                else if (whiteList != null && whiteList.meats.Contains(meat.defName))
                {
                    whiteListedByMeat.Add(source.defName);
                    target = null;
                    record += " -> whitelist (meat)";
                }
                else if (whiteList != null && whiteList.races.Contains(source.defName))
                {
                    target = null;
                    record += " -> whitelist (race)";
                }
                else
                {
                    target = source.race.Humanlike ? human : source.race.FleshType == FleshTypeDefOf.Insectoid ? insect : cow;
                    record += " -> " + target.defName;
                }

                if (target == null)
                {
                    kept.Add(meat);
                }
                else
                {
                    // 원래 고기 이름으로 찾는 참조(저장 파일 포함)는 합친 고기로 연결한다.
                    source.race.meatDef = target;
                    defsByName[meat.defName] = target;
                }

                debugRecords.Add(record);
            }

            cow.ResolveReferences();
            insect.ResolveReferences();
            human.ResolveReferences();
            return kept;
        }

        // Def 로딩이 끝난 뒤 이름 복구와 디버그 출력.
        public static void FinishLoading()
        {
            ThingDef cow = DefDatabase<ThingDef>.GetNamedSilentFail("Meat_Cow");
            ThingDef human = DefDatabase<ThingDef>.GetNamedSilentFail("Meat_Human");
            if (cow != null && human != null && cowLabel != null && humanLabel != null)
            {
                cow.label = cowLabel;
                cow.description = cowDescription;
                human.label = humanLabel;
                human.description = humanDescription;
            }

            if (Yuudong2Settings.Current.meatDebug)
            {
                debugRecords.Sort();
                Log.Message("[Yuudong2Mod] 고기 통합 결과\n" + string.Join("\n", debugRecords));
            }

            debugRecords.Clear();
        }

        private static string RaceLabel(ThingDef def)
        {
            return def.label + "::" + def.defName + "::" + def.modContentPack?.Name;
        }

        private static string FixedReason(ThingDef race, MeatWhiteListDef whiteList)
        {
            if (race.race.useMeatFrom != null)
            {
                return "Yuudong2.Meat.UseMeatFrom".Translate(RaceLabel(race.race.useMeatFrom));
            }

            if (race.race.specificMeatDef != null)
            {
                return "Yuudong2.Meat.SpecificMeat".Translate(race.race.specificMeatDef.label);
            }

            if (race.defName == "Human" || race.defName == "Cow" || race.defName == "Megaspider")
            {
                return "Yuudong2.Meat.Special".Translate();
            }

            if (whiteList != null && whiteList.races.Contains(race.defName))
            {
                return "Yuudong2.Meat.WhiteListRace".Translate();
            }

            return whiteListedByMeat.Contains(race.defName) ? "Yuudong2.Meat.WhiteListMeat".Translate() : null;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            if (races == null)
            {
                races = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && !d.IsCorpse && !d.race.IsMechanoid).OrderBy(d => d.label).ToList();
            }

            MeatWhiteListDef whiteList = DefDatabase<MeatWhiteListDef>.GetNamedSilentFail("Yuudong2_MeatWhiteList");

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(rect.x, rect.y, rect.width, 96f));
            listing.Label("Yuudong2.Meat.Note".Translate());
            listing.CheckboxLabeled("Yuudong2.Meat.Debug".Translate(), ref settings.meatDebug);
            listing.End();

            float top = rect.y + 96f;
            Widgets.Label(new Rect(rect.x, top, 60f, 24f), "Yuudong2.Meat.Search".Translate());
            search = Widgets.TextField(new Rect(rect.x + 60f, top, 240f, 24f), search);
            if (Widgets.ButtonText(new Rect(rect.xMax - 160f, top, 160f, 24f), "Yuudong2.Meat.Reset".Translate()))
            {
                settings.meatPolicy.Clear();
            }

            string[] columns = { "Yuudong2.Meat.Automatic", "Yuudong2.Meat.Vanilla", "Yuudong2.Meat.ToCow", "Yuudong2.Meat.ToHuman", "Yuudong2.Meat.ToInsect" };
            float nameWidth = rect.width * 0.42f;
            float columnWidth = (rect.width - 16f - nameWidth) / columns.Length;
            top += 30f;
            Text.Anchor = TextAnchor.MiddleCenter;
            for (int c = 0; c < columns.Length; c++)
            {
                Widgets.Label(new Rect(rect.x + nameWidth + c * columnWidth, top, columnWidth, 24f), columns[c].Translate());
            }

            Text.Anchor = TextAnchor.UpperLeft;
            top += 26f;

            List<ThingDef> shown = string.IsNullOrEmpty(search)
                ? races
                : races.Where(r => RaceLabel(r).IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Rect outRect = new Rect(rect.x, top, rect.width, rect.yMax - top);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, shown.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            for (int i = 0; i < shown.Count; i++)
            {
                Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                if (row.yMax < scroll.y || row.y > scroll.y + outRect.height)
                {
                    continue;
                }

                if (i % 2 == 0)
                {
                    Widgets.DrawLightHighlight(row);
                }

                ThingDef race = shown[i];
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, RowHeight - 4f, RowHeight - 4f), race);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + RowHeight + 4f, row.y, nameWidth - RowHeight - 4f, RowHeight), race.LabelCap + " (" + race.modContentPack?.Name + ")");
                string reason = FixedReason(race, whiteList);
                if (reason != null)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(row.x + nameWidth, row.y, row.width - nameWidth, RowHeight), reason);
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    continue;
                }

                Text.Anchor = TextAnchor.UpperLeft;
                settings.meatPolicy.TryGetValue(race.defName, out int current);
                for (int c = 0; c < columns.Length; c++)
                {
                    float x = row.x + nameWidth + c * columnWidth + columnWidth / 2f - 12f;
                    if (Widgets.RadioButton(x, row.y + (RowHeight - 24f) / 2f, current == c))
                    {
                        if (c == PolicyAutomatic)
                        {
                            settings.meatPolicy.Remove(race.defName);
                        }
                        else
                        {
                            settings.meatPolicy[race.defName] = c;
                        }
                    }
                }
            }

            Widgets.EndScrollView();
        }
    }

    // 고기 통합을 처음 켠 저장 파일을 불러올 때 경고한다.
    [HarmonyPatchCategory(Features.MeatOptimization)]
    [HarmonyPatch(typeof(ScribeMetaHeaderUtility), nameof(ScribeMetaHeaderUtility.TryCreateDialogsForVersionMismatchWarnings))]
    internal static class ScribeMetaHeaderUtility_TryCreateDialogs_Meat_Patch
    {
        private static void Postfix(bool __result)
        {
            if (__result && !ScribeMetaHeaderUtility.loadedModNamesList.Contains("Yuudong2Mod")
                && !ScribeMetaHeaderUtility.loadedModNamesList.Contains("Optimization: Meats - C# Edition"))
            {
                Messages.Message("Yuudong2.Meat.ExistingSaveWarning".Translate(), MessageTypeDefOf.CautionInput);
            }
        }
    }
}

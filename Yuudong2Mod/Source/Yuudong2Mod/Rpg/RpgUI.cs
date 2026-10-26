using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Yuudong2Mod.Rpg
{
    // 툴팁 문구. 능력치 효과 목록에서 바로 만들어서, 무엇이 얼마나 바뀌는지와 한계를 보여준다.
    [StaticConstructorOnStartup]
    public static class RpgTooltips
    {
        public static readonly Texture2D DistributeIcon = ContentFinder<Texture2D>.Get("Yuudong2/Rpg/Distribute");
        public static readonly Texture2D RerollIcon = ContentFinder<Texture2D>.Get("Yuudong2/Rpg/Reroll");

        public static string Attribute(RpgAttribute attribute, CompRpgLevel comp)
        {
            StringBuilder sb = new StringBuilder();
            int value = comp?.Get(attribute) ?? 0;
            sb.Append(RpgEffects.AttributeLabel(attribute).Colorize(ColoredText.TipSectionTitleColor));
            if (comp != null)
            {
                sb.Append(": " + value);
            }

            sb.AppendLine();
            sb.AppendLine(("Yuudong2.Rpg.Desc." + attribute).Translate());
            sb.AppendLine();
            sb.AppendLine("Yuudong2.Rpg.PerPoint".Translate().Colorize(ColoredText.TipSectionTitleColor));
            foreach (RpgEffect effect in RpgEffects.EffectsOf(attribute))
            {
                sb.Append(" - " + effect.stat.LabelCap + ": " + effect.DescribePerPoint());
                if (comp != null)
                {
                    sb.Append("  (" + "Yuudong2.Rpg.Now".Translate(effect.DescribeCurrent(value)) + ")");
                }

                sb.AppendLine();
            }

            foreach (string special in Specials(attribute, comp))
            {
                sb.AppendLine(" - " + special);
            }

            sb.AppendLine();
            sb.Append("Yuudong2.Rpg.Limits".Translate());
            return sb.ToString();
        }

        private static IEnumerable<string> Specials(RpgAttribute attribute, CompRpgLevel comp)
        {
            int value = comp?.Get(attribute) ?? 0;
            string Now(string text) => comp == null ? "" : "  (" + "Yuudong2.Rpg.Now".Translate(text) + ")";
            switch (attribute)
            {
                case RpgAttribute.STR:
                    yield return "Yuudong2.Rpg.Special.Melee".Translate() + Now("x" + (1f + 0.003f * value).ToStringPercent());
                    if (!RpgEffects.CarryCapacityHandledElsewhere())
                    {
                        yield return "Yuudong2.Rpg.Special.Mass".Translate() + Now("x" + (1f + 0.01f * value).ToStringPercent());
                    }

                    break;
                case RpgAttribute.CON:
                    string heal = "Yuudong2.Rpg.Special.Heal".Translate(CompRpgLevel.HealThreshold);
                    if (comp != null)
                    {
                        string state = value > CompRpgLevel.HealThreshold
                            ? (string)"Yuudong2.Rpg.Now".Translate((0.01f * (value - 100)).ToString("0.##"))
                            : (string)"Yuudong2.Rpg.Special.HealInactive".Translate();
                        heal += "  (" + state + ")";
                    }

                    yield return heal;
                    break;
                case RpgAttribute.INT:
                    yield return "Yuudong2.Rpg.Special.Exp".Translate() + Now("x" + (1f + 0.01f * value).ToStringPercent());
                    break;
            }
        }

        public static string Level(CompRpgLevel comp)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Yuudong2.Rpg.LevelTip".Translate());
            sb.AppendLine();
            sb.AppendLine("Yuudong2.Rpg.ExpSources".Translate().Colorize(ColoredText.TipSectionTitleColor));
            sb.AppendLine(" - " + "Yuudong2.Rpg.ExpFromSkills".Translate(s.rpgColonistExpPercent));
            sb.AppendLine(" - " + "Yuudong2.Rpg.ExpFromKills".Translate(s.rpgKillExpMult));
            sb.AppendLine(" - " + "Yuudong2.Rpg.ExpFromAnimals".Translate((s.rpgAnimalExpPer10Seconds * 0.01f).ToString("0.00")));
            sb.AppendLine();
            sb.AppendLine("Yuudong2.Rpg.NeedExpFormula".Translate());
            sb.Append(s.rpgMaxLevel == 0 ? "Yuudong2.Rpg.MaxLevelNone".Translate() : "Yuudong2.Rpg.MaxLevel".Translate(s.rpgMaxLevel));
            if (comp != null)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("Yuudong2.Rpg.LevelNow".Translate(comp.level, comp.exp / 100, comp.need_exp / 100));
            }

            return sb.ToString();
        }

        public static string Points(CompRpgLevel comp)
        {
            string text = "Yuudong2.Rpg.PointsTip".Translate(Yuudong2Settings.Current.rpgStartingStatMin);
            if (comp?.autoAttribute != null)
            {
                text += "\n\n" + "Yuudong2.Rpg.AutoNow".Translate(RpgEffects.AttributeLabel(comp.autoAttribute.Value));
            }

            return text;
        }

        public static string Distribute()
        {
            return "Yuudong2.Rpg.DistributeTip".Translate(Yuudong2Settings.Current.rpgStartingStatMin);
        }
    }

    public static class RpgActions
    {
        // 클릭 +1, Shift +10, Ctrl +100. Alt를 같이 누르면 그만큼 회수(시작 능력치 최솟값까지).
        public static void Distribute(CompRpgLevel comp, RpgAttribute attribute)
        {
            int amount = Input.GetKey(KeyCode.LeftShift) ? 10 : Input.GetKey(KeyCode.LeftControl) ? 100 : 1;
            int value = comp.Get(attribute);
            if (Input.GetKey(KeyCode.LeftAlt))
            {
                int refundable = Mathf.Min(value - Yuudong2Settings.Current.rpgStartingStatMin, amount);
                if (refundable > 0)
                {
                    comp.StatPoint += refundable;
                    comp.Set(attribute, value - refundable);
                }

                return;
            }

            int spend = Mathf.Min(comp.StatPoint, amount);
            comp.StatPoint -= spend;
            comp.Set(attribute, value + spend);
        }

        public static void Reroll(CompRpgLevel comp)
        {
            comp.StatPoint = comp.level;
            if (Yuudong2Settings.Current.rpgFlatStartingStats)
            {
                foreach (RpgAttribute attribute in RpgEffects.Attributes)
                {
                    comp.Set(attribute, 0);
                }
            }
            else
            {
                comp.RollStartingStats();
            }
        }
    }

    // ---------- 레벨 탭 ----------

    public class MainTabWindow_RpgLevel : MainTabWindow_PawnTable
    {
        public static int category;

        protected override PawnTableDef PawnTableDef => DefDatabase<PawnTableDef>.GetNamed("Yuudong2_RpgLevelTable");

        protected override IEnumerable<Pawn> Pawns
        {
            get
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    return Enumerable.Empty<Pawn>();
                }

                IEnumerable<Pawn> pawns = map.mapPawns.PawnsInFaction(Faction.OfPlayer);
                switch (category)
                {
                    case 1: return pawns.Where(p => p.RaceProps.Animal);
                    case 2: return pawns.Where(p => p.RaceProps.IsMechanoid);
                    case 3: return pawns.Where(p => p.IsGhoul);
                    default: return pawns.Where(p => p.IsColonist);
                }
            }
        }

        public static void NextCategory()
        {
            category++;
            if (category == 2 && !ModsConfig.BiotechActive)
            {
                category = 3;
            }

            if (category == 3 && !ModsConfig.AnomalyActive || category > 3)
            {
                category = 0;
            }
        }
    }

    public class PawnColumnWorker_RpgLabel : PawnColumnWorker_Label
    {
        private const int TopAreaHeight = 65;

        public override void DoHeader(Rect rect, PawnTable table)
        {
            base.DoHeader(rect, table);
            Rect button = new Rect(rect.x, rect.y + (rect.height - TopAreaHeight), Mathf.Min(rect.width, 360f), 32f);
            string label = "Yuudong2.Rpg.Category".Translate(("Yuudong2.Rpg.Category." + MainTabWindow_RpgLevel.category).Translate());
            if (Widgets.ButtonText(button, label))
            {
                MainTabWindow_RpgLevel.NextCategory();
                table?.SetDirty();
            }

            TooltipHandler.TipRegion(button, "Yuudong2.Rpg.CategoryTip".Translate());
        }

        public override int GetMinHeaderHeight(PawnTable table)
        {
            return Mathf.Max(base.GetMinHeaderHeight(table), TopAreaHeight);
        }
    }

    public abstract class PawnColumnWorker_RpgBase : PawnColumnWorker
    {
        protected abstract string Text(CompRpgLevel comp);

        protected abstract int SortValue(CompRpgLevel comp);

        protected abstract string Tip(CompRpgLevel comp);

        protected virtual bool DrawButton(Rect rect, Pawn pawn, CompRpgLevel comp)
        {
            return false;
        }

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            CompRpgLevel comp = pawn.GetComp<CompRpgLevel>();
            if (comp == null)
            {
                return;
            }

            Rect labelRect = new Rect(rect.x, rect.y, rect.width, Mathf.Min(rect.height, 30f));
            if (DrawButton(new Rect(rect.x, rect.y, 30f, 30f), pawn, comp))
            {
                labelRect.xMin += 35f;
            }

            Verse.Text.Font = GameFont.Small;
            Verse.Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(labelRect, Text(comp));
            Verse.Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(labelRect, Tip(comp));
        }

        protected override string GetHeaderTip(PawnTable table)
        {
            return Tip(null);
        }

        public override int Compare(Pawn a, Pawn b)
        {
            CompRpgLevel ca = a.GetComp<CompRpgLevel>();
            CompRpgLevel cb = b.GetComp<CompRpgLevel>();
            return (ca == null ? int.MinValue : SortValue(ca)).CompareTo(cb == null ? int.MinValue : SortValue(cb));
        }

        public override int GetMinWidth(PawnTable table)
        {
            return base.GetMinWidth(table) + 50;
        }
    }

    public class PawnColumnWorker_RpgLevel : PawnColumnWorker_RpgBase
    {
        protected override string Text(CompRpgLevel comp) => comp.level.ToString();

        protected override int SortValue(CompRpgLevel comp) => comp.level;

        protected override string Tip(CompRpgLevel comp) => RpgTooltips.Level(comp);

        protected override bool DrawButton(Rect rect, Pawn pawn, CompRpgLevel comp)
        {
            if (!DebugSettings.godMode)
            {
                return false;
            }

            TooltipHandler.TipRegion(rect, "Yuudong2.Rpg.Reroll".Translate());
            if (Widgets.ButtonImage(rect, RpgTooltips.RerollIcon))
            {
                RpgActions.Reroll(comp);
            }

            return true;
        }
    }

    public class PawnColumnWorker_RpgExp : PawnColumnWorker_RpgBase
    {
        protected override string Text(CompRpgLevel comp) => (comp.exp / 100).ToString();

        protected override int SortValue(CompRpgLevel comp) => comp.exp;

        protected override string Tip(CompRpgLevel comp) => RpgTooltips.Level(comp);
    }

    public class PawnColumnWorker_RpgNeedExp : PawnColumnWorker_RpgBase
    {
        protected override string Text(CompRpgLevel comp) => (comp.need_exp / 100).ToString();

        protected override int SortValue(CompRpgLevel comp) => comp.need_exp;

        protected override string Tip(CompRpgLevel comp) => "Yuudong2.Rpg.NeedExpFormula".Translate();
    }

    public class PawnColumnWorker_RpgPoints : PawnColumnWorker_RpgBase
    {
        protected override string Text(CompRpgLevel comp) => comp.StatPoint.ToString();

        protected override int SortValue(CompRpgLevel comp) => comp.StatPoint;

        protected override string Tip(CompRpgLevel comp) => RpgTooltips.Points(comp);

        protected override bool DrawButton(Rect rect, Pawn pawn, CompRpgLevel comp)
        {
            if (!DebugSettings.godMode)
            {
                return false;
            }

            TooltipHandler.TipRegion(rect, "Yuudong2.Rpg.GodPoints".Translate());
            if (Widgets.ButtonImage(rect, RpgTooltips.DistributeIcon))
            {
                if (Input.GetKey(KeyCode.LeftAlt))
                {
                    comp.StatPoint = 0;
                }
                else
                {
                    comp.StatPoint += Input.GetKey(KeyCode.LeftShift) ? 10 : Input.GetKey(KeyCode.LeftControl) ? 100 : 1;
                }
            }

            return true;
        }
    }

    // 능력치 열. PawnColumnDef 이름 끝(_STR 등)으로 능력치를 정한다.
    public class PawnColumnWorker_RpgAttribute : PawnColumnWorker_RpgBase
    {
        private RpgAttribute? attribute;

        private RpgAttribute Attribute
        {
            get
            {
                if (!attribute.HasValue)
                {
                    attribute = (RpgAttribute)Enum.Parse(typeof(RpgAttribute), def.defName.Substring(def.defName.LastIndexOf('_') + 1));
                }

                return attribute.Value;
            }
        }

        protected override string Text(CompRpgLevel comp) => comp.Get(Attribute).ToString();

        protected override int SortValue(CompRpgLevel comp) => comp.Get(Attribute);

        protected override string Tip(CompRpgLevel comp) => RpgTooltips.Attribute(Attribute, comp);

        protected override bool DrawButton(Rect rect, Pawn pawn, CompRpgLevel comp)
        {
            if (comp.StatPoint > 0)
            {
                TooltipHandler.TipRegion(rect, RpgTooltips.Distribute());
                if (Widgets.ButtonImage(rect, RpgTooltips.DistributeIcon))
                {
                    RpgActions.Distribute(comp, Attribute);
                }

                return true;
            }

            // 포인트가 없을 때는 자동 분배 체크박스를 보여준다. 하나만 켤 수 있다.
            bool on = comp.autoAttribute == Attribute;
            GUI.DrawTexture(rect, on ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex);
            TooltipHandler.TipRegion(rect, "Yuudong2.Rpg.AutoTip".Translate(RpgEffects.AttributeLabel(Attribute)));
            MouseoverSounds.DoRegion(rect);
            if (Widgets.ButtonInvisible(rect))
            {
                comp.autoAttribute = on ? (RpgAttribute?)null : Attribute;
                (on ? SoundDefOf.Checkbox_TurnedOff : SoundDefOf.Checkbox_TurnedOn).PlayOneShotOnCamera();
            }

            return true;
        }
    }

    // ---------- 폰 정보 탭 ----------

    public class ITab_RpgLevel : ITab
    {
        public ITab_RpgLevel()
        {
            labelKey = "Yuudong2.Rpg.Tab";
            size = new Vector2(320f, 360f);
        }

        public override bool IsVisible => SelPawn?.GetComp<CompRpgLevel>() != null && Yuudong2Mod.AppliedFeatures.Contains(Features.RpgLevelUp);

        protected override void FillTab()
        {
            Rect rect = new Rect(0f, 0f, size.x, size.y).ContractedBy(17f);
            rect.yMin += 10f;
            CompRpgLevel comp = SelPawn?.GetComp<CompRpgLevel>();
            if (comp == null)
            {
                return;
            }

            Text.Font = GameFont.Small;
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            Row(listing, "Yuudong2.Rpg.Level".Translate() + ": " + comp.level, RpgTooltips.Level(comp));
            Row(listing, "Yuudong2.Rpg.Exp".Translate() + ": " + comp.exp / 100 + " / " + comp.need_exp / 100, RpgTooltips.Level(comp));
            Row(listing, "Yuudong2.Rpg.Points".Translate() + ": " + comp.StatPoint, RpgTooltips.Points(comp));
            listing.GapLine();
            foreach (RpgAttribute attribute in RpgEffects.Attributes)
            {
                Row(listing, RpgEffects.AttributeLabel(attribute) + ": " + comp.Get(attribute) + (comp.autoAttribute == attribute ? "  (" + "Yuudong2.Rpg.Auto".Translate() + ")" : ""),
                    RpgTooltips.Attribute(attribute, comp));
            }

            listing.Gap();
            GUI.color = Color.gray;
            listing.Label("Yuudong2.Rpg.TabHint".Translate());
            GUI.color = Color.white;
            listing.End();
        }

        private static void Row(Listing_Standard listing, string text, string tip)
        {
            Rect row = listing.GetRect(Text.LineHeight);
            Widgets.Label(row, text);
            if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            TooltipHandler.TipRegion(row, tip);
        }
    }

    // ---------- 세부 설정 ----------

    public static class RpgSettingsUI
    {
        private static string maxLevelBuffer;

        public static void Draw(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Rpg.Set.Flat".Translate(), ref s.rpgFlatStartingStats, "Yuudong2.Rpg.Set.Flat.Desc".Translate());
            listing.GapLine();
            s.rpgColonistExpPercent = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.SkillExp".Translate(s.rpgColonistExpPercent), s.rpgColonistExpPercent, 1f, 200f, 0.6f, "Yuudong2.Rpg.Set.SkillExp.Desc".Translate());
            s.rpgAnimalExpPer10Seconds = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.AnimalExp".Translate((s.rpgAnimalExpPer10Seconds * 0.01f).ToString("0.00")), s.rpgAnimalExpPer10Seconds, 1f, 12000f, 0.6f, "Yuudong2.Rpg.Set.AnimalExp.Desc".Translate());
            s.rpgKillExpMult = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.KillExp".Translate(s.rpgKillExpMult), s.rpgKillExpMult, 1f, 1000f, 0.6f, "Yuudong2.Rpg.Set.KillExp.Desc".Translate());
            listing.GapLine();
            s.rpgStartingStatMin = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.StartMin".Translate(s.rpgStartingStatMin), s.rpgStartingStatMin, -50f, 200f, 0.6f, "Yuudong2.Rpg.Set.StartMin.Desc".Translate());
            s.rpgStartingStatMax = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.StartMax".Translate(s.rpgStartingStatMax), s.rpgStartingStatMax, -50f, 200f, 0.6f, "Yuudong2.Rpg.Set.StartMax.Desc".Translate());
            if (s.rpgStartingStatMax < s.rpgStartingStatMin)
            {
                s.rpgStartingStatMax = s.rpgStartingStatMin;
            }

            listing.GapLine();
            Rect maxRect = listing.GetRect(Text.LineHeight);
            Widgets.TextFieldNumericLabeled(maxRect, "Yuudong2.Rpg.Set.MaxLevel".Translate() + " ", ref s.rpgMaxLevel, ref maxLevelBuffer, 0f, 1E+09f);
            TooltipHandler.TipRegion(maxRect, "Yuudong2.Rpg.Set.MaxLevel.Desc".Translate());
            listing.Gap(6f);
            s.rpgLevelScaling = (int)listing.SliderLabeled("Yuudong2.Rpg.Set.Scaling".Translate(s.rpgLevelScaling), s.rpgLevelScaling, 0f, 100f, 0.6f, "Yuudong2.Rpg.Set.Scaling.Desc".Translate());
            listing.End();
        }
    }
}

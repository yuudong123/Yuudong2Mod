using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod.SurgeryManager
{
    public enum HealthColumn
    {
        Injuries,
        Illness,
        Chronic,
        Scars,
        MissingParts,
        Other,
    }

    public class HealthLine
    {
        public string text;
        public Color color = Color.white;
        public string tip;
    }

    // 정착민 한 명의 건강 문제를 종류별로 나눈 것. 수술 관리 창의 "건강 총괄" 탭이 그린다.
    public class HealthSummary
    {
        public static readonly int ColumnCount = System.Enum.GetValues(typeof(HealthColumn)).Length;

        private static readonly Color BadColor = new Color(1f, 0.45f, 0.4f);
        private static readonly Color WarnColor = new Color(1f, 0.8f, 0.4f);
        private static readonly Color GoodColor = new Color(0.6f, 0.9f, 0.6f);

        public readonly List<HealthLine>[] columns = new List<HealthLine>[ColumnCount];

        public int MaxLines => columns.Max(c => c.Count);

        public bool Any => columns.Any(c => c.Count > 0);

        public List<HealthLine> this[HealthColumn column] => columns[(int)column];

        public static HealthSummary Build(Pawn pawn)
        {
            HealthSummary summary = new HealthSummary();
            for (int i = 0; i < ColumnCount; i++)
            {
                summary.columns[i] = new List<HealthLine>();
            }

            HediffSet set = pawn.health.hediffSet;
            int wounds = 0;
            int untended = 0;
            foreach (Hediff hediff in set.hediffs)
            {
                if (!hediff.Visible || hediff is Hediff_MissingPart)
                {
                    continue;
                }

                if (hediff is Hediff_Injury)
                {
                    if (hediff.IsPermanent())
                    {
                        summary[HealthColumn.Scars].Add(Line(pawn, hediff, WithPart(hediff), Color.white));
                    }
                    else
                    {
                        wounds++;
                        untended += hediff.TendableNow() ? 1 : 0;
                    }

                    continue;
                }

                if (!hediff.def.isBad || hediff.def.countsAsAddedPartOrImplant)
                {
                    continue;
                }

                HediffComp_Immunizable immunizable = hediff.TryGetComp<HediffComp_Immunizable>();
                float lethal = hediff.def.lethalSeverity;
                if (immunizable != null && lethal > 0f)
                {
                    float immunity = immunizable.Immunity;
                    summary[HealthColumn.Illness].Add(Line(pawn, hediff,
                        "Yuudong2.Health.Immunity".Translate(WithPart(hediff), (hediff.Severity / lethal).ToStringPercent(), immunity.ToStringPercent()),
                        immunity >= hediff.Severity / lethal ? GoodColor : BadColor));
                }
                else if (lethal > 0f || hediff.TryGetComp<HediffComp_GrowthMode>() != null)
                {
                    float progress = hediff.Severity / (lethal > 0f ? lethal : hediff.def.maxSeverity < 100f ? Mathf.Max(hediff.def.maxSeverity, 0.01f) : 1f);
                    summary[HealthColumn.Illness].Add(Line(pawn, hediff, WithPart(hediff) + " " + progress.ToStringPercent(), progress >= 0.5f ? BadColor : WarnColor));
                }
                else if (hediff.def.chronic)
                {
                    summary[HealthColumn.Chronic].Add(Line(pawn, hediff, WithPart(hediff), WarnColor));
                }
                else
                {
                    summary[HealthColumn.Other].Add(Line(pawn, hediff, WithPart(hediff), hediff.TendableNow() ? BadColor : Color.white));
                }
            }

            foreach (Hediff_MissingPart missing in set.GetMissingPartsCommonAncestors())
            {
                summary[HealthColumn.MissingParts].Add(Line(pawn, missing, missing.Part.LabelCap, missing.Bleeding ? BadColor : Color.white));
            }

            float bleed = set.BleedRateTotal;
            if (bleed > 0.001f)
            {
                summary[HealthColumn.Injuries].Add(new HealthLine { text = "Yuudong2.Health.Bleeding".Translate(bleed.ToStringPercent()), color = BadColor });
            }

            if (wounds > 0)
            {
                summary[HealthColumn.Injuries].Add(new HealthLine
                {
                    text = untended > 0 ? "Yuudong2.Health.WoundsUntended".Translate(wounds, untended) : "Yuudong2.Health.Wounds".Translate(wounds),
                    color = untended > 0 ? WarnColor : Color.white,
                });
            }

            return summary;
        }

        private static string WithPart(Hediff hediff)
        {
            return hediff.Part == null ? hediff.LabelCap : hediff.LabelCap + " (" + hediff.Part.Label + ")";
        }

        private static HealthLine Line(Pawn pawn, Hediff hediff, string text, Color color)
        {
            return new HealthLine { text = text, color = color, tip = hediff.GetTooltip(pawn, false) };
        }
    }
}

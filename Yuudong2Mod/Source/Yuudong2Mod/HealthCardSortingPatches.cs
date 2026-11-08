using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 건강 탭의 부위 순서와 부위 안의 항목 순서를 정착민마다 같게 맞춘다.
    // 부위: 바닐라 우선순위(높이 × 10000 + 차지하는 비율)대로 두고, 같으면 몸 정의 순서로 정한다. 바닐라는 같을 때 상태 이상이 붙은 순서를 따라 정착민마다 달랐다.
    // 부위 안: 상처가 아닌 항목을 먼저, 상처를 뒤에 두고 각각 이름순으로 정렬한다.
    // 건강 탭을 그리는 동안 매 프레임 여러 번 불리므로 정착민별로 결과를 캐시하고, 상태 이상 목록이 바뀌었거나 0.5초가 지났을 때만 다시 계산한다.
    internal static class HealthCardHediffSortingUtility
    {
        private const int RefreshFrames = 30;

        private static readonly StringComparer LabelComparer = CreateLabelComparer();
        private static readonly Func<Pawn, bool, IEnumerable<Hediff>> VisibleHediffs =
            AccessTools.MethodDelegate<Func<Pawn, bool, IEnumerable<Hediff>>>(AccessTools.Method(typeof(HealthCardUtility), "VisibleHediffs"));
        private static readonly ConditionalWeakTable<Pawn, CachedGroups[]> Cache = new ConditionalWeakTable<Pawn, CachedGroups[]>();

        private static readonly Dictionary<BodyPartRecord, List<Hediff>> tmpByPart = new Dictionary<BodyPartRecord, List<Hediff>>();
        private static readonly List<BodyPartRecord> tmpParts = new List<BodyPartRecord>();

        public static List<IGrouping<BodyPartRecord, Hediff>> GroupsFor(Pawn pawn, bool showBloodLoss)
        {
            CachedGroups[] entries = Cache.GetValue(pawn, _ => new CachedGroups[2]);
            int slot = showBloodLoss ? 1 : 0;
            CachedGroups entry = entries[slot] ?? (entries[slot] = new CachedGroups());
            int frame = Time.frameCount;
            long signature = Signature(pawn);
            if (entry.groups == null || entry.signature != signature || frame - entry.frame >= RefreshFrames || frame < entry.frame)
            {
                entry.groups = Build(pawn, showBloodLoss);
                entry.signature = signature;
                entry.frame = frame;
            }

            return entry.groups;
        }

        // 상태 이상이 붙거나 떨어지면 바뀌는 값. 표시 여부처럼 이걸로 못 잡는 변화는 0.5초 안에 반영된다.
        private static long Signature(Pawn pawn)
        {
            List<Hediff> hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs == null)
            {
                return 0;
            }

            long signature = hediffs.Count;
            for (int i = 0; i < hediffs.Count; i++)
            {
                signature = signature * 31 + hediffs[i].loadID;
            }

            return signature;
        }

        private static List<IGrouping<BodyPartRecord, Hediff>> Build(Pawn pawn, bool showBloodLoss)
        {
            List<Hediff> wholeBody = null;
            tmpByPart.Clear();
            tmpParts.Clear();
            foreach (Hediff hediff in VisibleHediffs(pawn, showBloodLoss))
            {
                BodyPartRecord part = hediff.Part;
                List<Hediff> list;
                if (part == null)
                {
                    list = wholeBody ?? (wholeBody = new List<Hediff>());
                }
                else if (!tmpByPart.TryGetValue(part, out list))
                {
                    list = new List<Hediff>();
                    tmpByPart[part] = list;
                    tmpParts.Add(part);
                }

                list.Add(hediff);
            }

            tmpParts.Sort(ComparePart);
            List<IGrouping<BodyPartRecord, Hediff>> groups = new List<IGrouping<BodyPartRecord, Hediff>>(tmpParts.Count + 1);
            if (wholeBody != null)
            {
                SortHediffs(wholeBody);
                groups.Add(new OrderedHediffGrouping(null, wholeBody));
            }

            for (int i = 0; i < tmpParts.Count; i++)
            {
                List<Hediff> list = tmpByPart[tmpParts[i]];
                SortHediffs(list);
                groups.Add(new OrderedHediffGrouping(tmpParts[i], list));
            }

            tmpByPart.Clear();
            tmpParts.Clear();
            return groups;
        }

        private static int ComparePart(BodyPartRecord a, BodyPartRecord b)
        {
            int byPriority = ListPriority(b).CompareTo(ListPriority(a));
            return byPriority != 0 ? byPriority : a.Index.CompareTo(b.Index);
        }

        // 바닐라 HealthCardUtility.GetListPriority와 같은 계산.
        private static float ListPriority(BodyPartRecord part)
        {
            return (int)part.height * 10000 + part.coverageAbsWithChildren;
        }

        private static void SortHediffs(List<Hediff> hediffs)
        {
            if (hediffs.Count < 2)
            {
                return;
            }

            HediffSortEntry[] entries = new HediffSortEntry[hediffs.Count];
            for (int i = 0; i < hediffs.Count; i++)
            {
                entries[i] = new HediffSortEntry(hediffs[i], i);
            }

            Array.Sort(entries, CompareEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                hediffs[i] = entries[i].Hediff;
            }
        }

        private static int CompareEntries(HediffSortEntry a, HediffSortEntry b)
        {
            int result = (a.Hediff is Hediff_Injury ? 1 : 0).CompareTo(b.Hediff is Hediff_Injury ? 1 : 0);
            if (result == 0)
            {
                result = LabelComparer.Compare(a.Label, b.Label);
            }

            if (result == 0)
            {
                result = StringComparer.OrdinalIgnoreCase.Compare(a.Hediff.def?.defName ?? string.Empty, b.Hediff.def?.defName ?? string.Empty);
            }

            return result != 0 ? result : a.Index.CompareTo(b.Index);
        }

        private static StringComparer CreateLabelComparer()
        {
            try
            {
                return StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true);
            }
            catch (CultureNotFoundException)
            {
                return StringComparer.CurrentCultureIgnoreCase;
            }
        }

        private sealed class CachedGroups
        {
            public List<IGrouping<BodyPartRecord, Hediff>> groups;
            public long signature;
            public int frame;
        }

        private readonly struct HediffSortEntry
        {
            public HediffSortEntry(Hediff hediff, int index)
            {
                Hediff = hediff;
                Index = index;
                Label = hediff.LabelCap ?? string.Empty;
            }

            public Hediff Hediff { get; }
            public int Index { get; }
            public string Label { get; }
        }

        private sealed class OrderedHediffGrouping : IGrouping<BodyPartRecord, Hediff>
        {
            private readonly List<Hediff> hediffs;

            public OrderedHediffGrouping(BodyPartRecord key, List<Hediff> hediffs)
            {
                Key = key;
                this.hediffs = hediffs;
            }

            public BodyPartRecord Key { get; }

            public IEnumerator<Hediff> GetEnumerator()
            {
                return hediffs.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    [HarmonyPatchCategory(Features.HealthCardSorting)]
    [HarmonyPatch(typeof(HealthCardUtility), "VisibleHediffGroupsInOrder")]
    internal static class HealthCardUtility_VisibleHediffGroupsInOrder_SortPatch
    {
        private static bool Prefix(Pawn pawn, bool showBloodLoss, ref IEnumerable<IGrouping<BodyPartRecord, Hediff>> __result)
        {
            __result = HealthCardHediffSortingUtility.GroupsFor(pawn, showBloodLoss);
            return false;
        }
    }
}

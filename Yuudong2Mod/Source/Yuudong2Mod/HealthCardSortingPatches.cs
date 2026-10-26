using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    internal static class HealthCardHediffSortingUtility
    {
        private static readonly StringComparer LabelComparer = CreateLabelComparer();

        public static IEnumerable<IGrouping<BodyPartRecord, Hediff>> SortWithinBodyParts(
            IEnumerable<IGrouping<BodyPartRecord, Hediff>> groups)
        {
            if (groups == null)
            {
                yield break;
            }

            foreach (IGrouping<BodyPartRecord, Hediff> group in groups)
            {
                yield return new OrderedHediffGrouping(group.Key, SortHediffs(group));
            }
        }

        private static List<Hediff> SortHediffs(IEnumerable<Hediff> hediffs)
        {
            return hediffs
                .Select((hediff, index) => new HediffSortEntry(hediff, index))
                .OrderBy(entry => IsInjury(entry.Hediff) ? 1 : 0)
                .ThenBy(entry => LabelForSort(entry.Hediff), LabelComparer)
                .ThenBy(entry => entry.Hediff?.def?.defName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Hediff)
                .ToList();
        }

        private static bool IsInjury(Hediff hediff)
        {
            return hediff is Hediff_Injury;
        }

        private static string LabelForSort(Hediff hediff)
        {
            return hediff?.LabelCap ?? string.Empty;
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

        private readonly struct HediffSortEntry
        {
            public HediffSortEntry(Hediff hediff, int index)
            {
                Hediff = hediff;
                Index = index;
            }

            public Hediff Hediff { get; }
            public int Index { get; }
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
        private static void Postfix(ref IEnumerable<IGrouping<BodyPartRecord, Hediff>> __result)
        {
            __result = HealthCardHediffSortingUtility.SortWithinBodyParts(__result);
        }
    }
}

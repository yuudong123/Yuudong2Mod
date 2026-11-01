using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 바닐라 자동 재건은 부서진 건물 자리에 청사진을 놓으면서 저장 설정만 넘기고 작업 지시서는 버린다.
    // 부서진 작업대의 지시서를 복사해 두었다가 청사진 → 건설 중인 틀 → 다 지은 건물로 넘겨 준다.
    public class RebuildBillsComponent : MapComponent
    {
        private Dictionary<Thing, SavedBills> pending = new Dictionary<Thing, SavedBills>();
        private List<Thing> keys;
        private List<SavedBills> values;

        public RebuildBillsComponent(Map map) : base(map)
        {
        }

        public static RebuildBillsComponent For(Map map)
        {
            return map?.GetComponent<RebuildBillsComponent>();
        }

        public void Store(Thing carrier, List<Bill> bills)
        {
            if (!bills.NullOrEmpty())
            {
                pending[carrier] = new SavedBills { bills = bills };
            }
        }

        public List<Bill> Take(Thing carrier)
        {
            if (carrier == null || !pending.TryGetValue(carrier, out SavedBills saved))
            {
                return null;
            }

            pending.Remove(carrier);
            return saved.bills;
        }

        public override void ExposeData()
        {
            // 재건을 취소한 청사진의 지시서는 버린다.
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                pending.RemoveAll(entry => entry.Key == null || entry.Key.Destroyed);
            }

            Scribe_Collections.Look(ref pending, "pendingBills", LookMode.Reference, LookMode.Deep, ref keys, ref values);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pending ??= new Dictionary<Thing, SavedBills>();
                pending.RemoveAll(entry => entry.Key == null || entry.Value?.bills == null);
            }
        }

        public class SavedBills : IExposable
        {
            public List<Bill> bills = new List<Bill>();

            public void ExposeData()
            {
                Scribe_Collections.Look(ref bills, "bills", LookMode.Deep);
            }
        }
    }

    [HarmonyPatchCategory(Features.RebuildBills)]
    [HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildOnDestroyed))]
    internal static class ThingUtility_CheckAutoRebuildOnDestroyed_RebuildBills_Patch
    {
        private static void Postfix(Thing thing, Map map, Blueprint_Build __result)
        {
            RebuildBillsComponent component = RebuildBillsComponent.For(map);
            if (component == null)
            {
                return;
            }

            // 다시 짓던 틀이 또 부서지면 새 청사진으로 넘긴다. 재건하지 않으면 그냥 버린다.
            List<Bill> bills = component.Take(thing);
            if (__result == null)
            {
                return;
            }

            if (bills == null && thing is Building && thing is IBillGiver giver && giver.BillStack != null)
            {
                bills = giver.BillStack.Bills.Select(bill =>
                {
                    Bill clone = bill.Clone();
                    clone.InitializeAfterClone();
                    return clone;
                }).ToList();
            }

            if (bills != null)
            {
                component.Store(__result, bills);
            }
        }
    }

    [HarmonyPatchCategory(Features.RebuildBills)]
    [HarmonyPatch(typeof(Blueprint_Build), "MakeSolidThing")]
    internal static class Blueprint_Build_MakeSolidThing_RebuildBills_Patch
    {
        private static void Postfix(Blueprint_Build __instance, Thing __result)
        {
            RebuildBillsComponent component = RebuildBillsComponent.For(__instance.Map);
            List<Bill> bills = component?.Take(__instance);
            if (bills != null && __result != null)
            {
                component.Store(__result, bills);
            }
        }
    }

    [HarmonyPatchCategory(Features.RebuildBills)]
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    internal static class Frame_CompleteConstruction_RebuildBills_Patch
    {
        private static void Prefix(Frame __instance, out (Map map, List<Bill> bills) __state)
        {
            Map map = __instance.Map;
            __state = (map, RebuildBillsComponent.For(map)?.Take(__instance));
        }

        private static void Postfix(Frame __instance, (Map map, List<Bill> bills) __state)
        {
            if (__state.bills == null || __state.map == null)
            {
                return;
            }

            foreach (Thing thing in __instance.Position.GetThingList(__state.map))
            {
                if (thing.def == __instance.def.entityDefToBuild && thing is IBillGiver giver && giver.BillStack != null)
                {
                    foreach (Bill bill in __state.bills)
                    {
                        giver.BillStack.AddBill(bill);
                    }

                    return;
                }
            }
        }
    }
}

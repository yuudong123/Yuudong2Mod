using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 의류 정책 필터에 무기 분류를 더한다. 정책에서 뺀 무기는 정착민이 알아서 집지 않고, 들고 있으면 한가할 때 내려놓는다.
    // 플레이어가 직접 장착시킨 무기는 의류의 강제 착용처럼 정책에서 빠져 있어도 그대로 둔다.
    public class GameComponent_PolicyWeapons : GameComponent
    {
        public static GameComponent_PolicyWeapons Instance;

        private HashSet<int> forced = new HashSet<int>();

        public GameComponent_PolicyWeapons(Game game)
        {
            Instance = this;
        }

        public bool IsForced(Thing thing)
        {
            return forced.Contains(thing.thingIDNumber);
        }

        public void SetForced(Thing thing, bool value)
        {
            if (value)
            {
                forced.Add(thing.thingIDNumber);
            }
            else
            {
                forced.Remove(thing.thingIDNumber);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref forced, "forcedWeapons", LookMode.Value);
            forced ??= new HashSet<int>();
        }
    }

    public static class PolicyWeaponsUtility
    {
        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.PolicyWeapons);

        public static void AllowWeapons(ApparelPolicy policy)
        {
            policy.filter.SetAllow(ThingCategoryDefOf.Weapons, true);
        }

        private static ApparelPolicy PolicyOf(Pawn pawn)
        {
            if (pawn.outfits == null || pawn.Faction == null || !pawn.Faction.IsPlayer || !pawn.RaceProps.Humanlike)
            {
                return null;
            }
            return pawn.outfits.CurrentApparelPolicy;
        }

        // 알아서 집을 때: 정책 필터 전체(품질·내구도 범위 포함)를 본다.
        public static bool AllowsPickup(Pawn pawn, Thing weapon)
        {
            ApparelPolicy policy = PolicyOf(pawn);
            return policy == null || policy.filter.Allows(weapon);
        }

        // 들고 있는 무기를 내려놓을 때: 무기 종류가 빠졌는지만 본다. 싸우다 닳았다고 유일한 무기를 버리지 않게 한다.
        public static bool MustDrop(Pawn pawn, Thing weapon)
        {
            ApparelPolicy policy = PolicyOf(pawn);
            if (policy == null || weapon == null || !weapon.def.IsWeapon || policy.filter.Allows(weapon.def))
            {
                return false;
            }
            return GameComponent_PolicyWeapons.Instance == null || !GameComponent_PolicyWeapons.Instance.IsForced(weapon);
        }

        public static Job DropJob(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Drafted || pawn.equipment == null || pawn.InMentalState)
            {
                return null;
            }
            ThingWithComps primary = pawn.equipment.Primary;
            if (primary != null && MustDrop(pawn, primary))
            {
                return JobMaker.MakeJob(JobDefOf.DropEquipment, primary);
            }
            // 소지품에 넣어 둔 보조 무기(무기 2개 착용)도 정책에서 빠졌으면 그 자리에 내려놓는다.
            if (WeaponPairUtility.Active && pawn.inventory != null)
            {
                ThingOwner<Thing> held = pawn.inventory.innerContainer;
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    Thing thing = held[i];
                    if (WeaponPairUtility.IsKeptSidearm(pawn, thing) && MustDrop(pawn, thing))
                    {
                        held.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing _);
                    }
                }
            }
            return null;
        }
    }

    // 정책 창에 보이는 전체 목록에 무기 분류를 더한다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(Dialog_ManageApparelPolicies), "ApparelGlobalFilter", MethodType.Getter)]
    internal static class Dialog_ManageApparelPolicies_ApparelGlobalFilter_Patch
    {
        private static bool done;

        private static void Postfix(ThingFilter __result)
        {
            if (!done && __result != null)
            {
                __result.SetAllow(ThingCategoryDefOf.Weapons, true);
                done = true;
            }
        }
    }

    // 새로 만드는 정책은 무기를 모두 허용한 채로 시작한다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(OutfitDatabase), nameof(OutfitDatabase.MakeNewOutfit))]
    internal static class OutfitDatabase_MakeNewOutfit_Patch
    {
        private static void Postfix(ApparelPolicy __result)
        {
            PolicyWeaponsUtility.AllowWeapons(__result);
        }
    }

    // 새 게임의 기본 정책은 만든 뒤 필터를 비우고 옷만 다시 넣으므로, 끝난 뒤 무기를 허용한다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(OutfitDatabase), "GenerateStartingOutfits")]
    internal static class OutfitDatabase_GenerateStartingOutfits_Patch
    {
        private static void Postfix(OutfitDatabase __instance)
        {
            foreach (ApparelPolicy policy in __instance.AllOutfits)
            {
                PolicyWeaponsUtility.AllowWeapons(policy);
            }
        }
    }

    // 이 기능을 켜기 전에 만든 정책은 무기가 하나도 허용되지 않은 상태라, 불러올 때 한 번 모든 무기를 허용한다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(ApparelPolicy), nameof(ApparelPolicy.ExposeData))]
    internal static class ApparelPolicy_ExposeData_Patch
    {
        private static void Postfix(ApparelPolicy __instance)
        {
            bool weaponsSet = true;
            Scribe_Values.Look(ref weaponsSet, "yuudong2WeaponsSet", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !weaponsSet && __instance.filter != null)
            {
                PolicyWeaponsUtility.AllowWeapons(__instance);
            }
        }
    }

    // 플레이어가 시켜서 장착한 무기는 강제로 표시하고, 알아서 장착한 무기는 표시를 지운다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment))]
    internal static class Pawn_EquipmentTracker_AddEquipment_PolicyWeapons_Patch
    {
        private static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps newEq)
        {
            Pawn pawn = __instance.pawn;
            Job job = pawn?.CurJob;
            if (newEq == null || GameComponent_PolicyWeapons.Instance == null || job == null || job.def != JobDefOf.Equip || job.targetA.Thing != newEq)
            {
                return;
            }
            GameComponent_PolicyWeapons.Instance.SetForced(newEq, job.playerForced);
        }
    }

    // 한가할 때 정책에서 빠진 무기를 내려놓는다. 무기 2개 착용의 보조 무기 찾기보다 먼저 본다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    internal static class JobGiver_OptimizeApparel_TryGiveJob_PolicyWeapons_Patch
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
            {
                __result = PolicyWeaponsUtility.DropJob(pawn);
            }
        }
    }

    // 바닐라의 무기 줍기(습격 대기 등)에서도 정책에서 빠진 무기는 집지 않는다.
    [HarmonyPatchCategory(Features.PolicyWeapons)]
    [HarmonyPatch(typeof(JobGiver_PickUpOpportunisticWeapon), "ShouldEquipWeapon")]
    internal static class JobGiver_PickUpOpportunisticWeapon_ShouldEquipWeapon_Patch
    {
        private static void Postfix(Thing newWep, Pawn pawn, ref bool __result)
        {
            if (__result && !PolicyWeaponsUtility.AllowsPickup(pawn, newWep))
            {
                __result = false;
            }
        }
    }
}

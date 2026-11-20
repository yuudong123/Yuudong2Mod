using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    public enum WeaponPairMode
    {
        Auto,
        Melee,
        Ranged,
    }

    // 정착민별 무기 고정 설정. 자동(기본)은 저장하지 않는다.
    public class GameComponent_WeaponPair : GameComponent
    {
        public static GameComponent_WeaponPair Instance;

        private Dictionary<string, int> modes = new Dictionary<string, int>();

        public GameComponent_WeaponPair(Game game)
        {
            Instance = this;
        }

        public WeaponPairMode ModeOf(Pawn pawn)
        {
            return modes.Count > 0 && modes.TryGetValue(pawn.GetUniqueLoadID(), out int mode) ? (WeaponPairMode)mode : WeaponPairMode.Auto;
        }

        public void SetMode(Pawn pawn, WeaponPairMode mode)
        {
            if (mode == WeaponPairMode.Auto)
            {
                modes.Remove(pawn.GetUniqueLoadID());
            }
            else
            {
                modes[pawn.GetUniqueLoadID()] = (int)mode;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref modes, "modes", LookMode.Value, LookMode.Value);
            modes ??= new Dictionary<string, int>();
        }
    }

    // 정착민이 근접 무기와 원거리 무기를 하나씩 함께 지닌다. 손에 들지 않은 쪽은 소지품에 있다.
    // 주기적으로 검사하지 않고, 근접 공격을 시작할 때와 사격할 무기를 고를 때만 바꿔 든다.
    public static class WeaponPairUtility
    {
        private const int SwapCooldownTicks = 30;

        private static readonly Dictionary<int, int> lastSwapTick = new Dictionary<int, int>();

        private static bool unloadRetry;

        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.WeaponPair);

        public static bool IsWeapon(Thing thing)
        {
            ThingDef def = thing.def;
            return def.equipmentType == EquipmentType.Primary && (def.IsRangedWeapon || def.IsMeleeWeapon) && thing is ThingWithComps;
        }

        public static bool Eligible(Pawn pawn)
        {
            return pawn.inventory != null && pawn.equipment != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.RaceProps.Humanlike;
        }

        public static ThingWithComps Stowed(Pawn pawn, bool ranged)
        {
            ThingOwner<Thing> held = pawn.inventory.innerContainer;
            for (int i = 0; i < held.Count; i++)
            {
                Thing thing = held[i];
                if (thing.def.IsRangedWeapon == ranged && IsWeapon(thing))
                {
                    return (ThingWithComps)thing;
                }
            }

            return null;
        }

        // 손에 든 무기와 종류가 다른, 소지품 속 무기 하나. 바닐라의 소지품 내려놓기에서 지킨다.
        public static bool IsKeptSidearm(Pawn pawn, Thing thing)
        {
            if (!IsWeapon(thing) || !Eligible(pawn))
            {
                return false;
            }

            ThingWithComps primary = pawn.equipment.Primary;
            if (primary != null && IsWeapon(primary) && primary.def.IsRangedWeapon == thing.def.IsRangedWeapon)
            {
                return false;
            }

            return Stowed(pawn, thing.def.IsRangedWeapon) == thing;
        }

        public static WeaponPairMode ModeOf(Pawn pawn)
        {
            return GameComponent_WeaponPair.Instance?.ModeOf(pawn) ?? WeaponPairMode.Auto;
        }

        public static bool Swap(Pawn pawn, ThingWithComps stowed, bool ignoreCooldown = false)
        {
            int tick = Find.TickManager.TicksGame;
            if (!ignoreCooldown && lastSwapTick.TryGetValue(pawn.thingIDNumber, out int last) && tick - last < SwapCooldownTicks)
            {
                return false;
            }

            if (!EquipmentUtility.CanEquip(stowed, pawn, out string _, false))
            {
                return false;
            }

            ThingWithComps current = pawn.equipment.Primary;
            if (current != null && !pawn.equipment.TryTransferEquipmentToContainer(current, pawn.inventory.innerContainer))
            {
                return false;
            }

            pawn.inventory.innerContainer.Remove(stowed);
            pawn.equipment.AddEquipment(stowed);
            lastSwapTick[pawn.thingIDNumber] = tick;
            return true;
        }

        private static bool HostileAdjacent(Pawn pawn)
        {
            Map map = pawn.Map;
            IntVec3 position = pawn.Position;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = position + GenAdj.AdjacentCells[i];
                if (!c.InBounds(map))
                {
                    continue;
                }

                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int j = 0; j < things.Count; j++)
                {
                    if (things[j] is Pawn other && !other.Downed && other.HostileTo(pawn))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // 근접 공격을 시작하기 직전: 손에 총을 들고 있고 소지품에 근접 무기가 있으면 바꿔 든다.
        public static bool DrawMelee(Pawn pawn)
        {
            if (pawn.inventory == null || pawn.inventory.innerContainer.Count == 0 || !Eligible(pawn))
            {
                return false;
            }

            ThingWithComps primary = pawn.equipment.Primary;
            if ((primary != null && !primary.def.IsRangedWeapon) || ModeOf(pawn) == WeaponPairMode.Ranged)
            {
                return false;
            }

            ThingWithComps melee = Stowed(pawn, false);
            return melee != null && Swap(pawn, melee);
        }

        // 사격할 무기를 고르기 직전: 손에 근접 무기를 들고 있고, 붙어 있는 적이 없고, 소지품에 원거리 무기가 있으면 바꿔 든다.
        public static bool DrawRanged(Pawn pawn, Thing target)
        {
            if (pawn.inventory == null || pawn.inventory.innerContainer.Count == 0 || !Eligible(pawn) || !pawn.Spawned)
            {
                return false;
            }

            ThingWithComps primary = pawn.equipment.Primary;
            if ((primary != null && primary.def.IsRangedWeapon) || pawn.stances.FullBodyBusy || ModeOf(pawn) == WeaponPairMode.Melee)
            {
                return false;
            }

            ThingWithComps ranged = Stowed(pawn, true);
            if (ranged == null || (target != null && target.Spawned && pawn.Position.AdjacentTo8WayOrInside(target.Position)) || HostileAdjacent(pawn))
            {
                return false;
            }

            return Swap(pawn, ranged);
        }

        public static void SetMode(Pawn pawn, WeaponPairMode mode)
        {
            GameComponent_WeaponPair.Instance?.SetMode(pawn, mode);
            if (mode == WeaponPairMode.Auto)
            {
                return;
            }

            bool wantRanged = mode == WeaponPairMode.Ranged;
            ThingWithComps primary = pawn.equipment.Primary;
            if (primary != null && IsWeapon(primary) && primary.def.IsRangedWeapon == wantRanged)
            {
                return;
            }

            ThingWithComps stowed = Stowed(pawn, wantRanged);
            if (stowed != null)
            {
                Swap(pawn, stowed, true);
            }
        }

        public static string ModeLabel(WeaponPairMode mode)
        {
            return ("Yuudong2.WeaponPair.Mode." + mode).Translate();
        }

        public static IEnumerable<Gizmo> Gizmos(Pawn pawn)
        {
            ThingWithComps primary = pawn.equipment.Primary;
            ThingWithComps other = primary != null && IsWeapon(primary) ? Stowed(pawn, !primary.def.IsRangedWeapon) : Stowed(pawn, true) ?? Stowed(pawn, false);
            if (other == null)
            {
                yield break;
            }

            WeaponPairMode mode = ModeOf(pawn);
            yield return new Command_Action
            {
                defaultLabel = "Yuudong2.WeaponPair.Command".Translate(ModeLabel(mode)),
                defaultDesc = "Yuudong2.WeaponPair.CommandDesc".Translate(primary?.LabelCap ?? "-", other.LabelCap),
                icon = other.def.uiIcon,
                iconAngle = other.def.uiIconAngle,
                defaultIconColor = other.DrawColor,
                action = () =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (WeaponPairMode choice in Enum.GetValues(typeof(WeaponPairMode)))
                    {
                        WeaponPairMode chosen = choice;
                        options.Add(new FloatMenuOption(ModeLabel(chosen), () => SetMode(pawn, chosen)));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
        }

        // 다른 종류의 무기를 새로 들 때: 들고 있던 무기를 떨어뜨리지 않고 소지품에 넣는다. 같은 종류를 이미 지니고 있으면 바닐라대로 떨어뜨린다.
        public static bool StowInsteadOfDrop(Pawn pawn, ThingWithComps incoming)
        {
            ThingWithComps primary = pawn.equipment.Primary;
            if (primary == null || !Eligible(pawn) || !IsWeapon(primary) || !IsWeapon(incoming) || primary.def.IsRangedWeapon == incoming.def.IsRangedWeapon)
            {
                return false;
            }

            if (Stowed(pawn, primary.def.IsRangedWeapon) != null)
            {
                return false;
            }

            return pawn.equipment.TryTransferEquipmentToContainer(primary, pawn.inventory.innerContainer);
        }

        // 지키는 보조 무기가 "내려놓을 물건"으로 뽑혔으면 잠깐 빼고 다시 물어 다음 물건을 받는다.
        public static void SkipSidearm(Pawn_InventoryTracker inventory, ref ThingCount result)
        {
            Thing thing = result.Thing;
            if (unloadRetry || thing == null || !IsKeptSidearm(inventory.pawn, thing))
            {
                return;
            }

            unloadRetry = true;
            try
            {
                inventory.innerContainer.Remove(thing);
                result = inventory.FirstUnloadableThing;
                SkipSidearm(inventory, ref result);
            }
            finally
            {
                inventory.innerContainer.TryAdd(thing, false);
                unloadRetry = false;
            }
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
    internal static class Pawn_MeleeVerbs_TryMeleeAttack_WeaponPair_Patch
    {
        // 공격 수단을 지정해서 부른 경우(말다툼 주먹질 등)는 건드리지 않는다.
        private static void Prefix(Pawn ___pawn, Verb verbToUse)
        {
            if (verbToUse == null && !___pawn.stances.FullBodyBusy)
            {
                WeaponPairUtility.DrawMelee(___pawn);
            }
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.TryGetAttackVerb))]
    internal static class Pawn_TryGetAttackVerb_WeaponPair_Patch
    {
        private static void Prefix(Pawn __instance, Thing target)
        {
            WeaponPairUtility.DrawRanged(__instance, target);
        }
    }

    // 소집되어 서 있는 동안: 붙은 적이 없으면 다시 원거리 무기를 들어 자동 사격을 한다.
    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(JobDriver_Wait), "CheckForAutoAttack")]
    internal static class JobDriver_Wait_CheckForAutoAttack_WeaponPair_Patch
    {
        private static void Prefix(JobDriver_Wait __instance)
        {
            WeaponPairUtility.DrawRanged(__instance.pawn, null);
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(WorkGiver_HunterHunt), nameof(WorkGiver_HunterHunt.HasHuntingWeapon))]
    internal static class WorkGiver_HunterHunt_HasHuntingWeapon_WeaponPair_Patch
    {
        private static void Prefix(Pawn p)
        {
            WeaponPairUtility.DrawRanged(p, null);
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.MakeRoomFor), new[] { typeof(ThingWithComps), typeof(ThingWithComps) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class Pawn_EquipmentTracker_MakeRoomFor_WeaponPair_Patch
    {
        private static bool Prefix(Pawn_EquipmentTracker __instance, ThingWithComps eq, ref ThingWithComps dropped)
        {
            if (!WeaponPairUtility.StowInsteadOfDrop(__instance.pawn, eq))
            {
                return true;
            }

            dropped = null;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.FirstUnloadableThing), MethodType.Getter)]
    internal static class Pawn_InventoryTracker_FirstUnloadableThing_WeaponPair_Patch
    {
        private static void Postfix(Pawn_InventoryTracker __instance, ref ThingCount __result)
        {
            WeaponPairUtility.SkipSidearm(__instance, ref __result);
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Pawn_GetGizmos_WeaponPair_Patch
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
        {
            foreach (Gizmo gizmo in values)
            {
                yield return gizmo;
            }

            if (__instance.IsColonistPlayerControlled && __instance.inventory != null && __instance.inventory.innerContainer.Count > 0 && WeaponPairUtility.Eligible(__instance))
            {
                foreach (Gizmo gizmo in WeaponPairUtility.Gizmos(__instance))
                {
                    yield return gizmo;
                }
            }
        }
    }

    // NewRatkinPlus의 방패(잡는 방식 제한이 붙은 옷)는 정해진 무기 말고는 함께 들 수 없게 막는다. 어떤 무기든 허용한다.
    [HarmonyPatchCategory(Features.RatkinShieldAnyWeapon)]
    [HarmonyPatch]
    internal static class RatkinGripTypeFilter_TryIsWeaponAllowed_Patch
    {
        private static MethodBase target;

        private static bool Prepare()
        {
            Type type = AccessTools.TypeByName("NewRatkin.CompGripTypeFilter");
            target = type == null ? null : AccessTools.Method(type, "TryIsWeaponAllowed");
            return target != null;
        }

        private static MethodBase TargetMethod()
        {
            return target;
        }

        private static bool Prefix(ref bool __result, ref string reason)
        {
            __result = true;
            reason = null;
            return false;
        }
    }
}

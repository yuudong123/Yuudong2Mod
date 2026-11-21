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

    // 정착민별 무기 고정 설정(자동은 저장하지 않는다)과, 누가 어떤 무기를 보조 무기로 넣어 두었는지.
    // 보조 무기를 따로 적어 두어야 운반하려고 소지품에 담은 무기를 꺼내 들지 않는다.
    public class GameComponent_WeaponPair : GameComponent
    {
        public static GameComponent_WeaponPair Instance;

        private Dictionary<string, int> modes = new Dictionary<string, int>();

        private Dictionary<int, int> sidearmOwners = new Dictionary<int, int>();

        private bool sidearmsTracked;

        // 장비 탭에서 보조 무기를 직접 내려놓은 정착민. 다시 직접 무기를 장착할 때까지 알아서 보조 무기를 찾지 않는다.
        private HashSet<int> noAutoSidearm = new HashSet<int>();

        private readonly Dictionary<int, int> nextSidearmSearchTick = new Dictionary<int, int>();

        public GameComponent_WeaponPair(Game game)
        {
            Instance = this;
        }

        public bool IsSidearmOf(Pawn pawn, Thing thing)
        {
            return sidearmOwners.TryGetValue(thing.thingIDNumber, out int owner) && owner == pawn.thingIDNumber;
        }

        public void MarkSidearm(Pawn pawn, Thing thing)
        {
            sidearmOwners[thing.thingIDNumber] = pawn.thingIDNumber;
            noAutoSidearm.Remove(pawn.thingIDNumber);
        }

        public void StopAutoSidearm(Pawn pawn)
        {
            noAutoSidearm.Add(pawn.thingIDNumber);
        }

        public bool AutoSidearmAllowed(Pawn pawn)
        {
            return !noAutoSidearm.Contains(pawn.thingIDNumber);
        }

        public void SearchSidearmNow(Pawn pawn)
        {
            nextSidearmSearchTick.Remove(pawn.thingIDNumber);
        }

        public bool SidearmSearchDue(Pawn pawn, int tick)
        {
            if (nextSidearmSearchTick.TryGetValue(pawn.thingIDNumber, out int next) && tick < next)
            {
                return false;
            }

            nextSidearmSearchTick[pawn.thingIDNumber] = tick + WeaponPairUtility.SidearmSearchInterval + pawn.thingIDNumber % 600;
            return true;
        }

        public override void StartedNewGame()
        {
            sidearmsTracked = true;
        }

        // 이 기록이 없던 때의 세이브에서는 소지품 속 무기를 보조 무기로 본다.
        public override void FinalizeInit()
        {
            if (sidearmsTracked)
            {
                return;
            }

            sidearmsTracked = true;
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
            {
                if (pawn.inventory == null || !WeaponPairUtility.Eligible(pawn))
                {
                    continue;
                }

                foreach (Thing thing in pawn.inventory.innerContainer)
                {
                    if (WeaponPairUtility.IsWeapon(thing))
                    {
                        MarkSidearm(pawn, thing);
                    }
                }
            }
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
            Scribe_Collections.Look(ref sidearmOwners, "sidearmOwners", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref sidearmsTracked, "sidearmsTracked");
            Scribe_Collections.Look(ref noAutoSidearm, "noAutoSidearm", LookMode.Value);
            noAutoSidearm ??= new HashSet<int>();
            modes ??= new Dictionary<string, int>();
            sidearmOwners ??= new Dictionary<int, int>();
        }
    }

    // 정착민이 근접 무기와 원거리 무기를 하나씩 함께 지닌다. 손에 들지 않은 쪽은 소지품에 있다.
    // 주기적으로 검사하지 않고, 근접 공격을 시작할 때와 사격할 무기를 고를 때만 바꿔 든다.
    public static class WeaponPairUtility
    {
        private const int SwapCooldownTicks = 30;

        public const int SidearmSearchInterval = 2500;

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
            GameComponent_WeaponPair tracker = GameComponent_WeaponPair.Instance;
            ThingOwner<Thing> held = pawn.inventory.innerContainer;
            for (int i = 0; tracker != null && i < held.Count; i++)
            {
                Thing thing = held[i];
                if (thing.def.IsRangedWeapon == ranged && IsWeapon(thing) && tracker.IsSidearmOf(pawn, thing))
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

            if (current != null)
            {
                GameComponent_WeaponPair.Instance?.MarkSidearm(pawn, current);
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

        // 근접·원거리로 나눠 적고, 지금 손에 든 쪽을 표시한다.
        private static string CommandDesc(Thing primary, Thing other)
        {
            Thing melee = primary != null && primary.def.IsMeleeWeapon ? primary : (other.def.IsMeleeWeapon ? other : null);
            Thing ranged = primary != null && primary.def.IsRangedWeapon ? primary : (other.def.IsRangedWeapon ? other : null);
            return "Yuudong2.WeaponPair.CommandDesc".Translate(WeaponLine(melee, primary), WeaponLine(ranged, primary));
        }

        private static string WeaponLine(Thing weapon, Thing primary)
        {
            if (weapon == null)
            {
                return "-";
            }

            return weapon == primary ? "Yuudong2.WeaponPair.InHand".Translate(weapon.LabelCap).ToString() : weapon.LabelCap;
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
                defaultDesc = CommandDesc(primary, other),
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

            if (!pawn.equipment.TryTransferEquipmentToContainer(primary, pawn.inventory.innerContainer))
            {
                return false;
            }

            GameComponent_WeaponPair.Instance?.MarkSidearm(pawn, primary);
            return true;
        }

        // 무기를 하나만 든 정착민이 한가할 때: 다른 종류의 무기 중 가장 좋은 것을 찾아 장착하러 간다. 들고 있던 무기는 소지품으로 들어간다.
        public static Job AutoSidearmJob(Pawn pawn)
        {
            GameComponent_WeaponPair tracker = GameComponent_WeaponPair.Instance;
            if (tracker == null || !pawn.Spawned || !pawn.IsColonist || !Eligible(pawn) || !pawn.Map.IsPlayerHome)
            {
                return null;
            }

            ThingWithComps primary = pawn.equipment.Primary;
            if (primary == null || !IsWeapon(primary) || !tracker.SidearmSearchDue(pawn, Find.TickManager.TicksGame) || !tracker.AutoSidearmAllowed(pawn))
            {
                return null;
            }

            bool wantRanged = !primary.def.IsRangedWeapon;
            if (Stowed(pawn, wantRanged) != null || pawn.IsQuestLodger() || pawn.WorkTagIsDisabled(WorkTags.Violent)
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return null;
            }

            if (wantRanged && (pawn.WorkTagIsDisabled(WorkTags.Shooting) || (pawn.story != null && pawn.story.traits.HasTrait(TraitDefOf.Brawler))))
            {
                return null;
            }

            Thing best = null;
            float bestScore = 0f;
            List<Thing> weapons = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
            for (int i = 0; i < weapons.Count; i++)
            {
                Thing thing = weapons[i];
                if (thing.def.IsRangedWeapon != wantRanged || !IsWeapon(thing) || thing.IsForbidden(pawn) || thing.IsBurning())
                {
                    continue;
                }

                float score = SidearmScore(thing, wantRanged);
                if (score <= bestScore || !AutoEquippable(pawn, thing))
                {
                    continue;
                }

                best = thing;
                bestScore = score;
            }

            return best == null ? null : JobMaker.MakeJob(JobDefOf.Equip, best);
        }

        private static float SidearmScore(Thing thing, bool ranged)
        {
            return ranged ? thing.MarketValue : thing.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
        }

        // 유대 무기는 고르지 않는다. 장착하면 그 정착민에게 묶여 되돌릴 수 없다.
        private static bool AutoEquippable(Pawn pawn, Thing thing)
        {
            if (thing.TryGetComp<CompBladelinkWeapon>() != null || MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1))
            {
                return false;
            }

            if (pawn.Ideo != null && pawn.Ideo.GetDispositionForWeapon(thing.def) == IdeoWeaponDisposition.Despised)
            {
                return false;
            }

            return EquipmentUtility.CanEquip(thing, pawn, out string _, false) && pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, pawn.NormalMaxDanger());
        }

        private static readonly List<Thing> gearTabEquipment = new List<Thing>();

        private static readonly List<Thing> gearTabInventory = new List<Thing>();

        private static bool ShownAsEquipment(Pawn pawn, Thing thing)
        {
            GameComponent_WeaponPair tracker = GameComponent_WeaponPair.Instance;
            return tracker != null && IsWeapon(thing) && tracker.IsSidearmOf(pawn, thing);
        }

        // 장비 탭: 보조 무기는 소지품이 아니라 장비 칸에 보여 준다.
        public static List<Thing> EquipmentForGearTab(Pawn_EquipmentTracker equipment)
        {
            gearTabEquipment.Clear();
            gearTabEquipment.AddRange(equipment.AllEquipmentListForReading);
            Pawn pawn = equipment.pawn;
            if (Eligible(pawn))
            {
                foreach (Thing thing in pawn.inventory.innerContainer)
                {
                    if (ShownAsEquipment(pawn, thing))
                    {
                        gearTabEquipment.Add(thing);
                    }
                }
            }

            return gearTabEquipment;
        }

        public static List<Thing> InventoryForGearTab(Pawn_InventoryTracker inventory)
        {
            gearTabInventory.Clear();
            Pawn pawn = inventory.pawn;
            bool eligible = Eligible(pawn);
            foreach (Thing thing in inventory.innerContainer)
            {
                if (!eligible || !ShownAsEquipment(pawn, thing))
                {
                    gearTabInventory.Add(thing);
                }
            }

            return gearTabInventory;
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

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(ITab_Pawn_Gear), "FillTab")]
    internal static class ITab_Pawn_Gear_FillTab_WeaponPair_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo equipmentList = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AllEquipmentListForReading));
            FieldInfo inventoryList = AccessTools.Field(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.innerContainer));
            bool equipmentDone = false;
            bool inventoryDone = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(equipmentList))
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(WeaponPairUtility), nameof(WeaponPairUtility.EquipmentForGearTab));
                    equipmentDone = true;
                }
                else if (instruction.LoadsField(inventoryList))
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(WeaponPairUtility), nameof(WeaponPairUtility.InventoryForGearTab));
                    inventoryDone = true;
                }

                yield return instruction;
            }

            if (!equipmentDone || !inventoryDone)
            {
                Log.Error("[Yuudong2Mod] 무기 2개 착용: 장비 탭(ITab_Pawn_Gear.FillTab)에서 장비 목록이나 소지품 목록을 읽는 곳을 찾지 못해 보조 무기를 장비 칸에 보여 주지 못합니다. FillTab이 바뀌었는지 확인하세요.");
            }
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    internal static class JobGiver_OptimizeApparel_TryGiveJob_WeaponPair_Patch
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
            {
                __result = WeaponPairUtility.AutoSidearmJob(pawn);
            }
        }
    }

    [HarmonyPatchCategory(Features.WeaponPair)]
    [HarmonyPatch(typeof(ITab_Pawn_Gear), "InterfaceDrop")]
    internal static class ITab_Pawn_Gear_InterfaceDrop_WeaponPair_Patch
    {
        private static void Prefix(Thing t)
        {
            Thing holder = t?.ParentHolder is Pawn_InventoryTracker inventory ? inventory.pawn : null;
            GameComponent_WeaponPair tracker = GameComponent_WeaponPair.Instance;
            if (holder is Pawn pawn && tracker != null && tracker.IsSidearmOf(pawn, t))
            {
                tracker.StopAutoSidearm(pawn);
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

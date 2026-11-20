using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace Yuudong2Mod
{
    [DefOf]
    public static class ArmyAttackDefOf
    {
        public static WorldObjectDef Yuudong2_ArmyBattle;

        static ArmyAttackDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ArmyAttackDefOf));
        }
    }

    // 이동 중인 군대를 내가 기습해서 열린 임시 전투 맵. 군대는 세계 지도에 남아 멈춰 있고, 전투 결과에 따라 사라지거나 줄어든 채로 다시 움직인다.
    public class WorldObject_ArmyBattle : MapParent
    {
        public WorldObject_WarArmy army;
        // 전투에 나오는 병사 수. 세계 지도에서 군대에 적힌 병력과 같다.
        public int soldiers;
        public int initialEnemies;
        public bool won;

        public override string Label => Faction != null ? "Yuudong2.ArmyAttack.BattleLabel".Translate(Faction.Name) : base.Label;

        public override bool CanReformFoggedEnemies => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref army, "army");
            Scribe_Values.Look(ref soldiers, "soldiers", 0);
            Scribe_Values.Look(ref initialEnemies, "initialEnemies", 0);
            Scribe_Values.Look(ref won, "won", false);
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            SpawnEnemies();
        }

        private void SpawnEnemies()
        {
            Map map = Map;
            Faction faction = Faction;
            if (faction == null)
            {
                return;
            }

            List<Pawn> pawns = ArmyAttackUtility.GenerateSoldiers(faction, Tile, soldiers);
            if (!RCellFinder.TryFindRandomCellNearTheCenterOfTheMapWith(c => c.Standable(map) && !c.Fogged(map), map, out IntVec3 camp))
            {
                camp = map.Center;
            }

            int campRadius = Mathf.CeilToInt(Mathf.Sqrt(pawns.Count)) + 6;
            foreach (Pawn pawn in pawns)
            {
                GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(camp, map, campRadius), map, Rot4.Random);
            }

            initialEnemies = pawns.Count;
            if (pawns.Count > 0)
            {
                // 야영 중이던 군대라 잠시 모여 있다가 반격한다.
                LordMaker.MakeNewLord(faction, new LordJob_StageThenAttack(faction, camp, Rand.Int, true, false, false, ArmyAttackUtility.ReactionDelay), map, pawns);
            }

            Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
            Find.LetterStack.ReceiveLetter("Yuudong2.ArmyAttack.LetterLabel".Translate(), "Yuudong2.ArmyAttack.LetterText".Translate(faction.Name, pawns.Count), LetterDefOf.NeutralEvent, new GlobalTargetInfo(camp, map), faction);
        }

        private int StandingEnemies()
        {
            int count = 0;
            List<Pawn> pawns = Map.mapPawns.SpawnedPawnsInFaction(Faction);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (!pawns[i].Downed && !pawns[i].IsPrisoner)
                {
                    count++;
                }
            }
            return count;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (HasMap && !won && this.IsHashIntervalTick(60, delta) && !GenHostility.AnyHostileActiveThreatToPlayer(Map))
            {
                won = true;
                ArmyAttackUtility.ArmyDefeated(this);
            }
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (Map.mapPawns.AnyPawnBlockingMapRemoval || TransporterUtility.IncomingTransporterPreventingMapRemoval(Map))
            {
                return false;
            }

            // 중력선이 착륙 위치를 고르는 동안에는 맵에 내 사람이 아직 없다.
            if (Current.Game.Gravship != null)
            {
                return false;
            }

            alsoRemoveWorldObject = true;
            return true;
        }

        // 이기지 못하고 떠나면 남은 병력만큼 줄어든 군대가 다시 행군한다.
        public override void Notify_MyMapAboutToBeRemoved()
        {
            base.Notify_MyMapAboutToBeRemoved();
            if (won || army == null || army.Destroyed)
            {
                return;
            }

            float left = initialEnemies > 0 ? Mathf.Clamp01((float)StandingEnemies() / initialEnemies) : 1f;
            army.strength *= left;
            army.sizeFactor *= left;
            army.battle = null;
            if (left < 1f)
            {
                Messages.Message("Yuudong2.ArmyAttack.Withdrew".Translate(Faction.Name, left.ToStringPercent()), army, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        public override void PostRemove()
        {
            base.PostRemove();
            if (army != null && !army.Destroyed && army.battle == this)
            {
                army.battle = null;
            }
        }
    }

    public static class ArmyAttackUtility
    {
        private const int MaxGroupRounds = 40;
        public const int DefeatGoodwill = 10;
        public const int HoldTicks = GenDate.TicksPerDay;

        public static readonly IntRange ReactionDelay = new IntRange(600, 1200);

        private static Texture2D attackTex;

        public static Texture2D AttackTex => attackTex ?? (attackTex = ContentFinder<Texture2D>.Get("UI/Commands/AttackSettlement"));

        public static IntVec3 MapSize => Find.World.info.initialMapSize;

        public static int SoldiersOf(WorldObject_WarArmy army) => Mathf.Max(1, Mathf.RoundToInt(army.strength));

        // 그 세력의 습격 편성으로 정확히 count명을 만든다. 편성은 포인트 단위로만 뽑히므로 모자라면 더 뽑고 남으면 버린다.
        public static List<Pawn> GenerateSoldiers(Faction faction, PlanetTile tile, int count)
        {
            List<Pawn> pawns = new List<Pawn>();
            float cost = WorldComponent_FactionWar.AverageRaidPawnCost(faction);
            float minPoints = faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat);
            for (int round = 0; round < MaxGroupRounds && pawns.Count < count; round++)
            {
                PawnGroupMakerParms parms = new PawnGroupMakerParms
                {
                    groupKind = PawnGroupKindDefOf.Combat,
                    tile = tile,
                    faction = faction,
                    points = Mathf.Max(minPoints, (count - pawns.Count) * cost),
                };
                foreach (Pawn pawn in PawnGroupMakerUtility.GeneratePawns(parms))
                {
                    if (pawns.Count < count)
                    {
                        pawns.Add(pawn);
                    }
                    else
                    {
                        Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
                    }
                }
            }

            return pawns;
        }

        // 상단 둘을 합칠 수 있는 거리와 같은 기준: 같은 타일이거나 타일 반 칸 안.
        public static bool CloseEnough(Caravan caravan, WorldObject_WarArmy army)
        {
            if (caravan.Tile == army.Tile)
            {
                return true;
            }

            if (caravan.Tile.Layer != army.Tile.Layer)
            {
                return false;
            }

            float reach = Find.WorldGrid.AverageTileSize * 0.5f;
            return (caravan.DrawPos - army.DrawPos).sqrMagnitude < reach * reach;
        }

        public static List<Caravan> CaravansInReach(WorldObject_WarArmy army)
        {
            return Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled && CloseEnough(c, army)).ToList();
        }

        public static AcceptanceReport CanAttack(WorldObject_WarArmy army, PlanetTile battleTile)
        {
            if (army == null || army.Destroyed || army.Faction == null)
            {
                return false;
            }

            if (!army.Faction.HostileTo(Faction.OfPlayer))
            {
                return "Yuudong2.ArmyAttack.NotHostile".Translate();
            }

            if (army.battle != null && !army.battle.Destroyed)
            {
                return true;
            }

            if (Find.WorldObjects.MapParentAt(battleTile) != null || !Find.WorldGrid[battleTile].PrimaryBiome.implemented)
            {
                return "Yuudong2.ArmyAttack.NoRoom".Translate();
            }

            return true;
        }

        // 군대가 정착지 타일 위에 있으면 그 타일에는 전투를 열 수 없다. 그때는 바로 옆에 와 있는 상단의 타일에서 연다.
        public static PlanetTile BattleTile(WorldObject_WarArmy army, List<Caravan> caravans)
        {
            if (CanAttack(army, army.Tile).Accepted)
            {
                return army.Tile;
            }

            foreach (Caravan caravan in caravans)
            {
                if (CanAttack(army, caravan.Tile).Accepted)
                {
                    return caravan.Tile;
                }
            }

            return army.Tile;
        }

        public static WorldObject_ArmyBattle StartBattle(WorldObject_WarArmy army, PlanetTile tile)
        {
            if (army.battle != null && !army.battle.Destroyed)
            {
                return army.battle;
            }

            if (!CanAttack(army, tile).Accepted)
            {
                return null;
            }

            WorldObject_ArmyBattle battle = (WorldObject_ArmyBattle)WorldObjectMaker.MakeWorldObject(ArmyAttackDefOf.Yuudong2_ArmyBattle);
            battle.Tile = tile;
            battle.SetFaction(army.Faction);
            battle.army = army;
            battle.soldiers = SoldiersOf(army);
            Find.WorldObjects.Add(battle);
            army.battle = battle;
            return battle;
        }

        public static void AttackWithCaravans(WorldObject_WarArmy army, List<Caravan> caravans)
        {
            PlanetTile tile = BattleTile(army, caravans);
            if (caravans.Count == 0 || !CanAttack(army, tile).Accepted)
            {
                return;
            }

            LongEventHandler.QueueLongEvent(() =>
            {
                WorldObject_ArmyBattle battle = StartBattle(army, tile);
                if (battle == null)
                {
                    return;
                }

                Map map = GetOrGenerateMapUtility.GetOrGenerateMap(battle.Tile, MapSize, null);
                Pawn first = caravans[0].PawnsListForReading.FirstOrDefault();
                foreach (Caravan caravan in caravans)
                {
                    CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop, true);
                }

                if (first != null)
                {
                    CameraJumper.TryJump(first);
                }
            }, "GeneratingMapForNewEncounter", false, null);
        }

        public static void ArmyDefeated(WorldObject_ArmyBattle battle)
        {
            WorldObject_WarArmy army = battle.army;
            Faction loser = battle.Faction;
            string gains = "";
            if (loser != null)
            {
                foreach (Faction other in Find.FactionManager.AllFactionsVisible)
                {
                    if (other.IsPlayer || other == loser || other.defeated || !other.HostileTo(loser) || other.HostileTo(Faction.OfPlayer))
                    {
                        continue;
                    }

                    if (Faction.OfPlayer.TryAffectGoodwillWith(other, DefeatGoodwill, false, false))
                    {
                        gains += "\n" + "RelationsWith".Translate(other.Name) + ": " + DefeatGoodwill.ToStringWithSign();
                    }
                }
            }

            Find.LetterStack.ReceiveLetter("Yuudong2.ArmyAttack.WonLabel".Translate(), "Yuudong2.ArmyAttack.WonText".Translate(loser?.Name ?? "?") + (gains.Length > 0 ? "\n" + gains : ""), LetterDefOf.PositiveEvent, battle, loser);
            if (army != null && !army.Destroyed)
            {
                FactionWarUtility.Comp?.ArmyDefeatedByPlayer(army);
            }
        }

        public static IEnumerable<FloatMenuOption> TransporterOptions(WorldObject_WarArmy army, IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
        {
            if (army.Faction == null || !army.Faction.HostileTo(Faction.OfPlayer))
            {
                yield break;
            }

            foreach (PawnsArrivalModeDef mode in new[] { PawnsArrivalModeDefOf.EdgeDrop, PawnsArrivalModeDefOf.CenterDrop })
            {
                PawnsArrivalModeDef chosen = mode;
                string label = (mode == PawnsArrivalModeDefOf.CenterDrop ? "Yuudong2.ArmyAttack.DropCenter" : "Yuudong2.ArmyAttack.DropEdge").Translate(army.Label);
                foreach (FloatMenuOption option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                    () => TransportersArrivalAction_AttackArmy.CanAttack(army, army.Tile),
                    () => new TransportersArrivalAction_AttackArmy(army, chosen),
                    label, launchAction, army.Tile))
                {
                    yield return option;
                }
            }
        }
    }

    public class TransportersArrivalAction_AttackArmy : TransportersArrivalAction
    {
        private WorldObject_WarArmy army;
        private PawnsArrivalModeDef arrivalMode;

        public TransportersArrivalAction_AttackArmy()
        {
        }

        public TransportersArrivalAction_AttackArmy(WorldObject_WarArmy army, PawnsArrivalModeDef arrivalMode)
        {
            this.army = army;
            this.arrivalMode = arrivalMode;
        }

        public override bool GeneratesMap => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref army, "army");
            Scribe_Defs.Look(ref arrivalMode, "arrivalMode");
        }

        public static FloatMenuAcceptanceReport CanAttack(WorldObject_WarArmy army, PlanetTile tile)
        {
            AcceptanceReport report = ArmyAttackUtility.CanAttack(army, tile);
            return report.Accepted ? true : FloatMenuAcceptanceReport.WithFailReason(report.Reason);
        }

        // 군대가 그사이 움직였어도 도착한 타일에서 따라잡은 것으로 본다.
        public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(pods, destinationTile);
            return report ? CanAttack(army, destinationTile) : report;
        }

        public override bool ShouldUseLongEvent(List<ActiveTransporterInfo> pods, PlanetTile tile)
        {
            return army?.battle == null || !army.battle.HasMap;
        }

        public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
        {
            Thing lookTarget = TransportersArrivalActionUtility.GetLookTarget(transporters);
            WorldObject_ArmyBattle battle = ArmyAttackUtility.StartBattle(army, tile);
            if (battle == null)
            {
                // 날아오는 사이에 그 타일에 다른 장소가 생겼다. 싣고 온 것을 잃지 않게 그 타일에 상단으로 내린다.
                new TransportersArrivalAction_FormCaravan().Arrived(transporters, tile);
                return;
            }

            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(battle.Tile, ArmyAttackUtility.MapSize, null);
            Messages.Message((transporters.IsShuttle() ? "MessageShuttleArrived" : "MessageTransportPodsArrived").Translate(), lookTarget, MessageTypeDefOf.TaskCompletion);
            arrivalMode.Worker.TravellingTransportersArrived(transporters, map);
        }
    }

    // 중력선의 목적지로 적 군대가 있는 타일을 고르면 그 군대를 붙잡아 두고, 도착할 때 그 타일에 전투 맵을 연다.
    // 맵이 만들어진 뒤의 착륙 위치 선택과 착륙 장면은 바닐라 그대로다.
    [HarmonyPatchCategory(Features.FactionWar)]
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    internal static class WorldComponent_GravshipController_InitiateTakeoff_ArmyAttack_Patch
    {
        private static void Prefix(PlanetTile targetTile)
        {
            WorldComponent_FactionWar comp = FactionWarUtility.Active ? FactionWarUtility.Comp : null;
            if (comp == null || !targetTile.Valid)
            {
                return;
            }

            WorldObject_WarArmy army = comp.Armies.FirstOrDefault(a => a.Tile == targetTile && ArmyAttackUtility.CanAttack(a, targetTile).Accepted);
            comp.gravshipTarget = army;
            if (army != null)
            {
                army.heldUntilTick = Find.TickManager.TicksGame + ArmyAttackUtility.HoldTicks;
            }
        }
    }

    [HarmonyPatchCategory(Features.FactionWar)]
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.ArriveNewMap))]
    internal static class GravshipUtility_ArriveNewMap_ArmyAttack_Patch
    {
        private static void Prefix(Gravship gravship)
        {
            WorldComponent_FactionWar comp = FactionWarUtility.Active ? FactionWarUtility.Comp : null;
            WorldObject_WarArmy army = comp?.gravshipTarget;
            if (army == null)
            {
                return;
            }

            comp.gravshipTarget = null;
            army.heldUntilTick = 0;
            if (ArmyAttackUtility.CanAttack(army, gravship.destinationTile).Accepted)
            {
                ArmyAttackUtility.StartBattle(army, gravship.destinationTile);
            }
        }
    }
}

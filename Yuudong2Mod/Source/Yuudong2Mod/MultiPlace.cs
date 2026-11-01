using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Yuudong2Mod
{
    // 고른 건물(복제는 청사진·건설 중인 틀도) 여러 개를 지금 배치 모양 그대로 한 번에 놓는 지정 도구. 여러 건물 옮기기와 복제가 같이 쓴다.
    public abstract class Designator_MultiPlace : Designator
    {
        protected struct Placement
        {
            public Thing thing;
            public ThingDef def;
            public IntVec3 center;
            public Rot4 rot;
            public CellRect rect;

            public bool Moves => center != thing.Position || rot != thing.Rotation;
        }

        // 고를 때의 자리와 방향. 계속 쓰는 도구는 원본이 지어지거나 사라져도 이 배치를 쓴다.
        private struct Member
        {
            public Thing thing;
            public CellRect rect;
            public Rot4 rot;
        }

        private List<Member> members = new List<Member>();
        private Map groupMap;
        private IntVec3 anchorCell;
        private Rot4 groupRot = Rot4.North;

        protected abstract List<Thing> SelectedThings();

        // 놓은 뒤에도 도구를 그대로 들고 있을지. 바닐라 건설처럼 여러 번 놓을 수 있다.
        protected virtual bool Repeatable => false;

        // 그 자리에 놓을 건물. 청사진이나 틀은 다 지었을 때의 건물이다.
        protected virtual ThingDef PlacedDef(Thing thing)
        {
            return thing.def;
        }

        protected virtual void Started(List<Thing> group)
        {
        }

        protected abstract AcceptanceReport CanPlace(Placement placement, List<Placement> all);

        protected virtual AcceptanceReport CanPlaceGroup(List<Placement> placements)
        {
            return true;
        }

        protected abstract void Place(List<Placement> placements);

        public override void ProcessInput(Event ev)
        {
            List<Thing> things = SelectedThings();
            if (things.Count < 2)
            {
                return;
            }

            Started(things);
            members = things.Select(t => new Member { thing = t, rect = t.OccupiedRect(), rot = t.Rotation }).ToList();
            groupMap = things[0].Map;

            // 묶음 한가운데에 가장 가까운 건물을 마우스가 가리키는 기준으로 삼는다.
            CellRect bounds = CellRect.FromLimits(
                members.Min(m => m.rect.minX), members.Min(m => m.rect.minZ),
                members.Max(m => m.rect.maxX), members.Max(m => m.rect.maxZ));
            IntVec3 middle = bounds.CenterCell;
            anchorCell = GenCollection.MinBy(things, t => (t.Position - middle).LengthHorizontalSquared).Position;
            groupRot = Rot4.North;
            base.ProcessInput(ev);
        }

        public override bool CanRemainSelected()
        {
            return members.Count >= 2 && groupMap == Find.CurrentMap && (Repeatable || members.All(m => m.thing.Spawned && m.thing.Map == groupMap));
        }

        public override void Deselected()
        {
            members = new List<Member>();
            groupMap = null;
        }

        // 기준 건물을 cell에 놓았을 때 각 건물의 새 자리. 칸 단위로 기준 건물 칸을 중심으로 돌린다.
        private List<Placement> PlacementsAt(IntVec3 cell)
        {
            var placements = new List<Placement>(members.Count);
            foreach (Member member in members)
            {
                Thing thing = member.thing;
                ThingDef def = PlacedDef(thing);
                CellRect old = member.rect;
                IntVec3 min = RotateOffset(new IntVec3(old.minX, 0, old.minZ) - anchorCell);
                IntVec3 max = RotateOffset(new IntVec3(old.maxX, 0, old.maxZ) - anchorCell);
                CellRect rect = CellRect.FromLimits(min.x + cell.x, min.z + cell.z, max.x + cell.x, max.z + cell.z);

                Rot4 rot = def.rotatable ? new Rot4(member.rot.AsInt + groupRot.AsInt) : member.rot;
                CellRect atOrigin = GenAdj.OccupiedRect(IntVec3.Zero, rot, def.Size);
                IntVec3 center;
                if (atOrigin.Width == rect.Width && atOrigin.Height == rect.Height)
                {
                    center = new IntVec3(rect.minX - atOrigin.minX, 0, rect.minZ - atOrigin.minZ);
                }
                else
                {
                    // 돌릴 수 없는 직사각형 건물은 방향을 그대로 두고 가운데만 맞춘다.
                    center = rect.CenterCell;
                    rect = GenAdj.OccupiedRect(center, rot, def.Size);
                }

                placements.Add(new Placement { thing = thing, def = def, center = center, rot = rot, rect = rect });
            }

            return placements;
        }

        private IntVec3 RotateOffset(IntVec3 offset)
        {
            for (int i = 0; i < groupRot.AsInt; i++)
            {
                offset = new IntVec3(offset.z, 0, -offset.x);
            }

            return offset;
        }

        // 같이 놓는 건물끼리 새 자리에서 작업 칸을 막는지
        protected AcceptanceReport InteractionCellsFreeInGroup(Placement placement, List<Placement> all)
        {
            ThingDef def = placement.def;
            if (!def.HasSingleOrMultipleInteractionCells)
            {
                return true;
            }

            foreach (Placement other in all)
            {
                if (other.thing == placement.thing)
                {
                    continue;
                }

                if ((other.def.passability != Traversability.Standable || other.def == def)
                    && ThingUtility.InteractionCellsWhenAt(def, placement.center, placement.rot, Map).Any(other.rect.Contains))
                {
                    return new AcceptanceReport("InteractionSpotWillBeBlocked".Translate(other.thing.LabelNoCount, other.thing).CapitalizeFirst());
                }
            }

            return true;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            if (!loc.InBounds(Map) || members.Count == 0)
            {
                return false;
            }

            List<Placement> placements = PlacementsAt(loc);
            AcceptanceReport groupReport = CanPlaceGroup(placements);
            if (!groupReport.Accepted)
            {
                return groupReport;
            }

            foreach (Placement placement in placements)
            {
                AcceptanceReport report = CanPlace(placement, placements);
                if (!report.Accepted)
                {
                    return new AcceptanceReport(placement.thing.LabelCap + ": " + report.Reason);
                }
            }

            return true;
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            Place(PlacementsAt(c));
            if (!Repeatable)
            {
                Find.DesignatorManager.Deselect();
            }
        }

        public override void SelectedUpdate()
        {
            GenDraw.DrawNoBuildEdgeLines();
            IntVec3 cell = UI.MouseCell();
            if (members.Count == 0 || !cell.InBounds(Map))
            {
                return;
            }

            List<Placement> placements = PlacementsAt(cell);
            foreach (Placement placement in placements)
            {
                Color color = CanPlace(placement, placements).Accepted ? Designator_Place.CanPlaceColor : Designator_Place.CannotPlaceColor;
                DrawGhost(placement, color);
                GenDraw.DrawInteractionCells(placement.def, placement.center, placement.rot);
            }
        }

        protected virtual void DrawGhost(Placement placement, Color color)
        {
            Thing thing = placement.thing;
            GhostDrawer.DrawGhostThing(placement.center, placement.rot, placement.def, thing.Graphic.ExtractInnerGraphicFor(thing), color, AltitudeLayer.Blueprint, thing);
        }

        public override void SelectedProcessInput(Event ev)
        {
            RotationDirection direction = RotationDirection.None;
            if (KeyBindingDefOf.Designator_RotateRight.KeyDownEvent)
            {
                direction = RotationDirection.Clockwise;
            }
            else if (KeyBindingDefOf.Designator_RotateLeft.KeyDownEvent)
            {
                direction = RotationDirection.Counterclockwise;
            }

            if (direction != RotationDirection.None)
            {
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                groupRot = groupRot.Rotated(direction);
            }
        }

        public override void DoExtraGuiControls(float leftX, float bottomY)
        {
            DesignatorUtility.GUIDoRotationControls(leftX, bottomY, groupRot, rot => groupRot = rot);
        }
    }
}

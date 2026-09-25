using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidRobot
{
    internal sealed class CollisionDemoModel
    {
        internal sealed record Hull(double[] Coordinates, uint[] Indices);
        internal sealed class Obstacle
        {
            internal ulong Id;
            internal string Name = string.Empty;
            internal double X, Y, Z, Width, Depth, Height;
            public override string ToString() => Name;
        }
        internal readonly record struct Pair(ulong First, ulong Second)
        {
            internal static Pair Of(ulong a, ulong b) => a < b ? new Pair(a, b) : new Pair(b, a);
        }

        private readonly RobotBody body;
        private readonly bool showPreview;
        private readonly Dictionary<uint, List<Hull>> linkHulls = new();
        private readonly Dictionary<uint, GroupSceneNode> linkPreviews = new();
        private GroupSceneNode? candidatePreview;
        private readonly List<Obstacle> obstacles = new();
        private readonly HashSet<ulong> ownedObstacleIds = new();
        private readonly HashSet<ulong> ownedLinkIds = new();
        private readonly HashSet<Pair> adjacent = new();
        private readonly HashSet<Pair> ignored = new();
        private (uint Index, List<Hull> Hulls)? candidate;
        private ulong nextObstacleId = 1000000;
        internal GroupSceneNode PreviewRoot { get; } = new GroupSceneNode();
        internal IReadOnlyList<Obstacle> Obstacles => obstacles;
        internal IReadOnlyCollection<Pair> Ignored => ignored;
        internal IReadOnlyCollection<Pair> Adjacent => adjacent;
        internal IReadOnlyDictionary<uint, List<Hull>> LinkHulls => linkHulls;

        internal CollisionDemoModel(RobotBody body, bool showPreview = true)
        {
            this.body = body;
            this.showPreview = showPreview;
            PreviewRoot.SetName("Collision preview");
            using var model = body.GetModel();
            for (uint i = 0; i < body.GetJointCount(); ++i)
            {
                using var jointIndex = new RobotJointIndex(i);
                using var joint = model.GetJoint(jointIndex);
                using var parent = joint.GetParentLinkIndex();
                using var child = joint.GetChildLinkIndex();
                var pair = Pair.Of((ulong)parent.GetValue() + 1, (ulong)child.GetValue() + 1);
                adjacent.Add(pair);
                ignored.Add(pair);
            }
        }

        internal string LinkName(uint index)
        {
            using var key = new RobotLinkIndex(index);
            using var link = body.GetLink(key);
            return link?.GetName() ?? ("Link " + index);
        }

        internal string OwnerName(ulong id) => id <= body.GetLinkCount()
            ? LinkName((uint)(id - 1))
            : obstacles.FirstOrDefault(item => item.Id == id)?.Name ?? ("Entity " + id);

        internal void SetLinkHulls(uint index, List<Hull> hulls)
        {
            if (hulls == null || hulls.Count == 0) throw new ArgumentException("No convex hulls were generated.");
            linkHulls[index] = hulls;
            ownedLinkIds.Add((ulong)index + 1);
            candidate = null;
            if (showPreview) RebuildPreview();
        }

        internal void SetCandidate(uint index, List<Hull> hulls)
        {
            candidate = (index, hulls);
            if (showPreview) RebuildPreview();
        }

        internal void ClearCandidate()
        {
            candidate = null;
            if (showPreview) RebuildPreview();
        }

        internal void RemoveLinkHulls(uint index)
        {
            linkHulls.Remove(index);
            if (showPreview) RebuildPreview();
        }

        internal Obstacle AddObstacle()
        {
            var item = new Obstacle {
                Id = nextObstacleId++, Name = "Obstacle " + (obstacles.Count + 1),
                X = 2750, Y = 0, Z = 1470, Width = 300, Depth = 300, Height = 400
            };
            obstacles.Add(item);
            ownedObstacleIds.Add(item.Id);
            if (showPreview) RebuildPreview();
            return item;
        }

        internal void RemoveObstacle(Obstacle item)
        {
            if (!obstacles.Remove(item)) return;
            ignored.RemoveWhere(pair => pair.First == item.Id || pair.Second == item.Id);
            if (showPreview) RebuildPreview();
        }

        internal bool SetIgnored(ulong a, ulong b, bool value)
        {
            if (a == b) return false;
            var pair = Pair.Of(a, b);
            return value ? ignored.Add(pair) : ignored.Remove(pair);
        }

        internal void ApplyTo(RobotPlanRequest request)
        {
            using var scene = request.CollisionScene;
            using var bodies = scene.Bodies;
            using var bindings = request.CollisionBindings;
            for (int i = bodies.Count - 1; i >= 0; --i)
            {
                using var old = bodies[i];
                using var id = old.OwnerId;
                if (ownedObstacleIds.Contains(id.Value) || ownedLinkIds.Contains(id.Value))
                    bodies.RemoveAt(i);
            }
            for (int i = bindings.Count - 1; i >= 0; --i)
            {
                using var old = bindings[i];
                using var id = old.OwnerId;
                if (ownedLinkIds.Contains(id.Value)) bindings.RemoveAt(i);
            }
            foreach (var entry in linkHulls)
            {
                ulong id = (ulong)entry.Key + 1;
                using var ownerId = new ObjectId(id);
                using var linkIndex = new RobotLinkIndex(entry.Key);
                using var transform = body.GetLinkTransform(linkIndex, true);
                using var world = GTrsf.From(transform);
                using var collisionBody = new PlanningCollisionBody {
                    OwnerId = ownerId, OwnerName = LinkName(entry.Key),
                    OwnerType = EnumPlanningCollisionOwnerType.Link, WorldTransform = world
                };
                using (var shapes = collisionBody.Shapes)
                    for (int hullNumber = 0; hullNumber < entry.Value.Count; ++hullNumber)
                    {
                        var hull = entry.Value[hullNumber];
                        using var sourceId = new ObjectId(10000UL + id * 100 + (ulong)hullNumber);
                        using var shape = new PlanningCollisionShape {
                            SourceId = sourceId, Type = EnumPlanningCollisionShapeType.ConvexHull
                        };
                        using (var vertices = shape.Vertices)
                            for (int i = 0; i < hull.Coordinates.Length; i += 3)
                                using (var point = new Vector3d(hull.Coordinates[i],
                                    hull.Coordinates[i + 1], hull.Coordinates[i + 2]))
                                    vertices.Add(point);
                        using (var indices = shape.Indices)
                            foreach (var index in hull.Indices) indices.Add(index);
                        shapes.Add(shape);
                    }
                bodies.Add(collisionBody);
                using var identity = new Matrix4d(1);
                using var binding = new RobotCollisionBinding {
                    OwnerId = ownerId, LinkIndex = linkIndex, LinkToBody = identity
                };
                bindings.Add(binding);
            }
            foreach (var item in obstacles)
            {
                using var ownerId = new ObjectId(item.Id);
                using var worldMatrix = Matrix4d.makeTranslation(item.X, item.Y, item.Z);
                using var world = GTrsf.From(worldMatrix);
                using var collisionBody = new PlanningCollisionBody {
                    OwnerId = ownerId, OwnerName = item.Name,
                    OwnerType = EnumPlanningCollisionOwnerType.Environment,
                    WorldTransform = world, IncludeWorldTransformInFingerprint = true
                };
                using var sourceId = new ObjectId(item.Id + 1000000);
                using var size = new Vector3d(item.Width, item.Depth, item.Height);
                using var shape = new PlanningCollisionShape {
                    SourceId = sourceId, Type = EnumPlanningCollisionShapeType.Box, Size = size
                };
                using (var shapes = collisionBody.Shapes) shapes.Add(shape);
                bodies.Add(collisionBody);
            }
            var present = new HashSet<ulong>();
            foreach (var item in bodies)
            {
                using (item)
                using (var id = item.OwnerId) present.Add(id.Value);
            }
            using var ignoredPairs = scene.IgnoredPairs;
            ignoredPairs.Clear();
            foreach (var pair in ignored)
            {
                if (!present.Contains(pair.First) || !present.Contains(pair.Second)) continue;
                using var first = new ObjectId(pair.First);
                using var second = new ObjectId(pair.Second);
                using var entry = new PlanningCollisionIgnoredPair { FirstId = first, SecondId = second };
                ignoredPairs.Add(entry);
            }
        }

        internal void RebuildPreview()
        {
            PreviewRoot.Clear();
            linkPreviews.Clear();
            candidatePreview = null;
            foreach (var entry in linkHulls)
            {
                var group = CreateHullPreview(entry.Key, entry.Value, false);
                PreviewRoot.AddNode(group);
                linkPreviews.Add(entry.Key, group);
            }
            if (candidate is { } next)
            {
                var preview = CreateHullPreview(next.Index, next.Hulls, true);
                candidatePreview = preview;
                PreviewRoot.AddNode(preview);
                using var index = new RobotLinkIndex(next.Index);
                using var transform = body.GetLinkTransform(index, true);
                preview.SetTransform(transform);
            }
            foreach (var item in obstacles)
            {
                using var geometry = GeometryBuilder.CreateBox(
                    (float)item.Width, (float)item.Depth, (float)item.Height);
                var node = PrimitiveSceneNode.Create(geometry, null);
                node.SetName(item.Name);
                using var color = new Vector3(0.95f, 0.45f, 0.12f);
                node.SetColor(color);
                using var transform = Matrix4d.makeTranslation(item.X, item.Y, item.Z);
                node.SetTransform(transform);
                PreviewRoot.AddNode(node);
            }
            UpdateLinkTransforms();
            PreviewRoot.RequestUpdate();
        }

        private GroupSceneNode CreateHullPreview(uint index, List<Hull> hulls, bool pending)
        {
            var group = new GroupSceneNode();
            group.SetName(LinkName(index) + (pending ? " V-HACD preview" : " convex hulls"));
            group.SetPickable(false);
            foreach (var hull in hulls)
            {
                using var positions = new FloatList();
                foreach (var coordinate in hull.Coordinates) positions.Add((float)coordinate);
                using var indices = new Uint32List();
                foreach (var value in hull.Indices) indices.Add(value);
                using var positionBuffer = Float32Buffer.Create(positions);
                using var indexBuffer = Uint32Buffer.Create(indices);
                using var positionArray = new Float32Array(positionBuffer);
                using var indexArray = new Uint32Array(indexBuffer);
                using var geometry = GeometryBuilder.CreateGeometry(
                    EnumPrimitiveType.TRIANGLES, positionArray, null, null, indexArray);
                var node = PrimitiveSceneNode.Create(geometry, null);
                using var color = pending ? new Vector3(0.98f, 0.77f, 0.18f)
                    : new Vector3(0.15f, 0.75f, 0.95f);
                node.SetColor(color);
                node.SetPickable(false);
                group.AddNode(node);
            }
            return group;
        }

        internal void UpdateLinkTransforms()
        {
            foreach (var entry in linkPreviews)
            {
                using var index = new RobotLinkIndex(entry.Key);
                using var transform = body.GetLinkTransform(index, true);
                entry.Value.SetTransform(transform);
                entry.Value.RequestUpdate();
            }
            if (candidate is { } next && candidatePreview != null)
            {
                using var index = new RobotLinkIndex(next.Index);
                using var transform = body.GetLinkTransform(index, true);
                candidatePreview.SetTransform(transform);
                candidatePreview.RequestUpdate();
            }
        }
    }
}

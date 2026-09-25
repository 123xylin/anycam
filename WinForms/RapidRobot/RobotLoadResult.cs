using AnyCAD.Simulate;
using System;

namespace RapidRobot
{
    // Assembly UIs and vendor tables can return the same pair as the sample factories.
    internal sealed record RobotLoadResult(RobotBody Body,
        Func<string, CollisionTriangleMesh> LoadLinkMesh);
}

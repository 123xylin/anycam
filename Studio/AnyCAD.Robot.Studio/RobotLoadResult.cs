using AnyCAD.Simulate;
using System;

namespace AnyCAD.Robot.Studio
{
    // Assembly UIs and vendor tables can return the same pair as the sample factories.
    public sealed record RobotLoadResult(RobotBody Body,
        Func<string, CollisionTriangleMesh> LoadLinkMesh);
}

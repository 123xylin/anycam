using AnyCAD.Foundation;

namespace RapidRobot
{
    internal static class RobotAssemblyPose
    {
        // Link-local millimetres and URDF-compatible Rz * Ry * Rx radians.
        internal static Matrix4d Create(double x, double y, double z,
            double roll, double pitch, double yaw)
        {
            using var translation = Matrix4d.makeTranslation(x, y, z);
            using var xAxis = new Vector3d(1, 0, 0);
            using var yAxis = new Vector3d(0, 1, 0);
            using var zAxis = new Vector3d(0, 0, 1);
            using var rx = Matrix4d.makeRotationAxis(xAxis, roll);
            using var ry = Matrix4d.makeRotationAxis(yAxis, pitch);
            using var rz = Matrix4d.makeRotationAxis(zAxis, yaw);
            using var tRz = translation.multiplied(rz);
            using var tRzRy = tRz.multiplied(ry);
            return tRzRy.multiplied(rx);
        }
    }
}

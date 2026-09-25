using AnyCAD.Foundation;
using System;
using System.Collections.Generic;

namespace AnyCAD.Robot.Studio
{
    /// <summary>
    /// 为手建三角网格计算逐顶点法线（面积加权平滑法线）。
    /// STL 等来源的三角形网格在 GeometryBuilder.CreateGeometry 时法线传 null，
    /// 默认光照材质会因缺少法线而呈现无光照效果，此辅助用于补齐法线。
    /// </summary>
    public static class MeshNormals
    {
        /// <summary>
        /// 由顶点流与索引流计算平滑法线，输出与顶点一一对应的 FloatList（x,y,z 三元组）。
        /// </summary>
        public static FloatList ComputeSmooth(FloatList positions, Uint32List indices)
        {
            var pos = new List<float>(checked(positions.Count * 3));
            foreach (float value in positions)
                pos.Add(value);
            var idx = new List<uint>(checked(indices.Count));
            foreach (uint value in indices)
                idx.Add(value);

            int vertexCount = pos.Count / 3;
            var normals = new double[vertexCount * 3];

            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                uint ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
                if (ia >= vertexCount || ib >= vertexCount || ic >= vertexCount)
                    continue;
                int a3 = (int)ia * 3, b3 = (int)ib * 3, c3 = (int)ic * 3;
                double ax = pos[a3], ay = pos[a3 + 1], az = pos[a3 + 2];
                double bx = pos[b3], by = pos[b3 + 1], bz = pos[b3 + 2];
                double cx = pos[c3], cy = pos[c3 + 1], cz = pos[c3 + 2];
                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;
                normals[a3] += nx; normals[a3 + 1] += ny; normals[a3 + 2] += nz;
                normals[b3] += nx; normals[b3 + 1] += ny; normals[b3 + 2] += nz;
                normals[c3] += nx; normals[c3 + 1] += ny; normals[c3 + 2] += nz;
            }

            var result = new FloatList();
            for (int i = 0; i < vertexCount; i++)
            {
                int o = i * 3;
                double nx = normals[o], ny = normals[o + 1], nz = normals[o + 2];
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-12)
                {
                    result.Add((float)(nx / length));
                    result.Add((float)(ny / length));
                    result.Add((float)(nz / length));
                }
                else
                {
                    result.Add(0f);
                    result.Add(0f);
                    result.Add(1f);
                }
            }
            return result;
        }
    }
}

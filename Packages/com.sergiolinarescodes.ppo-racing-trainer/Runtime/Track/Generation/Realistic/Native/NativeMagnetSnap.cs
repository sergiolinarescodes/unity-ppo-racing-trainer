using Unity.Collections;
using Unity.Mathematics;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native
{
    // Burst port of MagnetSnapResolver.TryResolveAlignedAt operating on the
    // pre-baked PortDescriptor blob. Cardinal-output only (matches the original's
    // output domain — line 106 of the managed resolver). Pure function: no managed
    // allocations, no event publishing. Intentionally NOT decorated with
    // [BurstCompile] at the method level — Burst rejects vector-by-value across
    // external function boundaries; these helpers get inlined into Burst-compiled
    // jobs (FrontierSearchJobs) at use sites where they're treated as managed
    // calls and therefore safe to compile inline.
    public static class NativeMagnetSnap
    {
        public static bool TryResolveAligned(
            in NativeArray<PortDescriptor> ports,
            int anchorPortGlobalIdx,
            float2 targetWorldCell,
            int outwardDir,
            out int2 origin,
            out int alignedFacing)
        {
            origin = int2.zero;
            alignedFacing = 0;
            if (anchorPortGlobalIdx < 0 || anchorPortGlobalIdx >= ports.Length) return false;

            var p = ports[anchorPortGlobalIdx];
            int desired = (outwardDir + 4) & 7;
            alignedFacing = (desired - p.ShapeLocalSide) & 7;

            float2 sh = new float2(p.Shx, p.Shz);
            float2 shapeRot = RotateAroundHalf(sh, alignedFacing);
            float wx = targetWorldCell.x - 0.5f - shapeRot.x;
            float wz = targetWorldCell.y - 0.5f - shapeRot.y;
            origin = new int2((int)math.round(wx), (int)math.round(wz));
            return true;
        }

        public static bool TryResolveAt(
            in NativeArray<PortDescriptor> ports,
            int anchorPortGlobalIdx,
            float2 targetWorldCell,
            int shapeFacing,
            out int2 origin)
        {
            origin = int2.zero;
            if (anchorPortGlobalIdx < 0 || anchorPortGlobalIdx >= ports.Length) return false;

            var p = ports[anchorPortGlobalIdx];
            float2 sh = new float2(p.Shx, p.Shz);
            float2 shapeRot = RotateAroundHalf(sh, shapeFacing);
            float wx = targetWorldCell.x - 0.5f - shapeRot.x;
            float wz = targetWorldCell.y - 0.5f - shapeRot.y;
            origin = new int2((int)math.round(wx), (int)math.round(wz));
            return true;
        }

        public static float2 ResolvePortWorld(
            in NativeArray<PortDescriptor> ports,
            int portGlobalIdx,
            int2 origin,
            int shapeFacing)
        {
            var p = ports[portGlobalIdx];
            float2 sh = new float2(p.Shx, p.Shz);
            float2 shapeRot = RotateAroundHalf(sh, shapeFacing);
            return new float2(origin.x + 0.5f + shapeRot.x, origin.y + 0.5f + shapeRot.y);
        }

        public static int ResolvePortOutward(
            in NativeArray<PortDescriptor> ports,
            int portGlobalIdx,
            int shapeFacing)
        {
            var p = ports[portGlobalIdx];
            return (p.ShapeLocalSide + shapeFacing) & 7;
        }

        public static float2 RotateAroundHalf(float2 sh, int facing)
        {
            float dx = sh.x - 0.5f;
            float dz = sh.y - 0.5f;
            int f = facing & 7;
            switch (f)
            {
                case 0: return new float2(dx, dz);
                case 2: return new float2(dz, -dx);
                case 4: return new float2(-dx, -dz);
                case 6: return new float2(-dz, dx);
                default:
                    {
                        float yaw = f * (math.PI / 4f);
                        float cs = math.cos(yaw), sn = math.sin(yaw);
                        return new float2(dx * cs + dz * sn, -dx * sn + dz * cs);
                    }
            }
        }

        public static int2 RotateCellOffset(int dx, int dz, int facing)
        {
            switch (facing & 7)
            {
                case 0: return new int2(dx, dz);
                case 2: return new int2(dz, -dx);
                case 4: return new int2(-dx, -dz);
                case 6: return new int2(-dz, dx);
                default: return new int2(dx, dz);
            }
        }
    }
}

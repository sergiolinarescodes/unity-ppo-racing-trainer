using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Geometry
{
    /// <summary>
    /// Quantises world XZ positions to a 64-bit key suitable for dictionary lookup.
    /// Two points within <see cref="TrackPieceConstants.PortQuantizeGridSize"/> on
    /// each axis collide to the same key — used to pair up coincident open ports
    /// and to detect when adjacent spine endpoints meet at the same world point.
    /// </summary>
    public static class SpatialQuantizer
    {
        public static long Key(Vector3 p)
            => Key(p.x, p.z, TrackPieceConstants.PortQuantizeGridSize);

        public static long Key(Vector3 p, float step)
            => Key(p.x, p.z, step);

        public static long Key(float x, float z, float step)
        {
            int qx = Mathf.RoundToInt(x / step);
            int qz = Mathf.RoundToInt(z / step);
            return ((long)qx << 32) | ((long)(uint)qz);
        }
    }
}

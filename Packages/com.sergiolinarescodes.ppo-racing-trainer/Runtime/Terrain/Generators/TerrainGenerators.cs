using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain.Generators
{
    /// <summary>
    /// Pure functions that fill a [cornerWidth, cornerDepth] level array for each
    /// preset generation mode. Output is integer levels (height = level * StepHeight).
    /// Each call randomizes feature positions / sizes / counts based on the seed —
    /// the mode picks the STYLE, the seed picks the specific instance.
    /// </summary>
    internal static class TerrainGenerators
    {
        public static void Fill(TerrainGeneratorMode mode, int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    levels[x, z] = 0;

            switch (mode)
            {
                case TerrainGeneratorMode.Plains: Plains(levels); break;
                case TerrainGeneratorMode.GentleSlope: GentleSlope(levels, seed, maxLevel); break;
                case TerrainGeneratorMode.CenterPit: ScatteredPits(levels, seed, maxLevel); break;
                case TerrainGeneratorMode.CenterMound: ScatteredMounds(levels, seed, maxLevel); break;
                case TerrainGeneratorMode.PerimeterRing: PerimeterRing(levels, seed, maxLevel); break;
                case TerrainGeneratorMode.TerracedRows: TerracedRows(levels, seed, maxLevel); break;
                case TerrainGeneratorMode.Mountainous: Mountainous(levels, seed, maxLevel); break;
            }

            // Mountainous is already chaotic; everything else gets a chance for
            // sprinkled-on uneven features (small hills, dips, bumps).
            if (mode != TerrainGeneratorMode.Mountainous)
            {
                var overlayRng = new System.Random(seed ^ 0x5A3F1C);
                MaybeApplyUnevenOverlay(levels, overlayRng, maxLevel);
            }

            ReduceRangeIterative(levels, 30);
        }

        /// <summary>
        /// Sprinkles 0–4 small organic features (hills, dips, ripples) on top of
        /// the base height field with a configurable chance. Features are stepped
        /// pyramids/wells that integrate with the existing terrain via min/max.
        /// </summary>
        private static void MaybeApplyUnevenOverlay(int[,] levels, System.Random rng, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);

            // ~50% chance of any overlay; if it triggers, 1-4 features.
            if (rng.Next(100) >= 50) return;
            int featureCount = 1 + rng.Next(4);

            for (int i = 0; i < featureCount; i++)
            {
                int cx = rng.Next(cw);
                int cz = rng.Next(cd);
                int radius = 2 + rng.Next(5); // 2..6
                int strength = rng.Next(2) == 0 ? -1 : 1; // dip or hill
                int magnitude = 1 + rng.Next(2); // 1..2 step amplitude

                for (int z = 0; z < cd; z++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        int d = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                        if (d > radius) continue;
                        int rings = Math.Max(1, radius / Math.Max(1, magnitude));
                        int contrib = Math.Max(0, magnitude - d / rings);
                        if (contrib == 0) continue;
                        if (strength > 0)
                            levels[x, z] = Math.Min(maxLevel, levels[x, z] + contrib);
                        else
                            levels[x, z] = Math.Max(0, levels[x, z] - contrib);
                    }
                }
            }
        }

        // --- modes ---

        private static void Plains(int[,] levels)
        {
            // Always level 0. Easy reference frame for building.
        }

        private static void GentleSlope(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);
            int dir = rng.Next(4); // 0=rises N, 1=rises S, 2=rises E, 3=rises W
            int peak = 1 + rng.Next(Math.Min(maxLevel, 3)); // 1..3 step rise
            int axisLen = (dir < 2) ? cd - 1 : cw - 1;
            // Optional plateau-then-rise: split axis into "flat zone | ramp zone | flat-top"
            int flatHead = rng.Next(Math.Max(2, axisLen / 4));
            int flatTail = rng.Next(Math.Max(2, axisLen / 4));
            int rampLen = Math.Max(peak, axisLen - flatHead - flatTail);

            for (int z = 0; z < cd; z++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int axisPos = dir switch { 0 => z, 1 => cd - 1 - z, 2 => x, _ => cw - 1 - x };
                    int relative = axisPos - flatHead;
                    int level = relative <= 0 ? 0
                              : relative >= rampLen ? peak
                              : (relative * peak) / Math.Max(1, rampLen);
                    levels[x, z] = level;
                }
            }
        }

        private static void ScatteredPits(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);
            int ground = 2 + rng.Next(Math.Max(1, Math.Min(maxLevel - 1, 2))); // 2..3
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    levels[x, z] = ground;

            // Mix of pits AND small mounds across the map. 5–10 features at varied
            // radii and depths so the surface always reads as a worked-on landscape,
            // not just a flat plate with one tiny hole.
            int featureCount = 5 + rng.Next(6); // 5..10
            for (int i = 0; i < featureCount; i++)
            {
                int cx = rng.Next(cw / 6, 5 * cw / 6);
                int cz = rng.Next(cd / 6, 5 * cd / 6);
                int radius = 2 + rng.Next(6); // 2..7
                bool isPit = rng.Next(100) < 65; // 65% pits, 35% small hills

                if (isPit)
                {
                    int pitDepth = 1 + rng.Next(ground); // 1..ground
                    int floor = Math.Max(0, ground - pitDepth);
                    for (int z = 0; z < cd; z++)
                    {
                        for (int x = 0; x < cw; x++)
                        {
                            int d = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                            if (d > radius) continue;
                            int rings = Math.Max(1, radius / Math.Max(1, pitDepth));
                            int sink = Math.Max(0, pitDepth - d / rings);
                            int target = ground - sink;
                            target = Math.Max(floor, target);
                            levels[x, z] = Math.Min(levels[x, z], target);
                        }
                    }
                }
                else
                {
                    int peak = ground + 1 + rng.Next(2); // ground+1..ground+2
                    peak = Math.Min(peak, maxLevel);
                    for (int z = 0; z < cd; z++)
                    {
                        for (int x = 0; x < cw; x++)
                        {
                            int d = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                            if (d > radius) continue;
                            int rings = Math.Max(1, radius / Math.Max(1, peak - ground));
                            int rise = Math.Max(0, (peak - ground) - d / rings);
                            int target = ground + rise;
                            if (target > levels[x, z])
                                levels[x, z] = Math.Min(maxLevel, target);
                        }
                    }
                }
            }
        }

        private static void ScatteredMounds(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);
            // 5..10 features, mostly mounds with the occasional dip cut into one of
            // them to add character. Wider radius range so peaks and broader rises
            // mix in the same map. Slope chaining is automatic because each mound's
            // ring spacing is `radius / peak` so adjacent corner diffs stay at 1.
            int featureCount = 5 + rng.Next(6);
            for (int i = 0; i < featureCount; i++)
            {
                int cx = rng.Next(cw / 6, 5 * cw / 6);
                int cz = rng.Next(cd / 6, 5 * cd / 6);
                bool isMound = rng.Next(100) < 70; // 70% mounds, 30% dips

                if (isMound)
                {
                    int peak = 1 + rng.Next(Math.Min(maxLevel, 4)); // 1..4
                    int radius = peak * 2 + rng.Next(4); // peak*2..peak*2+3 — gentle slopes
                    for (int z = 0; z < cd; z++)
                    {
                        for (int x = 0; x < cw; x++)
                        {
                            int d = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                            if (d > radius) continue;
                            int rings = Math.Max(1, radius / Math.Max(1, peak));
                            int contribution = Math.Max(0, peak - d / rings);
                            if (contribution > levels[x, z])
                                levels[x, z] = Math.Min(maxLevel, contribution);
                        }
                    }
                }
                else
                {
                    int sinkDepth = 1 + rng.Next(2); // 1..2
                    int radius = 2 + rng.Next(4); // 2..5
                    for (int z = 0; z < cd; z++)
                    {
                        for (int x = 0; x < cw; x++)
                        {
                            int d = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                            if (d > radius) continue;
                            int rings = Math.Max(1, radius / Math.Max(1, sinkDepth));
                            int sink = Math.Max(0, sinkDepth - d / rings);
                            int target = levels[x, z] - sink;
                            if (target < levels[x, z])
                                levels[x, z] = Math.Max(0, target);
                        }
                    }
                }
            }
        }

        private static void PerimeterRing(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);
            int ringHeight = 1 + rng.Next(Math.Min(maxLevel, 2)); // 1..2
            int ringWidth = 2 + rng.Next(3); // 2..4
            // Optional asymmetry: each side gets independent width.
            int wN = ringWidth + rng.Next(2);
            int wS = ringWidth + rng.Next(2);
            int wE = ringWidth + rng.Next(2);
            int wW = ringWidth + rng.Next(2);

            for (int z = 0; z < cd; z++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int dN = cd - 1 - z;
                    int dS = z;
                    int dE = cw - 1 - x;
                    int dW = x;
                    int worstSide = Math.Min(Math.Min(dN < wN ? wN - dN : 0, dS < wS ? wS - dS : 0),
                                              Math.Min(dE < wE ? wE - dE : 0, dW < wW ? wW - dW : 0));
                    int distContribution = Math.Max(Math.Max(dN < wN ? wN - dN : 0, dS < wS ? wS - dS : 0),
                                                     Math.Max(dE < wE ? wE - dE : 0, dW < wW ? wW - dW : 0));
                    levels[x, z] = Math.Min(ringHeight, distContribution);
                }
            }
        }

        private static void TerracedRows(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);
            bool zOriented = rng.Next(2) == 0;

            int axisLen = zOriented ? cd : cw;
            int crossLen = zOriented ? cw : cd;
            int currentLevel = rng.Next(Math.Min(2, maxLevel + 1));
            int pos = 0;
            while (pos < axisLen)
            {
                int bandLen = 2 + rng.Next(5); // 2..6 corner-rows per band
                int bandEnd = Math.Min(pos + bandLen, axisLen);
                for (int a = pos; a < bandEnd; a++)
                    for (int b = 0; b < crossLen; b++)
                    {
                        if (zOriented) levels[b, a] = currentLevel;
                        else levels[a, b] = currentLevel;
                    }
                pos = bandEnd;
                if (pos >= axisLen) break;
                int r = rng.Next(10);
                if (r < 5) currentLevel = Math.Min(currentLevel + 1, maxLevel);
                else currentLevel = Math.Max(currentLevel - 1, 0);
            }
        }

        private static void Mountainous(int[,] levels, int seed, int maxLevel)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            var rng = new System.Random(seed);

            // Lower frequencies than before so adjacent corners only rarely differ
            // by more than one step — terrain prefers chained slopes (slope-flat-slope)
            // over single-tile cliffs after the range-fix pass.
            float fx1 = (float)(rng.NextDouble() * 0.10 + 0.08);
            float fz1 = (float)(rng.NextDouble() * 0.10 + 0.08);
            float fx2 = (float)(rng.NextDouble() * 0.18 + 0.18);
            float fz2 = (float)(rng.NextDouble() * 0.18 + 0.18);
            float px = (float)(rng.NextDouble() * Math.PI * 2);
            float pz = (float)(rng.NextDouble() * Math.PI * 2);
            float amp1 = 1.4f + (float)rng.NextDouble() * 0.8f;
            float amp2 = 0.5f + (float)rng.NextDouble() * 0.5f;

            // Buildable flat zone in the centre — covers 40–50% of the map's
            // smaller dimension. Outside the zone the noise amplitude ramps in
            // smoothly so the boundary doesn't read as a hard cliff. The flat
            // zone level is randomized per cycle so it isn't always at 0.
            float midX = cw * 0.5f;
            float midZ = cd * 0.5f;
            float flatPct = 0.40f + (float)rng.NextDouble() * 0.10f;
            float flatHalf = flatPct * 0.5f * Math.Min(cw, cd);
            float blendWidth = Math.Max(4f, Math.Min(cw, cd) * 0.15f);
            int flatLevel = rng.Next(Math.Min(2, maxLevel + 1));

            for (int z = 0; z < cd; z++)
            {
                for (int x = 0; x < cw; x++)
                {
                    float dx = Mathf.Abs(x - midX);
                    float dz = Mathf.Abs(z - midZ);
                    float distFromCenter = Mathf.Max(dx, dz);
                    float t = (distFromCenter - flatHalf) / blendWidth;
                    float noiseStrength = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

                    float n1 = Mathf.Sin(x * fx1 + px) * Mathf.Cos(z * fz1);
                    float n2 = Mathf.Cos(x * fx2) * Mathf.Sin(z * fz2 + pz);
                    float h = flatLevel + noiseStrength * (amp1 * n1 + amp2 * n2 + maxLevel * 0.3f);
                    levels[x, z] = Mathf.Clamp(Mathf.RoundToInt(h), 0, maxLevel);
                }
            }
        }

        // --- helpers ---

        private static void ReduceRangeIterative(int[,] levels, int maxIterations)
        {
            int cw = levels.GetLength(0);
            int cd = levels.GetLength(1);
            for (int iter = 0; iter < maxIterations; iter++)
            {
                bool changed = false;
                for (int z = 0; z < cd - 1; z++)
                {
                    for (int x = 0; x < cw - 1; x++)
                    {
                        int nw = levels[x, z + 1];
                        int ne = levels[x + 1, z + 1];
                        int se = levels[x + 1, z];
                        int sw = levels[x, z];
                        int lo = Math.Min(Math.Min(nw, ne), Math.Min(se, sw));
                        int hi = Math.Max(Math.Max(nw, ne), Math.Max(se, sw));
                        if (hi - lo <= 1) continue;
                        int target = hi - 1;
                        if (nw == hi) { levels[x, z + 1] = target; changed = true; }
                        if (ne == hi) { levels[x + 1, z + 1] = target; changed = true; }
                        if (se == hi) { levels[x + 1, z] = target; changed = true; }
                        if (sw == hi) { levels[x, z] = target; changed = true; }
                    }
                }
                if (!changed) break;
            }
        }
    }
}

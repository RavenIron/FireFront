using System;

namespace FireFront.Fire
{
    /// <summary>
    /// Discrete grid coordinate for ground-fire cells. World positions are
    /// snapped to a grid of GroundCellSize so ground spread is pure integer
    /// math (no FindObjectsOfType scans, no per-frame allocation) — cheap
    /// enough to check a whole neighborhood every cycle.
    /// </summary>
    public readonly struct GroundCellKey : IEquatable<GroundCellKey>
    {
        public readonly int X;
        public readonly int Z;

        public GroundCellKey(int x, int z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(GroundCellKey other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is GroundCellKey other && Equals(other);
        public override int GetHashCode() => (X * 397) ^ Z;
    }
}

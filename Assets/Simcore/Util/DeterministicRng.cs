using System;
using System.Collections.Generic;

namespace SimCore.Util
{
    /// <summary>
    /// The ONLY randomness source permitted anywhere in SimCore. Wraps System.Random
    /// with a recorded seed so every episode is reproducible from its config.
    /// Never use UnityEngine.Random (blocked by the assembly wall anyway) and never
    /// construct a bare new Random() — an unrecorded seed is an unreproducible experiment.
    /// </summary>
    public sealed class DeterministicRng
    {
        public int Seed { get; }
        private readonly Random _rng;

        public DeterministicRng(int seed)
        {
            Seed = seed;
            _rng = new Random(seed);
        }

        public int NextInt(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);

        public double NextDouble() => _rng.NextDouble();

        public T Pick<T>(IReadOnlyList<T> list)
        {
            if (list == null || list.Count == 0)
                throw new InvalidOperationException("Cannot pick from an empty list.");
            return list[_rng.Next(list.Count)];
        }
    }
}
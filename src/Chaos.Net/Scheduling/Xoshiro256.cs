using System.Numerics;

namespace ChaosNet.Scheduling;

/// <summary>
/// xoshiro256** pseudo-random generator seeded through SplitMix64.
/// </summary>
/// <remarks>
/// Chaos.Net ships its own generator instead of <see cref="System.Random"/> because the sequence produced by
/// a seeded <see cref="System.Random"/> is not guaranteed to be stable across .NET versions. Replaying a seed
/// must produce the same schedule forever, so the algorithm is pinned here.
/// </remarks>
internal struct Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>
    /// Derives the seed of iteration <paramref name="iteration"/> from a base seed. Iteration 0 uses the base seed
    /// itself, so "rerun with the iteration seed and one iteration" reproduces any iteration. Later iterations hash
    /// the base seed and the index, so runs with related base seeds (1, 2, 3, ...) do not share iteration streams.
    /// </summary>
    public static ulong IterationSeed(ulong baseSeed, int iteration) =>
        iteration == 0 ? baseSeed : Mix(unchecked(Mix(baseSeed) + ((ulong)iteration * 0xD1B54A32D192ED03UL)));

    /// <summary>The SplitMix64 finalizer: a stateless 64-bit bijective hash.</summary>
    public static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>Creates a fresh, non-deterministic seed (used when the user does not supply one).</summary>
    public static ulong NewSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    public static ulong SplitMix64(ref ulong x)
    {
        ulong z = x = unchecked(x + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    public ulong NextUInt64()
    {
        ulong result = unchecked(BitOperations.RotateLeft(_s1 * 5, 7) * 9);
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Returns a value in [0, maxExclusive) using Lemire's multiply-shift reduction.</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 1)
        {
            return 0;
        }

        ulong r = NextUInt64() >> 32;
        return (int)((r * (ulong)maxExclusive) >> 32);
    }

    public bool NextBoolean() => (NextUInt64() >> 63) != 0;
}

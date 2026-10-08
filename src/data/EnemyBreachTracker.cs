using System;
using UnityEngine;

/// <summary>Fixed-capacity server-local opening memory. No enemy broadcast or scene search.</summary>
public static class EnemyBreachTracker
{
    public const int Capacity = 32;

    public struct Opening
    {
        public uint Id;
        public Vector3 Position, Normal;
        public float HalfWidth, HalfThickness, HalfHeight;
    }

    private struct Entry
    {
        public uint Id;
        public Vector3 Position;
        public Opening Opening;
        public double ExpiresAt;
        public bool Active;
    }

    private static readonly Entry[] Entries = new Entry[Capacity];
    private static int nextIndex;
    private static uint nextId;
    private static double latestExpiry;
    private static int activeCount;
    public static uint Version { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void Reset()
    {
        Array.Clear(Entries, 0, Entries.Length);
        nextIndex = 0;
        nextId = 0;
        Version = 0;
        latestExpiry = 0;
        activeCount = 0;
    }

    public static uint Register(Vector3 position, double now, float lifetimeSeconds)
        => RegisterOpening(position, Vector3.zero, 0, float.PositiveInfinity,
            float.PositiveInfinity, now, lifetimeSeconds);

    public static bool HasOpenings(double now) => activeCount > 0 && now < latestExpiry;

    public static uint RegisterOpening(Vector3 position, Vector3 normal, float halfThickness,
        float halfWidth, float halfHeight, double now, float lifetimeSeconds)
    {
        if (!EnemyProxyMath.Finite(position) || !EnemyProxyMath.Finite(normal) ||
            double.IsNaN(now) || double.IsInfinity(now) || halfThickness < 0 ||
            float.IsNaN(halfThickness) || halfWidth <= 0 || float.IsNaN(halfWidth) ||
            halfHeight <= 0 || float.IsNaN(halfHeight) || float.IsNaN(lifetimeSeconds)) return 0;
        normal.y = 0;
        normal.Normalize();
        unchecked
        {
            nextId++;
            if (nextId == 0) nextId++;
            Version++;
        }
        if (!Entries[nextIndex].Active) activeCount++;
        double expires = now + Mathf.Max(0.1f, lifetimeSeconds);
        latestExpiry = Math.Max(latestExpiry, expires);
        Entries[nextIndex] = new Entry
        {
            Id = nextId,
            Position = position,
            Opening = new Opening { Id = nextId, Position = position, Normal = normal,
                HalfThickness = halfThickness, HalfWidth = halfWidth, HalfHeight = halfHeight },
            ExpiresAt = expires,
            Active = true
        };
        nextIndex = (nextIndex + 1) % Capacity;
        return nextId;
    }

    public static bool TryGet(uint id, double now, out Vector3 position)
    {
        position = default;
        if (id == 0) return false;
        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (!entry.Active || entry.Id != id || now >= entry.ExpiresAt) continue;
            position = entry.Position;
            return true;
        }
        return false;
    }

    public static bool TryGetOpening(uint id, double now, out Opening opening)
    {
        opening = default;
        if (id == 0) return false;
        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (!entry.Active || entry.Id != id || now >= entry.ExpiresAt) continue;
            opening = entry.Opening;
            return true;
        }
        return false;
    }

    /// <summary>Two points on opposite sides, distributed across the usable opening width.</summary>
    public static bool TryWaypoints(Opening opening, Vector3 enemy, Vector3 target, uint seed,
        float radius, float halfHeight, float clearance, out Vector3 approach, out Vector3 exit)
    {
        approach = exit = default;
        if (opening.Normal.sqrMagnitude < 0.5f ||
            Mathf.Abs(enemy.y - opening.Position.y) + halfHeight > opening.HalfHeight + 0.05f)
            return false;
        float usable = opening.HalfWidth - radius - clearance;
        if (usable < 0) return false;
        float enemySide = Vector3.Dot(enemy - opening.Position, opening.Normal);
        float targetSide = Vector3.Dot(target - opening.Position, opening.Normal);
        float crossingDistance = opening.HalfThickness + radius + clearance;
        // Already beyond the wall, on the same side as the target, or standing in the gap.
        // The extra approach clearance is not an exclusion zone: enemies already
        // pressed against an intact neighboring wall must be allowed to move sideways.
        if (Mathf.Abs(enemySide) <= opening.HalfThickness + radius || enemySide * targetSide >= 0) return false;
        float spacing = Mathf.Max(0.1f, radius * 2 + clearance);
        int lanes = Mathf.Max(1, Mathf.FloorToInt(usable * 2 / spacing) + 1);
        float lateral = lanes > 1 ? Mathf.Lerp(-usable, usable, (seed % (uint)lanes) / (float)(lanes - 1)) : 0;
        Vector3 middle = opening.Position + Vector3.Cross(Vector3.up, opening.Normal) * lateral;
        middle.y = enemy.y;
        Vector3 offset = opening.Normal * (Mathf.Sign(enemySide) * crossingDistance);
        approach = middle + offset;
        exit = middle - offset;
        return true;
    }

    public static bool TryFindRoute(Vector3 enemy, Vector3 target, uint seed, double now,
        float awarenessRadius, float maximumDetour, float minimumForwardDot,
        float radius, float halfHeight, float clearance,
        out uint id, out Vector3 approach, out Vector3 exit)
    {
        id = 0;
        approach = exit = default;
        if (!HasOpenings(now)) return false;
        Vector3 direct = target - enemy; direct.y = 0;
        float directDistance = direct.magnitude;
        Vector3 forward = direct.normalized;
        float best = float.PositiveInfinity;
        float awarenessSquared = awarenessRadius * awarenessRadius;
        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (!entry.Active || now >= entry.ExpiresAt) continue;
            Vector3 toOpening = entry.Position - enemy; toOpening.y = 0;
            if (toOpening.sqrMagnitude > awarenessSquared ||
                Vector3.Dot(toOpening.normalized, forward) < minimumForwardDot ||
                !TryWaypoints(entry.Opening, enemy, target, seed, radius, halfHeight, clearance,
                    out Vector3 a, out Vector3 b)) continue;
            Vector3 tail = target - b; tail.y = 0;
            float length = (a - enemy).magnitude + (b - a).magnitude + tail.magnitude;
            if (length >= best || length - directDistance > maximumDetour) continue;
            best = length; id = entry.Id; approach = a; exit = b;
        }
        return id != 0;
    }

    public static bool TryFindBest(Vector3 enemyPosition, Vector3 targetPosition, double now,
        float awarenessRadius, float maximumDetour, float minimumForwardDot,
        out uint id, out Vector3 position)
    {
        id = 0;
        position = default;
        Vector3 directOffset = targetPosition - enemyPosition;
        directOffset.y = 0f;
        float directDistance = directOffset.magnitude;
        Vector3 targetDirection = directDistance > 0.001f ? directOffset / directDistance : Vector3.zero;
        float radius = Mathf.Max(0.1f, awarenessRadius);
        float radiusSquared = radius * radius;
        float bestPath = float.PositiveInfinity;
        float forwardDot = Mathf.Clamp(minimumForwardDot, -1f, 1f);

        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (!entry.Active || now >= entry.ExpiresAt) continue;
            Vector3 toBreach = entry.Position - enemyPosition;
            toBreach.y = 0f;
            float toBreachSquared = toBreach.sqrMagnitude;
            if (toBreachSquared > radiusSquared) continue;
            float toBreachDistance = Mathf.Sqrt(toBreachSquared);
            if (directDistance > 0.001f && toBreachDistance > 0.001f &&
                Vector3.Dot(toBreach / toBreachDistance, targetDirection) < forwardDot) continue;
            Vector3 breachToTarget = targetPosition - entry.Position;
            breachToTarget.y = 0f;
            float pathDistance = toBreachDistance + breachToTarget.magnitude;
            if (pathDistance - directDistance > Mathf.Max(0f, maximumDetour) || pathDistance >= bestPath)
                continue;
            bestPath = pathDistance;
            id = entry.Id;
            position = entry.Position;
        }
        return id != 0;
    }

    public static int InvalidateNear(Vector3 position, float radius)
    {
        float radiusSquared = Mathf.Max(0f, radius) * Mathf.Max(0f, radius);
        int removed = 0;
        for (int i = 0; i < Entries.Length; i++)
        {
            if (!Entries[i].Active) continue;
            Vector3 offset = Entries[i].Position - position;
            offset.y = 0f;
            if (offset.sqrMagnitude > radiusSquared) continue;
            Entries[i].Active = false;
            activeCount--;
            removed++;
        }
        if (removed > 0) unchecked { Version++; }
        return removed;
    }
}

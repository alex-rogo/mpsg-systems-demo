using UnityEngine;

/// <summary>One fixed King formation. Inner vacancies are filled before outer waiting rings.</summary>
public sealed class EnemyAttackSlots
{
    private readonly Vector3[] offsets;
    private readonly int[] rings, next, previous, heads;
    private readonly bool[] claimed;
    private int firstFreeRing;
    public int Capacity => offsets.Length;
    public int ClaimedCount { get; private set; }
    public float OuterRadius { get; private set; }

    private EnemyAttackSlots(int capacity)
    {
        capacity = Mathf.Max(1, capacity);
        offsets = new Vector3[capacity];
        rings = new int[capacity];
        next = new int[capacity];
        previous = new int[capacity];
        heads = new int[capacity];
        claimed = new bool[capacity];
    }

    public EnemyAttackSlots(int capacity, float innerRadius, float spacing) : this(capacity)
    {
        innerRadius = Mathf.Max(0.1f, innerRadius);
        spacing = Mathf.Max(0.1f, spacing);
        int index = 0;
        for (int ring = 0; index < capacity; ring++)
        {
            float radius = innerRadius + ring * spacing;
            int size = Mathf.Max(1, Mathf.FloorToInt(Mathf.PI /
                Mathf.Asin(Mathf.Min(1f, spacing / (2f * radius)))));
            int count = Mathf.Min(size, capacity - index);
            for (int j = 0; j < count; j++, index++)
            {
                float angle = (j + (ring % 2) * 0.5f) * Mathf.PI * 2f / size;
                offsets[index] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                rings[index] = ring;
            }
            OuterRadius = radius;
        }
        Clear();
    }

    /// <summary>Creates uniformly spaced rings around an axis-aligned rectangular footprint.</summary>
    public EnemyAttackSlots(int capacity, Vector3 centerOffset, Vector2 halfExtents,
        float padding, float spacing) : this(capacity)
    {
        halfExtents.x = Mathf.Max(0.05f, halfExtents.x);
        halfExtents.y = Mathf.Max(0.05f, halfExtents.y);
        padding = Mathf.Max(0.1f, padding);
        spacing = Mathf.Max(0.1f, spacing);
        centerOffset.y = 0f;
        int index = 0;
        for (int ring = 0; index < Capacity; ring++)
        {
            float expansion = padding + ring * spacing;
            float extentX = halfExtents.x + expansion;
            float extentZ = halfExtents.y + expansion;
            float perimeter = 4f * (extentX + extentZ);
            int size = Mathf.Max(4, Mathf.FloorToInt(perimeter / spacing));
            int count = Mathf.Min(size, Capacity - index);
            float phase = ring % 2 == 0 ? 0f : 0.5f;
            for (int j = 0; j < count; j++, index++)
            {
                float distance = (j + phase) * perimeter / size;
                offsets[index] = centerOffset + PointOnRectangle(distance, extentX, extentZ);
                rings[index] = ring;
                OuterRadius = Mathf.Max(OuterRadius, offsets[index].magnitude);
            }
        }
        Clear();
    }

    private static Vector3 PointOnRectangle(float distance, float extentX, float extentZ)
    {
        float width = extentX * 2f;
        float depth = extentZ * 2f;
        float perimeter = (width + depth) * 2f;
        distance = Mathf.Repeat(distance, perimeter);
        if (distance < width) return new Vector3(-extentX + distance, 0f, -extentZ);
        distance -= width;
        if (distance < depth) return new Vector3(extentX, 0f, -extentZ + distance);
        distance -= depth;
        if (distance < width) return new Vector3(extentX - distance, 0f, extentZ);
        distance -= width;
        return new Vector3(-extentX, 0f, extentZ - distance);
    }

    public bool IsClaimed(int slot) => slot >= 0 && slot < Capacity && claimed[slot];
    public bool IsInnerRing(int slot) => slot >= 0 && slot < Capacity && rings[slot] == 0;
    public Vector3 GetOffset(int slot) => offsets[slot];

    public int Claim(Vector3 preferredOffset)
    {
        if (ClaimedCount == Capacity) return -1;
        while (heads[firstFreeRing] < 0) firstFreeRing++;
        int start = heads[firstFreeRing];
        int best = start;
        int current = start;
        float bestDistance = float.PositiveInfinity;
        // Bound selection even on outer rings. Free-list rotation avoids fixed-index bias.
        for (int i = 0; i < 16; i++)
        {
            float distance = (offsets[current] - preferredOffset).sqrMagnitude;
            if (distance < bestDistance) { best = current; bestDistance = distance; }
            current = next[current];
            if (current == start) break;
        }
        if (next[best] == best) heads[firstFreeRing] = -1;
        else
        {
            next[previous[best]] = next[best];
            previous[next[best]] = previous[best];
            heads[firstFreeRing] = next[best];
        }
        claimed[best] = true;
        ClaimedCount++;
        return best;
    }

    public void Release(int slot)
    {
        if (!IsClaimed(slot)) return;
        int ring = rings[slot];
        int head = heads[ring];
        if (head < 0) { heads[ring] = slot; next[slot] = previous[slot] = slot; }
        else
        {
            previous[slot] = previous[head];
            next[slot] = head;
            next[previous[head]] = slot;
            previous[head] = slot;
        }
        claimed[slot] = false;
        ClaimedCount--;
        firstFreeRing = Mathf.Min(firstFreeRing, ring);
    }

    public int Promote(int slot, Vector3 preferredOffset)
    {
        if (!IsClaimed(slot)) return Claim(preferredOffset);
        if (firstFreeRing >= rings[slot]) return slot;
        // firstFreeRing may point at an exhausted ring until the next claim.
        int freeRing = firstFreeRing;
        while (freeRing < rings[slot] && heads[freeRing] < 0) freeRing++;
        if (freeRing >= rings[slot]) return slot;
        Release(slot);
        return Claim(preferredOffset);
    }

    public void Clear()
    {
        System.Array.Clear(claimed, 0, claimed.Length);
        for (int i = 0; i < heads.Length; i++) heads[i] = -1;
        for (int i = 0; i < Capacity; i++)
        {
            int ring = rings[i];
            int head = heads[ring];
            if (head < 0) { heads[ring] = i; next[i] = previous[i] = i; }
            else
            {
                previous[i] = previous[head]; next[i] = head;
                next[previous[head]] = i; previous[head] = i;
            }
        }
        firstFreeRing = ClaimedCount = 0;
    }
}

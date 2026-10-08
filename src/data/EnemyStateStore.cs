using System;
using System.Collections.Generic;
using UnityEngine;

public enum EnemyBehavior : byte { Idle, Chase, Attack, AttackWall }

/// <summary>Numeric gameplay state. IDs survive slot reuse; Unity views are not referenced here.</summary>
public struct EnemyState
{
    public uint Id;
    public Vector3 Position, Velocity, Destination;
    public float Yaw, StopDistance;
    public int Health, MaxHealth, GridIndex, KingSlot;
    public ushort Archetype;
    public EnemyBehavior Behavior;
    public double NextDecision, NextAttack;
}

public sealed class EnemyStateStore
{
    public readonly EnemyState[] States;
    public readonly EnemySpatialGrid Grid;
    public readonly int[] ActiveSlots;
    private readonly int[] activeIndices, free, gridToSlot;
    private readonly Dictionary<uint, int> slots;
    private int freeCount;
    private uint nextId;
    public int Count { get; private set; }
    public int Capacity => States.Length;

    public EnemyStateStore(int capacity, float cellSize)
    {
        States = new EnemyState[capacity];
        ActiveSlots = new int[capacity];
        activeIndices = new int[capacity];
        free = new int[capacity];
        gridToSlot = new int[capacity];
        slots = new Dictionary<uint, int>(capacity);
        Grid = new EnemySpatialGrid(capacity, cellSize);
        Clear();
    }

    public bool TryGetSlot(uint id, out int slot) => slots.TryGetValue(id, out slot);
    public int SlotFromGrid(int index) => gridToSlot[index];

    public bool Spawn(Vector3 position, float yaw, int health, out uint id)
    {
        id = 0;
        if (freeCount == 0 || health <= 0 || nextId == uint.MaxValue || !EnemyProxyMath.Finite(position))
            return false; // Never wrap IDs onto an earlier life.
        id = ++nextId;
        return Add(id, position, yaw, health);
    }

    public bool AddReplica(EnemySnapshot snapshot) =>
        snapshot.Id != 0 && !slots.ContainsKey(snapshot.Id) &&
        Add(snapshot.Id, snapshot.Position, snapshot.Yaw, Mathf.Max(1, snapshot.Health));

    private bool Add(uint id, Vector3 position, float yaw, int health)
    {
        if (freeCount == 0) return false;
        int slot = free[--freeCount];
        int grid = Grid.Add(position);
        if (grid < 0) { free[freeCount++] = slot; return false; }
        States[slot] = new EnemyState { Id = id, Position = position, Yaw = yaw,
            Health = health, MaxHealth = health, GridIndex = grid, KingSlot = -1 };
        slots.Add(id, slot);
        gridToSlot[grid] = slot;
        activeIndices[slot] = Count;
        ActiveSlots[Count++] = slot;
        return true;
    }

    public void Move(int slot, Vector3 position)
    {
        States[slot].Position = position;
        Grid.UpdatePosition(States[slot].GridIndex, position);
    }

    public bool Remove(uint id, out EnemyState removed)
    {
        removed = default;
        if (!slots.TryGetValue(id, out int slot)) return false;
        removed = States[slot];
        Grid.Remove(removed.GridIndex);
        gridToSlot[removed.GridIndex] = -1;
        slots.Remove(id);
        int index = activeIndices[slot];
        int moved = ActiveSlots[--Count];
        ActiveSlots[index] = moved;
        activeIndices[moved] = index;
        States[slot] = default;
        free[freeCount++] = slot;
        return true;
    }

    public void Clear()
    {
        slots.Clear();
        Grid.Clear();
        Array.Clear(States, 0, States.Length);
        Count = 0;
        freeCount = Capacity;
        for (int i = 0; i < Capacity; i++) { free[i] = Capacity - 1 - i; gridToSlot[i] = -1; }
        // nextId is intentionally retained even when an encounter is cleared.
    }
}

public struct EnemySnapshot
{
    public uint Id;
    public Vector3 Position, Velocity;
    public float Yaw;
    public ushort Health;
    public EnemyBehavior Behavior;
    public static EnemySnapshot From(EnemyState state) => new EnemySnapshot {
        Id = state.Id, Position = state.Position, Velocity = state.Velocity, Yaw = state.Yaw,
        Health = (ushort)Mathf.Clamp(state.Health, 0, ushort.MaxValue), Behavior = state.Behavior };
}

/// <summary>Fixed history for delayed rendering, with bounded prediction and correction blending.</summary>
public struct EnemySnapshotTrack
{
    public const int HistoryCapacity = 8;
    private struct TimedSample { public EnemySnapshot State; public double Time; }
    // Inline storage: no array/object allocation per spawn, packet or rendered enemy.
    private TimedSample s0, s1, s2, s3, s4, s5, s6, s7;
    private EnemySnapshot latest, lastTarget;
    private int nextWrite, count;
    private uint revision, renderedRevision;
    private bool renderInitialized;
    private double lastRenderTime;
    private Vector3 correction;
    private float yawCorrection, currentDelay;
    public double LatestTime { get; private set; }
    public bool HasSample { get; private set; }
    public int StoredSamples => count;
    public float EstimatedInterval { get; private set; }
    public float RenderDelay { get; private set; }
    public bool IsPredicting { get; private set; }

    private TimedSample Get(int index)
    {
        switch (index & 7) {
            case 0: return s0; case 1: return s1; case 2: return s2; case 3: return s3;
            case 4: return s4; case 5: return s5; case 6: return s6; default: return s7;
        }
    }
    private void Set(int index, TimedSample sample)
    {
        switch (index & 7) {
            case 0: s0 = sample; break; case 1: s1 = sample; break;
            case 2: s2 = sample; break; case 3: s3 = sample; break;
            case 4: s4 = sample; break; case 5: s5 = sample; break;
            case 6: s6 = sample; break; default: s7 = sample; break;
        }
    }

    public bool Push(EnemySnapshot sample, double time)
    {
        if (sample.Id == 0 || double.IsNaN(time) || double.IsInfinity(time) ||
            !EnemyProxyMath.Finite(sample.Position) || !EnemyProxyMath.Finite(sample.Velocity) ||
            float.IsNaN(sample.Yaw) || float.IsInfinity(sample.Yaw)) return false;
        if (HasSample && (sample.Id != latest.Id || time <= LatestTime)) return false;
        if (!HasSample || (sample.Position - latest.Position).sqrMagnitude > 100f)
        {
            nextWrite = count = 0;
            EstimatedInterval = 0;
            renderInitialized = false; // A teleport must not blend through old terrain.
            correction = Vector3.zero;
            yawCorrection = 0;
        }
        else
        {
            float gap = Mathf.Clamp((float)(time - LatestTime), 1f / 60, 1f);
            // One lost packet must not suddenly double everyone's visual latency.
            if (EstimatedInterval > 0)
                gap = Mathf.Min(gap, EstimatedInterval * 2);
            EstimatedInterval = EstimatedInterval > 0
                ? Mathf.Lerp(EstimatedInterval, gap, 0.25f) : gap;
        }
        Set(nextWrite, new TimedSample { State = sample, Time = time });
        nextWrite = (nextWrite + 1) & 7;
        count = Math.Min(count + 1, HistoryCapacity);
        latest = sample;
        LatestTime = time;
        HasSample = true;
        revision++;
        return true;
    }

    public EnemySnapshot Sample(double renderTime, float maxExtrapolation = 0.2f)
    {
        EnemySnapshot result = latest;
        if (!HasSample) return result;
        if (renderTime > LatestTime)
        {
            result.Position += latest.Velocity * Mathf.Clamp(
                (float)(renderTime - LatestTime), 0f, Mathf.Max(0, maxExtrapolation));
            return result;
        }
        TimedSample after = Get(nextWrite - 1);
        // Search newest-first: normal playback needs just the last 2-3 samples.
        for (int i = 1; i < count; i++)
        {
            TimedSample before = Get(nextWrite - 1 - i);
            if (renderTime >= before.Time)
            {
                float t = Mathf.Clamp01((float)((renderTime - before.Time) / (after.Time - before.Time)));
                result.Position = Vector3.LerpUnclamped(before.State.Position, after.State.Position, t);
                result.Yaw = Mathf.LerpAngle(before.State.Yaw, after.State.Yaw, t);
                return result;
            }
            after = before;
        }
        result.Position = after.State.Position;
        result.Yaw = after.State.Yaw;
        return result;
    }

    /// <summary>Correction-only smoothing: regular motion is not a low-pass filtered transform.</summary>
    public EnemySnapshot Render(double now, float dt, float baseDelay, float maximumDelay,
        float maxExtrapolation, float correctionBlend)
    {
        float wantedDelay = Mathf.Clamp(Mathf.Max(baseDelay, EstimatedInterval * 1.05f),
            baseDelay, Mathf.Max(baseDelay, maximumDelay));
        if (!renderInitialized) currentDelay = wantedDelay;
        else currentDelay = Mathf.MoveTowards(currentDelay, wantedDelay, Mathf.Max(0, dt) * 0.5f);
        double renderTime = now - currentDelay;
        if (renderInitialized)
        {
            // Changing cadence or NGO clock adjustment must not play motion backward.
            renderTime = Math.Max(lastRenderTime, renderTime);
            if (renderedRevision != revision)
            {
                // Re-evaluate the *previous* render instant with the new history.
                // Preserve the displayed pose there, then decay only its correction.
                EnemySnapshot revised = Sample(lastRenderTime, maxExtrapolation);
                correction += lastTarget.Position - revised.Position;
                yawCorrection = Mathf.DeltaAngle(0, yawCorrection +
                    Mathf.DeltaAngle(revised.Yaw, lastTarget.Yaw));
            }
        }
        float remaining = 1f - Mathf.Clamp01(correctionBlend);
        correction *= remaining;
        yawCorrection *= remaining;
        EnemySnapshot target = Sample(renderTime, maxExtrapolation);
        lastTarget = target;
        lastRenderTime = renderTime;
        renderedRevision = revision;
        renderInitialized = true;
        RenderDelay = Mathf.Max(0, (float)(now - renderTime));
        IsPredicting = renderTime > LatestTime;
        target.Position += correction;
        target.Yaw += yawCorrection;
        return target;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Numeric XZ membership. Steering samples; combat enumerates every candidate.</summary>
public sealed class EnemySpatialGrid
{
    private struct Cell
    {
        public int head, cursor, count;
    }

    private readonly Dictionary<Vector2Int, Cell> cells;
    private readonly Vector2Int[] keys;
    private readonly int[] next, previous, free;
    private readonly bool[] registered;
    private readonly Vector3[] positions;
    private readonly uint[] rayStamps;
    private uint rayStamp;
    private int freeCount;
    public float CellSize { get; }
    public int Count { get; private set; }
    public int CellCount => cells.Count;

    public EnemySpatialGrid(int capacity, float cellSize)
    {
        if (capacity <= 0 || float.IsNaN(cellSize) || float.IsInfinity(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException();
        CellSize = cellSize;
        cells = new Dictionary<Vector2Int, Cell>(capacity);
        keys = new Vector2Int[capacity];
        next = new int[capacity];
        previous = new int[capacity];
        free = new int[capacity];
        registered = new bool[capacity];
        positions = new Vector3[capacity];
        rayStamps = new uint[capacity];
        Clear();
    }

    public Vector2Int GetCell(Vector3 position) => new Vector2Int(
        Mathf.FloorToInt(position.x / CellSize), Mathf.FloorToInt(position.z / CellSize));

    public bool IsRegistered(int index) => index >= 0 && index < registered.Length && registered[index];
    public bool IsCellHead(int index) => IsRegistered(index) && cells[keys[index]].head == index;

    public int Add(Vector3 position)
    {
        if (freeCount == 0) return -1;
        int index = free[--freeCount];
        registered[index] = true;
        positions[index] = position;
        Insert(index, GetCell(position));
        Count++;
        return index;
    }

    public void UpdatePosition(int index, Vector3 position)
    {
        if (!IsRegistered(index)) return;
        positions[index] = position;
        Vector2Int key = GetCell(position);
        if (keys[index] == key) return;
        Unlink(index);
        Insert(index, key);
    }

    public bool Remove(int index)
    {
        if (!IsRegistered(index)) return false;
        Unlink(index);
        registered[index] = false;
        free[freeCount++] = index;
        Count--;
        return true;
    }

    private void Insert(int index, Vector2Int key)
    {
        keys[index] = key;
        if (!cells.TryGetValue(key, out Cell cell))
        {
            next[index] = previous[index] = index;
            cell = new Cell { head = index, cursor = index, count = 1 };
        }
        else
        {
            int tail = previous[cell.head];
            previous[index] = tail;
            next[index] = cell.head;
            next[tail] = index;
            previous[cell.head] = index;
            cell.count++;
        }
        cells[key] = cell;
    }

    private void Unlink(int index)
    {
        Vector2Int key = keys[index];
        Cell cell = cells[key];
        if (cell.count == 1) cells.Remove(key);
        else
        {
            next[previous[index]] = next[index];
            previous[next[index]] = previous[index];
            if (cell.head == index) cell.head = next[index];
            if (cell.cursor == index) cell.cursor = next[index];
            cell.count--;
            cells[key] = cell;
        }
    }

    /// <summary>Bounded rotating samples, including in an extremely crowded cell.</summary>
    public int QueryNearby(Vector3 position, int exclude, int[] results, int perCellBudget = 8)
    {
        if (results == null || results.Length == 0) return 0;
        Vector2Int center = GetCell(position);
        int count = 0;
        // Visit our cell first, then its eight immediate neighbors. Radius <= cell size.
        for (int order = 0; order < 9 && count < results.Length; order++)
        {
            int flattened = order == 0 ? 4 : order <= 4 ? order - 1 : order;
            Vector2Int key = center + new Vector2Int(flattened % 3 - 1, flattened / 3 - 1);
            if (!cells.TryGetValue(key, out Cell cell)) continue;
            int index = cell.cursor;
            int visits = Mathf.Min(cell.count, Mathf.Max(1, perCellBudget));
            for (int i = 0; i < visits && count < results.Length; i++)
            {
                if (index != exclude) results[count++] = index;
                index = next[index];
            }
            cell.cursor = index;
            cells[key] = cell;
        }
        return count;
    }

    public void Clear()
    {
        cells.Clear();
        Array.Clear(registered, 0, registered.Length);
        Count = 0;
        freeCount = free.Length;
        for (int i = 0; i < free.Length; i++) free[i] = free.Length - 1 - i;
    }

    public Vector3 Position(int index) => positions[index];

    /// <summary>Caller supplies capacity-sized storage. Never silently truncates combat.</summary>
    public int QueryRadius(Vector3 center, float radius, int[] results)
    {
        if (results == null || results.Length < registered.Length)
            throw new ArgumentException("Exhaustive queries require grid-capacity storage.");
        Vector2Int min = GetCell(center - new Vector3(radius, 0, radius));
        Vector2Int max = GetCell(center + new Vector3(radius, 0, radius));
        int count = 0;
        for (int z = min.y; z <= max.y; z++)
        for (int x = min.x; x <= max.x; x++)
        {
            if (!cells.TryGetValue(new Vector2Int(x, z), out Cell cell)) continue;
            int index = cell.head;
            for (int i = 0; i < cell.count; i++)
            {
                if ((positions[index] - center).sqrMagnitude <= radius * radius)
                    results[count++] = index;
                index = next[index];
            }
        }
        return count;
    }

    /// <summary>DDA through shot cells, expanded for capsule radius; no sampled victims.</summary>
    public bool RaycastCapsules(Vector3 origin, Vector3 direction, float range,
        float radius, float halfSegment, out int hitIndex, out float distance)
    {
        hitIndex = -1;
        distance = range;
        if (!EnemyProxyMath.Finite(origin) || !EnemyProxyMath.Finite(direction) ||
            float.IsNaN(range) || float.IsInfinity(range) || range < 0f) return false;
        if (++rayStamp == 0) { Array.Clear(rayStamps, 0, rayStamps.Length); rayStamp = 1; }
        Vector2Int cellKey = GetCell(origin);
        int sx = direction.x >= 0 ? 1 : -1, sz = direction.z >= 0 ? 1 : -1;
        float dx = Mathf.Abs(direction.x) > 0.000001f ? CellSize / Mathf.Abs(direction.x) : float.PositiveInfinity;
        float dz = Mathf.Abs(direction.z) > 0.000001f ? CellSize / Mathf.Abs(direction.z) : float.PositiveInfinity;
        float tx = float.IsInfinity(dx) ? dx :
            ((cellKey.x + (sx > 0 ? 1 : 0)) * CellSize - origin.x) / direction.x;
        float tz = float.IsInfinity(dz) ? dz :
            ((cellKey.y + (sz > 0 ? 1 : 0)) * CellSize - origin.z) / direction.z;
        int padding = Mathf.CeilToInt(radius / CellSize);
        int steps = Mathf.CeilToInt(range * (Mathf.Abs(direction.x) + Mathf.Abs(direction.z)) / CellSize) + 2;
        for (int step = 0; step < steps; step++)
        {
            for (int z = -padding; z <= padding; z++)
            for (int x = -padding; x <= padding; x++)
            {
                if (!cells.TryGetValue(cellKey + new Vector2Int(x, z), out Cell cell)) continue;
                int index = cell.head;
                for (int i = 0; i < cell.count; i++)
                {
                    if (rayStamps[index] != rayStamp)
                    {
                        rayStamps[index] = rayStamp;
                        if (EnemyProxyMath.RayCapsule(origin, direction, positions[index], radius,
                            halfSegment, distance, out float t)) { distance = t; hitIndex = index; }
                    }
                    index = next[index];
                }
            }
            if (Mathf.Min(tx, tz) > distance) break;
            if (tx < tz) { cellKey.x += sx; tx += dx; }
            else { cellKey.y += sz; tz += dz; }
        }
        return hitIndex >= 0;
    }
}

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Server-owned population and lifecycle. Not an AI controller.</summary>
[DisallowMultipleComponent]
public sealed class EnemyDirector : NetworkBehaviour
{
    public const int MaximumPopulationLimit = 10000;
    [SerializeField] private EnemyPool pool;
    [SerializeField] private KingHealth king;
    [SerializeField] private EnemyAIScheduler scheduler;
    [SerializeField] private EnemyTargetRegistry targetRegistry;
    [SerializeField] private EnemySimulation simulation;
    public bool UsesDataSimulation => simulation != null && simulation.enabled;
    public EnemyTargetRegistry Targets => targetRegistry;
    [SerializeField, Range(1, MaximumPopulationLimit)] private int populationLimit = 32;

    [Header("Horde spacing (server)")]
    [SerializeField, Min(0.1f)] private float separationRadius = 1.1f;
    [SerializeField, Range(0f, 0.9f)] private float separationStrength = 0.85f;
    [SerializeField, Range(1, 16)] private int maximumNeighbors = 12;
    [Tooltip("Clamped to at least Separation Radius so queries need only nine cells.")]
    [SerializeField, Min(0.1f)] private float gridCellSize = 1.1f;
    [Header("King approach rings")]
    [Tooltip("Distance between the King collider surface and the center of an enemy in the first ring.")]
    [SerializeField, Min(0.1f)] private float kingApproachPadding = 0.55f;
    [SerializeField, Min(0.1f)] private float attackSlotSpacing = 0.95f;
    [SerializeField, Min(1f)] private float approachDistance = 22f;
    [Header("Editor visualization")]
    [SerializeField] private bool drawHordeDebug;

    private EnemySpatialGrid spatialGrid;
    private EnemyAttackSlots kingSlots;
    private readonly EnemyAgent[] gridAgents = new EnemyAgent[MaximumPopulationLimit];
    private readonly int[] nearbyCandidates = new int[72];
    public float SeparationStrength => Mathf.Clamp(separationStrength, 0f, 0.9f);
    public float SeparationRadius => Mathf.Max(0.1f, separationRadius);
    public int SpatialCount => spatialGrid != null ? spatialGrid.Count : 0;
    public int ClaimedKingPositions => kingSlots != null ? kingSlots.ClaimedCount : 0;

    [Header("Diagnostics")]
    [SerializeField] private bool logEnemyCount = true;
    [SerializeField, Min(0.1f)] private float enemyCountLogInterval = 1f;
    private double nextEnemyCountLogTime;
    private int lastLoggedEnemyCount = -1;
    private int lastLoggedPopulationLimit = -1;

    // Sized for the eventual target; registration/removal allocate nothing.
    private readonly List<EnemyAgent> activeEnemies = new List<EnemyAgent>(MaximumPopulationLimit);
    private bool acceptingSpawns;
    public int ActiveCount => UsesDataSimulation ? simulation.Store.Count : activeEnemies.Count;
    public int PopulationLimit => Mathf.Clamp(populationLimit, 1, MaximumPopulationLimit);
    public KingHealth King => king;
    public bool IsReady => acceptingSpawns && isActiveAndEnabled && IsSpawned && IsServer &&
        !NetworkManager.ShutdownInProgress && king != null &&
        king.IsSpawned && king.health.Value > 0 && targetRegistry != null &&
        targetRegistry.King != null && targetRegistry.King.IsValid &&
        (UsesDataSimulation ? simulation.IsReady : pool != null && pool.IsReady && pool.Manager == NetworkManager &&
            scheduler != null && scheduler.IsSpawned && scheduler.IsServer && scheduler.isActiveAndEnabled);

    // Read-only indexed access for the future scheduler. Order is not stable on removal.
    public EnemyAgent GetActiveEnemy(int index) => activeEnemies[index];

    public override void OnNetworkSpawn()
    {
        acceptingSpawns = IsServer;
        if (IsServer && UsesDataSimulation) simulation.InitializeServer(this, targetRegistry);
        if (IsServer && !UsesDataSimulation)
        {
            spatialGrid = new EnemySpatialGrid(MaximumPopulationLimit, Mathf.Max(SeparationRadius, gridCellSize));
            Collider kingShape = king != null ? king.GetComponent<Collider>() : null;
            if (kingShape != null)
            {
                Bounds bounds = kingShape.bounds;
                Vector3 centerOffset = bounds.center - king.transform.position;
                kingSlots = new EnemyAttackSlots(MaximumPopulationLimit, centerOffset,
                    new Vector2(bounds.extents.x, bounds.extents.z), kingApproachPadding,
                    attackSlotSpacing);
            }
            else
                kingSlots = new EnemyAttackSlots(MaximumPopulationLimit,
                    kingApproachPadding, attackSlotSpacing);
            Array.Clear(gridAgents, 0, gridAgents.Length);
        }
        nextEnemyCountLogTime = 0;
        lastLoggedEnemyCount = lastLoggedPopulationLimit = -1;
        if (IsServer && (king == null || targetRegistry == null || (!UsesDataSimulation && (pool == null || scheduler == null))))
            Debug.LogError("EnemyDirector needs an enemy pool, King, scheduler and target registry reference.", this);
    }

    private void LateUpdate()
    {
        if (!logEnemyCount || !IsSpawned || !IsServer || NetworkManager.ShutdownInProgress)
            return;

        // Read the existing population; coalesce batch changes without scene searches.
        int count = ActiveCount;
        int limit = PopulationLimit;
        if ((count == lastLoggedEnemyCount && limit == lastLoggedPopulationLimit) ||
            Time.unscaledTimeAsDouble < nextEnemyCountLogTime)
            return;

        Debug.Log($"[EnemyDirector] Active enemies: {count} / {limit}", this);
        lastLoggedEnemyCount = count;
        lastLoggedPopulationLimit = limit;
        nextEnemyCountLogTime = Time.unscaledTimeAsDouble + Mathf.Max(0.1f, enemyCountLogInterval);
    }

    public bool SpawnEnemy(Vector3 position, Quaternion rotation, out EnemyAgent enemy)
    {
        enemy = null;
        if (UsesDataSimulation) return false; // Never create a second authoritative enemy path.
        if (!IsReady || activeEnemies.Count >= PopulationLimit ||
            !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
            !IsFinite(rotation.x) || !IsFinite(rotation.y) ||
            !IsFinite(rotation.z) || !IsFinite(rotation.w)) return false;

        EnemyAgent instance = null;
        try
        {
            instance = pool.AcquireServer(position, rotation, gameObject.scene);
            if (instance == null) return false;
            instance.InitializeServer(this, targetRegistry.King);
            if (!RegisterServer(instance))
            {
                pool.Release(instance);
                return false;
            }
            // Reserve population BEFORE spawn callbacks can make further spawn requests.
            instance.NetworkObject.Spawn(destroyWithScene: true);
            if (!instance.IsSpawned)
                throw new InvalidOperationException("Enemy network spawn did not complete.");
            enemy = instance;
            return true;
        }
        catch (Exception exception)
        {
            if (instance != null)
            {
                Unregister(instance);
                if (instance.IsSpawned) instance.NetworkObject.Despawn();
                else pool.Release(instance);
            }
            Debug.LogException(exception, this);
            return false;
        }
    }

    internal bool RegisterServer(EnemyAgent enemy)
    {
        if (!IsReady || enemy == null || enemy.Director != this) return false;
        if (enemy.DirectorIndex >= 0)
            return enemy.DirectorIndex < activeEnemies.Count && activeEnemies[enemy.DirectorIndex] == enemy;
        if (activeEnemies.Count >= PopulationLimit) return false;
        int gridIndex = spatialGrid.Add(enemy.transform.position);
        if (gridIndex < 0) return false;
        if (!scheduler.RegisterServer(enemy))
        {
            spatialGrid.Remove(gridIndex);
            return false;
        }
        enemy.GridIndex = gridIndex;
        gridAgents[gridIndex] = enemy;
        enemy.DirectorIndex = activeEnemies.Count;
        activeEnemies.Add(enemy);
        return true;
    }

    internal void Unregister(EnemyAgent enemy)
    {
        // Also called during shutdown, when NGO may already have cleared IsServer.
        if (enemy == null || enemy.Director != this) return;
        int index = enemy.DirectorIndex;
        if (index < 0 || index >= activeEnemies.Count || activeEnemies[index] != enemy) return;
        ReleaseKingPosition(enemy);
        if (spatialGrid != null && spatialGrid.Remove(enemy.GridIndex)) gridAgents[enemy.GridIndex] = null;
        enemy.GridIndex = -1;
        int last = activeEnemies.Count - 1;
        EnemyAgent moved = activeEnemies[last];
        activeEnemies[index] = moved;
        moved.DirectorIndex = index;
        activeEnemies.RemoveAt(last);
        enemy.DetachDirector();
    }

    public void DespawnEnemy(EnemyAgent enemy)
    {
        if (!IsServer || enemy == null || enemy.Director != this) return;
        Unregister(enemy);
        // Despawn(true) reaches NGO's registered Destroy handler on ALL peers.
        // Do not also return it here: the handler is the sole successful-despawn release.
        if (enemy.IsSpawned) enemy.NetworkObject.Despawn(true);
        else pool.Release(enemy);
    }

    internal void UpdateSpatialPosition(EnemyAgent enemy)
    {
        if (spatialGrid != null && enemy.Director == this)
            spatialGrid.UpdatePosition(enemy.GridIndex, enemy.transform.position);
    }

    internal Vector3 CalculateSeparation(EnemyAgent enemy)
    {
        if (spatialGrid == null || SeparationStrength <= 0f) return Vector3.zero;
        Vector3 position = enemy.transform.position;
        int count = spatialGrid.QueryNearby(position, enemy.GridIndex, nearbyCandidates);
        int accepted = 0;
        Vector3 separation = Vector3.zero;
        int limit = Mathf.Clamp(maximumNeighbors, 1, 16);
        float radius = Mathf.Min(SeparationRadius, spatialGrid.CellSize);
        for (int i = 0; i < count && accepted < limit; i++)
        {
            EnemyAgent neighbor = gridAgents[nearbyCandidates[i]];
            if (neighbor == null || !neighbor.IsSpawned || !neighbor.isActiveAndEnabled) continue;
            Vector3 push = EnemySteering.Repulsion(position, neighbor.transform.position,
                enemy.GridIndex, neighbor.GridIndex, radius);
            if (push.sqrMagnitude <= 0f) continue;
            separation += push;
            accepted++;
        }
        return Vector3.ClampMagnitude(separation, 1f);
    }

    internal bool TryGetKingApproach(EnemyAgent enemy, out Vector3 destination, out bool innerRing)
    {
        destination = enemy.Target.Position;
        innerRing = false;
        Vector3 offset = enemy.transform.position - destination;
        offset.y = 0f;
        float distance = Mathf.Max(approachDistance, kingSlots.OuterRadius + attackSlotSpacing);
        if (enemy.KingSlot < 0 && offset.sqrMagnitude > distance * distance) return false;
        enemy.KingSlot = kingSlots.Promote(enemy.KingSlot, offset);
        if (enemy.KingSlot < 0) return false;
        Vector3 approachOffset = kingSlots.GetOffset(enemy.KingSlot);
        innerRing = kingSlots.IsInnerRing(enemy.KingSlot);
        destination += approachOffset;
        return true;
    }

    internal void ReleaseKingPosition(EnemyAgent enemy)
    {
        if (kingSlots != null) kingSlots.Release(enemy.KingSlot);
        enemy.KingSlot = -1;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!drawHordeDebug || spatialGrid == null || !IsServer) return;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.4f);
        for (int i = 0; i < activeEnemies.Count; i++)
        {
            if (!spatialGrid.IsCellHead(activeEnemies[i].GridIndex)) continue;
            Vector3 position = activeEnemies[i].transform.position;
            Vector2Int cell = spatialGrid.GetCell(position);
            // Each occupied cell is drawn once; no temporary debug collection.
            Gizmos.DrawWireCube(new Vector3((cell.x + 0.5f) * spatialGrid.CellSize,
                position.y, (cell.y + 0.5f) * spatialGrid.CellSize),
                new Vector3(spatialGrid.CellSize, 0.02f, spatialGrid.CellSize));
        }
    }

    internal void DrawEnemyDebug(EnemyAgent enemy, EnemyMotor motor)
    {
        if (!drawHordeDebug || !IsServer || motor == null) return;
        Vector3 position = enemy.transform.position;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(position, SeparationRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawLine(position, position + motor.TargetDirection * 2f);
        Gizmos.color = Color.red;
        Gizmos.DrawLine(position, position + motor.SeparationDirection * 2f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(position, position + motor.SteeringDirection * 2f);
        if (enemy.KingSlot >= 0 && kingSlots != null && king != null)
        {
            Gizmos.color = Color.magenta;
            Vector3 claim = king.transform.position + kingSlots.GetOffset(enemy.KingSlot);
            Gizmos.DrawWireSphere(claim, 0.15f);
            Gizmos.DrawLine(position, claim);
        }
    }
#endif

    public override void OnNetworkDespawn()
    {
        acceptingSpawns = false;
        if (UsesDataSimulation) simulation.Clear();
        while (activeEnemies.Count > 0)
        {
            EnemyAgent enemy = activeEnemies[activeEnemies.Count - 1];
            if (enemy == null)
            {
                activeEnemies.RemoveAt(activeEnemies.Count - 1);
                continue;
            }
            // Detach first: enemy despawn callbacks cannot mutate this entry again.
            Unregister(enemy);
            if (IsServer && !NetworkManager.ShutdownInProgress && enemy.IsSpawned)
                enemy.NetworkObject.Despawn();
        }
        spatialGrid?.Clear();
        kingSlots?.Clear();
        Array.Clear(gridAgents, 0, gridAgents.Length);
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public bool SpawnEnemy(Vector3 position, Quaternion rotation, out uint enemyId)
    {
        enemyId = 0;
        return UsesDataSimulation && IsReady && simulation.SpawnServer(position, rotation, out enemyId);
    }
    public void ClearDataEnemies() { if (IsServer && UsesDataSimulation) simulation.Clear(); }
}

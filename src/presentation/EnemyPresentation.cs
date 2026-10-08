using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;

/// <summary>Pooled visual-only views on host and clients. Never writes authoritative state.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
public sealed class EnemyPresentation : MonoBehaviour
{
    [SerializeField] private EnemyDefinition definition;
    [SerializeField, Range(0.02f, 0.3f)] private float interpolationDelay = 0.12f;
    [Tooltip("Maximum extra buffering as per-enemy updates become sparse. Does not raise network traffic.")]
    [SerializeField, Range(0.12f, 0.6f)] private float maximumInterpolationDelay = 0.35f;
    [Tooltip("Blend prediction corrections, not ordinary movement. Large teleports still snap.")]
    [SerializeField, Range(0.01f, 0.3f)] private float correctionBlendSeconds = 0.08f;
    [SerializeField, Range(0f, 0.5f)] private float maximumPredictionSeconds = 0.3f;
    [SerializeField, Range(1, 256)] private int viewCreationsPerFrame = 32;
    [SerializeField, Min(0)] private int prewarmViews = 64;
    public EnemyStateStore Roster { get; private set; }
    public double LastPresentationMs { get; private set; }
    public double OldestSampleAge { get; private set; }
    public float AverageSnapshotInterval { get; private set; }
    public float AverageRenderDelay { get; private set; }
    public int PredictedCount { get; private set; }
    public int VisibleCount { get; private set; }
    private NetworkManager manager;
    private EnemySimulation simulation;
    private EnemyBenchmark benchmark;
    private EnemySnapshotTrack[] tracks;
    private Transform[] views;
    private readonly Stack<Transform> available = new Stack<Transform>(EnemyDirector.MaximumPopulationLimit);
    private readonly int[] neighbors = new int[72];
    private Transform root;

    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        simulation = GetComponent<EnemySimulation>();
        benchmark = GetComponent<EnemyBenchmark>();
        Roster = new EnemyStateStore(EnemyDirector.MaximumPopulationLimit, 4f);
        tracks = new EnemySnapshotTrack[Roster.Capacity];
        views = new Transform[Roster.Capacity];
        root = new GameObject("Enemy presentation (no gameplay components)").transform;
        root.SetParent(transform, false);
        if (definition == null || definition.presentationPrefab == null) return;
        GameObject prefab = definition.presentationPrefab;
        // Refuse network/physics prefabs before Instantiate can execute their Awake.
        if (prefab.GetComponentInChildren<NetworkObject>(true) != null ||
            prefab.GetComponentInChildren<Collider>(true) != null ||
            prefab.GetComponentInChildren<Rigidbody>(true) != null)
        { UnityEngine.Debug.LogError("Enemy presentation prefab must contain only visuals.", this); enabled = false; return; }
        for (int i = 0; i < prewarmViews; i++) available.Push(CreateView());
        simulation.Spawned += OnHostSpawn;
        simulation.Removed += OnHostRemove;
        manager.OnClientStopped += OnStopped;
        manager.OnServerStopped += OnStopped;
    }
    private Transform CreateView()
    {
        GameObject view = Instantiate(definition.presentationPrefab, root);
        view.SetActive(false);
        return view.transform;
    }
    private void OnHostSpawn(EnemySnapshot snapshot)
    { if (manager.IsServer) Spawn(snapshot, manager.ServerTime.Time); }
    private void OnHostRemove(uint id, bool died) { if (manager.IsServer) Remove(id); }
    public void Spawn(EnemySnapshot snapshot, double time)
    {
        if (!Roster.AddReplica(snapshot)) return;
        Roster.TryGetSlot(snapshot.Id, out int slot);
        tracks[slot] = default;
        tracks[slot].Push(snapshot, time);
    }
    public void Apply(EnemySnapshot snapshot, double time)
    {
        // Motion never creates an enemy: a delayed packet cannot resurrect a dead ID.
        if (Roster.TryGetSlot(snapshot.Id, out int slot)) tracks[slot].Push(snapshot, time);
    }
    public uint Raycast(Vector3 origin, Vector3 direction, float range)
    {
        if (definition == null || !Roster.Grid.RaycastCapsules(origin, direction, range,
            definition.hitRadius, definition.hitHalfSegment, out int index, out _)) return 0;
        return Roster.States[Roster.SlotFromGrid(index)].Id;
    }
    public void Remove(uint id)
    {
        if (!Roster.TryGetSlot(id, out int slot)) return;
        Transform view = views[slot];
        if (view != null) { view.gameObject.SetActive(false); available.Push(view); views[slot] = null; }
        tracks[slot] = default;
        Roster.Remove(id, out _);
    }
    public void ResetRoster()
    {
        while (Roster.Count > 0) Remove(Roster.States[Roster.ActiveSlots[Roster.Count - 1]].Id);
        VisibleCount = 0;
        OldestSampleAge = 0;
        AverageSnapshotInterval = AverageRenderDelay = 0;
        PredictedCount = 0;
    }
    public void CaptureHost(double now)
    {
        if (!manager.IsServer) return;
        for (int i = 0; i < simulation.Store.Count; i++)
            Apply(EnemySnapshot.From(simulation.Store.States[simulation.Store.ActiveSlots[i]]), now);
    }
    private void LateUpdate()
    {
        if (!manager.IsListening || definition == null) return;
        long start = benchmark != null && benchmark.Enabled ? Stopwatch.GetTimestamp() : 0;
        double now = manager.ServerTime.Time;
        float dt = Time.unscaledDeltaTime;
        float delay = manager.IsServer ? 1f / 30 : interpolationDelay;
        float maximumDelay = manager.IsServer ? delay : maximumInterpolationDelay;
        // One exponential per frame, not per enemy. Host playback needs no correction filter.
        float blend = manager.IsServer ? 1f : 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, correctionBlendSeconds));
        int budget = viewCreationsPerFrame;
        VisibleCount = 0;
        OldestSampleAge = 0;
        AverageSnapshotInterval = AverageRenderDelay = 0;
        PredictedCount = 0;
        for (int i = 0; i < Roster.Count; i++)
        {
            int slot = Roster.ActiveSlots[i];
            EnemySnapshot sample = tracks[slot].Render(now, dt, delay, maximumDelay,
                maximumPredictionSeconds, blend);
            Roster.Move(slot, sample.Position);
            Roster.States[slot].Health = sample.Health;
            OldestSampleAge = System.Math.Max(OldestSampleAge, now - tracks[slot].LatestTime);
            AverageSnapshotInterval += tracks[slot].EstimatedInterval;
            AverageRenderDelay += tracks[slot].RenderDelay;
            if (tracks[slot].IsPredicting) PredictedCount++;
            if (views[slot] == null)
            {
                if (available.Count == 0 && budget-- <= 0) continue;
                views[slot] = available.Count > 0 ? available.Pop() : CreateView();
                views[slot].gameObject.SetActive(true);
            }
            views[slot].SetPositionAndRotation(sample.Position, Quaternion.Euler(0, sample.Yaw, 0));
            VisibleCount++;
        }
        if (Roster.Count > 0) {
            AverageSnapshotInterval /= Roster.Count;
            AverageRenderDelay /= Roster.Count;
        }
        LastPresentationMs = start == 0 ? 0 : (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }
    public Vector3 ResolvePlayerMotion(Vector3 center, Vector3 displacement, float radius, float halfHeight)
    {
        if (definition == null) return displacement;
        Vector3 requested = displacement;
        EnemyStateStore store = manager.IsServer && simulation.IsReady ? simulation.Store : Roster;
        Vector3 candidate = center + displacement;
        int count = store.Grid.QueryNearby(candidate, -1, neighbors);
        float contactRadius = radius + definition.hitRadius * 0.9f;
        for (int i = 0; i < count; i++)
        {
            Vector3 enemy = store.Grid.Position(neighbors[i]);
            if (Mathf.Abs(candidate.y - enemy.y) > halfHeight + radius +
                definition.hitHalfSegment + definition.hitRadius) continue;
            Vector3 offset = candidate - enemy; offset.y = 0;
            float distance = offset.magnitude;
            if (distance >= contactRadius) continue;
            Vector3 normal = distance > 0.0001f ? offset / distance : Vector3.right;
            float inward = Vector3.Dot(new Vector3(displacement.x, 0, displacement.z), normal);
            if (inward < 0) displacement -= normal * inward;
            displacement += normal * Mathf.Min(0.08f, contactRadius - distance);
            candidate = center + displacement;
        }
        // Approximate contact is deliberately soft and bounded, not a rigid crowd solver.
        return requested + Vector3.ClampMagnitude(displacement - requested, 0.25f);
    }
    private void OnStopped(bool wasHost) => ResetRoster();
    private void OnDestroy()
    {
        if (simulation != null) { simulation.Spawned -= OnHostSpawn; simulation.Removed -= OnHostRemove; }
        if (manager != null) { manager.OnClientStopped -= OnStopped; manager.OnServerStopped -= OnStopped; }
    }
}

using System;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;

/// <summary>One host-owned numeric melee simulation. No enemy GameObject is gameplay state.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
public sealed class EnemySimulation : MonoBehaviour
{
    [SerializeField] private EnemyDefinition definition;
    [SerializeField, Range(10, 60)] private float movementHz = 30;
    [SerializeField, Range(1, 20)] private float decisionHz = 5;
    [SerializeField, Min(1)] private int decisionsPerFrame = 512;
    [SerializeField] private LayerMask groundMask = 1;
    [SerializeField] private LayerMask wallMask = 256;
    [SerializeField, Min(0.5f)] private float cellSize = 4f;
    [Header("Ground following")]
    [SerializeField, Min(1f)] private float spawnGroundProbeHeight = 100f;
    [SerializeField, Min(1f)] private float spawnGroundProbeDepth = 200f;
    [SerializeField, Min(0.1f)] private float groundProbeHeight = 2f;
    [SerializeField, Min(0.1f)] private float groundProbeDepth = 12f;
    [SerializeField, Min(0.1f)] private float groundCorrectionSpeed = 12f;
    [SerializeField, Min(0.1f)] private float ungroundedFallSpeed = 12f;
    [SerializeField, Range(0f, 1f)] private float minimumGroundNormalY = 0.5f;
    [Header("Soft crowd spacing")]
    [SerializeField, Min(0f)] private float separationPadding = 0.15f;
    [SerializeField, Range(0f, 3f)] private float separationStrength = 1.5f;
    [Header("Breach response (server)")]
    [SerializeField] private bool respondToBreaches = true;
    [SerializeField, Min(0.5f)] private float breachAwarenessRadius = 16f;
    [SerializeField, Min(0f)] private float maximumBreachDetour = 12f;
    [SerializeField, Range(-1f, 1f)] private float minimumBreachForwardDot = -0.1f;
    [SerializeField, Range(0.05f, 0.5f)] private float breachArrivalDistance = 0.3f;
    [SerializeField, Min(0.03f)] private float breachClearance = 0.15f;
    [SerializeField, Min(0.2f)] private float breachReconsiderInterval = 1f;
    [Tooltip("Shared limit across all scheduled AI, not a budget for each enemy.")]
    [SerializeField, Range(8, 256)] private int maximumBreachQueriesPerFrame = 64;
    public EnemyDefinition Definition => definition;
    public EnemyStateStore Store { get; private set; }
    public bool IsReady => initialized && manager.IsServer && manager.IsListening &&
        !manager.ShutdownInProgress && definition != null;
    public double LastSimulationMs { get; private set; }
    public int LastDecisions { get; private set; }
    public int LastMovementSteps { get; private set; }
    public int LastBreachQueries { get; private set; }
    public int ActiveBreachRoutes { get; private set; }
    public event Action<EnemySnapshot> Spawned;
    public event Action<uint, bool> Removed;
    private NetworkManager manager;
    private EnemyDirector director;
    private EnemyTargetRegistry targets;
    private EnemyPresentation presentation;
    private EnemyRewardBank rewards;
    private EnemyBenchmark benchmark;
    private EnemyAttackSlots slots;
    private readonly EnemyTargetable[] target = new EnemyTargetable[EnemyDirector.MaximumPopulationLimit];
    private readonly EnemyTargetable[] attacker = new EnemyTargetable[EnemyDirector.MaximumPopulationLimit];
    private readonly double[] attackerUntil = new double[EnemyDirector.MaximumPopulationLimit];
    private readonly BuildWall[] blocker = new BuildWall[EnemyDirector.MaximumPopulationLimit];
    private readonly Vector3[] heading = new Vector3[EnemyDirector.MaximumPopulationLimit];
    private readonly float[] desiredSpeed = new float[EnemyDirector.MaximumPopulationLimit];
    private readonly float[] travel = new float[EnemyDirector.MaximumPopulationLimit];
    private readonly uint[] geometry = new uint[EnemyDirector.MaximumPopulationLimit];
    private readonly float[] groundHeight = new float[EnemyDirector.MaximumPopulationLimit];
    private readonly bool[] grounded = new bool[EnemyDirector.MaximumPopulationLimit];
    private readonly int[] neighbors = new int[72];
    private readonly RaycastHit[] groundHits = new RaycastHit[16];
    private struct BreachRoute
    {
        public uint Id, CheckedVersion, CheckedGeometry;
        public Vector3 Approach, Exit, Goal;
        public EnemyTargetable Target;
        public double NextCheck;
        public bool Crossing, HasChecked;
    }
    private readonly BreachRoute[] breachRoutes = new BreachRoute[EnemyDirector.MaximumPopulationLimit];
    private int breachQueriesRemaining;
    private int cursor;
    private double movementCredit, decisionCredit;
    private bool initialized;

    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        presentation = GetComponent<EnemyPresentation>();
        rewards = GetComponent<EnemyRewardBank>();
        benchmark = GetComponent<EnemyBenchmark>();
        Store = new EnemyStateStore(EnemyDirector.MaximumPopulationLimit, Mathf.Max(1f, cellSize));
        manager.OnServerStopped += OnStopped;
        breachQueriesRemaining = maximumBreachQueriesPerFrame;
    }

    public void InitializeServer(EnemyDirector owner, EnemyTargetRegistry registry)
    {
        if (!manager.IsServer || definition == null) return;
        Clear();
        director = owner;
        targets = registry;
        Collider shape = owner.King != null ? owner.King.GetComponent<Collider>() : null;
        slots = shape != null
            ? new EnemyAttackSlots(Store.Capacity, shape.bounds.center - owner.King.transform.position,
                new Vector2(shape.bounds.extents.x, shape.bounds.extents.z), 0.55f, 0.95f)
            : new EnemyAttackSlots(Store.Capacity, 1.5f, 0.95f);
        initialized = true;
    }

    public bool SpawnServer(Vector3 position, Quaternion rotation, out uint id)
    {
        id = 0;
        if (!IsReady || Store.Count >= director.PopulationLimit ||
            !Store.Spawn(position, rotation.eulerAngles.y, definition.maxHealth, out id)) return false;
        Store.TryGetSlot(id, out int slot);
        target[slot] = targets.King;
        attacker[slot] = null;
        blocker[slot] = null;
        heading[slot] = Vector3.zero;
        desiredSpeed[slot] = 0;
        travel[slot] = 0;
        geometry[slot] = uint.MaxValue;
        groundHeight[slot] = position.y;
        grounded[slot] = false;
        breachRoutes[slot] = default;
        // Spawn sources may use the King's pivot or an airborne player's height.
        // Correct before publishing the first snapshot, not after the view appears.
        Ground(slot, true);
        if (grounded[slot]) {
            position.y = groundHeight[slot];
            Store.Move(slot, position);
        }
        // Phase offsets keep tiny encounters detailed without synchronizing every decision.
        Store.States[slot].NextDecision = manager.ServerTime.Time + (id % 16) / (16.0 * decisionHz);
        Spawned?.Invoke(EnemySnapshot.From(Store.States[slot]));
        return true;
    }

    private void Update()
    {
        LastDecisions = LastMovementSteps = 0;
        LastBreachQueries = 0;
        breachQueriesRemaining = Mathf.Max(8, maximumBreachQueriesPerFrame);
        if (!IsReady) return;
        long start = benchmark != null && benchmark.Enabled ? Stopwatch.GetTimestamp() : 0;
        double now = manager.ServerTime.Time;
        int count = Store.Count;
        int budget = Mathf.Max(1, decisionsPerFrame);
        decisionCredit = Math.Min(budget, decisionCredit + count * decisionHz * Time.deltaTime);
        int visits = (int)decisionCredit;
        decisionCredit -= visits;
        for (int i = 0; i < visits && Store.Count > 0; i++)
        {
            if (cursor >= Store.Count) cursor = 0;
            int slot = Store.ActiveSlots[cursor++];
            ref EnemyState state = ref Store.States[slot];
            if (now + 0.5 / movementHz < state.NextDecision) continue;
            state.NextDecision = Math.Max(now + 0.1 / decisionHz, state.NextDecision + 1.0 / decisionHz);
            Think(slot, now);
            LastDecisions++;
        }
        double step = 1.0 / movementHz;
        movementCredit = Math.Min(step * 2, movementCredit + Time.deltaTime);
        while (movementCredit >= step)
        {
            movementCredit -= step;
            for (int i = 0; i < Store.Count; i++) Move(Store.ActiveSlots[i], (float)step);
            LastMovementSteps++;
        }
        if (presentation != null && LastMovementSteps > 0) presentation.CaptureHost(now);
        LastSimulationMs = start == 0 ? 0 : (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }

    private float Score(EnemyTargetable candidate, int slot, double now)
    {
        if (candidate == null || !candidate.IsValid || candidate.Registry != targets) return 0;
        if (candidate.Kind == EnemyTargetable.TargetKind.King) return 1;
        float d = (candidate.Position - Store.States[slot].Position).sqrMagnitude;
        if (d > 225f) return 0;
        float nearby = candidate == target[slot] ? 144f : 100f;
        float score = d <= nearby ? 1.5f + Mathf.Max(0, 1 - d / 100f) : 0;
        if (candidate == attacker[slot] && now < attackerUntil[slot]) score += 3;
        return score;
    }

    private void Think(int slot, double now)
    {
        ref EnemyState state = ref Store.States[slot];
        Ground(slot);
        EnemyTargetable selected = target[slot];
        float best = Score(selected, slot, now);
        if (best > 0) best += 0.3f;
        Consider(targets.King, slot, now, ref selected, ref best);
        for (int i = 0; i < targets.PlayerCount; i++)
            Consider(targets.GetPlayer(i), slot, now, ref selected, ref best);
        target[slot] = best > 0 ? selected : null;
        if (target[slot] == null) {
            ClearBreachRoute(slot, now);
            Stop(ref state); desiredSpeed[slot] = 0; ReleaseSlot(ref state); return;
        }
        selected = target[slot];
        if (blocker[slot] != null && !blocker[slot].IsAlive) blocker[slot] = null;
        bool followingBreach = TryBreachDestination(slot, now, out Vector3 breachDestination);
        if (!followingBreach && blocker[slot] != null &&
            (blocker[slot].Shape.ClosestPoint(state.Position) - state.Position).sqrMagnitude <=
                definition.wallAttackRange * definition.wallAttackRange)
        {
            state.Behavior = EnemyBehavior.AttackWall;
            state.Velocity = Vector3.zero;
            desiredSpeed[slot] = 0;
            if (now >= state.NextAttack) {
                state.NextAttack = now + definition.attackCooldown;
                blocker[slot].Health.TakeDamage(definition.damage);
            }
            return;
        }
        Vector3 destination = followingBreach ? breachDestination : selected.Position;
        bool atSlot = !followingBreach;
        if (!followingBreach && selected.Kind == EnemyTargetable.TargetKind.King)
        {
            state.KingSlot = slots.Promote(state.KingSlot, state.Position - destination);
            if (state.KingSlot >= 0) destination += slots.GetOffset(state.KingSlot);
            Vector3 approach = destination - state.Position; approach.y = 0;
            atSlot = approach.sqrMagnitude < 0.0225f;
        }
        else ReleaseSlot(ref state);
        Vector3 point = selected.Kind == EnemyTargetable.TargetKind.King
            ? selected.ClosestPoint(state.Position) : selected.Position;
        bool attack = !followingBreach && atSlot && (point - state.Position).sqrMagnitude <=
            definition.attackRange * definition.attackRange;
        Vector3 offset = destination - state.Position; offset.y = 0;
        state.Destination = destination;
        // Player pivots are often at their feet, while enemy state uses capsule centers.
        // Match the planar stop distance to the existing 3D attack-range test.
        float verticalOffset = point.y - state.Position.y;
        state.StopDistance = followingBreach ? 0.05f : state.KingSlot >= 0 ? 0.06f : Mathf.Max(0,
            Mathf.Sqrt(Mathf.Max(0, definition.attackRange * definition.attackRange -
                verticalOffset * verticalOffset)) - 0.05f);
        Vector3 pursuit = !attack && offset.sqrMagnitude > state.StopDistance * state.StopDistance
            ? offset.normalized : Vector3.zero;
        Vector3 crowdVelocity = GetCrowdVelocity(slot, pursuit);
        desiredSpeed[slot] = crowdVelocity.magnitude;
        Vector3 desiredHeading = crowdVelocity.normalized;
        // Cached straight corridor, not a full navigation system. Walls remain solid.
        bool needsProbe = BuildWall.ActiveServerCount > 0 && desiredSpeed[slot] > 0.001f &&
            (geometry[slot] != BuildWall.GeometryVersion || travel[slot] <= 0.75f ||
                Vector3.Dot(heading[slot].normalized, desiredHeading) < 0.978f);
        if (needsProbe)
        {
            heading[slot] = desiredHeading;
            // Do not certify beyond a planned turn at the approach/exit waypoint.
            float probeDistance = followingBreach ? Mathf.Min(4f, offset.magnitude) : 4f;
            if (Physics.CapsuleCast(state.Position + Vector3.up * definition.hitHalfSegment,
                state.Position - Vector3.up * definition.hitHalfSegment, definition.hitRadius,
                desiredHeading, out RaycastHit wallHit, probeDistance, wallMask, QueryTriggerInteraction.Ignore))
            {
                blocker[slot] = wallHit.collider.GetComponent<BuildWall>();
                travel[slot] = Mathf.Max(0, wallHit.distance - 0.03f);
                attack = false;
                if (followingBreach && blocker[slot] != null) {
                    BuildWall found = blocker[slot];
                    ClearBreachRoute(slot, now);
                    blocker[slot] = found;
                }
            }
            else { blocker[slot] = null; travel[slot] = probeDistance; }
            geometry[slot] = BuildWall.GeometryVersion;
        }
        if (BuildWall.ActiveServerCount == 0) heading[slot] = desiredHeading;
        // Attackers face their target even when crowd pressure moves them sideways.
        Vector3 facing = offset.sqrMagnitude > 0.0001f ? offset : desiredHeading;
        state.Yaw = facing.sqrMagnitude > 0.0001f ? Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg : state.Yaw;
        if (attack && blocker[slot] == null)
        {
            state.Behavior = EnemyBehavior.Attack;
            if (now >= state.NextAttack) {
                state.NextAttack = now + definition.attackCooldown;
                selected.TakeDamageServer(definition.damage);
            }
        }
        else state.Behavior = EnemyBehavior.Chase;
    }

    private void Consider(EnemyTargetable candidate, int slot, double now,
        ref EnemyTargetable best, ref float score)
    {
        float value = Score(candidate, slot, now);
        if (value > score) { best = candidate; score = value; }
    }

    private void ClearBreachRoute(int slot, double now)
    {
        ref BreachRoute route = ref breachRoutes[slot];
        if (route.Id != 0)
        {
            route.Id = 0;
            ActiveBreachRoutes--;
            // Re-evaluate the normal target corridor; no global horde invalidation.
            blocker[slot] = null;
            travel[slot] = 0;
            geometry[slot] = uint.MaxValue;
        }
        route.Crossing = false;
        route.NextCheck = now + Mathf.Max(0.2f, breachReconsiderInterval);
    }

    private bool BreachPathClear(Vector3 start, Vector3 end)
    {
        end.y = start.y;
        Vector3 offset = end - start;
        breachQueriesRemaining--;
        LastBreachQueries++;
        if (Physics.CapsuleCast(start + Vector3.up * definition.hitHalfSegment,
            start - Vector3.up * definition.hitHalfSegment, definition.hitRadius,
            offset.normalized, offset.magnitude, wallMask, QueryTriggerInteraction.Ignore)) return false;
        breachQueriesRemaining--;
        LastBreachQueries++;
        // CapsuleCast alone does not detect an endpoint embedded in a newly placed wall.
        return !Physics.CheckCapsule(end + Vector3.up * definition.hitHalfSegment,
            end - Vector3.up * definition.hitHalfSegment, definition.hitRadius,
            wallMask, QueryTriggerInteraction.Ignore);
    }

    private bool TryBreachDestination(int slot, double now, out Vector3 destination)
    {
        destination = default;
        ref EnemyState state = ref Store.States[slot];
        ref BreachRoute route = ref breachRoutes[slot];
        EnemyTargetable selected = target[slot];
        Vector3 goal = selected.Position;
        if (!respondToBreaches || BuildWall.ActiveServerCount == 0 ||
            (selected.Kind == EnemyTargetable.TargetKind.Player &&
                (goal - state.Position).sqrMagnitude <= definition.attackRange * definition.attackRange))
        { ClearBreachRoute(slot, now); return false; }
        bool changedTarget = selected != route.Target || (goal - route.Goal).sqrMagnitude > 9;
        if (changedTarget) {
            ClearBreachRoute(slot, now);
            route.HasChecked = false;
        }
        if (route.Id != 0)
        {
            if (!EnemyBreachTracker.TryGetOpening(route.Id, now, out EnemyBreachTracker.Opening opening) ||
                Vector3.Dot(goal - opening.Position, opening.Normal) *
                    Vector3.Dot(route.Approach - opening.Position, opening.Normal) >= 0) {
                ClearBreachRoute(slot, now);
                return false;
            }
            if (route.CheckedGeometry != BuildWall.GeometryVersion)
            {
                if (breachQueriesRemaining < 6) {
                    destination = state.Position; // Wait safely for a shared validation slot.
                    return true;
                }
                Vector3 waypoint = route.Crossing ? route.Exit : route.Approach;
                if (!BreachPathClear(state.Position, waypoint) ||
                    (!route.Crossing && !BreachPathClear(route.Approach, route.Exit)) ||
                    !BreachPathClear(route.Exit, goal)) {
                    ClearBreachRoute(slot, now);
                    return false;
                }
                route.CheckedGeometry = BuildWall.GeometryVersion;
            }
            Vector3 remaining = (route.Crossing ? route.Exit : route.Approach) - state.Position;
            remaining.y = 0;
            if (remaining.sqrMagnitude <= breachArrivalDistance * breachArrivalDistance)
            {
                if (route.Crossing) {
                    ClearBreachRoute(slot, now);
                    return false;
                }
                // Arrival tolerance can leave us a little short of the approach point.
                // Validate the actual crossing rather than cutting a neighboring corner.
                if (breachQueriesRemaining < 2 ||
                    !BreachPathClear(state.Position, route.Exit)) {
                    destination = route.Approach;
                    return true;
                }
                route.Crossing = true;
                travel[slot] = 0; // Certify the exit heading, not the previous approach heading.
            }
            destination = route.Crossing ? route.Exit : route.Approach;
            destination.y = state.Position.y;
            return true;
        }
        if (!EnemyBreachTracker.HasOpenings(now)) return false;
        uint version = EnemyBreachTracker.Version;
        if (route.HasChecked && route.CheckedVersion == version && now < route.NextCheck) return false;
        if (!EnemyBreachTracker.TryFindRoute(state.Position, goal, state.Id, now,
            Mathf.Max(0.5f, breachAwarenessRadius), Mathf.Max(0, maximumBreachDetour),
            minimumBreachForwardDot, definition.hitRadius, definition.hitHalfSegment + definition.hitRadius,
            Mathf.Max(0.03f, breachClearance), out uint id, out Vector3 approach, out Vector3 exit))
        {
            route.HasChecked = true; route.CheckedVersion = version;
            route.Target = selected; route.Goal = goal;
            route.NextCheck = now + Mathf.Max(0.2f, breachReconsiderInterval);
            return false;
        }
        // Up to four legs, two queries each. Budget exhaustion leaves the request
        // eligible on its next scheduled tick rather than broadcasting a synchronous retry.
        if (breachQueriesRemaining < 8) return false;
        route.HasChecked = true; route.CheckedVersion = version;
        route.Target = selected; route.Goal = goal;
        route.NextCheck = now + Mathf.Max(0.2f, breachReconsiderInterval);
        if (BreachPathClear(state.Position, goal) || !BreachPathClear(state.Position, approach) ||
            !BreachPathClear(approach, exit) || !BreachPathClear(exit, goal)) return false;
        route.Id = id; route.Approach = approach; route.Exit = exit;
        route.Crossing = false; route.CheckedGeometry = BuildWall.GeometryVersion;
        ActiveBreachRoutes++;
        blocker[slot] = null; travel[slot] = 0; geometry[slot] = uint.MaxValue;
        state.Velocity = Vector3.zero;
        destination = approach;
        return true;
    }

    private Vector3 GetCrowdVelocity(int slot, Vector3 pursuit)
    {
        ref EnemyState state = ref Store.States[slot];
        int count = Store.Grid.QueryNearby(state.Position, state.GridIndex, neighbors);
        float spacing = Mathf.Min(Store.Grid.CellSize, definition.hitRadius * 2 + separationPadding);
        Vector3 push = Vector3.zero;
        float forwardScale = 1f;
        // All returned samples are bounded (8 per cell, 9 cells); do not throw away
        // useful neighbors after the first cell. This is not an exhaustive pair solver.
        for (int i = 0; i < count; i++)
        {
            Vector3 neighbor = Store.Grid.Position(neighbors[i]);
            if (Mathf.Abs(neighbor.y - state.Position.y) >
                2 * (definition.hitRadius + definition.hitHalfSegment)) continue;
            push += EnemySteering.Repulsion(state.Position, neighbor,
                state.GridIndex, neighbors[i], spacing);
            Vector3 offset = neighbor - state.Position; offset.y = 0;
            float ahead = Vector3.Dot(offset, pursuit);
            if (ahead <= 0 || offset.sqrMagnitude - ahead * ahead > spacing * spacing * 0.36f) continue;
            // Brake behind a nearby body instead of continuing full-speed into it.
            forwardScale = Mathf.Min(forwardScale, Mathf.Clamp01(
                (ahead - spacing * 0.8f) / (spacing * 0.8f)));
        }
        return EnemySteering.CrowdVelocity(pursuit, push, forwardScale,
            separationStrength, definition.speed);
    }

    private void Ground(int slot, bool spawning = false)
    {
        Vector3 position = Store.States[slot].Position;
        float height = spawning ? spawnGroundProbeHeight : groundProbeHeight;
        float depth = spawning ? spawnGroundProbeDepth : groundProbeDepth;
        int count = Physics.RaycastNonAlloc(position + Vector3.up * height, Vector3.down,
            groundHits, height + depth, groundMask, QueryTriggerInteraction.Ignore);
        float nearest = float.PositiveInfinity;
        grounded[slot] = false;
        // A King/player on the same layer must not hide the terrain beneath it.
        // On saturation we cannot safely choose the nearest supported surface.
        if (count == groundHits.Length) return;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = groundHits[i];
            if (hit.distance >= nearest || hit.normal.y < minimumGroundNormalY ||
                hit.collider.GetComponentInParent<NetworkObject>() != null) continue;
            nearest = hit.distance;
            groundHeight[slot] = hit.point.y + definition.hitRadius + definition.hitHalfSegment;
            grounded[slot] = true;
        }
    }

    private void Move(int slot, float dt)
    {
        ref EnemyState state = ref Store.States[slot];
        Vector3 position = state.Position;
        position.y = grounded[slot]
            ? Mathf.MoveTowards(position.y, groundHeight[slot], groundCorrectionSpeed * dt)
            : position.y - ungroundedFallSpeed * dt;
        if (state.Behavior == EnemyBehavior.Idle || desiredSpeed[slot] <= 0.001f) {
            state.Velocity = Vector3.zero; Store.Move(slot, position); return;
        }
        if (BuildWall.ActiveServerCount > 0 && geometry[slot] != BuildWall.GeometryVersion)
        { state.Velocity = Vector3.zero; Store.Move(slot, position); return; }
        Vector3 offset = state.Destination - position; offset.y = 0;
        state.Velocity = Vector3.MoveTowards(state.Velocity, heading[slot] * desiredSpeed[slot], definition.acceleration * dt);
        Vector3 displacement = state.Velocity * dt;
        if (state.Behavior == EnemyBehavior.Chase)
        {
            float remaining = Mathf.Max(0, offset.magnitude - state.StopDistance);
            Vector3 forward = offset.normalized;
            float inward = Vector3.Dot(displacement, forward);
            if (inward > remaining) displacement -= forward * (inward - remaining);
        }
        if (BuildWall.ActiveServerCount > 0)
        {
            float distance = Mathf.Min(displacement.magnitude, travel[slot]);
            displacement = heading[slot] * distance;
            travel[slot] -= distance;
        }
        state.Velocity = displacement / dt;
        Store.Move(slot, position + displacement);
    }

    private void ReleaseSlot(ref EnemyState state)
    { slots.Release(state.KingSlot); state.KingSlot = -1; }
    private static void Stop(ref EnemyState state)
    { state.Behavior = EnemyBehavior.Idle; state.Velocity = Vector3.zero; }

    public bool Raycast(Vector3 origin, Vector3 direction, float range, out uint id, out float distance)
    {
        id = 0; distance = range;
        if (!IsReady || !Store.Grid.RaycastCapsules(origin, direction, range,
            definition.hitRadius, definition.hitHalfSegment, out int index, out distance)) return false;
        id = Store.States[Store.SlotFromGrid(index)].Id;
        return true;
    }

    public bool DamageServer(uint id, int damage, EnemyTargetable source = null)
    {
        if (!IsReady || damage <= 0 || !Store.TryGetSlot(id, out int slot)) return false;
        ref EnemyState state = ref Store.States[slot];
        state.Health = Mathf.Max(0, state.Health - damage);
        if (source != null && source.IsValid && source.Registry == targets &&
            source.Kind == EnemyTargetable.TargetKind.Player)
        { attacker[slot] = source; attackerUntil[slot] = manager.ServerTime.Time + 6; state.NextDecision = 0; }
        if (state.Health != 0) return true;
        ClearBreachRoute(slot, manager.ServerTime.Time);
        ReleaseSlot(ref state);
        Vector3 position = state.Position;
        Store.Remove(id, out _); // Remove before callbacks: duplicate hits cannot repeat death/rewards.
        target[slot] = attacker[slot] = null;
        blocker[slot] = null;
        try { if (rewards != null) rewards.AddServer(position, definition.rewardQuantity); }
        finally { Removed?.Invoke(id, true); }
        return true;
    }

    // Co-op tolerance for a visible client target that moved just off the server ray.
    // Existing nearer host hits win; the hint cannot move the origin, bypass cover or set damage.
    public bool ConfirmReportedHit(uint id, Vector3 origin, Vector3 direction, float range, out float distance)
    {
        distance = range;
        if (!IsReady || id == 0 || !Store.TryGetSlot(id, out int slot)) return false;
        EnemyState state = Store.States[slot];
        float projected = Vector3.Dot(state.Position - origin, direction);
        if (state.Health <= 0 || projected < 0 || projected > range ||
            (state.Position - origin).sqrMagnitude > (range + definition.hitRadius) * (range + definition.hitRadius))
            return false;
        return EnemyProxyMath.RayCapsule(origin, direction, state.Position, definition.hitRadius + 0.6f,
            definition.hitHalfSegment, range, out distance);
    }

    public void Clear()
    {
        if (Store == null) return;
        while (Store.Count > 0) {
            uint id = Store.States[Store.ActiveSlots[Store.Count - 1]].Id;
            Store.Remove(id, out _);
            Removed?.Invoke(id, false);
        }
        Array.Clear(target, 0, target.Length);
        Array.Clear(attacker, 0, attacker.Length);
        Array.Clear(blocker, 0, blocker.Length);
        Array.Clear(desiredSpeed, 0, desiredSpeed.Length);
        Array.Clear(breachRoutes, 0, breachRoutes.Length);
        ActiveBreachRoutes = 0;
        slots?.Clear();
        cursor = 0;
        movementCredit = decisionCredit = 0;
    }
    private void OnStopped(bool wasHost) {
        initialized = false; Clear(); director = null; targets = null;
        EnemyBreachTracker.Reset(); // Server time restarts on the next session.
    }
    private void OnDestroy() { if (manager != null) manager.OnServerStopped -= OnStopped; }
}

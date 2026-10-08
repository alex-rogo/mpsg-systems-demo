using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class EnemyHordeTests
{
    private static EnemyBreachTracker.Opening Opening() => new EnemyBreachTracker.Opening {
        Position = new Vector3(0, 1.5f, 5), Normal = Vector3.forward,
        HalfWidth = 2.5f, HalfThickness = 0.185f, HalfHeight = 1.5f };

    [Test]
    public void BreachWaypointsCrossBothFacesAndSpreadAcrossSafeLanes()
    {
        var opening = Opening();
        Vector3 enemy = Vector3.up, goal = new Vector3(0, 1, 10);
        Assert.IsTrue(EnemyBreachTracker.TryWaypoints(opening, enemy, goal, 0, 0.5f, 1, 0.15f,
            out Vector3 entry, out Vector3 exit));
        Assert.Less(entry.z, 5 - opening.HalfThickness - 0.5f);
        Assert.Greater(exit.z, 5 + opening.HalfThickness + 0.5f);
        Assert.AreEqual(enemy.y, entry.y);
        Assert.AreEqual(entry.x, exit.x);
        Assert.LessOrEqual(Mathf.Abs(entry.x) + 0.5f + 0.15f, opening.HalfWidth + 0.0001f);
        Assert.IsTrue(EnemyBreachTracker.TryWaypoints(opening, enemy, goal, 3, 0.5f, 1, 0.15f,
            out Vector3 other, out _));
        Assert.Greater((other - entry).sqrMagnitude, 1);
        Assert.IsTrue(EnemyBreachTracker.TryWaypoints(opening, new Vector3(4, 1, 5.715f),
            new Vector3(0, 1, 0), 1, 0.5f, 1, 0.15f, out _, out _),
            "Front-line enemies hugging an intact neighbor can approach the breach sideways.");
    }

    [Test]
    public void BreachWaypointsRejectAlreadyBeyondSameSideAndUnusableApertures()
    {
        var opening = Opening();
        Assert.IsFalse(EnemyBreachTracker.TryWaypoints(opening, new Vector3(0, 1, 7),
            new Vector3(0, 1, 10), 1, 0.5f, 1, 0.15f, out _, out _));
        Assert.IsFalse(EnemyBreachTracker.TryWaypoints(opening, Vector3.up,
            new Vector3(0, 1, 1), 1, 0.5f, 1, 0.15f, out _, out _));
        opening.HalfWidth = 0.6f;
        Assert.IsFalse(EnemyBreachTracker.TryWaypoints(opening, Vector3.up,
            new Vector3(0, 1, 10), 1, 0.5f, 1, 0.15f, out _, out _));
        opening = Opening(); opening.HalfHeight = 0.5f;
        Assert.IsFalse(EnemyBreachTracker.TryWaypoints(opening, Vector3.up,
            new Vector3(0, 1, 10), 1, 0.5f, 1, 0.15f, out _, out _));
    }

    [Test]
    public void NumericBreachSelectionRespectsRangeDetourMemoryAndRebuild()
    {
        EnemyBreachTracker.Reset();
        var opening = Opening();
        uint id = EnemyBreachTracker.RegisterOpening(opening.Position, opening.Normal,
            opening.HalfThickness, opening.HalfWidth, opening.HalfHeight, 10, 30);
        Assert.IsTrue(EnemyBreachTracker.HasOpenings(11));
        Assert.IsTrue(EnemyBreachTracker.TryFindRoute(Vector3.up, new Vector3(0, 1, 10),
            1, 11, 16, 12, -0.1f, 0.5f, 1, 0.15f, out uint selected, out _, out _));
        Assert.AreEqual(id, selected);
        Assert.IsFalse(EnemyBreachTracker.TryFindRoute(Vector3.up, new Vector3(0, 1, 10),
            1, 11, 2, 12, -0.1f, 0.5f, 1, 0.15f, out _, out _, out _));
        Assert.IsFalse(EnemyBreachTracker.TryFindRoute(Vector3.up, new Vector3(0, 1, 10),
            1, 11, 16, 0, -0.1f, 0.5f, 1, 0.15f, out _, out _, out _));
        Assert.IsFalse(EnemyBreachTracker.TryGetOpening(id, 40, out _));
        Assert.IsFalse(EnemyBreachTracker.HasOpenings(40));
        Assert.AreEqual(1, EnemyBreachTracker.InvalidateNear(opening.Position, 1));
        Assert.IsFalse(EnemyBreachTracker.TryGetOpening(id, 11, out _));
        Assert.IsFalse(EnemyBreachTracker.HasOpenings(11));
    }

    [Test]
    public void BreachesChooseTheShortestAcceptableForwardDetour()
    {
        EnemyBreachTracker.Reset();
        uint best = EnemyBreachTracker.Register(new Vector3(3f, 0f, 1f), 10.0, 30f);
        EnemyBreachTracker.Register(new Vector3(6f, 0f, 4f), 10.0, 30f);
        EnemyBreachTracker.Register(new Vector3(-2f, 0f, 0f), 10.0, 30f);
        Assert.IsTrue(EnemyBreachTracker.TryFindBest(Vector3.zero, Vector3.right * 10f, 11.0,
            12f, 8f, -0.1f, out uint selected, out Vector3 position));
        Assert.AreEqual(best, selected);
        Assert.AreEqual(new Vector3(3f, 0f, 1f), position);
    }

    [Test]
    public void BreachesRespectAwarenessDetourExpirationAndRebuildInvalidation()
    {
        EnemyBreachTracker.Reset();
        uint expired = EnemyBreachTracker.Register(Vector3.right * 3f, 0.0, 1f);
        Assert.IsFalse(EnemyBreachTracker.TryGet(expired, 1.0, out _));
        uint far = EnemyBreachTracker.Register(Vector3.right * 20f, 2.0, 30f);
        uint detour = EnemyBreachTracker.Register(new Vector3(2f, 0f, 10f), 2.0, 30f);
        Assert.IsFalse(EnemyBreachTracker.TryFindBest(Vector3.zero, Vector3.right * 10f, 3.0,
            12f, 2f, -0.1f, out _, out _));
        Assert.AreEqual(1, EnemyBreachTracker.InvalidateNear(Vector3.right * 20f, 0.5f));
        Assert.IsFalse(EnemyBreachTracker.TryGet(far, 3.0, out _));
        Assert.IsTrue(EnemyBreachTracker.TryGet(detour, 3.0, out _));
    }

    [Test]
    public void WarmBreachQueriesDoNotAllocate()
    {
        EnemyBreachTracker.Reset();
        for (int i = 0; i < EnemyBreachTracker.Capacity; i++)
            EnemyBreachTracker.Register(new Vector3(i % 8, 0f, i / 8), 0.0, 30f);
        EnemyBreachTracker.TryFindBest(Vector3.zero, Vector3.right * 10f, 1.0,
            12f, 8f, -0.1f, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            EnemyBreachTracker.TryFindBest(Vector3.zero, Vector3.right * 10f, 1.0,
                12f, 8f, -0.1f, out _, out _);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    public void GridUsesFloorForNegativeCoordinates()
    {
        var grid = new EnemySpatialGrid(4, 1f);
        Assert.AreEqual(new Vector2Int(-1, -1), grid.GetCell(new Vector3(-0.01f, 50f, -0.01f)));
        Assert.AreEqual(new Vector2Int(-2, 1), grid.GetCell(new Vector3(-1.01f, -50f, 1.9f)));
    }

    [Test]
    public void GridIncludesAdjacentCellsAndExcludesDistantCellsAndSelf()
    {
        var grid = new EnemySpatialGrid(4, 1f);
        int self = grid.Add(new Vector3(0.9f, 0f, 0.9f));
        int adjacent = grid.Add(new Vector3(1.1f, 0f, 1.1f));
        int far = grid.Add(new Vector3(3.1f, 0f, 0f));
        int[] results = new int[72];
        int count = grid.QueryNearby(new Vector3(0.9f, 0f, 0.9f), self, results);
        Assert.AreEqual(1, count);
        Assert.AreEqual(adjacent, results[0]);
        Assert.AreNotEqual(far, results[0]);
    }

    [Test]
    public void CrossingCellsRemovesOldMembership()
    {
        var grid = new EnemySpatialGrid(2, 1f);
        int moving = grid.Add(Vector3.zero);
        int[] results = new int[72];
        grid.UpdatePosition(moving, new Vector3(-5f, 0f, 8f));
        Assert.AreEqual(0, grid.QueryNearby(Vector3.zero, -1, results));
        Assert.AreEqual(1, grid.QueryNearby(new Vector3(-5f, 0f, 8f), -1, results));
        Assert.AreEqual(moving, results[0]);
        Assert.AreEqual(1, grid.CellCount);
    }

    [Test]
    public void RemovalIsIdempotentAndCapacityCanBeReused()
    {
        var grid = new EnemySpatialGrid(2, 1f);
        int first = grid.Add(Vector3.zero);
        int second = grid.Add(Vector3.zero);
        Assert.AreEqual(-1, grid.Add(Vector3.zero));
        Assert.IsTrue(grid.Remove(first));
        Assert.IsFalse(grid.Remove(first));
        Assert.AreEqual(first, grid.Add(Vector3.right * 4f));
        Assert.IsTrue(grid.Remove(second));
        grid.Clear();
        Assert.AreEqual(0, grid.Count);
        Assert.AreEqual(0, grid.CellCount);
        Assert.AreEqual(0, grid.Add(Vector3.zero));
    }

    [Test]
    public void CrowdedCellQueriesStayBoundedAndRotateAcrossAllThousandMembers()
    {
        var grid = new EnemySpatialGrid(1000, 1f);
        for (int i = 0; i < 1000; i++) grid.Add(Vector3.zero);
        int[] results = new int[72];
        var seen = new HashSet<int>();
        for (int query = 0; query < 125; query++)
        {
            int count = grid.QueryNearby(Vector3.zero, -1, results);
            Assert.AreEqual(8, count);
            for (int i = 0; i < count; i++) seen.Add(results[i]);
        }
        Assert.AreEqual(1000, seen.Count);
    }

    [Test]
    public void RemovingQueryCursorAndCellHeadDoesNotLeaveStaleEntries()
    {
        var grid = new EnemySpatialGrid(5, 1f);
        for (int i = 0; i < 5; i++) grid.Add(Vector3.zero);
        int[] results = new int[5];
        grid.QueryNearby(Vector3.zero, -1, results, 1);
        grid.Remove(0);
        grid.Remove(1);
        int count = grid.QueryNearby(Vector3.zero, -1, results);
        Assert.AreEqual(3, count);
        for (int i = 0; i < count; i++) Assert.That(results[i], Is.InRange(2, 4));
    }

    [Test]
    public void MovingAndQueryingWarmGridDoesNotAllocate()
    {
        var grid = new EnemySpatialGrid(1000, 1.1f);
        int[] results = new int[72];
        for (int i = 0; i < 1000; i++) grid.Add(new Vector3(i % 32, 0f, i / 32));
        // Warm both membership churn and dictionary/query code before measuring.
        for (int i = 0; i < 1000; i++) grid.UpdatePosition(i, new Vector3(i % 32 + 2f, 0f, i / 32));
        grid.QueryNearby(Vector3.zero, -1, results);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 20; frame++)
            for (int i = 0; i < 1000; i++)
            {
                Vector3 position = new Vector3(i % 32 + (frame % 2) * 2f, 0f, i / 32);
                grid.UpdatePosition(i, position);
                grid.QueryNearby(position, i, results);
            }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(1000, grid.Count);
    }

    [Test]
    public void RingReservationsAreUniqueAndSpacingFitsThousandAgents()
    {
        var slots = new EnemyAttackSlots(1000, 1.25f, 0.95f);
        var claimed = new HashSet<int>();
        for (int i = 0; i < 1000; i++)
        {
            int slot = slots.Claim(Vector3.right * 30f);
            Assert.IsTrue(claimed.Add(slot));
        }
        Assert.AreEqual(1000, slots.ClaimedCount);
        Assert.AreEqual(-1, slots.Claim(Vector3.zero));
        for (int a = 0; a < 1000; a++)
            for (int b = a + 1; b < 1000; b++)
                if ((slots.GetOffset(a) - slots.GetOffset(b)).sqrMagnitude < 0.949f * 0.949f)
                    Assert.Fail($"Slots {a} and {b} overlap their configured spacing.");
    }

    [Test]
    public void VacantFrontSlotsPromoteWaitingAgentsAndDoubleReleaseIsSafe()
    {
        var slots = new EnemyAttackSlots(1000, 1.25f, 0.95f);
        int inner = slots.Claim(Vector3.right);
        int outer = -1;
        for (int i = 1; i < 1000; i++) outer = slots.Claim(Vector3.right);
        slots.Release(inner);
        slots.Release(inner);
        Assert.AreEqual(999, slots.ClaimedCount);
        int promoted = slots.Promote(outer, Vector3.right);
        Assert.AreEqual(inner, promoted);
        Assert.IsTrue(slots.IsInnerRing(promoted));
        Assert.IsFalse(slots.IsClaimed(outer));
        Assert.AreEqual(999, slots.ClaimedCount);
    }

    [Test]
    public void RectangularKingSlotsRemainOutsideColliderFootprint()
    {
        Vector3 centerOffset = new Vector3(1f, 0f, -2f);
        Vector2 halfExtents = new Vector2(3f, 2f);
        const float padding = 0.5f;
        var slots = new EnemyAttackSlots(512, centerOffset, halfExtents, padding, 1f);

        // Expanded perimeter is 24 metres, producing 24 first-ring positions.
        for (int i = 0; i < 24; i++)
        {
            int slot = slots.Claim(Vector3.zero);
            Assert.IsTrue(slots.IsInnerRing(slot));
            Vector3 local = slots.GetOffset(slot) - centerOffset;
            bool onExpandedPerimeter = Mathf.Abs(Mathf.Abs(local.x) - 3.5f) < 0.001f ||
                Mathf.Abs(Mathf.Abs(local.z) - 2.5f) < 0.001f;
            Assert.IsTrue(onExpandedPerimeter);
            Assert.IsFalse(Mathf.Abs(local.x) < halfExtents.x &&
                Mathf.Abs(local.z) < halfExtents.y);
        }
        Assert.IsFalse(slots.IsInnerRing(slots.Claim(Vector3.zero)));
    }

    [Test]
    public void RingClearRemovesAllReservationsForSessionRestart()
    {
        var slots = new EnemyAttackSlots(1000, 1.25f, 0.95f);
        for (int i = 0; i < 1000; i++) slots.Claim(Vector3.zero);
        slots.Clear();
        Assert.AreEqual(0, slots.ClaimedCount);
        int slot = slots.Claim(Vector3.right);
        Assert.IsTrue(slots.IsInnerRing(slot));
    }

    [Test]
    public void GridAndRingsSupportTenThousandActiveEnemies()
    {
        const int capacity = 10000;
        var grid = new EnemySpatialGrid(capacity, 1.1f);
        var slots = new EnemyAttackSlots(capacity, 1.25f, 0.95f);
        int[] claims = new int[capacity];
        for (int i = 0; i < capacity; i++)
        {
            Assert.GreaterOrEqual(grid.Add(new Vector3(i % 100, 0f, i / 100)), 0);
            claims[i] = slots.Claim(new Vector3(i % 100, 0f, i / 100));
            Assert.GreaterOrEqual(claims[i], 0);
        }
        Assert.AreEqual(capacity, grid.Count);
        Assert.AreEqual(capacity, slots.ClaimedCount);
        for (int i = 0; i < capacity; i++) slots.Release(claims[i]);
        Assert.AreEqual(0, slots.ClaimedCount);
    }

    [Test]
    public void CloserNeighborsRepelMoreAndDistantNeighborsDoNotRepel()
    {
        Vector3 near = EnemySteering.Repulsion(Vector3.zero, Vector3.right * 0.1f, 1, 2, 1f);
        Vector3 far = EnemySteering.Repulsion(Vector3.zero, Vector3.right * 0.8f, 1, 2, 1f);
        Assert.Greater(near.magnitude, far.magnitude);
        Assert.Less(near.x, 0f);
        Assert.AreEqual(Vector3.zero, EnemySteering.Repulsion(Vector3.zero, Vector3.right * 2f, 1, 2, 1f));
    }

    [Test]
    public void ExactOverlapsProduceStableOppositeDirections()
    {
        Vector3 a = EnemySteering.Repulsion(Vector3.zero, Vector3.zero, 17, 29, 1f);
        Vector3 b = EnemySteering.Repulsion(Vector3.zero, Vector3.zero, 29, 17, 1f);
        Assert.Greater(a.sqrMagnitude, 0.99f);
        Assert.Less((a + b).sqrMagnitude, 0.000001f);
    }

    [Test]
    public void DirectBlockersCauseSidewaysFlowWhileRetainingForwardMovement()
    {
        Vector3 steering = EnemySteering.Combine(Vector3.forward, Vector3.back, 0.85f, 2);
        Assert.Greater(steering.x, 0.1f);
        Assert.Greater(steering.z, 0f);
        Vector3 oppositeSide = EnemySteering.Combine(Vector3.forward, Vector3.back, 0.85f, 3);
        Assert.Less(oppositeSide.x, -0.1f);
    }

    [Test]
    public void SeparationCannotReversePursuitOrExceedSpeed()
    {
        for (int i = 0; i < 360; i++)
        {
            float angle = i * Mathf.Deg2Rad;
            Vector3 separation = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 20f;
            Vector3 steering = EnemySteering.Combine(Vector3.forward, separation, 10f);
            Assert.Greater(Vector3.Dot(steering, Vector3.forward), 0f);
            Assert.LessOrEqual(steering.sqrMagnitude, 1.00001f);
        }
    }
}

using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public sealed class EnemyDataTests
{
    private static EnemySnapshot MovingSample(double time, float speed = 2) => new EnemySnapshot {
        Id = 17, Position = Vector3.right * (float)(time * speed),
        Velocity = Vector3.right * speed, Health = 100 };

    [Test]
    public void FastSnapshotsRetainTheSamplesDelayedRenderingStillNeeds()
    {
        var track = new EnemySnapshotTrack();
        track.Push(MovingSample(0), 0);
        track.Push(MovingSample(0.1), 0.1);
        Assert.AreEqual(0.1f, track.Sample(0.05).Position.x, 0.0001f);
        track.Push(MovingSample(0.2), 0.2);
        Assert.AreEqual(0.16f, track.Sample(0.08).Position.x, 0.0001f,
            "The previous two-sample history jumped to 0.2 here.");
    }

    [Test]
    public void SnapshotRingWrapsWithoutGrowingOrLosingChronologicalBrackets()
    {
        var track = new EnemySnapshotTrack();
        for (int i = 0; i < 1000; i++) track.Push(MovingSample(i * 0.1, 1), i * 0.1);
        Assert.AreEqual(EnemySnapshotTrack.HistoryCapacity, track.StoredSamples);
        Assert.AreEqual(99.35f, track.Sample(99.35).Position.x, 0.0001f);
        Assert.AreEqual(99.2f, track.Sample(0).Position.x, 0.0001f);
    }

    [TestCase(60, false)]
    [TestCase(144, false)]
    [TestCase(60, true)]
    [TestCase(144, true)]
    public void TenHzPlaybackIsContinuousAtRenderRateWithDelayedDelivery(int fps, bool jitter)
    {
        var track = new EnemySnapshotTrack();
        int packet = 0;
        float dt = 1f / fps;
        float blend = 1 - Mathf.Exp(-dt / 0.08f);
        float previousX = 0;
        for (int frame = 0; frame < fps * 4; frame++)
        {
            double wall = frame / (double)fps;
            double arrival = packet * 0.1 + (jitter && packet % 3 == 1 ? 0.08 : 0);
            while (arrival <= wall + 0.0000001)
            {
                double stamp = packet++ * 0.1;
                track.Push(MovingSample(stamp), stamp);
                arrival = packet * 0.1 + (jitter && packet % 3 == 1 ? 0.08 : 0);
            }
            // Model NGO's buffered server clock plus the configured render delay.
            EnemySnapshot pose = track.Render(wall - 0.05, dt, 0.12f, 0.35f, 0.3f, blend);
            if (wall > 0.5)
            {
                Assert.AreEqual((wall - 0.05 - 0.12) * 2, pose.Position.x, 0.0001,
                    "Regular motion must not freeze, jump or acquire a correction-filter lag.");
                Assert.AreEqual(dt * 2, pose.Position.x - previousX, 0.0001);
            }
            previousX = pose.Position.x;
        }
    }

    [TestCase(0.18)]
    [TestCase(0.27)]
    [TestCase(0.36)]
    [TestCase(0.89)]
    public void RenderBufferFollowsCadenceWithinItsConfiguredLimit(double interval)
    {
        var track = new EnemySnapshotTrack();
        const float dt = 1f / 60;
        int packet = 0;
        float blend = 1 - Mathf.Exp(-dt / 0.08f);
        for (int frame = 0; frame < 600; frame++)
        {
            double wall = frame / 60.0;
            while (packet * interval <= wall + 0.0000001) {
                double stamp = packet++ * interval;
                track.Push(MovingSample(stamp), stamp);
            }
            track.Render(wall - 0.05, dt, 0.12f, 0.35f, 0.3f, blend);
            Assert.LessOrEqual(track.StoredSamples, EnemySnapshotTrack.HistoryCapacity);
            if (wall < 3) continue;
            Assert.AreEqual(Mathf.Clamp((float)interval * 1.05f, 0.12f, 0.35f),
                track.RenderDelay, 0.001f);
            if (interval <= 0.36) Assert.IsFalse(track.IsPredicting,
                "Steady delivery should be interpolated, not forced into prediction.");
            Assert.LessOrEqual(track.Sample(wall + 100, 0.3f).Position.x,
                (float)(track.LatestTime * 2) + 0.6001f, "Prediction remains bounded even at 1Hz.");
        }
    }

    [Test]
    public void NewSnapshotBlendsPredictionErrorAndYawRatherThanSnapping()
    {
        var track = new EnemySnapshotTrack();
        track.Push(MovingSample(0), 0);
        track.Push(MovingSample(0.1), 0.1);
        EnemySnapshot before = track.Render(0.3, 1f / 60, 0.12f, 0.35f, 0.3f, 0.2f);
        var stopped = new EnemySnapshot { Id = 17, Position = Vector3.right * 0.2f, Yaw = 90 };
        track.Push(stopped, 0.3);
        EnemySnapshot corrected = track.Render(0.3 + 1.0 / 60, 1f / 60, 0.12f, 0.35f, 0.3f, 0.2f);
        Assert.Less((corrected.Position - before.Position).magnitude, 0.08f);
        Assert.Greater(corrected.Position.x, stopped.Position.x);
        Assert.Less(Mathf.Abs(Mathf.DeltaAngle(before.Yaw, corrected.Yaw)), 30);
        for (int frame = 2; frame < 90; frame++)
            corrected = track.Render(0.3 + frame / 60.0, 1f / 60, 0.12f, 0.35f, 0.3f, 0.2f);
        Assert.AreEqual(stopped.Position.x, corrected.Position.x, 0.0001f);
        Assert.AreEqual(90, corrected.Yaw, 0.001f);
    }

    [Test]
    public void TeleportsAndReusedLivesClearHistoryAndPresentationCorrections()
    {
        var track = new EnemySnapshotTrack();
        track.Push(MovingSample(0), 0);
        track.Render(0.2, 1f / 60, 0.12f, 0.35f, 0.3f, 0.1f);
        track.Push(new EnemySnapshot { Id = 17, Position = Vector3.right * 100, Yaw = 180 }, 0.3);
        EnemySnapshot teleported = track.Render(0.31, 1f / 60, 0.12f, 0.35f, 0.3f, 0.1f);
        Assert.AreEqual(1, track.StoredSamples);
        Assert.AreEqual(100, teleported.Position.x);
        Assert.AreEqual(180, teleported.Yaw);
        track = default;
        track.Push(new EnemySnapshot { Id = 18, Position = Vector3.forward * 3 }, 1);
        EnemySnapshot fresh = track.Render(1.1, 1f / 60, 0.12f, 0.35f, 0.3f, 0.1f);
        Assert.AreEqual(Vector3.forward * 3, fresh.Position);
    }

    [Test]
    public void InvalidOrStalePacketsCannotCorruptSnapshotHistory()
    {
        var track = new EnemySnapshotTrack();
        track.Push(MovingSample(1), 1);
        Assert.IsFalse(track.Push(MovingSample(2), double.NaN));
        Assert.IsFalse(track.Push(MovingSample(2), double.PositiveInfinity));
        var invalid = MovingSample(2); invalid.Position.x = float.NaN;
        Assert.IsFalse(track.Push(invalid, 2));
        Assert.IsFalse(track.Push(MovingSample(0), 0));
        Assert.AreEqual(1, track.StoredSamples);
        Assert.AreEqual(1, track.LatestTime);
    }

    [Test]
    public void CrowdBrakingIsNotNormalizedBackToFullSpeed()
    {
        Vector3 free = EnemySteering.CrowdVelocity(Vector3.forward, Vector3.zero, 1, 1.5f, 2);
        Vector3 blocked = EnemySteering.CrowdVelocity(Vector3.forward, Vector3.zero, 0.2f, 1.5f, 2);
        Assert.AreEqual(2, free.magnitude, 0.001f);
        Assert.AreEqual(0.4f, blocked.magnitude, 0.001f);
    }

    [Test]
    public void StationaryAttackersStillSeparateAndCrowdSpeedRemainsBounded()
    {
        Vector3 separating = EnemySteering.CrowdVelocity(Vector3.zero, Vector3.right, 1, 1.5f, 2);
        Assert.Greater(separating.x, 0);
        Assert.LessOrEqual(separating.magnitude, 2);
        Vector3 crowded = EnemySteering.CrowdVelocity(Vector3.forward, Vector3.one * 10, 1, 3, 2);
        Assert.LessOrEqual(crowded.magnitude, 2.0001f);
        Assert.AreEqual(0, crowded.y);
    }

    [Test]
    public void IDsAreNotSlotsAndStaleDeathCannotRemoveReusedSlot()
    {
        var store = new EnemyStateStore(1, 4);
        Assert.IsTrue(store.Spawn(Vector3.zero, 0, 100, out uint oldId));
        store.TryGetSlot(oldId, out int oldSlot);
        Assert.IsTrue(store.Remove(oldId, out _));
        Assert.IsFalse(store.Remove(oldId, out _));
        Assert.IsTrue(store.Spawn(Vector3.one, 90, 100, out uint newId));
        store.TryGetSlot(newId, out int newSlot);
        Assert.AreEqual(oldSlot, newSlot);
        Assert.AreNotEqual(oldId, newId);
        Assert.IsFalse(store.Remove(oldId, out _));
        Assert.AreEqual(1, store.Count);
        Assert.AreEqual(100, store.States[newSlot].Health);
        Assert.AreEqual(EnemyBehavior.Idle, store.States[newSlot].Behavior);
    }
    [Test]
    public void ClearingEncounterDoesNotRecycleLogicalIdentity()
    {
        var store = new EnemyStateStore(2, 4);
        store.Spawn(Vector3.zero, 0, 100, out uint first);
        store.Clear();
        store.Spawn(Vector3.zero, 0, 100, out uint second);
        Assert.Greater(second, first);
        Assert.IsFalse(store.TryGetSlot(first, out _));
    }
    [Test]
    public void ActiveAndGridMappingsSurviveSwapRemovalAndFiveThousandReuseCycles()
    {
        var store = new EnemyStateStore(5000, 4);
        for (int i = 0; i < 5000; i++) Assert.IsTrue(store.Spawn(new Vector3(i % 50, 1, i / 50), 0, 100, out _));
        for (int i = 0; i < 5000; i++) {
            uint id = store.States[store.ActiveSlots[store.Count / 2]].Id;
            Assert.IsTrue(store.Remove(id, out _));
        }
        Assert.AreEqual(0, store.Grid.Count);
        for (int i = 0; i < 5000; i++) {
            store.Spawn(new Vector3(i, 0, 0), 0, 100, out uint id);
            Assert.Greater(id, 5000);
        }
        for (int i = 0; i < store.Count; i++) {
            int slot = store.ActiveSlots[i];
            Assert.AreEqual(slot, store.SlotFromGrid(store.States[slot].GridIndex));
        }
    }
    [Test]
    public void ExhaustiveCombatQueryDoesNotSampleAwayCrowdedVictims()
    {
        var grid = new EnemySpatialGrid(1000, 4);
        for (int i = 0; i < 1000; i++) grid.Add(new Vector3(0.01f * (i % 10), 0, 0.01f * (i / 10)));
        var hits = new int[1000];
        Assert.AreEqual(1000, grid.QueryRadius(Vector3.zero, 2, hits));
        Assert.Throws<System.ArgumentException>(() => grid.QueryRadius(Vector3.zero, 2, new int[32]));
    }
    [TestCase(1f, 0f)]
    [TestCase(-1f, 0f)]
    [TestCase(0f, 1f)]
    [TestCase(0f, -1f)]
    public void GridRayFindsNearestCapsuleAcrossPositiveAndNegativeCells(float x, float z)
    {
        var grid = new EnemySpatialGrid(1000, 4);
        Vector3 direction = new Vector3(x, 0, z);
        int closest = grid.Add(direction * 5 + Vector3.up);
        for (int i = 0; i < 999; i++) grid.Add(direction * (8 + i * 0.05f) + Vector3.up);
        Assert.IsTrue(grid.RaycastCapsules(Vector3.up, direction, 100, 0.5f, 0.5f, out int hit, out float d));
        Assert.AreEqual(closest, hit);
        Assert.AreEqual(4.5f, d, 0.001f);
    }
    [Test]
    public void CapsuleCapsAndWorldRangeLimitAreRespected()
    {
        Assert.IsTrue(EnemyProxyMath.RayCapsule(new Vector3(0, 4, 0), Vector3.down,
            Vector3.up, 0.5f, 0.5f, 10, out float d));
        Assert.AreEqual(2, d, 0.001f);
        var grid = new EnemySpatialGrid(1, 4);
        grid.Add(new Vector3(5, 1, 0));
        Assert.IsFalse(grid.RaycastCapsules(Vector3.up, Vector3.right, 4, 0.5f, 0.5f, out _, out _));
    }
    [Test]
    public void SnapshotHistoryRejectsOldTimesAndNewLives()
    {
        var track = new EnemySnapshotTrack();
        var first = new EnemySnapshot { Id = 17, Position = Vector3.zero, Velocity = Vector3.right };
        var next = first; next.Position = Vector3.right;
        Assert.IsTrue(track.Push(first, 1));
        Assert.IsTrue(track.Push(next, 2));
        Assert.IsFalse(track.Push(first, 1.5));
        Assert.IsFalse(track.Push(first, 2));
        var wrongId = first; wrongId.Id = 18;
        Assert.IsFalse(track.Push(wrongId, 3));
        Assert.AreEqual(0.5f, track.Sample(1.5).Position.x, 0.001f);
        Assert.AreEqual(1.2f, track.Sample(100).Position.x, 0.001f);
    }
    [Test]
    public void LargeCorrectionsAndReusedTrackStartWithoutOldLifeInterpolation()
    {
        var track = new EnemySnapshotTrack();
        track.Push(new EnemySnapshot { Id = 1 }, 1);
        track.Push(new EnemySnapshot { Id = 1, Position = Vector3.right * 100 }, 2);
        Assert.AreEqual(100, track.Sample(1.5).Position.x);
        track = default;
        Assert.IsTrue(track.Push(new EnemySnapshot { Id = 2, Position = Vector3.forward }, 3));
        Assert.AreEqual(Vector3.forward, track.Sample(3).Position);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void WireRecordsRoundTripAndStayInsideNonFragmentedBudget(bool escape)
    {
        var origin = new Vector3(1000, 0, -1000);
        var snapshot = new EnemySnapshot { Id = 400000, Position = origin + new Vector3(escape ? 2000 : 3.137f, 1.111f, 9.234f),
            Velocity = new Vector3(1.123f, -2.456f, 0.32f), Yaw = 189.32f, Health = 123, Behavior = EnemyBehavior.Chase };
        using (var writer = new FastBufferWriter(1100, Allocator.Temp))
        {
            EnemyWireCodec.WriteHeader(writer, new EnemyWireCodec.Header { Epoch = 77,
                Kind = EnemyWireCodec.Motion, Time = 12.5, Origin = origin, Count = EnemyWireCodec.RecordsPerPacket });
            for (int i = 0; i < EnemyWireCodec.RecordsPerPacket; i++) EnemyWireCodec.Write(writer, snapshot, origin);
            Assert.LessOrEqual(writer.Length, 1100);
            using (var readerValue = new FastBufferReader(writer, Allocator.Temp))
            {
                var reader = readerValue;
                Assert.IsTrue(EnemyWireCodec.ReadHeader(ref reader, out var header));
                Assert.AreEqual(77, header.Epoch);
                for (int i = 0; i < header.Count; i++) {
                    Assert.IsTrue(EnemyWireCodec.Read(ref reader, header.Origin, out var decoded));
                    Assert.AreEqual(snapshot.Id, decoded.Id);
                    Assert.Less((snapshot.Position - decoded.Position).magnitude, 0.02f);
                    Assert.Less((snapshot.Velocity - decoded.Velocity).magnitude, 0.01f);
                    Assert.AreEqual(snapshot.Health, decoded.Health);
                    Assert.Less(Mathf.Abs(Mathf.DeltaAngle(snapshot.Yaw, decoded.Yaw)), 0.01f);
                }
            }
        }
    }
    [Test]
    public void TruncatedNetworkDataIsRejected()
    {
        using (var writer = new FastBufferWriter(8, Allocator.Temp))
        {
            writer.WriteValueSafe((uint)1);
            using (var value = new FastBufferReader(writer, Allocator.Temp)) {
                var reader = value;
                Assert.IsFalse(EnemyWireCodec.ReadHeader(ref reader, out _));
                Assert.IsFalse(EnemyWireCodec.Read(ref reader, Vector3.zero, out _));
            }
        }
    }
}

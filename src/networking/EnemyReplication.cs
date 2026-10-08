using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>NGO connection, batched enemy IDs. Byte budgets send latest state, never old motion queues.</summary>
[DefaultExecutionOrder(50)]
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager), typeof(EnemySimulation), typeof(EnemyPresentation))]
public sealed class EnemyReplication : MonoBehaviour
{
    private const string LifeMessage = "htf.enemy.life.v1", MotionMessage = "htf.enemy.motion.v1", ReadyMessage = "htf.enemy.ready.v1";
    private const int HistoryCapacity = EnemyDirector.MaximumPopulationLimit * 2;
    [SerializeField, Range(1, 20)] private float snapshotHz = 10f;
    [SerializeField, Min(8192)] private int bytesPerSecondPerClient = 256000;
    [SerializeField, Range(1, 32)] private int packetsPerClientPerFrame = 8;
    private struct Life { public ulong Sequence; public bool Spawn, Died; public EnemySnapshot State; public uint Id; }
    private sealed class Peer
    {
        public ulong Id, Sequence;
        public bool Active, Capture;
        public int Phase, BaselineCount, BaselineCursor, MotionCursor, MotionRemaining;
        public double BaselineTime, NextMotion, Credit;
        public readonly EnemySnapshot[] Baseline = new EnemySnapshot[EnemyDirector.MaximumPopulationLimit];
    }
    private readonly Peer[] peers = { new Peer(), new Peer(), new Peer() };
    private readonly Life[] history = new Life[HistoryCapacity];
    private NetworkManager manager;
    private EnemySimulation simulation;
    private EnemyPresentation presentation;
    private EnemyBenchmark benchmark;
    private ulong sequence;
    private uint epoch;
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long SentRecords { get; private set; }
    public long ReceivedRecords { get; private set; }
    public long SentMessages { get; private set; }
    public long ReceivedMessages { get; private set; }
    public long RejectedPackets { get; private set; }
    public double LastReplicationMs { get; private set; }

    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        simulation = GetComponent<EnemySimulation>();
        presentation = GetComponent<EnemyPresentation>();
        benchmark = GetComponent<EnemyBenchmark>();
        simulation.Spawned += OnSpawn;
        simulation.Removed += OnRemove;
        manager.OnServerStarted += OnServerStarted;
        manager.OnClientStarted += RegisterHandlers;
        manager.OnClientConnectedCallback += OnConnected;
        manager.OnClientDisconnectCallback += OnDisconnected;
        manager.OnServerStopped += OnStopped;
        manager.OnClientStopped += OnStopped;
    }
    private void RegisterHandlers()
    {
        manager.CustomMessagingManager.RegisterNamedMessageHandler(LifeMessage, ReceiveLife);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(MotionMessage, ReceiveMotion);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(ReadyMessage, ReceiveReady);
    }
    private void OnServerStarted()
    {
        RegisterHandlers();
        epoch = unchecked((uint)Guid.NewGuid().GetHashCode());
        if (epoch == 0) epoch = 1;
    }
    private void OnConnected(ulong id)
    {
        if (manager.IsServer || id != manager.LocalClientId) return;
        using (var writer = new FastBufferWriter(1, Allocator.Temp)) {
            writer.WriteValueSafe(EnemyWireCodec.Version);
            manager.CustomMessagingManager.SendNamedMessage(ReadyMessage, NetworkManager.ServerClientId,
                writer, NetworkDelivery.ReliableSequenced);
        }
    }
    private void ReceiveReady(ulong sender, FastBufferReader reader)
    {
        if (!manager.IsServer || sender == NetworkManager.ServerClientId ||
            !manager.ConnectedClients.ContainsKey(sender) || !reader.TryBeginRead(1)) return;
        reader.ReadValueSafe(out byte version);
        if (version != EnemyWireCodec.Version) return;
        Peer peer = null;
        for (int i = 0; i < peers.Length; i++) if (peers[i].Active && peers[i].Id == sender) peer = peers[i];
        if (peer == null) for (int i = 0; i < peers.Length; i++) if (!peers[i].Active) { peer = peers[i]; break; }
        if (peer == null) return; // Four-player host: three remote peers.
        peer.Id = sender;
        peer.Active = true;
        peer.Capture = true;
        peer.Credit = 2200;
    }
    private void Capture(Peer peer, double now)
    {
        peer.Capture = false;
        peer.Phase = peer.BaselineCursor = peer.MotionCursor = peer.MotionRemaining = 0;
        peer.BaselineTime = now;
        peer.Sequence = sequence;
        peer.BaselineCount = simulation.Store.Count;
        for (int i = 0; i < peer.BaselineCount; i++)
            peer.Baseline[i] = EnemySnapshot.From(simulation.Store.States[simulation.Store.ActiveSlots[i]]);
    }
    private void OnSpawn(EnemySnapshot state)
    {
        ulong n = ++sequence;
        history[n % HistoryCapacity] = new Life { Sequence = n, Spawn = true, State = state };
    }
    private void OnRemove(uint id, bool died)
    {
        ulong n = ++sequence;
        history[n % HistoryCapacity] = new Life { Sequence = n, Id = id, Died = died };
    }
    private void LateUpdate()
    {
        if (!manager.IsServer || !simulation.IsReady || epoch == 0) return;
        long start = benchmark != null && benchmark.Enabled ? Stopwatch.GetTimestamp() : 0;
        double now = manager.ServerTime.Time;
        for (int i = 0; i < peers.Length; i++)
        {
            Peer peer = peers[i];
            if (!peer.Active) continue;
            if (peer.Capture || sequence - peer.Sequence > HistoryCapacity) Capture(peer, now);
            peer.Credit = Math.Min(bytesPerSecondPerClient * 0.1, peer.Credit + bytesPerSecondPerClient * Time.unscaledDeltaTime);
            for (int packet = 0; packet < packetsPerClientPerFrame && peer.Credit >= 1100; packet++)
            {
                if (peer.Phase < 2 || peer.Sequence < sequence) SendLife(peer, now);
                else if (!SendMotion(peer, now)) break;
            }
        }
        LastReplicationMs = start == 0 ? 0 : (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }
    private void SendLife(Peer peer, double now)
    {
        byte kind;
        int count;
        Vector3 origin = Vector3.zero;
        if (peer.Phase == 0) { kind = EnemyWireCodec.Reset; count = 0; peer.Phase = 1; }
        else if (peer.Phase == 1)
        {
            kind = EnemyWireCodec.Spawn;
            count = Math.Min(EnemyWireCodec.RecordsPerPacket, peer.BaselineCount - peer.BaselineCursor);
            if (count > 0) origin = peer.Baseline[peer.BaselineCursor].Position;
        }
        else
        {
            Life first = history[(peer.Sequence + 1) % HistoryCapacity];
            kind = first.Spawn ? EnemyWireCodec.Spawn : EnemyWireCodec.Remove;
            int limit = first.Spawn ? EnemyWireCodec.RecordsPerPacket : 180;
            count = 0;
            while (count < limit && peer.Sequence + (ulong)count < sequence &&
                history[(peer.Sequence + (ulong)count + 1) % HistoryCapacity].Spawn == first.Spawn) count++;
            if (first.Spawn) origin = first.State.Position;
        }
        using (var writer = new FastBufferWriter(1100, Allocator.Temp))
        {
            EnemyWireCodec.WriteHeader(writer, new EnemyWireCodec.Header { Kind = kind, Epoch = epoch,
                Time = peer.Phase == 1 ? peer.BaselineTime : now, Origin = origin, Count = (ushort)count });
            for (int j = 0; j < count; j++)
            {
                if (peer.Phase == 1) EnemyWireCodec.Write(writer, peer.Baseline[peer.BaselineCursor++], origin);
                else {
                    Life item = history[++peer.Sequence % HistoryCapacity];
                    if (item.Spawn) EnemyWireCodec.Write(writer, item.State, origin);
                    else { writer.WriteValueSafe(item.Id); writer.WriteValueSafe(item.Died); }
                }
            }
            Send(peer, LifeMessage, writer, NetworkDelivery.ReliableSequenced, count);
        }
        if (peer.Phase == 1 && peer.BaselineCursor == peer.BaselineCount) peer.Phase = 2;
    }
    private bool SendMotion(Peer peer, double now)
    {
        if (simulation.Store.Count == 0) { peer.MotionRemaining = 0; return false; }
        if (peer.MotionRemaining == 0)
        {
            if (now < peer.NextMotion) return false;
            peer.MotionRemaining = simulation.Store.Count;
            peer.NextMotion = now + 1.0 / snapshotHz;
        }
        int count = Math.Min(EnemyWireCodec.RecordsPerPacket, peer.MotionRemaining);
        peer.MotionCursor %= simulation.Store.Count;
        Vector3 origin = simulation.Store.States[simulation.Store.ActiveSlots[peer.MotionCursor]].Position;
        using (var writer = new FastBufferWriter(1100, Allocator.Temp))
        {
            EnemyWireCodec.WriteHeader(writer, new EnemyWireCodec.Header {
                Kind = EnemyWireCodec.Motion, Epoch = epoch, Time = now, Origin = origin, Count = (ushort)count });
            for (int j = 0; j < count; j++)
            {
                peer.MotionCursor %= simulation.Store.Count;
                EnemyWireCodec.Write(writer, EnemySnapshot.From(
                    simulation.Store.States[simulation.Store.ActiveSlots[peer.MotionCursor++]]), origin);
            }
            Send(peer, MotionMessage, writer, NetworkDelivery.Unreliable, count);
        }
        peer.MotionRemaining -= count;
        return true;
    }
    private void Send(Peer peer, string name, FastBufferWriter writer, NetworkDelivery delivery, int count)
    {
        manager.CustomMessagingManager.SendNamedMessage(name, peer.Id, writer, delivery);
        peer.Credit -= writer.Length;
        SentBytes += writer.Length; SentRecords += count; SentMessages++;
    }
    private bool Header(ulong sender, ref FastBufferReader reader, out EnemyWireCodec.Header h)
    {
        h = default;
        if (manager.IsServer || sender != NetworkManager.ServerClientId) return false;
        ReceivedBytes += reader.Length; ReceivedMessages++;
        if (!EnemyWireCodec.ReadHeader(ref reader, out h)) { RejectedPackets++; return false; }
        return true;
    }
    private void ReceiveLife(ulong sender, FastBufferReader reader)
    {
        if (!Header(sender, ref reader, out EnemyWireCodec.Header h)) return;
        if (h.Kind == EnemyWireCodec.Reset) { epoch = h.Epoch; presentation.ResetRoster(); return; }
        if (h.Epoch != epoch) return;
        if (h.Kind == EnemyWireCodec.Remove && h.Count <= 180)
        {
            for (int i = 0; i < h.Count; i++) {
                if (!reader.TryBeginRead(5)) { RejectedPackets++; return; }
                reader.ReadValueSafe(out uint id); reader.ReadValueSafe(out bool died);
                presentation.Remove(id);
            }
        }
        else if (h.Kind == EnemyWireCodec.Spawn && h.Count <= EnemyWireCodec.RecordsPerPacket)
            for (int i = 0; i < h.Count; i++) {
                if (!EnemyWireCodec.Read(ref reader, h.Origin, out EnemySnapshot s)) { RejectedPackets++; return; }
                presentation.Spawn(s, h.Time);
            }
        else { RejectedPackets++; return; }
        ReceivedRecords += h.Count;
    }
    private void ReceiveMotion(ulong sender, FastBufferReader reader)
    {
        if (!Header(sender, ref reader, out EnemyWireCodec.Header h) || h.Epoch != epoch ||
            h.Kind != EnemyWireCodec.Motion || h.Count > EnemyWireCodec.RecordsPerPacket) return;
        for (int i = 0; i < h.Count; i++) {
            if (!EnemyWireCodec.Read(ref reader, h.Origin, out EnemySnapshot s)) { RejectedPackets++; return; }
            presentation.Apply(s, h.Time);
        }
        ReceivedRecords += h.Count;
    }
    private void OnDisconnected(ulong id) { foreach (Peer peer in peers) if (peer.Id == id) peer.Active = false; }
    private void OnStopped(bool wasHost)
    {
        epoch = 0; sequence = 0;
        foreach (Peer peer in peers) peer.Active = false;
        manager.CustomMessagingManager?.UnregisterNamedMessageHandler(LifeMessage);
        manager.CustomMessagingManager?.UnregisterNamedMessageHandler(MotionMessage);
        manager.CustomMessagingManager?.UnregisterNamedMessageHandler(ReadyMessage);
        presentation.ResetRoster();
    }
    private void OnDestroy()
    {
        simulation.Spawned -= OnSpawn; simulation.Removed -= OnRemove;
        manager.OnServerStarted -= OnServerStarted; manager.OnClientStarted -= RegisterHandlers;
        manager.OnClientConnectedCallback -= OnConnected; manager.OnClientDisconnectCallback -= OnDisconnected;
        manager.OnServerStopped -= OnStopped; manager.OnClientStopped -= OnStopped;
    }
}

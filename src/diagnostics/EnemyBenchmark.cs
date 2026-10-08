using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>Opt-in counters, no profiler recorders. Enemy byte counts are application payload, not UDP.</summary>
[DefaultExecutionOrder(200)]
public sealed class EnemyBenchmark : MonoBehaviour
{
    [SerializeField] private bool benchmarkEnabled;
    [SerializeField, Min(5)] private float logInterval = 10;
    public bool Enabled => benchmarkEnabled;
    private NetworkManager manager;
    private UnityTransport transport;
    private EnemySimulation simulation;
    private EnemyReplication replication;
    private EnemyPresentation presentation;
    private EnemyRewardBank rewards;
    private int frames;
    private float elapsed, worst;
    private double simSum, netSum, viewSum;
    private double oldestSamplePeak;
    private int breachQueryPeak;
    private long sent, received, sentRecords, receivedRecords, sentMessages, receivedMessages, allocated;
    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        transport = GetComponent<UnityTransport>();
        simulation = GetComponent<EnemySimulation>();
        replication = GetComponent<EnemyReplication>();
        presentation = GetComponent<EnemyPresentation>();
        rewards = GetComponent<EnemyRewardBank>();
        ResetWindow();
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F11)) { benchmarkEnabled = !benchmarkEnabled; ResetWindow(); }
    }
    private void LateUpdate()
    {
        if (!benchmarkEnabled || !manager.IsListening) return;
        float dt = Time.unscaledDeltaTime;
        elapsed += dt; frames++; worst = Mathf.Max(worst, dt);
        simSum += simulation.LastSimulationMs; netSum += replication.LastReplicationMs;
        viewSum += presentation.LastPresentationMs;
        oldestSamplePeak = Math.Max(oldestSamplePeak, presentation.OldestSampleAge);
        breachQueryPeak = Math.Max(breachQueryPeak, simulation.LastBreachQueries);
        if (elapsed < logInterval) return;
        ulong rtt = 0;
        if (!manager.IsServer) rtt = transport.GetCurrentRtt(NetworkManager.ServerClientId);
        else foreach (ulong id in manager.ConnectedClientsIds)
            if (id != NetworkManager.ServerClientId) rtt = Math.Max(rtt, transport.GetCurrentRtt(id));
        Debug.Log($"[EnemyBench] role={(manager.IsHost ? "Host" : manager.IsServer ? "Server" : "Client")} " +
            $"peers={(manager.IsServer ? manager.ConnectedClientsIds.Count - 1 : 1)} " +
            $"enemies={(manager.IsServer ? simulation.Store.Count : presentation.Roster.Count)} visible={presentation.VisibleCount} " +
            $"fps={frames / elapsed:0.0} worstMs={worst * 1000:0.0} " +
            $"simMsAvg={simSum / frames:0.000} replicationMsAvg={netSum / frames:0.000} viewMsAvg={viewSum / frames:0.000} " +
            $"enemyTxKBps={(replication.SentBytes - sent) / elapsed / 1024:0.0} " +
            $"enemyRxKBps={(replication.ReceivedBytes - received) / elapsed / 1024:0.0} " +
            $"txRecordsPS={(replication.SentRecords - sentRecords) / elapsed:0} rxRecordsPS={(replication.ReceivedRecords - receivedRecords) / elapsed:0} " +
            $"txMessagesPS={(replication.SentMessages - sentMessages) / elapsed:0} rxMessagesPS={(replication.ReceivedMessages - receivedMessages) / elapsed:0} " +
            $"rttMs={rtt} oldestSampleMs={presentation.OldestSampleAge * 1000:0.0} " +
            $"oldestSamplePeakMs={oldestSamplePeak * 1000:0.0} " +
            $"snapshotGapMsAvg={presentation.AverageSnapshotInterval * 1000:0.0} " +
            $"renderDelayMsAvg={presentation.AverageRenderDelay * 1000:0.0} " +
            $"predictionPct={(presentation.Roster.Count > 0 ? 100f * presentation.PredictedCount / presentation.Roster.Count : 0):0.0} " +
            $"breachRoutes={simulation.ActiveBreachRoutes} breachQueryPeak={breachQueryPeak} " +
            $"mainThreadAllocatedKB={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024d:0.0} " +
            $"pendingRewards={rewards.PendingQuantity} rejectedPackets={replication.RejectedPackets}", this);
        ResetWindow();
    }
    private void ResetWindow()
    {
        elapsed = worst = 0; frames = 0; simSum = netSum = viewSum = 0;
        oldestSamplePeak = 0;
        breachQueryPeak = 0;
        if (replication != null) {
            sent = replication.SentBytes; received = replication.ReceivedBytes;
            sentRecords = replication.SentRecords; receivedRecords = replication.ReceivedRecords;
            sentMessages = replication.SentMessages; receivedMessages = replication.ReceivedMessages;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread();
    }
}

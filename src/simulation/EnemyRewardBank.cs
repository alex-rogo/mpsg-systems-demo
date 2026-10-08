using System.Collections.Generic;
using HoldTheFort.Items;
using Unity.Netcode;
using UnityEngine;

/// <summary>Temporary aggregated resource drops. Rewards persist in numeric piles, not 3k Rigidbodies.</summary>
[DisallowMultipleComponent]
public sealed class EnemyRewardBank : MonoBehaviour
{
    [SerializeField] private EnemyDefinition definition;
    [SerializeField, Min(1)] private float pileCellSize = 4f;
    [SerializeField, Min(1)] private float collectRadius = 5f;
    private NetworkManager manager;
    private EnemyTargetRegistry targets;
    private readonly Dictionary<Vector2Int, long> piles = new Dictionary<Vector2Int, long>(4096);
    private readonly List<Vector2Int> keys = new List<Vector2Int>(4096);
    private double nextCollect;
    public long PendingQuantity { get; private set; }
    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        targets = GetComponent<EnemyTargetRegistry>();
        manager.OnServerStopped += OnStopped;
    }
    public void AddServer(Vector3 position, int quantity)
    {
        if (!manager.IsServer || definition == null || definition.rewardItem == null || quantity <= 0) return;
        Vector2Int key = new Vector2Int(Mathf.FloorToInt(position.x / pileCellSize), Mathf.FloorToInt(position.z / pileCellSize));
        piles.TryGetValue(key, out long current);
        piles[key] = current + quantity;
        PendingQuantity += quantity;
    }
    private void Update()
    {
        if (!manager.IsServer || !manager.IsListening || piles.Count == 0 ||
            manager.ServerTime.Time < nextCollect || definition.rewardItem == null) return;
        nextCollect = manager.ServerTime.Time + 0.5;
        keys.Clear();
        foreach (Vector2Int key in piles.Keys) keys.Add(key);
        for (int k = 0; k < keys.Count; k++)
        {
            Vector2Int key = keys[k];
            long remaining = piles[key];
            Vector3 center = new Vector3((key.x + 0.5f) * pileCellSize, 0, (key.y + 0.5f) * pileCellSize);
            for (int p = 0; p < targets.PlayerCount && remaining > 0; p++)
            {
                EnemyTargetable player = targets.GetPlayer(p);
                if (!player.IsValid || player.Inventory == null) continue;
                Vector3 offset = player.Position - center; offset.y = 0;
                if (offset.sqrMagnitude > collectRadius * collectRadius) continue;
                int amount = (int)System.Math.Min(int.MaxValue, remaining);
                ItemInstanceData remainder = player.Inventory.AddItemServer(
                    new ItemInstanceData(definition.rewardItem.itemId, amount));
                int accepted = amount - (remainder.IsEmpty ? 0 : remainder.quantity);
                remaining -= accepted;
                PendingQuantity -= accepted;
            }
            if (remaining == 0) piles.Remove(key);
            else piles[key] = remaining;
        }
    }
    private void OnStopped(bool wasHost) { piles.Clear(); PendingQuantity = 0; nextCollect = 0; }
    private void OnDestroy() { if (manager != null) manager.OnServerStopped -= OnStopped; }
}

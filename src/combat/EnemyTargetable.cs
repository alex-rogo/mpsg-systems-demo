using Unity.Netcode;
using UnityEngine;
using HoldTheFort.Inventory;

/// <summary>Only explicitly attached King/player health targets can enter the registry.</summary>
[DisallowMultipleComponent]
public sealed class EnemyTargetable : NetworkBehaviour
{
    public enum TargetKind : byte { King, Player }
    [SerializeField] private TargetKind kind;
    public TargetKind Kind => kind;
    public EnemyTargetRegistry Registry { get; private set; }
    private KingHealth king;
    private PlayerHealth player;
    private Collider shape;
    public PlayerInventory Inventory { get; private set; }
    public Vector3 Position => transform.position;
    public Collider Shape => shape;
    public bool IsValid => IsSpawned && IsServer && isActiveAndEnabled &&
        Registry != null && Registry.isActiveAndEnabled && !NetworkManager.ShutdownInProgress &&
        (kind == TargetKind.King
            ? king != null && king.IsSpawned && king.isActiveAndEnabled && king.health.Value > 0
            : player != null && player.IsAlive);

    private void Awake()
    {
        king = GetComponent<KingHealth>();
        player = GetComponent<PlayerHealth>();
        shape = GetComponent<Collider>();
        Inventory = GetComponent<PlayerInventory>();
    }

    public Vector3 ClosestPoint(Vector3 position) => shape != null
        ? shape.ClosestPoint(position) : transform.position;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        // Direct component lookup once per network life, not a global locator or AI search.
        Registry = NetworkManager.GetComponent<EnemyTargetRegistry>();
        if (Registry == null)
        {
            Debug.LogError("NetworkManager needs EnemyTargetRegistry.", this);
            return;
        }
        if (kind == TargetKind.King && king != null) king.health.OnValueChanged += OnHealthChanged;
        else if (kind == TargetKind.Player && player != null) player.CurrentHealth.OnValueChanged += OnHealthChanged;
        else Debug.LogError("EnemyTargetable needs the matching health component.", this);
        RefreshRegistration();
    }

    private void OnHealthChanged(int previous, int current) => RefreshRegistration();
    // Covers different health/target component order on future prefab variants.
    protected override void OnNetworkPostSpawn() => RefreshRegistration();
    private void RefreshRegistration()
    {
        if (Registry == null) return;
        if (IsValid) Registry.Register(this);
        else Registry.Unregister(this);
    }

    private void OnEnable() => RefreshRegistration();
    private void OnDisable()
    {
        if (Registry != null) Registry.Unregister(this);
    }

    public override void OnNetworkDespawn()
    {
        if (king != null) king.health.OnValueChanged -= OnHealthChanged;
        if (player != null) player.CurrentHealth.OnValueChanged -= OnHealthChanged;
        if (Registry != null) Registry.Unregister(this);
        Registry = null;
    }

    internal void TakeDamageServer(int damage)
    {
        if (!IsValid || damage <= 0) return;
        if (kind == TargetKind.King) king.TakeDamage(damage);
        else player.TakeDamage(damage);
    }
}

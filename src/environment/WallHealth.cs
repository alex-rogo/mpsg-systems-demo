using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class WallHealth : NetworkBehaviour
{
    [SerializeField, Min(1)] private int maxHealth = 100;
    public int MaxHealth => Mathf.Max(1, maxHealth);
    public readonly NetworkVariable<int> CurrentHealth = new NetworkVariable<int>(100,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private bool dead;
    private BuildWall wall;
    public bool IsAlive => IsSpawned && isActiveAndEnabled && !dead && CurrentHealth.Value > 0;

    private void Awake() => wall = GetComponent<BuildWall>();

    public override void OnNetworkSpawn()
    {
        dead = false;
        if (IsServer) CurrentHealth.Value = MaxHealth;
    }

    public void TakeDamage(int damage)
    {
        if (!IsServer || !IsAlive || damage <= 0 || NetworkManager.ShutdownInProgress) return;
        CurrentHealth.Value = Mathf.Max(0, CurrentHealth.Value - damage);
        if (CurrentHealth.Value != 0) return;
        dead = true; // Multiple scheduled attackers cannot despawn the same wall twice.
        if (wall != null) wall.ReportBreachServer();
        NetworkObject.Despawn(true);
    }
}

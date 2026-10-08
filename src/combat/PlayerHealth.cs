using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PlayerHealth : NetworkBehaviour
{
    [SerializeField, Min(1)] private int maxHealth = 100;
    public int MaxHealth => Mathf.Max(1, maxHealth);
    public NetworkVariable<int> CurrentHealth = new NetworkVariable<int>(100,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public bool IsAlive => IsSpawned && isActiveAndEnabled && CurrentHealth.Value > 0;

    public override void OnNetworkSpawn()
    {
        // Only a new network life resets health; re-enabling a component does not heal.
        if (IsServer) CurrentHealth.Value = MaxHealth;
    }

    public void TakeDamage(int damage)
    {
        if (!IsServer || !IsAlive || damage <= 0 || NetworkManager.ShutdownInProgress) return;
        CurrentHealth.Value = Mathf.Max(0, CurrentHealth.Value - damage);
    }
}

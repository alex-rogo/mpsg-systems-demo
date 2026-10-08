using UnityEngine;
using Unity.Netcode;

public class KingHealth : NetworkBehaviour
{
    [Tooltip("Stress testing only: server ignores damage while enabled. Does not revive a dead King.")]
    [SerializeField] private bool invulnerableForStressTest;

    public NetworkVariable<int> health = new NetworkVariable<int>(100);

    public void TakeDamage(int damage)
    {
        if (!IsSpawned || !IsServer || !isActiveAndEnabled || invulnerableForStressTest ||
            damage <= 0 || health.Value <= 0)
            return;

        health.Value = Mathf.Max(0, health.Value - damage);
    }
}

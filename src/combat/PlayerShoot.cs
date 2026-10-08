using System;
using UnityEngine;
using Unity.Netcode;
using HoldTheFort.Player;
using HoldTheFort.Inventory;
using HoldTheFort.Items;
using HoldTheFort.Weapons;

public class PlayerShoot : NetworkBehaviour
{
    public Camera playerCamera;

    private HoldTheFortInputReader input;
    private PlayerInventory inventory;
    private PlayerHeldItemController heldItem;
    private EnemyTargetable attackerTarget;
    private PlayerHealth playerHealth;
    private EnemySimulation enemySimulation;
    private EnemyPresentation enemyPresentation;
    private double nextLocalFireTime;
    private double nextServerFireTime;
    private uint nextLocalShotId;
    private Vector3 cameraRestLocalPosition;
    private RaycastHit[] hitBuffer = new RaycastHit[32];

    // Presentation only: immediate owner feedback, even if the server rejects.
    public event Action<WeaponDefinition, uint> LocalFireRequested;
    public event Action<int, uint, Vector3, Vector3> HitscanConfirmed;

    private void Awake()
    {
        input = GetComponent<HoldTheFortInputReader>();
        inventory = GetComponent<PlayerInventory>();
        heldItem = GetComponent<PlayerHeldItemController>();
        attackerTarget = GetComponent<EnemyTargetable>();
        playerHealth = GetComponent<PlayerHealth>();
    }

    public override void OnNetworkSpawn()
    {
        enemySimulation = NetworkManager.GetComponent<EnemySimulation>();
        enemyPresentation = NetworkManager.GetComponent<EnemyPresentation>();
        nextLocalFireTime = 0;
        nextServerFireTime = 0;
        nextLocalShotId = 0;
        if (playerCamera != null)
            cameraRestLocalPosition = playerCamera.transform.localPosition;
    }

    // Called by the shared primary-use router; this component never consumes input.
    public void RequestPrimaryUse()
    {
        if (!IsSpawned || !IsOwner || input == null || inventory == null || heldItem == null)
            return;

        if (!input.InputEnabled || playerCamera == null || (playerHealth != null && !playerHealth.IsAlive))
            return;

        WeaponDefinition weapon = heldItem.CurrentDefinition as WeaponDefinition;
        int slot = inventory.SelectedHotbarSlot.Value;
        if (slot < 0 || slot >= PlayerInventory.HotbarSize || !CanExecute(weapon) ||
            heldItem.CurrentItem.ammoInMagazine <= 0 || Time.timeAsDouble < nextLocalFireTime)
            return;

        nextLocalFireTime = Time.timeAsDouble + 1.0 / weapon.fireRate;
        Vector3 direction = playerCamera.transform.forward;
        uint shotId = unchecked(++nextLocalShotId);
        LocalFireRequested?.Invoke(weapon, shotId);
        uint reportedEnemy = enemyPresentation != null
            ? enemyPresentation.Raycast(playerCamera.transform.position, direction, weapon.range) : 0;
        RequestFireRpc(slot, weapon.itemId, direction, shotId, reportedEnemy);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestFireRpc(int slot, int itemId, Vector3 direction, uint shotId, uint reportedEnemy, RpcParams rpcParams = default)
    {
        if (!IsSpawned || !IsServer || (playerHealth != null && !playerHealth.IsAlive) ||
            rpcParams.Receive.SenderClientId != OwnerClientId ||
            inventory == null || heldItem == null || playerCamera == null ||
            slot < 0 || slot >= PlayerInventory.HotbarSize ||
            slot != inventory.SelectedHotbarSlot.Value || !IsFinite(direction.x) ||
            !IsFinite(direction.y) || !IsFinite(direction.z))
            return;

        float lengthSquared = direction.sqrMagnitude;
        if (lengthSquared < 0.99f || lengthSquared > 1.01f)
            return;

        WeaponDefinition weapon = heldItem.CurrentDefinition as WeaponDefinition;
        ItemInstanceData item = inventory.GetItem(slot);
        double now = NetworkManager.ServerTime.Time;
        if (!CanExecute(weapon) || weapon.itemId != itemId || item.IsEmpty ||
            item.itemId != itemId || item.ammoInMagazine <= 0 || now < nextServerFireTime)
            return;

        // Only aim is requested. Origin, stats, ammo and target come from the server.
        // Ignore cosmetic camera translation, including on a host's own player.
        Transform cameraTransform = playerCamera.transform;
        Vector3 origin = cameraTransform.parent != null
            ? cameraTransform.parent.TransformPoint(cameraRestLocalPosition)
            : cameraRestLocalPosition;
        direction.Normalize();
        if (!inventory.TryConsumeAmmoServer(slot, itemId))
            return;

        // Player-wide cooldown cannot be bypassed by swapping inventory slots.
        nextServerFireTime = now + 1.0 / weapon.fireRate;
        // Echo the ID only to associate presentation; it grants no gameplay authority.
        ExecuteAttackServer(weapon, origin, direction, shotId, reportedEnemy);
    }

    private static bool CanExecute(WeaponDefinition weapon)
    {
        if (weapon == null || !IsFinite(weapon.fireRate) || weapon.fireRate <= 0f ||
            !IsFinite(weapon.range) || weapon.range <= 0f ||
            !IsFinite(weapon.baseDamage) || weapon.baseDamage < 0f)
            return false;

        // Future attack types must implement execution before spending ammo.
        return weapon.fireMode == WeaponFireMode.Hitscan;
    }

    private void ExecuteAttackServer(WeaponDefinition weapon, Vector3 origin, Vector3 direction, uint shotId, uint reportedEnemy)
    {
        // Shared validation stays above this dispatch. Future attacks take a server-built
        // stat snapshot here, then own travel/impact behavior on their prefab.
        switch (weapon.fireMode)
        {
            case WeaponFireMode.Hitscan:
                FireHitscanServer(weapon, origin, direction, shotId, reportedEnemy);
                break;
            case WeaponFireMode.Projectile:
            case WeaponFireMode.GroundTarget:
                break;
        }
    }

    private void FireHitscanServer(WeaponDefinition weapon, Vector3 origin, Vector3 direction, uint shotId, uint reportedEnemy)
    {
        int count;
        do
        {
            count = Physics.RaycastNonAlloc(origin, direction, hitBuffer, weapon.range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count < hitBuffer.Length) break;
            // Transitional collider path grows only when saturated, never losing a paid shot.
            // Ordinary numeric enemies do not enter this physics query at all.
            Array.Resize(ref hitBuffer, checked(hitBuffer.Length * 2));
        } while (true);

        float nearestDistance = float.PositiveInfinity;
        Collider nearest = null;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hitBuffer[i];
            if (hit.collider.transform.IsChildOf(transform) || hit.distance >= nearestDistance)
                continue;
            nearestDistance = hit.distance;
            nearest = hit.collider;
        }

        float impactDistance = nearest != null ? nearestDistance : weapon.range;
        uint enemyId = 0;
        if (enemySimulation != null && enemySimulation.IsReady &&
            enemySimulation.Raycast(origin, direction, impactDistance, out enemyId, out float numericDistance))
            impactDistance = numericDistance;
        else if (enemySimulation != null && enemySimulation.IsReady &&
            enemySimulation.ConfirmReportedHit(reportedEnemy, origin, direction, impactDistance, out float hintedDistance))
        { enemyId = reportedEnemy; impactDistance = hintedDistance; }
        Vector3 endpoint = origin + direction * impactDistance;
        ShowHitscanRpc(weapon.itemId, shotId, origin, endpoint);

        if (enemyId != 0)
        {
            enemySimulation.DamageServer(enemyId, Mathf.RoundToInt(weapon.baseDamage), attackerTarget);
            return;
        }

        // Walls stop the shot; enemies behind the nearest collider are never selected.
        if (nearest == null)
            return;
        EnemyHealth enemy = nearest.GetComponentInParent<EnemyHealth>();
        if (enemy != null)
            enemy.TakeDamage(Mathf.RoundToInt(weapon.baseDamage), attackerTarget);
    }

    // One cosmetic message per accepted shot; no networked bullet objects or client hit claims.
    [Rpc(SendTo.ClientsAndHost, InvokePermission = RpcInvokePermission.Server,
        Delivery = RpcDelivery.Unreliable)]
    private void ShowHitscanRpc(int itemId, uint shotId, Vector3 origin, Vector3 endpoint)
    {
        HitscanConfirmed?.Invoke(itemId, shotId, origin, endpoint);
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

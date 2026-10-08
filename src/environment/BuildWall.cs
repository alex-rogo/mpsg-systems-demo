using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject), typeof(BoxCollider), typeof(WallHealth))]
public sealed class BuildWall : NetworkBehaviour
{
    [SerializeField, Min(1f)] private float breachMemorySeconds = 30f;
    public BoxCollider Shape { get; private set; }
    public WallHealth Health { get; private set; }
    public bool IsAlive => IsSpawned && isActiveAndEnabled && Health != null && Health.IsAlive;
    // Only added obstacles invalidate cached clear paths. Removing a wall cannot make
    // an already-certified corridor unsafe and must not stall the entire horde.
    public static uint GeometryVersion { get; private set; }
    public static int ActiveServerCount { get; private set; }
    private bool countedOnServer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        GeometryVersion = 0;
        ActiveServerCount = 0;
    }

    private void Awake()
    {
        Shape = GetComponent<BoxCollider>();
        Health = GetComponent<WallHealth>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        countedOnServer = true;
        ActiveServerCount++;
        unchecked { GeometryVersion++; }
        if (Shape != null)
        {
            // Spawn can precede the next physics transform sync. Read the actual
            // Transform/box data, not collider bounds cached at the prefab's old pose.
            Vector3 size = WorldBoxSize();
            EnemyBreachTracker.InvalidateNear(Shape.transform.TransformPoint(Shape.center),
                0.5f * Mathf.Sqrt(size.x * size.x + size.z * size.z) + 0.25f);
        }
    }

    public void ReportBreachServer()
    {
        if (!IsSpawned || !IsServer || !countedOnServer || NetworkManager.ShutdownInProgress) return;
        if (Shape == null) return;
        Vector3 position = Shape.transform.TransformPoint(Shape.center);
        Vector3 size = WorldBoxSize();
        bool thinX = size.x < size.z;
        Vector3 normal = Shape.transform.TransformDirection(thinX ? Vector3.right : Vector3.forward);
        // Capture the removed wall's aperture before NGO disables/destroys its collider.
        EnemyBreachTracker.RegisterOpening(position, normal,
            (thinX ? size.x : size.z) * 0.5f, (thinX ? size.z : size.x) * 0.5f,
            size.y * 0.5f, NetworkManager.ServerTime.Time, Mathf.Max(1f, breachMemorySeconds));
    }

    private Vector3 WorldBoxSize()
    {
        Vector3 scale = Shape.transform.lossyScale;
        return Vector3.Scale(Shape.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
    }

    public override void OnNetworkDespawn()
    {
        if (Shape != null) Shape.enabled = false;
        ReleaseServerRegistration();
    }

    public override void OnDestroy()
    {
        ReleaseServerRegistration();
        base.OnDestroy();
    }

    private void ReleaseServerRegistration()
    {
        if (!countedOnServer) return;
        countedOnServer = false;
        ActiveServerCount = Mathf.Max(0, ActiveServerCount - 1);
    }
}

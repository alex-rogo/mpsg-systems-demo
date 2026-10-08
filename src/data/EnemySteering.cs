using UnityEngine;

/// <summary>Allocation-free planar steering math shared by the director and tests.</summary>
public static class EnemySteering
{
    public static Vector3 Repulsion(Vector3 position, Vector3 neighbor, int selfId, int neighborId, float radius)
    {
        Vector3 away = position - neighbor;
        away.y = 0f;
        float squared = away.sqrMagnitude;
        if (radius <= 0f || squared >= radius * radius) return Vector3.zero;
        if (squared < 0.000001f)
        {
            // Exact overlaps need opposite directions, stable across frames and pooling.
            uint low = (uint)Mathf.Min(selfId, neighborId);
            uint high = (uint)Mathf.Max(selfId, neighborId);
            uint hash = unchecked(low * 73856093u ^ high * 19349663u);
            float angle = (hash % 4096u) * (Mathf.PI * 2f / 4096f);
            away = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            return selfId < neighborId ? away : -away;
        }
        float distance = Mathf.Sqrt(squared);
        float weight = 1f - distance / radius;
        return away * (weight * weight / distance);
    }

    /// <summary>Velocity, not just direction: keep braking and separate stationary attackers.</summary>
    public static Vector3 CrowdVelocity(Vector3 pursuit, Vector3 separation,
        float forwardScale, float strength, float speed)
    {
        pursuit.y = separation.y = 0f;
        Vector3 push = Vector3.ClampMagnitude(separation, 1f) * Mathf.Max(0f, strength);
        Vector3 wanted = pursuit.normalized * Mathf.Clamp01(forwardScale) + push;
        // Do not normalize: that would restore full speed when a crowd blocks pursuit.
        return Vector3.ClampMagnitude(wanted, 1f) * Mathf.Max(0f, speed);
    }

    public static Vector3 Combine(Vector3 targetDirection, Vector3 separation, float strength, int selfId = 0)
    {
        targetDirection.y = separation.y = 0f;
        Vector3 forward = targetDirection.normalized;
        Vector3 push = Vector3.ClampMagnitude(separation, 1f) * Mathf.Clamp(strength, 0f, 0.9f);
        Vector3 steering = forward + push;
        float blocking = -Vector3.Dot(push, forward);
        if (blocking > 0.2f)
        {
            // A directly aligned blocker otherwise only slows pursuit until bodies
            // pass through it. Add a stable sideways bias so the horde flows around.
            Vector3 sideways = new Vector3(forward.z, 0f, -forward.x);
            float lateral = Vector3.Dot(push, sideways);
            float sign = Mathf.Abs(lateral) > 0.01f ? Mathf.Sign(lateral) : (selfId % 2 == 0 ? 1f : -1f);
            steering += sideways * (sign * (blocking - 0.2f) * 0.65f);
        }
        // With a unit target direction and strength < 1, separation cannot reverse pursuit.
        return Vector3.ClampMagnitude(steering, 1f);
    }
}

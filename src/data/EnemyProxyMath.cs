using UnityEngine;

public static class EnemyProxyMath
{
    public static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

    // Ray direction must be normalized. An upright capsule is a cylinder plus two caps.
    public static bool RayCapsule(Vector3 origin, Vector3 direction, Vector3 center,
        float radius, float halfSegment, float maximum, out float distance)
    {
        distance = maximum;
        Vector3 local = origin - center;
        Vector3 closest = new Vector3(0f, Mathf.Clamp(local.y, -halfSegment, halfSegment), 0f);
        if ((local - closest).sqrMagnitude <= radius * radius) { distance = 0; return true; }
        bool found = false;
        float a = direction.x * direction.x + direction.z * direction.z;
        if (a > 0.000001f)
        {
            float b = local.x * direction.x + local.z * direction.z;
            float c = local.x * local.x + local.z * local.z - radius * radius;
            float discriminant = b * b - a * c;
            if (discriminant >= 0f)
            {
                float t = (-b - Mathf.Sqrt(discriminant)) / a;
                float y = local.y + direction.y * t;
                if (t >= 0 && t <= distance && Mathf.Abs(y) <= halfSegment)
                { distance = t; found = true; }
            }
        }
        found |= RaySphere(local - Vector3.up * halfSegment, direction, radius, ref distance);
        found |= RaySphere(local + Vector3.up * halfSegment, direction, radius, ref distance);
        return found;
    }
    private static bool RaySphere(Vector3 offset, Vector3 direction, float radius, ref float distance)
    {
        float b = Vector3.Dot(offset, direction), c = offset.sqrMagnitude - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0f) return false;
        float t = -b - Mathf.Sqrt(discriminant);
        if (t < 0 || t > distance) return false;
        distance = t;
        return true;
    }
}

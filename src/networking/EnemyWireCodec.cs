using Unity.Netcode;
using UnityEngine;

/// <summary>Absolute chunk-relative records. No unacknowledged delta-compression baseline.</summary>
public static class EnemyWireCodec
{
    public const byte Version = 1, Reset = 0, Spawn = 1, Remove = 2, Motion = 3;
    public const int RecordsPerPacket = 36, HeaderBytes = 28;
    public struct Header { public byte Kind; public uint Epoch; public double Time; public Vector3 Origin; public ushort Count; }
    public static void WriteHeader(FastBufferWriter writer, Header h)
    {
        writer.WriteValueSafe(Version);
        writer.WriteValueSafe(h.Kind);
        writer.WriteValueSafe(h.Epoch);
        writer.WriteValueSafe(h.Time);
        writer.WriteValueSafe(h.Origin);
        writer.WriteValueSafe(h.Count);
    }
    public static bool ReadHeader(ref FastBufferReader reader, out Header h)
    {
        h = default;
        if (!reader.TryBeginRead(HeaderBytes)) return false;
        reader.ReadValueSafe(out byte version);
        if (version != Version) return false;
        reader.ReadValueSafe(out h.Kind);
        reader.ReadValueSafe(out h.Epoch);
        reader.ReadValueSafe(out h.Time);
        reader.ReadValueSafe(out h.Origin);
        reader.ReadValueSafe(out h.Count);
        return h.Epoch != 0 && !double.IsNaN(h.Time) && !double.IsInfinity(h.Time) &&
            EnemyProxyMath.Finite(h.Origin);
    }
    public static void Write(FastBufferWriter writer, EnemySnapshot s, Vector3 origin)
    {
        Vector3 relative = s.Position - origin;
        bool full = Mathf.Abs(relative.x) > 650 || Mathf.Abs(relative.y) > 650 || Mathf.Abs(relative.z) > 650;
        writer.WriteValueSafe(s.Id);
        writer.WriteValueSafe((byte)(full ? 1 : 0));
        if (full) writer.WriteValueSafe(s.Position);
        else { WriteShort(writer, relative.x, 50); WriteShort(writer, relative.y, 50); WriteShort(writer, relative.z, 50); }
        WriteShort(writer, s.Velocity.x, 100); WriteShort(writer, s.Velocity.y, 100); WriteShort(writer, s.Velocity.z, 100);
        writer.WriteValueSafe((ushort)Mathf.RoundToInt(Mathf.Repeat(s.Yaw, 360) / 360 * ushort.MaxValue));
        writer.WriteValueSafe(s.Health);
        writer.WriteValueSafe((byte)s.Behavior);
    }
    private static void WriteShort(FastBufferWriter w, float value, float scale) =>
        w.WriteValueSafe((short)Mathf.Clamp(Mathf.RoundToInt(value * scale), short.MinValue, short.MaxValue));
    public static bool Read(ref FastBufferReader reader, Vector3 origin, out EnemySnapshot s)
    {
        s = default;
        if (!reader.TryBeginRead(22)) return false;
        reader.ReadValueSafe(out s.Id);
        reader.ReadValueSafe(out byte full);
        if (full > 1 || (full == 1 && !reader.TryBeginRead(23))) return false;
        if (full == 1) reader.ReadValueSafe(out s.Position);
        else {
            reader.ReadValueSafe(out short x); reader.ReadValueSafe(out short y); reader.ReadValueSafe(out short z);
            s.Position = origin + new Vector3(x, y, z) / 50f;
        }
        reader.ReadValueSafe(out short vx); reader.ReadValueSafe(out short vy); reader.ReadValueSafe(out short vz);
        s.Velocity = new Vector3(vx, vy, vz) / 100f;
        reader.ReadValueSafe(out ushort yaw);
        s.Yaw = yaw * (360f / ushort.MaxValue);
        reader.ReadValueSafe(out s.Health);
        reader.ReadValueSafe(out byte behavior);
        s.Behavior = (EnemyBehavior)behavior;
        return s.Id != 0 && behavior <= (byte)EnemyBehavior.AttackWall && EnemyProxyMath.Finite(s.Position);
    }
}

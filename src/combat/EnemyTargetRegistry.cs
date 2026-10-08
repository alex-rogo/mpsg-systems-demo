using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Server-local target list on the existing NetworkManager. No scene searches.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
public sealed class EnemyTargetRegistry : MonoBehaviour
{
    private readonly List<EnemyTargetable> players = new List<EnemyTargetable>(4);
    public EnemyTargetable King { get; private set; }
    public int PlayerCount => players.Count;
    public EnemyTargetable GetPlayer(int index) => players[index];

    internal void Register(EnemyTargetable target)
    {
        if (target == null || !target.IsValid || target.Registry != this) return;
        if (target.Kind == EnemyTargetable.TargetKind.King)
        {
            if (King != null && King != target)
            {
                Debug.LogError("EnemyTargetRegistry already has an active King.", target);
                return;
            }
            King = target;
        }
        else if (!players.Contains(target)) players.Add(target);
    }

    internal void Unregister(EnemyTargetable target)
    {
        if (King == target) King = null;
        int index = players.IndexOf(target);
        if (index < 0) return;
        int last = players.Count - 1;
        players[index] = players[last];
        players.RemoveAt(last);
    }
}

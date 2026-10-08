using HoldTheFort.Items;
using UnityEngine;

[CreateAssetMenu(menuName = "Hold The Fort/Enemies/Basic melee")]
public sealed class EnemyDefinition : ScriptableObject
{
    public GameObject presentationPrefab;
    [Range(1, 65535)] public int maxHealth = 100;
    [Min(0.1f)] public float speed = 2f;
    [Min(0.1f)] public float acceleration = 12f;
    [Min(0.1f)] public float attackRange = 2f;
    [Min(1)] public int damage = 5;
    [Min(0.1f)] public float attackCooldown = 1f;
    [Min(0.1f)] public float wallAttackRange = 1f;
    [Min(0.1f)] public float hitRadius = 0.5f;
    [Min(0f)] public float hitHalfSegment = 0.5f;
    public ItemDefinition rewardItem;
    [Min(1)] public int rewardQuantity = 1;
}

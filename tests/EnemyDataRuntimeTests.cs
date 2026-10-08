using System;
using System.Collections;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class EnemyDataRuntimeTests
{
    private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static void Set(Component component, string field, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(component);
        serialized.FindProperty(field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
    private static EnemyStateStore Store(Component component, string property) =>
        (EnemyStateStore)component.GetType().GetProperty(property).GetValue(component);

    [Test]
    public void SceneAssetsUseVisualOnlyEnemiesAndPlayerOwnershipAdapter()
    {
        GameObject view = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyVisual.prefab");
        Assert.IsNotNull(view.GetComponent<MeshRenderer>());
        Assert.IsNull(view.GetComponentInChildren<NetworkObject>(true));
        Assert.IsNull(view.GetComponentInChildren<Collider>(true));
        Assert.IsNull(view.GetComponentInChildren<Rigidbody>(true));
        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.IsNotNull(player.GetComponent(T("PlayerLocomotionOwner")));
    }

    private static NetworkManager Manager(string name, ushort port)
    {
        GameObject go = new GameObject(name);
        go.SetActive(false);
        NetworkManager manager = go.AddComponent<NetworkManager>();
        var transport = go.AddComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", port);
        manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = false,
            ForceSamePrefabs = false, NetworkTransport = transport };
        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        manager.NetworkConfig.PlayerPrefab = player;
        manager.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = player });
        manager.NetworkConfig.Prefabs.Add(new NetworkPrefab {
            Prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Wall.prefab") });
        go.AddComponent(T("EnemyTargetRegistry"));
        Component sim = go.AddComponent(T("EnemySimulation"));
        Component presentation = go.AddComponent(T("EnemyPresentation"));
        go.AddComponent(T("EnemyReplication"));
        Component bank = go.AddComponent(T("EnemyRewardBank"));
        go.AddComponent(T("EnemyBenchmark"));
        UnityEngine.Object definition = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Prefabs/BasicMeleeEnemy.asset");
        Set(sim, "definition", definition); Set(presentation, "definition", definition); Set(bank, "definition", definition);
        go.SetActive(true);
        return manager;
    }

    [UnityTest]
    public IEnumerator NumericBreachRoutingCrossesAnOpeningAndRejectsUnsafeRedirects()
    {
        yield return new EnterPlayMode();
        NetworkManager host = null;
        var objects = new List<GameObject>();
        try
        {
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            host = Manager("Breach data host", port);
            Assert.IsTrue(host.StartHost());
            EnemyBreachTracker.Reset();
            Vector3 center = new Vector3(3000, 1, 3000);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            objects.Add(floor);
            floor.transform.position = center + Vector3.down * 1.1f;
            floor.transform.localScale = new Vector3(250, 0.2f, 250);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Wall.prefab");
            GameObject king = UnityEngine.Object.Instantiate(prefab);
            objects.Add(king);
            king.layer = 0; king.transform.position = center;
            Component kingHealth = king.AddComponent(T("KingHealth"));
            var kingSettings = new SerializedObject(kingHealth);
            kingSettings.FindProperty("invulnerableForStressTest").boolValue = true;
            kingSettings.ApplyModifiedPropertiesWithoutUndo();
            king.AddComponent(T("EnemyTargetable"));
            king.GetComponent<NetworkObject>().SpawnWithObservers = false;
            king.GetComponent<NetworkObject>().Spawn();
            GameObject owner = UnityEngine.Object.Instantiate(prefab);
            objects.Add(owner);
            owner.layer = 0; owner.transform.position = center + Vector3.up * 100;
            Component director = owner.AddComponent(T("EnemyDirector"));
            Component sim = host.GetComponent(T("EnemySimulation"));
            Set(director, "king", kingHealth);
            Set(director, "targetRegistry", host.GetComponent(T("EnemyTargetRegistry")));
            Set(director, "simulation", sim);
            var directorSettings = new SerializedObject(director);
            directorSettings.FindProperty("populationLimit").intValue = 100;
            directorSettings.FindProperty("logEnemyCount").boolValue = false;
            directorSettings.ApplyModifiedPropertiesWithoutUndo();
            owner.GetComponent<NetworkObject>().SpawnWithObservers = false;
            owner.GetComponent<NetworkObject>().Spawn();
            var settings = new SerializedObject(sim);
            settings.FindProperty("separationStrength").floatValue = 0;
            settings.FindProperty("maximumBreachQueriesPerFrame").intValue = 8;
            settings.ApplyModifiedPropertiesWithoutUndo();
            EnemyStateStore store = Store(sim, "Store");
            MethodInfo spawn = Method(sim.GetType(), "SpawnServer");
            MethodInfo think = Method(sim.GetType(), "Think");
            MethodInfo move = Method(sim.GetType(), "Move");
            MethodInfo clear = Method(director.GetType(), "ClearDataEnemies");
            FieldInfo budget = sim.GetType().GetField("breachQueriesRemaining", BindingFlags.NonPublic | BindingFlags.Instance);
            uint SpawnAt(float x, float z)
            {
                object[] args = { center + new Vector3(x, 0, z), Quaternion.identity, (uint)0 };
                Assert.IsTrue((bool)spawn.Invoke(sim, args));
                uint id = (uint)args[2];
                store.TryGetSlot(id, out int slot);
                store.States[slot].NextAttack = double.MaxValue;
                return id;
            }
            int RouteCount() => (int)sim.GetType().GetProperty("ActiveBreachRoutes").GetValue(sim);
            GameObject WallAt(float x, float z)
            {
                GameObject wall = UnityEngine.Object.Instantiate(prefab);
                objects.Add(wall);
                wall.transform.position = center + new Vector3(x, 0.5f, z);
                wall.GetComponent<NetworkObject>().SpawnWithObservers = false;
                wall.GetComponent<NetworkObject>().Spawn();
                return wall;
            }
            GameObject middle = WallAt(0, 10);
            WallAt(-5, 10); WallAt(5, 10);
            Physics.SyncTransforms();
            uint enemy = SpawnAt(4, 10.715f);
            store.TryGetSlot(enemy, out int enemySlot);
            double now = host.ServerTime.Time;
            think.Invoke(sim, new object[] { enemySlot, now });
            think.Invoke(sim, new object[] { enemySlot, now + 0.2 });
            Assert.AreEqual(EnemyBehavior.AttackWall, store.States[enemySlot].Behavior);
            uint geometryBefore = (uint)T("BuildWall").GetProperty("GeometryVersion").GetValue(null);
            Method(T("WallHealth"), "TakeDamage").Invoke(middle.GetComponent(T("WallHealth")), new object[] { int.MaxValue });
            Physics.SyncTransforms();
            Assert.IsTrue(EnemyBreachTracker.HasOpenings(now + 0.2));
            Assert.AreEqual(geometryBefore, (uint)T("BuildWall").GetProperty("GeometryVersion").GetValue(null),
                "A removal must not invalidate every movement corridor.");
            Assert.IsTrue(EnemyBreachTracker.TryFindRoute(store.States[enemySlot].Position, center, enemy,
                now + 0.2, 16, 12, -0.1f, 0.5f, 1, 0.15f, out uint openingId, out _, out _));
            Assert.IsTrue(EnemyBreachTracker.TryGetOpening(openingId, now + 0.2, out var opening));
            Assert.AreEqual(2.5f, opening.HalfWidth, 0.001f);
            Assert.AreEqual(0.185f, opening.HalfThickness, 0.001f);
            Assert.AreEqual(Vector3.forward, opening.Normal);
            budget.SetValue(sim, 8);
            think.Invoke(sim, new object[] { enemySlot, now + 0.3 });
            Assert.AreEqual(1, RouteCount(), "An enemy attacking an intact neighbor should use the fresh gap.");
            Assert.AreEqual(EnemyBehavior.Chase, store.States[enemySlot].Behavior);
            float gapZ = center.z + 10;
            for (int tick = 0; tick < 180; tick++)
            {
                if (tick % 6 == 0) {
                    budget.SetValue(sim, 8);
                    think.Invoke(sim, new object[] { enemySlot, now + 0.3 + tick / 30.0 });
                }
                move.Invoke(sim, new object[] { enemySlot, 1f / 30 });
            }
            Assert.Less(store.States[enemySlot].Position.z, gapZ - 0.7f,
                "Route must cross the far side, not stop at the breach center.");
            Assert.AreEqual(0, RouteCount());
            clear.Invoke(director, null);

            // A single shared budget cannot validate the entire nearby crowd in one tick.
            for (int i = 0; i < 24; i++) SpawnAt(4 + i * 0.01f, 10.715f);
            budget.SetValue(sim, 8);
            int queryStart = (int)sim.GetType().GetProperty("LastBreachQueries").GetValue(sim);
            for (int i = 0; i < store.Count; i++)
                think.Invoke(sim, new object[] { store.ActiveSlots[i], now + 7 });
            int queryEnd = (int)sim.GetType().GetProperty("LastBreachQueries").GetValue(sim);
            Assert.LessOrEqual(queryEnd - queryStart, 8);
            Assert.Greater(RouteCount(), 0);
            Assert.Less(RouteCount(), 24);
            int routed = RouteCount();
            budget.SetValue(sim, 8);
            for (int i = 0; i < store.Count; i++)
                think.Invoke(sim, new object[] { store.ActiveSlots[i], now + 7.2 });
            Assert.Greater(RouteCount(), routed, "Deferred enemies remain eligible on later AI ticks.");
            clear.Invoke(director, null);

            foreach (Vector2 point in new[] { new Vector2(40, 14), new Vector2(4, 5), new Vector2(0, 14) })
            {
                uint id = SpawnAt(point.x, point.y);
                store.TryGetSlot(id, out int slot);
                budget.SetValue(sim, 8);
                think.Invoke(sim, new object[] { slot, now + 8 });
                Assert.AreEqual(0, RouteCount(), "Reject distant, already-beyond, and direct-clear enemies.");
                clear.Invoke(director, null);
            }
            GameObject obstruction = WallAt(3, 12);
            Physics.SyncTransforms();
            enemy = SpawnAt(4, 14);
            store.TryGetSlot(enemy, out enemySlot);
            budget.SetValue(sim, 8);
            think.Invoke(sim, new object[] { enemySlot, now + 8 });
            Assert.AreEqual(0, RouteCount(), "A remembered breach is not reachable through another wall.");
            clear.Invoke(director, null);
            obstruction.GetComponent<NetworkObject>().Despawn(true);
            Physics.SyncTransforms();

            enemy = SpawnAt(4, 10.715f);
            store.TryGetSlot(enemy, out enemySlot);
            budget.SetValue(sim, 8);
            think.Invoke(sim, new object[] { enemySlot, now + 9 });
            Assert.AreEqual(1, RouteCount());
            // A valid nearby player takes priority; no unnecessary King-breach diversion.
            NetworkObject player = host.LocalClient.PlayerObject;
            Component motor = player.GetComponent(T("HoldTheFort.Player.HoldTheFortPlayerMotor"));
            Method(motor.GetType(), "SetSimulationEnabled").Invoke(motor, new object[] { false });
            Vector3 oldPosition = player.transform.position;
            player.transform.position = store.States[enemySlot].Position + Vector3.forward;
            think.Invoke(sim, new object[] { enemySlot, now + 9.2 });
            Assert.AreEqual(0, RouteCount());
            Assert.AreEqual(EnemyBehavior.Attack, store.States[enemySlot].Behavior);
            player.transform.position = oldPosition;
            clear.Invoke(director, null);

            enemy = SpawnAt(4, 10.715f);
            store.TryGetSlot(enemy, out enemySlot);
            budget.SetValue(sim, 8);
            think.Invoke(sim, new object[] { enemySlot, now + 10 });
            Assert.AreEqual(1, RouteCount());
            WallAt(0, 10); // Same aperture rebuilt: tracker invalidates it.
            Physics.SyncTransforms();
            Assert.IsFalse(EnemyBreachTracker.TryGetOpening(openingId, now + 10.2, out _),
                "Rebuild invalidation must use the placed pose, even before a physics sync.");
            think.Invoke(sim, new object[] { enemySlot, now + 10.2 });
            Assert.AreEqual(0, RouteCount());
            Assert.IsFalse(EnemyBreachTracker.TryGetOpening(openingId, now + 10.2, out _));
            clear.Invoke(director, null);

            EnemyBreachTracker.RegisterOpening(center + new Vector3(0, 0.5f, 10),
                Vector3.forward, 0.185f, 2.5f, 1.5f, now, 1);
            enemy = SpawnAt(4, 10.715f);
            store.TryGetSlot(enemy, out enemySlot);
            budget.SetValue(sim, 8);
            think.Invoke(sim, new object[] { enemySlot, now + 20 });
            Assert.AreEqual(0, RouteCount(), "Expired openings cannot redirect a later lifetime.");
        }
        finally
        {
            if (host != null) { host.Shutdown(); UnityEngine.Object.Destroy(host.gameObject); }
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) UnityEngine.Object.Destroy(objects[i]);
            EnemyBreachTracker.Reset();
        }
        yield return null;
        yield return new ExitPlayMode();
    }

    [UnityTest]
    public IEnumerator ThreeThousandMovingEnemiesReplicateDieAndReuseWithoutEnemyNetworkObjects()
    {
        yield return new EnterPlayMode();
        NetworkManager host = null, client = null;
        GameObject king = null, owner = null, ground = null;
        try
        {
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            host = Manager("Data host", port);
            Assert.IsTrue(host.StartHost());
            Vector3 center = new Vector3(2000, 1, 2000);
            ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.transform.SetPositionAndRotation(center + Vector3.down * 1.1f, Quaternion.identity);
            ground.transform.localScale = new Vector3(250, 0.2f, 250);
            GameObject wallPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Wall.prefab");
            // Registered prefab-backed fixtures, hidden from the remote client.
            king = UnityEngine.Object.Instantiate(wallPrefab);
            king.layer = 0; king.transform.position = center;
            Component kingHealth = king.AddComponent(T("KingHealth"));
            var kingSettings = new SerializedObject(kingHealth);
            kingSettings.FindProperty("invulnerableForStressTest").boolValue = true;
            kingSettings.ApplyModifiedPropertiesWithoutUndo();
            king.AddComponent(T("EnemyTargetable"));
            king.GetComponent<NetworkObject>().SpawnWithObservers = false;
            king.GetComponent<NetworkObject>().Spawn();
            owner = UnityEngine.Object.Instantiate(wallPrefab);
            owner.layer = 0; owner.transform.position = center + Vector3.up * 100;
            Component director = owner.AddComponent(T("EnemyDirector"));
            Set(director, "king", kingHealth);
            Set(director, "targetRegistry", host.GetComponent(T("EnemyTargetRegistry")));
            Set(director, "simulation", host.GetComponent(T("EnemySimulation")));
            var directorSettings = new SerializedObject(director);
            directorSettings.FindProperty("populationLimit").intValue = 5000;
            directorSettings.FindProperty("logEnemyCount").boolValue = false;
            directorSettings.ApplyModifiedPropertiesWithoutUndo();
            owner.GetComponent<NetworkObject>().SpawnWithObservers = false;
            owner.GetComponent<NetworkObject>().Spawn();
            Physics.SyncTransforms();
            Component sim = host.GetComponent(T("EnemySimulation"));
            MethodInfo spawn = Method(sim.GetType(), "SpawnServer");
            EnemyStateStore authority = Store(sim, "Store");
            // A high King/player pivot must not become permanent enemy altitude.
            // The King itself is a networked collider on the ground layer: skip it.
            object[] highSpawn = { center + Vector3.up * 30, Quaternion.identity, (uint)0 };
            Assert.IsTrue((bool)spawn.Invoke(sim, highSpawn));
            authority.TryGetSlot((uint)highSpawn[2], out int groundedSlot);
            Assert.AreEqual(1, authority.States[groundedSlot].Position.y, 0.01f,
                "Ground the first authoritative spawn snapshot, even 30m above terrain.");
            EnemyStateStore hostRoster = Store(host.GetComponent(T("EnemyPresentation")), "Roster");
            hostRoster.TryGetSlot((uint)highSpawn[2], out int viewSlot);
            Assert.AreEqual(1, hostRoster.States[viewSlot].Position.y, 0.01f);
            Method(director.GetType(), "ClearDataEnemies").Invoke(director, null);

            GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.transform.position = center + new Vector3(50, 4, 0);
            platform.transform.localScale = new Vector3(10, 1, 10);
            platform.transform.SetParent(ground.transform, true);
            Physics.SyncTransforms();
            object[] raisedSpawn = { center + new Vector3(50, 30, 0), Quaternion.identity, (uint)0 };
            Assert.IsTrue((bool)spawn.Invoke(sim, raisedSpawn));
            authority.TryGetSlot((uint)raisedSpawn[2], out groundedSlot);
            Assert.AreEqual(6.5f, authority.States[groundedSlot].Position.y, 0.01f);
            MethodInfo grounding = Method(sim.GetType(), "Ground");
            MethodInfo movement = Method(sim.GetType(), "Move");
            // Re-probe after crossing from raised ground to low ground.
            authority.Move(groundedSlot, center + new Vector3(60, 5.5f, 0));
            grounding.Invoke(sim, new object[] { groundedSlot, false });
            for (int i = 0; i < 30; i++) movement.Invoke(sim, new object[] { groundedSlot, 1f / 30 });
            Assert.AreEqual(1, authority.States[groundedSlot].Position.y, 0.01f);
            // Missing support must fall, not retain the previous surface height.
            var simSettings = new SerializedObject(sim);
            simSettings.FindProperty("groundMask").intValue = 0;
            simSettings.ApplyModifiedPropertiesWithoutUndo();
            grounding.Invoke(sim, new object[] { groundedSlot, false });
            float unsupportedY = authority.States[groundedSlot].Position.y;
            movement.Invoke(sim, new object[] { groundedSlot, 1f / 30 });
            Assert.Less(authority.States[groundedSlot].Position.y, unsupportedY);
            simSettings.Update();
            simSettings.FindProperty("groundMask").intValue = 1;
            simSettings.ApplyModifiedPropertiesWithoutUndo();
            Method(director.GetType(), "ClearDataEnemies").Invoke(director, null);
            UnityEngine.Object.Destroy(platform);

            // Forty exact overlaps in player attack range must spread, even when
            // pursuit is stopped. No per-enemy physics body or coroutine is involved.
            NetworkObject hostPlayer = host.LocalClient.PlayerObject;
            Vector3 originalPlayerPosition = hostPlayer.transform.position;
            Component hostMotor = hostPlayer.GetComponent(T("HoldTheFort.Player.HoldTheFortPlayerMotor"));
            Method(hostMotor.GetType(), "SetSimulationEnabled").Invoke(hostMotor, new object[] { false });
            hostPlayer.transform.position = center + Vector3.forward * 10;
            Vector3 cluster = hostPlayer.transform.position + Vector3.forward * 1.5f;
            for (int i = 0; i < 40; i++) {
                object[] spawnInCrowd = { cluster, Quaternion.identity, (uint)0 };
                Assert.IsTrue((bool)spawn.Invoke(sim, spawnInCrowd));
                authority.TryGetSlot((uint)spawnInCrowd[2], out int crowdSlot);
                authority.States[crowdSlot].NextAttack = double.MaxValue;
            }
            MethodInfo thinking = Method(sim.GetType(), "Think");
            bool attackedPlayer = false;
            for (int tick = 0; tick < 30; tick++) {
                for (int i = 0; i < authority.Count; i++) {
                    int crowdSlot = authority.ActiveSlots[i];
                    if (tick % 6 == 0) thinking.Invoke(sim, new object[] { crowdSlot, host.ServerTime.Time + tick / 30.0 });
                    attackedPlayer |= authority.States[crowdSlot].Behavior == EnemyBehavior.Attack;
                    movement.Invoke(sim, new object[] { crowdSlot, 1f / 30 });
                }
            }
            int spread = 0;
            for (int i = 0; i < authority.Count; i++)
                if ((authority.States[authority.ActiveSlots[i]].Position - cluster).sqrMagnitude > 0.0625f) spread++;
            Assert.IsTrue(attackedPlayer, "Exercise stationary player attackers, not just a chase formation.");
            Assert.Greater(spread, 10, "A player-bound crowd must not remain stacked at one position.");
            Method(director.GetType(), "ClearDataEnemies").Invoke(director, null);
            hostPlayer.transform.position = originalPlayerPosition;
            Method(hostMotor.GetType(), "SetSimulationEnabled").Invoke(hostMotor, new object[] { true });

            for (int i = 0; i < 3000; i++) {
                float angle = i * 2.39996323f;
                Vector3 position = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (25 + (i % 20) * 0.2f);
                object[] parameters = { position, Quaternion.identity, (uint)0 };
                Assert.IsTrue((bool)spawn.Invoke(sim, parameters));
            }
            uint firstId = authority.States[authority.ActiveSlots[0]].Id;
            Vector3 firstPosition = authority.States[authority.ActiveSlots[0]].Position;
            client = Manager("Data client", port);
            Assert.IsTrue(client.StartClient());
            float deadline = Time.realtimeSinceStartup + 20;
            Component remoteViews = client.GetComponent(T("EnemyPresentation"));
            while ((!client.IsConnectedClient || Store(remoteViews, "Roster").Count != 3000) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(client.IsConnectedClient);
            Assert.AreEqual(3000, Store(remoteViews, "Roster").Count);
            // Multiple managers in one test process share an audio scene; real machines do not.
            foreach (AudioListener listener in UnityEngine.Object.FindObjectsByType<AudioListener>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)) listener.enabled = false;
            Assert.Less(host.SpawnManager.SpawnedObjects.Count, 10, "Enemy IDs must not become NGO objects.");
            Assert.Less(client.SpawnManager.SpawnedObjects.Count, 10);
            authority.TryGetSlot(firstId, out int slot);
            Assert.Greater((authority.States[slot].Position - firstPosition).sqrMagnitude, 0.01f);
            foreach (NetworkClient connected in host.ConnectedClients.Values)
                if (connected.ClientId != 0 && connected.PlayerObject != null) {
                    Component motor = connected.PlayerObject.GetComponent(T("HoldTheFort.Player.HoldTheFortPlayerMotor"));
                    Assert.IsFalse((bool)motor.GetType().GetProperty("SimulationEnabled").GetValue(motor));
                    Assert.IsFalse(((Behaviour)connected.PlayerObject.GetComponent(
                        T("HoldTheFort.Player.HoldTheFortInputReader"))).enabled);
                }
            MethodInfo damage = Method(sim.GetType(), "DamageServer");
            Assert.IsTrue((bool)damage.Invoke(sim, new object[] { firstId, int.MaxValue, null }));
            Assert.IsFalse((bool)damage.Invoke(sim, new object[] { firstId, int.MaxValue, null }));
            deadline = Time.realtimeSinceStartup + 5;
            while (Store(remoteViews, "Roster").TryGetSlot(firstId, out _) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(Store(remoteViews, "Roster").TryGetSlot(firstId, out _));
            object[] reuse = { center + Vector3.forward * 30, Quaternion.identity, (uint)0 };
            Assert.IsTrue((bool)spawn.Invoke(sim, reuse));
            uint replacement = (uint)reuse[2];
            Assert.AreNotEqual(firstId, replacement);
            deadline = Time.realtimeSinceStartup + 5;
            while (!Store(remoteViews, "Roster").TryGetSlot(replacement, out _) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(Store(remoteViews, "Roster").TryGetSlot(replacement, out _));
            Assert.AreEqual(3000, Store(remoteViews, "Roster").Count);
            // Exercise the actual owner -> NGO RPC -> inventory -> numeric damage path.
            object[] isolated = { center + Vector3.right * 90, Quaternion.identity, (uint)0 };
            Assert.IsTrue((bool)spawn.Invoke(sim, isolated));
            uint shotTarget = (uint)isolated[2];
            authority.TryGetSlot(shotTarget, out int shotSlot);
            authority.States[shotSlot].NextDecision = double.MaxValue;
            deadline = Time.realtimeSinceStartup + 5;
            while (!Store(remoteViews, "Roster").TryGetSlot(shotTarget, out _) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(Store(remoteViews, "Roster").TryGetSlot(shotTarget, out int remoteSlot));
            Vector3 rendered = Store(remoteViews, "Roster").States[remoteSlot].Position;
            MethodInfo contact = Method(remoteViews.GetType(), "ResolvePlayerMotion");
            Vector3 blocked = (Vector3)contact.Invoke(remoteViews, new object[] {
                rendered + Vector3.back, Vector3.forward * 0.1f, 0.5f, 0.5f });
            Assert.Less(blocked.z, 0.05f, "Nearby numeric contact must obstruct inward movement.");
            Vector3 escaping = (Vector3)contact.Invoke(remoteViews, new object[] {
                rendered + Vector3.back, Vector3.back * 0.1f, 0.5f, 0.5f });
            Assert.AreEqual(-0.1f, escaping.z, 0.001f);
            NetworkObject localPlayer = client.LocalClient.PlayerObject;
            Assert.IsNotNull(localPlayer);
            var localMotor = localPlayer.GetComponent(T("HoldTheFort.Player.HoldTheFortPlayerMotor"));
            Assert.IsTrue((bool)localMotor.GetType().GetProperty("SimulationEnabled").GetValue(localMotor));
            Method(localMotor.GetType(), "SetSimulationEnabled").Invoke(localMotor, new object[] { false });
            ((Behaviour)localPlayer.GetComponent(T("PlayerLook"))).enabled = false;
            localPlayer.transform.position = authority.States[shotSlot].Position + new Vector3(0, 4, -15);
            NetworkObject serverPlayer = host.ConnectedClients[client.LocalClientId].PlayerObject;
            deadline = Time.realtimeSinceStartup + 5;
            while ((serverPlayer.transform.position - localPlayer.transform.position).sqrMagnitude > 0.01f &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.Less((serverPlayer.transform.position - localPlayer.transform.position).sqrMagnitude, 0.01f);
            Component shooter = localPlayer.GetComponent(T("PlayerShoot"));
            Camera camera = (Camera)shooter.GetType().GetField("playerCamera").GetValue(shooter);
            Component serverInventory = serverPlayer.GetComponent(T("HoldTheFort.Inventory.PlayerInventory"));
            MethodInfo getItem = Method(serverInventory.GetType(), "GetItem");
            UnityEngine.Object weapon = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Weapons/Revolver.asset");
            object magazine = Method(weapon.GetType(), "CreateDefaultInstance").Invoke(weapon, new object[] { 1 });
            object remainder = Method(serverInventory.GetType(), "AddItemServer").Invoke(serverInventory, new object[] { magazine });
            Assert.IsTrue((bool)remainder.GetType().GetProperty("IsEmpty").GetValue(remainder));
            int weaponSlot = -1;
            for (int i = 0; i < 6; i++) {
                object item = getItem.Invoke(serverInventory, new object[] { i });
                if ((int)item.GetType().GetField("itemId").GetValue(item) == 1) { weaponSlot = i; break; }
            }
            Assert.GreaterOrEqual(weaponSlot, 0);
            Component clientInventory = localPlayer.GetComponent(T("HoldTheFort.Inventory.PlayerInventory"));
            Method(clientInventory.GetType(), "SelectHotbarSlot").Invoke(clientInventory, new object[] { weaponSlot });
            Component held = localPlayer.GetComponent(T("HoldTheFort.Items.PlayerHeldItemController"));
            deadline = Time.realtimeSinceStartup + 5;
            while ((((NetworkVariable<int>)serverInventory.GetType().GetField("SelectedHotbarSlot").GetValue(serverInventory)).Value != weaponSlot ||
                held.GetType().GetProperty("CurrentDefinition").GetValue(held) != weapon) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(weapon, held.GetType().GetProperty("CurrentDefinition").GetValue(held));
            object initialItem = getItem.Invoke(serverInventory, new object[] { weaponSlot });
            int initialAmmo = (int)initialItem.GetType().GetField("ammoInMagazine").GetValue(initialItem);
            int itemId = (int)initialItem.GetType().GetField("itemId").GetValue(initialItem);
            for (int shot = 0; shot < 4; shot++)
            {
                Vector3 aim = authority.States[shotSlot].Position - camera.transform.position;
                camera.transform.rotation = Quaternion.LookRotation(aim);
                Method(shooter.GetType(), "RequestPrimaryUse").Invoke(shooter, null);
                // EditMode's test enumerator does not reliably honor WaitForSeconds
                // after EnterPlayMode. Explicitly wait on the gameplay cooldown clock.
                double shotDeadline = Time.timeAsDouble + 0.2;
                while (Time.timeAsDouble < shotDeadline) yield return null;
            }
            object afterShots = getItem.Invoke(serverInventory, new object[] { weaponSlot });
            int shotAmmo = (int)afterShots.GetType().GetField("ammoInMagazine").GetValue(afterShots);
            int shotHealth = authority.TryGetSlot(shotTarget, out int remainingSlot) ? authority.States[remainingSlot].Health : 0;
            Debug.Log($"[EnemyMigrationShotCheck] ammo={initialAmmo}->{shotAmmo} targetHP={shotHealth}");
            Assert.IsFalse(authority.TryGetSlot(shotTarget, out _), "Four revolver shots must kill the numeric target.");
            Assert.AreEqual(initialAmmo - 4, (int)afterShots.GetType().GetField("ammoInMagazine").GetValue(afterShots));
            Component bank = host.GetComponent(T("EnemyRewardBank"));
            Assert.AreEqual(2L, bank.GetType().GetProperty("PendingQuantity").GetValue(bank),
                "Numeric piles must preserve both rewards without networked pickup objects.");
            // Even bypassing the owner's local dead check cannot spend ammo on the host.
            Component serverHealth = serverPlayer.GetComponent(T("PlayerHealth"));
            Method(serverHealth.GetType(), "TakeDamage").Invoke(serverHealth, new object[] { int.MaxValue });
            Component localHealth = localPlayer.GetComponent(T("PlayerHealth"));
            deadline = Time.realtimeSinceStartup + 5;
            while ((bool)localHealth.GetType().GetProperty("IsAlive").GetValue(localHealth) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse((bool)localHealth.GetType().GetProperty("IsAlive").GetValue(localHealth));
            Method(shooter.GetType(), "RequestFireRpc").Invoke(shooter, new object[] {
                weaponSlot, itemId, camera.transform.forward, (uint)999999, (uint)0, default(RpcParams) });
            double deadRequestDeadline = Time.timeAsDouble + 0.2;
            while (Time.timeAsDouble < deadRequestDeadline) yield return null;
            object afterDead = getItem.Invoke(serverInventory, new object[] { weaponSlot });
            Assert.AreEqual(initialAmmo - 4, (int)afterDead.GetType().GetField("ammoInMagazine").GetValue(afterDead));
            // Reconnect must rebuild the live roster, never resurrect old IDs.
            client.Shutdown();
            while (client.IsListening || client.ShutdownInProgress) yield return null;
            Assert.AreEqual(0, Store(remoteViews, "Roster").Count);
            Assert.IsTrue(client.StartClient());
            deadline = Time.realtimeSinceStartup + 20;
            while ((!client.IsConnectedClient || Store(remoteViews, "Roster").Count != 3000) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(3000, Store(remoteViews, "Roster").Count);
            Assert.IsFalse(Store(remoteViews, "Roster").TryGetSlot(firstId, out _));
            Assert.IsFalse(Store(remoteViews, "Roster").TryGetSlot(shotTarget, out _));
            Assert.IsTrue(Store(remoteViews, "Roster").TryGetSlot(replacement, out _));
            Debug.Log("[EnemyMigrationTest] 3000 moving IDs replicated over loopback, exactly-once death and slot reuse verified. This is not an internet/rendering benchmark.");
        }
        finally
        {
            if (client != null) { client.Shutdown(); UnityEngine.Object.Destroy(client.gameObject); }
            if (host != null) { host.Shutdown(); UnityEngine.Object.Destroy(host.gameObject); }
            if (king != null) UnityEngine.Object.Destroy(king);
            if (owner != null) UnityEngine.Object.Destroy(owner);
            if (ground != null) UnityEngine.Object.Destroy(ground);
        }
        yield return null;
        yield return new ExitPlayMode();
    }
}

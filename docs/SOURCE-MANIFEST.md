# Source Selection Manifest

The files in this folder are exact copies from the current `enemy-ai` working tree. They were selected as one coherent execution path rather than as isolated “best looking” snippets.

## Included

| Folder | Files | Reason |
| --- | --- | --- |
| `src/simulation` | `EnemySimulation`, `EnemyDirector`, `EnemyDefinition`, `EnemyRewardBank` | Authority, scheduling, lifecycle routing, configuration, damage/death, and scalable reward handling |
| `src/data` | `EnemyStateStore`, `EnemySpatialGrid`, `EnemyProxyMath`, `EnemySteering`, `EnemyAttackSlots`, `EnemyBreachTracker` | Core data structures and algorithms supporting high-count simulation |
| `src/networking` | `EnemyReplication`, `EnemyWireCodec` | Per-peer bootstrap/lifecycle/motion protocol and compact serialization |
| `src/presentation` | `EnemyPresentation` | Client roster, snapshot playback, visual pooling, and local numeric collision queries |
| `src/combat` | `PlayerShoot`, `EnemyTargetRegistry`, `EnemyTargetable`, `KingHealth`, `PlayerHealth` | End-to-end authority and weapon-to-enemy damage context |
| `src/environment` | `BuildWall`, `WallHealth` | Real wall destruction path that records breach geometry for scheduled AI |
| `src/diagnostics` | `EnemyBenchmark` | The counters behind performance claims and future measurements |
| `tests` | `EnemyDataTests`, `EnemyHordeTests`, `EnemyDataRuntimeTests`, test asmdef | Algorithm tests and the 3,000-enemy NGO/UTP regression |

## Intentionally omitted

- Legacy per-enemy `EnemyAgent`, `EnemyMotor`, `EnemyAttack`, `EnemyHealth`, `EnemyPool`, and `EnemyAIScheduler`. They remain in the game but are not the active mass-fodder path.
- `EnemySpawner`, because its current stress UI and hotkeys are test convenience rather than core architecture.
- Inventory/item/held-item/weapon-definition code. `PlayerShoot` references these existing systems, but copying the full gameplay graph would turn this into a partial game dump.
- Player movement implementation. `EnemyPresentation.ResolvePlayerMotion` is present, but the owner adapter and `CharacterController` motor are outside the showcase's enemy/network focus.
- Prefabs, scenes, ScriptableObjects, `.meta` files, art, materials, shaders, and package source.

## Independent compilation

The snapshot cannot compile independently. In addition to Unity/NGO packages, it references omitted project types such as `PlayerInventory`, `PlayerHeldItemController`, `HoldTheFortInputReader`, `WeaponDefinition`, `WeaponFireMode`, `ItemDefinition`, and `ItemInstanceData`. `EnemyDataRuntimeTests` also loads project prefabs/assets by their original `Assets/...` paths and uses reflection to cross the game's assembly boundary.

This is deliberate: no replacement mocks or simplified reimplementations were added, because the goal is to show the real production-in-progress code and document its context honestly.


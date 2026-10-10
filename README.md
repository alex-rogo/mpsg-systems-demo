# Large-Scale Enemy Simulation Showcase

This folder is a curated technical snapshot of **Hold the Fort**, a Unity cooperative survival/horde game being designed for as many as four players and encounters involving thousands of basic melee enemies.

The engineering problem is not simply drawing many meshes. The server must own enemy spawning, target selection, movement, attacks, health, death, rewards, and hit validation while remote clients receive timely motion within a bounded bandwidth budget. A conventional `NetworkObject`/`NetworkTransform`/physics body per fodder enemy did not fit that goal.

## What the implementation does

The active fodder path uses one server-owned `EnemySimulation` and compact numeric records stored in preallocated arrays. Expensive decisions are scheduled at 5 Hz by default; movement integrates at 30 Hz. A custom XZ spatial grid supports bounded crowd samples, exhaustive radius queries, and grid-traversed capsule hit tests. Enemy lifecycle events are sent reliably, while absolute motion snapshots are sent unreliably in compact batches under a per-client byte budget. Clients keep an eight-sample history per enemy, interpolate on a delayed timeline, apply bounded prediction, and drive pooled visual-only transforms.

The result is a deliberately hybrid Unity architecture: Netcode for GameObjects still handles sessions, players, ownership, health synchronization, and RPC-based weapon requests, while mass fodder uses a purpose-built replicated data model.

## Highlights

- Stable enemy IDs with O(1) slot reuse and swap removal in `EnemyStateStore`.
- Centralized server simulation with separate decision and movement rates.
- Allocation-conscious linked-cell spatial partitioning and numeric capsule hit detection.
- Reliable lifecycle plus unreliable, absolute, chunk-relative motion records—no fragile unacknowledged delta baseline.
- Fixed-history client interpolation with adaptive delay, capped extrapolation, and correction-only smoothing.
- Server-authoritative firing: clients send aim and a bounded visual target hint, while the server derives origin, weapon stats, ammo, cooldown, obstruction, hit, and damage.
- Visual-only pooling and aggregated numeric rewards instead of thousands of networked physics pickups.
- Automated host/client regression coverage for 3,000 moving IDs, shooting, death, slot reuse, disconnect, and reconnect.

## Evidence, stated carefully

The latest isolated Unity test run passed **69/69 tests**. Its integration test runs an NGO/Unity Transport host and client on loopback with 3,000 numeric enemies. This proves important correctness properties; it is not a rendering or internet benchmark.

A separate user-supplied two-machine diagnostic run recorded a host at approximately 5,700 enemies between **77.6 and 94.6 FPS** in the sampled windows. The custom enemy stream was capped near **250 KB/s** and the host reported roughly **3.1–3.8 ms** average simulation time and **2.9–3.1 ms** average presentation time in those windows. Exact processor/GPU models were not captured, the matching client log did not cover the same 5,700-enemy interval, and this run predates the latest interpolation and breach-routing changes. Therefore this repository does **not** claim that every machine or network runs 5,000 enemies without lag.

See [PERFORMANCE.md](PERFORMANCE.md) for the complete evidence boundary and a reproducible benchmark plan.

## Repository map

- [`src/simulation`](src/simulation): authority, lifecycle, AI scheduling, movement, damage, and reward aggregation.
- [`src/data`](src/data): compact state, spatial grid, proxy geometry, steering, attack slots, and breach memory.
- [`src/networking`](src/networking): wire format and per-peer replication.
- [`src/presentation`](src/presentation): client roster, interpolation, and visual pooling.
- [`src/combat`](src/combat): server-authoritative targeting, health, and hitscan integration.
- [`src/environment`](src/environment): the wall/breach connection used by the numeric AI.
- [`src/diagnostics`](src/diagnostics): opt-in aggregate benchmark counters.
- [`tests`](tests): selected unit and NGO/UTP integration tests.
- [ARCHITECTURE.md](ARCHITECTURE.md): execution paths and design tradeoffs.
- [LICENSE-REVIEW.md](LICENSE-REVIEW.md): publication and dependency review.
- [`docs/SOURCE-MANIFEST.md`](docs/SOURCE-MANIFEST.md): why each file was selected and what was intentionally omitted.

## How to explore

Start with `EnemySimulation.Update`, then follow `EnemyStateStore`, `EnemySpatialGrid`, and `EnemyReplication.LateUpdate`. On the client, follow `EnemyReplication.ReceiveMotion` into `EnemyPresentation.Apply` and `EnemySnapshotTrack.Render`. For combat, trace `PlayerShoot.RequestPrimaryUse` through `RequestFireRpc`, `EnemySimulation.Raycast`, and `DamageServer`.

This is a source showcase, **not a standalone Unity project**. It intentionally omits scenes, prefabs, art, package caches, inventory/UI code, and most ordinary gameplay systems. The copied files are exact snapshots of the game source and retain references to omitted project types and Unity packages.

These systems are still in active development.


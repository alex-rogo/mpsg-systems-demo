# Performance and Evidence

## Implemented scaling mechanisms

The following are verified in the copied source:

| Area | Mechanism | Primary code |
| --- | --- | --- |
| Gameplay objects | Basic fodder is stored as numeric records; it does not spawn one NGO object, rigidbody, collider, or MonoBehaviour graph per enemy | `EnemySimulation`, `EnemyStateStore` |
| Scheduling | Decisions default to 5 Hz with a 512/frame visit cap; movement defaults to 30 Hz with two-step catch-up | `EnemySimulation.Update` |
| Memory/lifecycle | Preallocated arrays, free-slot stack, stable ID map, swap removal | `EnemyStateStore` |
| Neighborhoods | Linked-cell XZ grid; bounded rotating steering samples; exhaustive combat queries | `EnemySpatialGrid` |
| Hits | DDA cell traversal and numeric upright-capsule intersection | `EnemySpatialGrid.RaycastCapsules`, `EnemyProxyMath` |
| Walls | Cached short corridor casts; breach validation uses one shared 64-query/frame budget | `EnemySimulation`, `EnemyBreachTracker` |
| Replication | Batched compact records, per-peer token budget, reliable lifecycle, unreliable absolute motion | `EnemyReplication`, `EnemyWireCodec` |
| Client motion | Eight-sample inline history, adaptive interpolation delay, bounded prediction | `EnemySnapshotTrack` |
| Rendering | Pooled visual-only transforms, prewarm and cold-creation budget | `EnemyPresentation` |
| Rewards | Numeric cell aggregation instead of a networked physics pickup per death | `EnemyRewardBank` |
| Diagnostics | Opt-in 10-second aggregate stage/network/interpolation counters | `EnemyBenchmark` |

Hot paths avoid LINQ and per-enemy coroutines. Most recurring buffers are retained arrays. This does not imply zero allocation everywhere: dictionaries allocate during growth, visual instantiation allocates, `FastBufferWriter(Allocator.Temp)` is created per packet, and the transitional collider hit buffer can resize when saturated.

## Confirmed automated result

Fresh showcase-verification run: Unity **6000.5.5f1**, EditMode test runner in an isolated project copy, completed 2026-10-08 03:52:20Z.

- 69 total, 69 passed, 0 failed, 0 skipped.
- Includes pure tests for state reuse, grids, ray/capsule math, wire codec, interpolation, steering, slots, and breach routing.
- Includes an NGO 2.13.2 / Unity Transport loopback host-client integration test with 3,000 moving numeric IDs.
- The integration test verifies fewer than ten spawned NGO objects despite 3,000 enemies, owner/remote player simulation separation, exactly-once death, unique ID reuse, numeric player contact, four owner-client weapon RPCs consuming four rounds and killing a 100-HP target, reward preservation, dead-player fire rejection, disconnect cleanup, and roster reconstruction after reconnect.

This is a functional stress regression under `-batchmode -nographics`. It does **not** measure GPU rendering, internet transport behavior, four-player load, or representative frame rate.

## Observed two-machine diagnostics

The project also contains an opt-in `[EnemyBench]` logger. User-supplied standalone-player logs from a reported two-machine remote/VPN test provide observational evidence, not a controlled benchmark.

The strongest host log captured:

| Live enemies | Samples | Observed FPS range | Avg simulation | Avg host presentation | Custom enemy TX |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 4,197 | 29 | 112.8–152.3 | 1.63 ms | 2.03 ms | ~250 KB/s |
| 4,947 | 27 | 86.0–123.2 | 2.35 ms | 2.48 ms | ~250 KB/s |
| 5,657–5,697 | 13 combined | 77.6–94.6 | approximately 3.1–3.8 ms | approximately 2.9–3.1 ms | ~250 KB/s |

The same log reported about 11,200 enemy records/second and RTT samples around 50–63 ms in the >5,600-enemy windows. It exposed only 12 logical processors and 8,018 MB graphics memory; exact CPU and GPU models were not captured.

A separate client log covered up to 4,000 enemies, with 329.5–385.5 FPS across eleven 10-second samples at that count, approximately 253 KB/s custom enemy receive payload, and 50–55 ms RTT. That client exposed 16 logical processors and 10,051 MB graphics memory, but again no exact models. It also showed population-dependent sample age and a 678 ms outlier at 1,000 enemies.

Important caveats:

- The client and >5,600 host evidence are not a matched, synchronized 5,000-enemy pair.
- The logs predate the latest eight-sample interpolation and breach-response changes.
- Reported bytes cover custom enemy application payload, not UDP/IP/UTP overhead.
- Stage times are averages per rendered frame; worst-frame counters do not attribute spikes.
- Very high uncapped FPS on simple proxy visuals does not predict performance with final skinned models and animation.
- No packet-loss percentage or transport receive-queue depth was recorded.

Accordingly, the accurate claim is: **the architecture has automated 3,000-enemy network correctness coverage and has been observed simulating roughly 5,700 proxy enemies on a networked host above 60 FPS in sampled windows. End-to-end 5,000-enemy client smoothness is not yet proven.**

## Reproducible benchmark plan

1. Build matching Development builds from the same commit/working-tree snapshot; avoid profiling one peer in the Editor.
2. Record exact CPU, GPU, RAM, OS, resolution, quality level, build hash, transport route, and host/client role.
3. Use one host plus one remote client first, then repeat with three clients.
4. Clear between plateaus. Test 250, 1,000, 2,000, 3,000, 4,000, and 5,000 enemies for at least 60 seconds each after spawn/prewarm settles.
5. Enable F11 on every peer and retain complete `[EnemyBench]` lines. Record median/1% low FPS, worst frame, simulation/replication/presentation time, custom bytes and records per second, RTT, oldest/peak sample age, snapshot gap, render delay, prediction percentage, allocation delta, rejected packets, and breach query peaks.
6. At each plateau, move through the crowd, fire repeatedly, kill batches, break/rebuild a wall, disconnect/reconnect the client, and check for receive-queue warnings.
7. Capture standalone-player Unity Profiler traces for representative steady-state and spike windows. Keep Deep Profile disabled for headline numbers.
8. Repeat with final-quality models separately; compare proxy, animation-disabled, animation-LOD, and full-animation cases.

## Known bottlenecks and next measurements

- Motion bandwidth reaches its configured budget before the largest populations, reducing per-enemy update cadence. Nearby-interest prioritization is not implemented.
- Server movement is still an O(N) pass at 30 Hz; grounding and wall physics need profiling on real terrain.
- Every visible enemy still has a Unity transform/renderer. Skinned meshes and animators may dominate long before numeric simulation does.
- Lifecycle bursts remain reliable and could pressure the transport during mass spawn/death.
- The three-peer replication array encodes the current four-player product assumption rather than a general connection-count system.
- Reward aggregation can grow with many distinct occupied cells.
- No Jobs/Burst/ECS, terrain-height cache, distance-based AI tiers, or visibility/animation LOD is implemented. These are possible future optimizations, not current achievements.

## Recommended captures for a public portfolio

- Unity Profiler CPU Timeline showing `EnemySimulation`, `EnemyReplication`, and `EnemyPresentation` at 3,000 and 5,000 enemies.
- Network Profiler or transport statistics showing total bytes/packets alongside the custom payload counters.
- A synchronized host/client video with the same enemy count and visible F11 summaries.
- A frame-time graph with median, p95, p99, and worst frame, not only FPS.
- A screenshot of the numeric proxy scene and a separate final-model test so rendering cost is not conflated with simulation cost.

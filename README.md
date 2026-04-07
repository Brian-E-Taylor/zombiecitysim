# Zombie City Sim

A large-scale agent-based simulation built with **Unity 6 DOTS** (Data-Oriented Technology Stack). Thousands of zombies hunt humans through a procedurally generated city, with line-of-sight, hearing, and pathfinding all running at interactive frame rates.

![Zombie City Sim](https://i.ibb.co/6RgyHKD/Unity-h-CMxx-Mafnv.gif)

## Why DOTS / ECS?

Traditional Unity uses GameObjects and MonoBehaviours: each entity is a heap-allocated object with virtual dispatch, scattered across memory. At 100 agents this is fine. At 10,000 it falls apart -- cache misses dominate, the main thread serializes everything, and the GC pauses add up.

The Entity Component System (ECS) flips this around:

- **Components** are plain structs (no inheritance, no heap) stored in tightly packed arrays called archetypes. Iterating 10,000 `GridPosition` values touches contiguous memory, so the CPU prefetcher works instead of fighting you.
- **Systems** are stateless functions that operate on component queries. Because they declare what they read and write, the job scheduler can run them in parallel across cores with no manual locking.
- **Burst Compiler** takes the C# in systems/jobs and emits SIMD-vectorized native code. A Bresenham line-of-sight check that would be "fast enough" in managed C# becomes nearly free when Burst auto-vectorizes the inner loop.

The result: the simulation runs the same logic as a naive OOP version, but the data layout and execution model let it scale to 10,000+ agents where the OOP version would be single-digit FPS.

## Architecture Overview

The simulation runs in a fixed pipeline of **system groups** that execute in order every frame:

```
InitialGroup          hash spatial data, prepare caches
    |
MoveUnitsGroup        decide where every agent wants to go
    |
DamageGroup           resolve combat between adjacent agents
    |
EndGroup              advance turn counters, animate movement
```

Each group contains multiple systems that the job scheduler runs in parallel when their data dependencies allow it.

### The Turn Model

The simulation is turn-based at configurable speed (default: as fast as possible, `turnDelayTime = 0`). Each agent type has a **turn delay** -- zombies act every 5 turns, humans every 3. The `AdvanceTurnSystem` decrements per-entity counters and enables a `TurnActive` component tag when an agent's turn arrives. All movement and damage systems filter on `TurnActive`, so agents that aren't acting this turn are skipped entirely with zero cost.

This means the effective per-frame workload is a fraction of the total agent count, which is critical for scaling.

## Spatial Hashing

The single most important optimization. Every system that asks "what's near this position?" needs an answer in O(1), not O(n).

### Position Hash Maps

Grid positions are encoded into collision-free `uint` keys by bit-packing:

```
key = (x & 0xFFFF) | (z << 16)
```

This supports grids up to 65,536 x 65,536 and guarantees no hash collisions -- every unique position maps to a unique key. These keys index into `NativeParallelHashMap` containers, giving O(1) lookup for "is there an entity at position (x, z)?".

Three categories of hash maps are maintained:

- **Static collidables** (buildings, walls) -- hashed once, rebuilt only when geometry changes via a change filter
- **Dynamic collidables** (humans, zombies) -- cleared and rebuilt every frame using a pooled map
- **Shared unit positions** (humans, zombies separately) -- computed once in `InitialGroup`, then read by movement and damage systems downstream

The shared position maps eliminate redundant work: without them, every system that needs "where are the humans?" would hash them independently. With the shared maps, hashing happens once and four systems read the result.

### Cell-Based Broadphase

Checking every position within vision range against a hash map is still O(vision_area). A second layer of hashing groups entities into coarse cells:

```
cell_size = vision_distance * 2 + 1
cell_key  = hash(position / cell_size)
```

Before doing a detailed search, each agent checks whether its cell (and adjacent cells) contain any potential targets. If not, the entire vision scan is skipped. In a city where most agents are separated by buildings, this early-out eliminates the majority of work.

## Line-of-Sight

Zombies only chase humans they can actually see. Visibility is checked with Bresenham's line algorithm, which steps through each grid cell between source and target and checks for static obstacles. This is exact (no false positives from approximation) and branch-light, which Burst compiles efficiently.

### LOS Cache

Multiple zombies often check LOS to the same human, and the human checks LOS back to those same zombies. Computing Bresenham twice for the same pair is waste.

The LOS cache is a per-frame `NativeParallelHashMap<ulong, byte>` keyed by packed source-target position pairs. The key is **symmetric** -- `LOS(A, B)` and `LOS(B, A)` produce the same key since Bresenham traverses the same cells in both directions. This means a zombie checking visibility to a human automatically caches the result for when that human checks visibility back to flee.

Cache writes use `TryAdd` on a `ParallelWriter`, which is atomic and ignores duplicates. This makes the cache safe for concurrent job writes without locks or synchronization.

The cache persists across frames and is only cleared when static collidables change (detected via `IsValid` on `LOSCacheComponent`). Its primary benefit is intra-frame deduplication, but it also reuses results across frames as long as the static geometry is unchanged. The capacity grows as needed but is never proactively shrunk.

## Zombie AI

Zombie decision-making follows a priority hierarchy:

1. **Vision** (range: 8 tiles) -- Scan outward ring by ring. For each human found, verify LOS through buildings. Chase the nearest visible human using Manhattan-distance pathfinding with fallback to adjacent cardinal directions.
2. **Hearing** (range: 16 tiles) -- If no human is visible, check for audible events. Audible events are created when humans move near zombies and persist for 20 turns, decaying naturally. Zombies move toward the sound source position.
3. **Random walk** -- If nothing is detected, pick a random open direction.

Pathfinding is deliberately simple: move one step toward the target along the dominant axis, fall back to the other axis if blocked. This avoids the cost of A* for thousands of agents and produces emergent crowd behavior as zombies funnel through streets and alleys.

### Human AI

Humans check for visible zombies and flee in the opposite direction. The escape direction is computed as the **average position of all visible zombies**, so a human surrounded on two sides will flee perpendicular rather than toward either group. If no zombies are visible, humans wander randomly.

## Combat

Damage is calculated spatially: each active attacker checks the 8 adjacent cells for targets using hash map lookups. Damage amounts are accumulated in a `NativeParallelMultiHashMap` keyed by target position, then applied in a second pass. This two-phase approach allows damage from multiple attackers to be resolved correctly in parallel.

Dead humans are converted to zombies at their death position, creating an expanding horde dynamic.

## Procedural City Generation

The city is generated through four layered systems that build on each other:

### 1. L-System Arterial Roads

Organic main roads are generated using an L-system grammar:

```
Axiom: A
A -> F[+A]F[-A]FA    (grow forward, branch left and right)
F -> FF              (double segment length)
```

A turtle graphics interpreter traces the expanded string, with `F` drawing road segments, `+/-` rotating by 45 degrees, and `[/]` pushing/popping state for branches. This produces naturally branching road networks.

The generation runs in four phases: spine roads (primary cross-city arteries), L-system branches from edge and interior seed points, connector roads linking nearby endpoints, and Bresenham rasterization to the tile grid.

### 2. BSP Block Subdivision

Binary Space Partitioning recursively divides the remaining space into city blocks. At each level, the space is split along its longer axis with configurable variance. Road width decreases with depth (5 tiles for arterials down to 3 for side streets), creating a natural hierarchy.

The BSP respects pre-placed arterial roads: if a proposed split overlaps more than 30% with existing L-system roads, it's skipped and the space becomes a single block.

### 3. Building Templates

A flood-fill algorithm detects contiguous building regions and classifies them by size and shape (small, medium, large; square, elongated, irregular). Templates are assigned based on classification:

- Small regions get solid fill
- Medium regions get L-shapes or small courtyards
- Large regions get U-shapes, courtyards with passages, or complex multi-building arrangements

Height variation is applied per-building for visual diversity.

### 4. Alley Generation

Large building regions receive alleys carved by random walks from region edges toward the centroid. The walk is guided by a scoring heuristic that balances direction toward center with random exploration, and alleys can terminate early as dead-ends with configurable probability.

## Rendering

### Procedural Mesh Batching

Buildings are not individual GameObjects. Instead, the city mesh generator groups buildings into 16x16 tile spatial cells, each becoming a single mesh with a single `MeshRenderer`. This serves two purposes:

- **Draw call reduction**: thousands of buildings collapse into dozens of draw calls
- **Frustum culling granularity**: each cell has tight bounds, so the camera only renders visible chunks. From most viewpoints roughly half the cells are culled.

Each building is a 5-face cube (bottom face omitted since it sits on the ground plane), with vertex colors encoding height for visual distinction. The custom shader uses half-Lambert diffuse lighting with shadow support and GPU instancing.

### Agent Rendering

Agents use Unity's ECS hybrid rendering with `URPMaterialPropertyBaseColor` for per-entity color updates. Health is visualized as color intensity: full-health humans are bright green, damaged humans darken toward red. Zombies follow the inverse pattern. The color update happens in the damage job with no separate rendering pass.

## Memory Management

### Pooled Hash Maps

Every per-frame hash map is pooled: allocated once with `Allocator.Persistent`, then cleared and capacity-checked each frame rather than disposed and reallocated. This eliminates the allocation churn that would otherwise dominate frame time at scale.

The pattern is consistent across all systems:

```
map.Clear()
if map.Capacity < needed:
    map.Capacity = needed * 1.2
```

The 1.2x growth factor provides headroom so capacity adjustments are rare after the first few frames.

### Change Filters

Static collidable hash maps use ECS change filters to skip rebuilding when nothing has changed. Since buildings don't move, the static hash map is typically built once and reused for the entire session. Only dynamic collidables (moving agents) are rehashed each frame.

## Configuration

Key parameters exposed in `GameController`:

| Parameter | Default | Effect |
|-----------|---------|--------|
| Grid size | 900x900 | City dimensions in tiles |
| Humans | 20,000 | Starting human population |
| Zombies | 10 | Starting zombie count |
| Zombie vision | 8 tiles | Detection range (LOS required) |
| Zombie hearing | 16 tiles | Sound detection range |
| Human vision | 10 tiles | Flee trigger range |
| Turn delay | 0 ms | Simulation speed (0 = as fast as possible) |
| Zombie turn delay | 5 | Zombies act every N turns |
| Human turn delay | 3 | Humans act every N turns |
| Audible decay | 20 turns | Sound event lifetime |

All values are adjustable at runtime via UI sliders and input fields. The city can be regenerated with a new seed without restarting.

## Potential Future Work

- **A* or flow-field pathfinding** -- The current Manhattan-distance movement gets zombies stuck on concave building corners. A flow field computed once per frame from the zombie horde's center of mass would give globally optimal pathing at O(grid_size) cost shared across all agents.
- **Spatial partitioning for vision queries** -- The current ring-scan checks every cell on each perimeter ring including corners outside the circular vision radius. A spatial structure (quadtree or grid-aligned circle lookup table) would reduce unnecessary hash lookups at larger vision distances.
- **Population equilibrium mechanics** -- Dead humans always convert to zombies, so the zombie population grows monotonically. Survivor spawning, safe zones, or zombie decay would create more interesting long-term dynamics.
- **Mesh LOD or instanced rendering for agents** -- At very high agent counts (50k+), the per-entity rendering becomes the bottleneck rather than simulation logic. Shader-based instanced rendering or impostor sprites for distant agents would push the ceiling higher.
- **Parallel city generation** -- The spawn system runs on the main thread due to managed code dependencies (procedural mesh generation). Splitting generation into a Burst-compiled job for tile layout and a main-thread pass for mesh creation would reduce regeneration stalls.
- **NativeParallelHashMap disposal on regeneration** -- When the city is regenerated, singleton entities holding native containers are destroyed without explicitly disposing the containers. Adding disposal hooks before entity destruction would prevent these minor memory leaks.

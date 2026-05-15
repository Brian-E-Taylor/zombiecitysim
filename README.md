# Zombie City Sim

A large-scale agent-based simulation built with Unity 6 DOTS. Thousands of zombies hunt humans through a procedurally generated city with line-of-sight, hearing, and pathfinding.

![Zombie City Sim](https://i.ibb.co/6RgyHKD/Unity-h-CMxx-Mafnv.gif)

## Architecture

Systems run in a fixed pipeline of groups every frame:

```
InitialGroup     -> hash collidables and unit positions
MoveUnitsGroup   -> AI decides desired moves, resolver applies them
DamageGroup      -> resolve combat, kill, spawn new zombies
EndGroup         -> advance turn counters, animate movement
```

### Turn Model

The sim is turn-based with a configurable tick delay (default 0). Each agent has a per-type turn delay (zombies act every 5 turns, humans every 3). `AdvanceTurnSystem` decrements per-entity counters and toggles a `TurnActive` enableable component. All movement and damage queries filter on `TurnActive`, so inactive agents are skipped at the chunk level — the per-frame workload is a fraction of total population.

## Spatial Hashing

The core optimization. Every "what's near me?" query needs to be O(1).

Grid positions pack into collision-free `uint` keys:

```
key = (x & 0xFFFF) | (z << 16)
```

Supports grids up to 65,536² with no hash collisions. Keys index `NativeParallelHashMap` containers built once per frame in `InitialGroup`:

- **Static collidables** (buildings) — built with a change filter, so the rebuild is skipped while geometry is stable.
- **Dynamic collidables** (all agents) — cleared and rebuilt each frame from a pooled map.
- **Shared unit positions** (humans and zombies, separately) — built once, read by every downstream movement and damage system. Without this, four systems would each hash agents independently.

### Cell Broadphase

Vision queries layer a coarser cell hash on top:

```
cell_size = vision_distance * 2 + 1
cell_key  = hash(position / cell_size)
```

Each agent first checks whether its cell or neighbors contain any candidates before walking the vision ring. In a city where most agents are blocked by buildings, the early-out skips the bulk of LOS work.

## Line-of-Sight

Bresenham through the static collidable hash, exact and Burst-friendly. There is no LOS cache — recomputing per-pair turned out to be cheaper than the cache bookkeeping in dense scenes, since most LOS calls early-out on the first or second cell.

## Zombie AI

Priority order:

1. **Vision** (8 tiles) — ring scan outward, LOS-verify each candidate, chase the nearest.
2. **Hearing** (16 tiles) — humans moving near zombies emit `Audible` events that persist for 20 turns. If nothing is visible, move toward the loudest source.
3. **Random walk** otherwise.

Pathfinding is intentionally one step toward the target on the dominant axis with cardinal fallback. Cheap per agent, and the funnel behavior through streets is an emergent benefit, not a limitation worth fixing for this scale.

## Human AI

Flee from the average position of all visible zombies — surrounded humans escape perpendicular instead of toward one of the groups. Wander otherwise.

## Combat

Each active attacker scans the 8 adjacent cells via the unit-position hash. Damage accumulates in a `NativeParallelMultiHashMap` keyed by target position, then applies in a second pass — two phases so multiple attackers on one target resolve correctly under parallel jobs.

Dead humans respawn as zombies in place.

## Procedural City Generation

Four layered passes:

### 1. L-System Arterial Roads

Standard turtle interpreter on `A -> F[+A]F[-A]FA`, `F -> FF`, with `+/-` rotating 45° and `[/]` for branch state. Four phases: spine arteries, edge and interior L-system seeds, connector roads between nearby endpoints, then Bresenham rasterization to the tile grid.

### 2. BSP Block Subdivision

Recursive split along the longer axis with variance. Road width tapers with depth (5 tiles for arterials down to 3 for side streets). A proposed split is skipped if it overlaps existing L-system roads by more than 30%, leaving that subtree as a single block.

### 3. Building Templates

Flood-fill detects contiguous building regions and classifies them by size and shape. Small regions get solid fill; medium get L-shapes or small courtyards; large get U-shapes, courtyards with passages, or compound arrangements. Per-building height variation for visual differentiation.

### 4. Alleys

Large regions get alleys carved by random walks from the edge toward the centroid, biased by a centroid-attraction score, with a configurable dead-end probability.

## Rendering

### City Mesh Batching

Buildings are not GameObjects. The mesh generator groups them into 16×16 tile cells and emits one mesh per cell with one `MeshRenderer`. Two wins: dramatically fewer draw calls, and tight per-cell bounds make frustum culling effective — most viewpoints cull about half the cells.

Buildings are 5-face cubes (no bottom face) with vertex-color-encoded height. The shader is half-Lambert with shadows and GPU instancing.

### Agents

Hybrid renderer with `URPMaterialPropertyBaseColor` for per-entity color. Health is encoded as color intensity — humans desaturate from green toward red as they take damage; zombies invert. Color writes happen inside the damage job, no separate pass.

## Memory

### Pooled Native Containers

Per-frame hash maps allocate once with `Allocator.Persistent`, then `Clear()` and capacity-check each frame:

```
map.Clear()
if map.Capacity < needed:
    map.Capacity = needed * 1.2
```

Eliminates the per-frame allocation churn that would otherwise dominate at scale. The 1.2× growth keeps capacity bumps rare after warmup.

### Change Filters on Static Data

The static collidable hash uses ECS change filters. Buildings don't move, so the static hash is built once per city and reused for the rest of the session.

## Configuration

Exposed on `GameController`, all live-editable via UI:

| Parameter | Default | Effect |
|-----------|---------|--------|
| Grid size | 900×900 | City dimensions in tiles |
| Humans | 20,000 | Starting population |
| Zombies | 10 | Starting population |
| Zombie vision | 8 | LOS-gated detection range |
| Zombie hearing | 16 | Audible event range |
| Human vision | 10 | Flee trigger range |
| Turn delay | 0 ms | 0 = uncapped |
| Zombie turn delay | 5 | Acts every N turns |
| Human turn delay | 3 | Acts every N turns |
| Audible decay | 20 turns | Sound event lifetime |

City regenerates with a new seed at runtime without a domain reload.

## Potential Future Work

- **Flow-field pathfinding** — current dominant-axis movement gets zombies stuck on concave corners. A flow field computed per frame from the horde center would give global pathing at O(grid) shared across all agents.
- **Population dynamics** — humans always become zombies, so the curve is monotonic. Survivor spawning, safe zones, or zombie decay would create longer-term equilibria.

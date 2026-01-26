using Unity.Collections;
using Unity.Mathematics;

public struct TurtleState
{
    public float2 Position;
    public float Angle;  // radians
}

public struct LineSegment
{
    public float2 Start;
    public float2 End;
}

public static class LSystemArterialGenerator
{
    private const int MaxStringLength = 4096;

    /// <summary>
    /// Generates organic arterial roads using L-System grammar, turtle graphics, and rasterization.
    /// Creates a comprehensive road network with spines, branches, and connectors.
    /// </summary>
    public static void GenerateArterials(
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        int numTilesX, int numTilesY,
        int iterations,
        float branchAngleDegrees,
        float segmentLength,
        int roadWidth,
        int numSeeds,
        ref Random rng)
    {
        // Skip for very small grids
        if (numTilesX < 50 || numTilesY < 50)
            return;

        var branchAngleRadians = math.radians(branchAngleDegrees);
        var segments = new NativeList<LineSegment>(512, Allocator.Temp);
        var endpoints = new NativeList<float2>(64, Allocator.Temp);

        // Phase 1: Generate primary spine roads (cross-city arterials)
        GenerateSpineRoads(
            ref segments,
            ref endpoints,
            numTilesX, numTilesY,
            segmentLength,
            ref rng);

        // Phase 2: Generate L-System branches from multiple seed points
        var lSystemString = ExpandGrammar(iterations, Allocator.Temp);

        // Scale branch segment length with map size
        var branchSegmentLength = segmentLength * math.clamp((float)math.min(numTilesX, numTilesY) / 200f, 0.8f, 2.5f);

        // Edge seeds (pointing inward)
        var edgeSeeds = numSeeds;
        for (var seed = 0; seed < edgeSeeds; seed++)
        {
            GetEdgeSeedPoint(seed, edgeSeeds, numTilesX, numTilesY, out var startPos, out var startAngle, ref rng);

            InterpretTurtle(
                ref lSystemString,
                startPos,
                startAngle,
                branchSegmentLength,
                branchAngleRadians,
                ref segments,
                ref endpoints,
                numTilesX,
                numTilesY,
                ref rng);
        }

        // Internal seeds - fewer, just to add variety
        var internalSeeds = math.max(1, numSeeds / 3);
        for (var seed = 0; seed < internalSeeds; seed++)
        {
            var startPos = new float2(
                rng.NextFloat(numTilesX * 0.25f, numTilesX * 0.75f),
                rng.NextFloat(numTilesY * 0.25f, numTilesY * 0.75f)
            );
            var startAngle = rng.NextFloat(0, math.PI * 2);

            InterpretTurtle(
                ref lSystemString,
                startPos,
                startAngle,
                branchSegmentLength * 0.8f,
                branchAngleRadians * 1.1f,
                ref segments,
                ref endpoints,
                numTilesX,
                numTilesY,
                ref rng);
        }

        lSystemString.Dispose();

        // Phase 3: Connect nearby endpoints to form a network (conservative)
        GenerateConnectorRoads(
            ref segments,
            ref endpoints,
            numTilesX, numTilesY,
            branchSegmentLength * 1.5f, // Max connection distance
            ref rng);

        // Phase 4: Rasterize all segments to the grid
        RasterizeSegments(
            ref segments,
            roadWidth,
            ref tileExists,
            ref roadHierarchy,
            numTilesX,
            numTilesY);

        segments.Dispose();
        endpoints.Dispose();
    }

    /// <summary>
    /// Generates primary spine roads that cross the city from edge to edge.
    /// These form the backbone of the road network.
    /// </summary>
    private static void GenerateSpineRoads(
        ref NativeList<LineSegment> segments,
        ref NativeList<float2> endpoints,
        int numTilesX, int numTilesY,
        float segmentLength,
        ref Random rng)
    {
        // Scale segment length with map size for spines
        var spineSegmentLength = math.max(segmentLength, math.min(numTilesX, numTilesY) / 20f);

        // Horizontal spines - cap at 3-4 regardless of map size
        var numHorizontalSpines = math.clamp(numTilesY / 150, 2, 4);
        for (var i = 0; i < numHorizontalSpines; i++)
        {
            var yBase = (i + 1f) / (numHorizontalSpines + 1f) * numTilesY;
            var current = new float2(2, yBase + rng.NextFloat(-10, 10));

            while (current.x < numTilesX - 3)
            {
                var next = current + new float2(
                    spineSegmentLength + rng.NextFloat(-5, 5),
                    rng.NextFloat(-spineSegmentLength * 0.1f, spineSegmentLength * 0.1f)
                );
                next.y = math.clamp(next.y, 3, numTilesY - 4);
                next.x = math.min(next.x, numTilesX - 3);

                segments.Add(new LineSegment { Start = current, End = next });
                current = next;
            }
            endpoints.Add(current);
        }

        // Vertical spines - cap at 3-4 regardless of map size
        var numVerticalSpines = math.clamp(numTilesX / 150, 2, 4);
        for (var i = 0; i < numVerticalSpines; i++)
        {
            var xBase = (i + 1f) / (numVerticalSpines + 1f) * numTilesX;
            var current = new float2(xBase + rng.NextFloat(-10, 10), 2);

            while (current.y < numTilesY - 3)
            {
                var next = current + new float2(
                    rng.NextFloat(-spineSegmentLength * 0.1f, spineSegmentLength * 0.1f),
                    spineSegmentLength + rng.NextFloat(-5, 5)
                );
                next.x = math.clamp(next.x, 3, numTilesX - 4);
                next.y = math.min(next.y, numTilesY - 3);

                segments.Add(new LineSegment { Start = current, End = next });
                current = next;
            }
            endpoints.Add(current);
        }

        // Optional diagonal - just 1-2 max
        var numDiagonalSpines = math.clamp((numTilesX + numTilesY) / 600, 1, 2);
        for (var i = 0; i < numDiagonalSpines; i++)
        {
            var fromBottomLeft = rng.NextBool();
            float2 current, direction;

            if (fromBottomLeft)
            {
                current = new float2(2, rng.NextFloat(5, numTilesY * 0.3f));
                direction = math.normalize(new float2(1, rng.NextFloat(0.5f, 1.5f)));
            }
            else
            {
                current = new float2(rng.NextFloat(5, numTilesX * 0.3f), 2);
                direction = math.normalize(new float2(rng.NextFloat(0.5f, 1.5f), 1));
            }

            while (current.x > 1 && current.x < numTilesX - 2 &&
                   current.y > 1 && current.y < numTilesY - 2)
            {
                var next = current + direction * (spineSegmentLength + rng.NextFloat(-3, 3));
                // Add slight curve
                direction = math.normalize(direction + new float2(
                    rng.NextFloat(-0.03f, 0.03f),
                    rng.NextFloat(-0.03f, 0.03f)
                ));

                if (next.x < 2 || next.x > numTilesX - 3 ||
                    next.y < 2 || next.y > numTilesY - 3)
                    break;

                segments.Add(new LineSegment { Start = current, End = next });
                current = next;
            }
            endpoints.Add(current);
        }
    }

    /// <summary>
    /// Gets a seed point on the edge of the map.
    /// </summary>
    private static void GetEdgeSeedPoint(
        int seed, int totalSeeds,
        int numTilesX, int numTilesY,
        out float2 startPos, out float startAngle,
        ref Random rng)
    {
        var edge = seed % 4;
        var seedsPerEdge = (totalSeeds + 3) / 4;
        var indexOnEdge = seed / 4;
        var edgePosition = (indexOnEdge + 1f) / (seedsPerEdge + 1f);

        switch (edge)
        {
            case 0: // Bottom edge, pointing up
                startPos = new float2(numTilesX * edgePosition, 2);
                startAngle = math.PI / 2 + rng.NextFloat(-0.3f, 0.3f);
                break;
            case 1: // Left edge, pointing right
                startPos = new float2(2, numTilesY * edgePosition);
                startAngle = rng.NextFloat(-0.3f, 0.3f);
                break;
            case 2: // Top edge, pointing down
                startPos = new float2(numTilesX * edgePosition, numTilesY - 3);
                startAngle = -math.PI / 2 + rng.NextFloat(-0.3f, 0.3f);
                break;
            case 3: // Right edge, pointing left
                startPos = new float2(numTilesX - 3, numTilesY * edgePosition);
                startAngle = math.PI + rng.NextFloat(-0.3f, 0.3f);
                break;
            default:
                startPos = new float2(numTilesX / 2, 2);
                startAngle = math.PI / 2;
                break;
        }
    }

    /// <summary>
    /// Connects nearby arterial endpoints to form a more connected network.
    /// </summary>
    private static void GenerateConnectorRoads(
        ref NativeList<LineSegment> segments,
        ref NativeList<float2> endpoints,
        int numTilesX, int numTilesY,
        float maxConnectionDistance,
        ref Random rng)
    {
        // Also collect segment endpoints that aren't already tracked
        var allEndpoints = new NativeList<float2>(endpoints.Length * 2, Allocator.Temp);
        foreach (var endpoint in endpoints)
            allEndpoints.Add(endpoint);

        // Sample some segment endpoints
        for (var i = 0; i < segments.Length; i += 3)
        {
            allEndpoints.Add(segments[i].End);
        }

        // Try to connect nearby endpoints (conservatively)
        var maxConnections = math.min(allEndpoints.Length / 3, 10); // Cap total connections
        var connections = 0;

        for (var i = 0; i < allEndpoints.Length && connections < maxConnections; i++)
        {
            var p1 = allEndpoints[i];

            for (var j = i + 1; j < allEndpoints.Length && connections < maxConnections; j++)
            {
                var p2 = allEndpoints[j];
                var dist = math.distance(p1, p2);

                // Connect if within range and with low probability
                if (dist < maxConnectionDistance && dist > 10 && rng.NextFloat() < 0.2f)
                {
                    // Check if both points are in valid bounds
                    if (p1.x > 2 && p1.x < numTilesX - 3 &&
                        p1.y > 2 && p1.y < numTilesY - 3 &&
                        p2.x > 2 && p2.x < numTilesX - 3 &&
                        p2.y > 2 && p2.y < numTilesY - 3)
                    {
                        // Add connector with slight curve
                        var mid = (p1 + p2) / 2;
                        var perp = math.normalize(new float2(-(p2.y - p1.y), p2.x - p1.x));
                        mid += perp * rng.NextFloat(-dist * 0.1f, dist * 0.1f);

                        segments.Add(new LineSegment { Start = p1, End = mid });
                        segments.Add(new LineSegment { Start = mid, End = p2 });
                        connections++;
                    }
                }
            }
        }

        allEndpoints.Dispose();
    }

    /// <summary>
    /// Expands the L-System grammar for the given number of iterations.
    /// Uses a more aggressive branching grammar for better coverage.
    /// Axiom: A
    /// Rules: A -> F[+A]F[-A]FA, F -> FF
    /// </summary>
    private static NativeList<byte> ExpandGrammar(int iterations, Allocator allocator)
    {
        var current = new NativeList<byte>(64, allocator);
        var next = new NativeList<byte>(512, allocator);

        // Start with axiom 'A'
        current.Add((byte)'A');

        for (var i = 0; i < iterations; i++)
        {
            next.Clear();

            for (var j = 0; j < current.Length && next.Length < MaxStringLength; j++)
            {
                var c = current[j];

                if (c == 'A')
                {
                    // A -> F[+A]F[-A]FA (more branching, forward between branches)
                    next.Add((byte)'F');
                    next.Add((byte)'[');
                    next.Add((byte)'+');
                    next.Add((byte)'A');
                    next.Add((byte)']');
                    next.Add((byte)'F');
                    next.Add((byte)'[');
                    next.Add((byte)'-');
                    next.Add((byte)'A');
                    next.Add((byte)']');
                    next.Add((byte)'F');
                    next.Add((byte)'A');
                }
                else if (c == 'F')
                {
                    // F -> FF
                    next.Add((byte)'F');
                    next.Add((byte)'F');
                }
                else
                {
                    // Keep other symbols unchanged (+, -, [, ])
                    next.Add(c);
                }
            }

            // Swap buffers
            (current, next) = (next, current);
        }

        next.Dispose();
        return current;
    }

    /// <summary>
    /// Interprets the L-System string using turtle graphics to generate line segments.
    /// F = move forward, + = rotate left, - = rotate right, [ = push state, ] = pop state
    /// </summary>
    private static void InterpretTurtle(
        ref NativeList<byte> lSystem,
        float2 startPos,
        float startAngle,
        float segmentLength,
        float branchAngle,
        ref NativeList<LineSegment> segments,
        ref NativeList<float2> endpoints,
        int numTilesX,
        int numTilesY,
        ref Random rng)
    {
        var stateStack = new NativeList<TurtleState>(32, Allocator.Temp);

        var state = new TurtleState
        {
            Position = startPos,
            Angle = startAngle
        };

        var lastValidPos = startPos;

        foreach (var c in lSystem)
        {
            switch (c)
            {
                case (byte)'F':
                    // Move forward and draw
                    var variation = rng.NextFloat(0.7f, 1.3f);
                    var newPos = state.Position + new float2(
                        math.cos(state.Angle),
                        math.sin(state.Angle)
                    ) * segmentLength * variation;

                    // Only add segment if it stays within bounds
                    if (newPos.x > 2 && newPos.x < numTilesX - 3 &&
                        newPos.y > 2 && newPos.y < numTilesY - 3)
                    {
                        segments.Add(new LineSegment
                        {
                            Start = state.Position,
                            End = newPos
                        });
                        state.Position = newPos;
                        lastValidPos = newPos;
                    }
                    break;

                case (byte)'+':
                    // Rotate left (counter-clockwise)
                    state.Angle += branchAngle + rng.NextFloat(-0.15f, 0.15f);
                    break;

                case (byte)'-':
                    // Rotate right (clockwise)
                    state.Angle -= branchAngle + rng.NextFloat(-0.15f, 0.15f);
                    break;

                case (byte)'[':
                    // Push state
                    stateStack.Add(state);
                    break;

                case (byte)']':
                    // Pop state - track endpoint before popping
                    if (lastValidPos.x > 2 && lastValidPos.x < numTilesX - 3 &&
                        lastValidPos.y > 2 && lastValidPos.y < numTilesY - 3)
                    {
                        endpoints.Add(lastValidPos);
                    }

                    if (stateStack.Length > 0)
                    {
                        state = stateStack[^1];
                        stateStack.RemoveAt(stateStack.Length - 1);
                        lastValidPos = state.Position;
                    }
                    break;

                case (byte)'A':
                    // Growth symbol - no turtle action
                    break;
            }
        }

        // Track final endpoint
        if (lastValidPos.x > 2 && lastValidPos.x < numTilesX - 3 &&
            lastValidPos.y > 2 && lastValidPos.y < numTilesY - 3)
        {
            endpoints.Add(lastValidPos);
        }

        stateStack.Dispose();
    }

    /// <summary>
    /// Rasterizes line segments to the grid using Bresenham's algorithm with road width expansion.
    /// </summary>
    private static void RasterizeSegments(
        ref NativeList<LineSegment> segments,
        int roadWidth,
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        int numTilesX,
        int numTilesY)
    {
        var halfWidth = roadWidth / 2;

        foreach (var segment in segments)
        {
            var x0 = (int)math.round(segment.Start.x);
            var y0 = (int)math.round(segment.Start.y);
            var x1 = (int)math.round(segment.End.x);
            var y1 = (int)math.round(segment.End.y);

            // Bresenham's line algorithm
            var dx = math.abs(x1 - x0);
            var dy = math.abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            var x = x0;
            var y = y0;

            while (true)
            {
                // Mark cells within road width
                for (var wx = -halfWidth; wx <= halfWidth; wx++)
                {
                    for (var wy = -halfWidth; wy <= halfWidth; wy++)
                    {
                        var px = x + wx;
                        var py = y + wy;

                        // Stay within bounds (leave border)
                        if (px > 0 && px < numTilesX - 1 &&
                            py > 0 && py < numTilesY - 1)
                        {
                            var idx = py * numTilesX + px;
                            tileExists[idx] = false;
                            // Mark as arterial (highest hierarchy)
                            if (roadHierarchy[idx] == 0 || (byte)RoadHierarchyLevel.Arterial < roadHierarchy[idx])
                                roadHierarchy[idx] = (byte)RoadHierarchyLevel.Arterial;
                        }
                    }
                }

                if (x == x1 && y == y1)
                    break;

                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }
    }

    /// <summary>
    /// Checks if a cell is already marked as a road (for BSP integration).
    /// </summary>
    public static bool IsRoadCell(ref NativeArray<bool> tileExists, ref NativeArray<byte> roadHierarchy, int idx)
    {
        return !tileExists[idx] && roadHierarchy[idx] > 0;
    }

    /// <summary>
    /// Checks what percentage of a proposed road area is already occupied by arterial roads.
    /// Used by BSP to avoid splitting through arterials.
    /// </summary>
    public static float GetArterialOverlapRatio(
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        int numTilesX,
        bool isHorizontal,
        int position,
        int start,
        int end,
        int width)
    {
        var totalCells = 0;
        var arterialCells = 0;

        if (isHorizontal)
        {
            for (var w = 0; w < width; w++)
            {
                var y = position + w;
                for (var x = start; x <= end; x++)
                {
                    var idx = y * numTilesX + x;
                    totalCells++;
                    if (roadHierarchy[idx] == (byte)RoadHierarchyLevel.Arterial)
                        arterialCells++;
                }
            }
        }
        else
        {
            for (var w = 0; w < width; w++)
            {
                var x = position + w;
                for (var y = start; y <= end; y++)
                {
                    var idx = y * numTilesX + x;
                    totalCells++;
                    if (roadHierarchy[idx] == (byte)RoadHierarchyLevel.Arterial)
                        arterialCells++;
                }
            }
        }

        return totalCells > 0 ? (float)arterialCells / totalCells : 0f;
    }
}

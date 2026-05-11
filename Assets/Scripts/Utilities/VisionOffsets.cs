using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

public static class VisionOffsets
{
    private struct LengthSqComparer : IComparer<int2>
    {
        public int Compare(int2 a, int2 b)
        {
            var da = a.x * a.x + a.y * a.y;
            var db = b.x * b.x + b.y * b.y;
            return da.CompareTo(db);
        }
    }

    public static NativeArray<int2> Build(int radius, Allocator allocator)
    {
        var radiusSq = radius * radius;
        var capacity = (2 * radius + 1) * (2 * radius + 1);
        var temp = new NativeList<int2>(capacity, Allocator.Temp);

        for (var z = -radius; z <= radius; z++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                var distSq = x * x + z * z;
                if (distSq == 0 || distSq > radiusSq)
                    continue;
                temp.Add(new int2(x, z));
            }
        }

        temp.Sort(new LengthSqComparer());

        var result = new NativeArray<int2>(temp.Length, allocator);
        NativeArray<int2>.Copy(temp.AsArray(), result, temp.Length);
        temp.Dispose();
        return result;
    }
}

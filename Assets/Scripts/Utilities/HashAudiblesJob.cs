using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[BurstCompile]
public partial struct HashAudiblesJob : IJobEntity
{
    public NativeParallelMultiHashMap<uint, int3>.ParallelWriter ParallelWriter;

    public void Execute(in Audible audible)
    {
        var key = GridPositionHash.GetKey(audible.GridPositionValue.x,  audible.GridPositionValue.z);
        ParallelWriter.Add(key, audible.Target);
    }
}

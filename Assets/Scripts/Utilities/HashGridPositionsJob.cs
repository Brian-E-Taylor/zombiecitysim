using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

[BurstCompile]
public partial struct HashGridPositionsJob : IJobEntity
{
    public NativeParallelHashMap<uint, int>.ParallelWriter ParallelWriter;

    public void Execute([EntityIndexInQuery] int entityIndexInQuery, [ReadOnly] in GridPosition gridPosition)
    {
        var key = GridPositionHash.GetKey(gridPosition.Value.x, gridPosition.Value.z);
        ParallelWriter.TryAdd(key, entityIndexInQuery);
    }
}

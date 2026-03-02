using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

[BurstCompile]
public partial struct DealDamageJob : IJobEntity
{
    public float4 FullHealthColor;
    [ReadOnly] public NativeParallelMultiHashMap<uint, int> DamageAmountHashMap;

    public void Execute(ref Health health, ref URPMaterialPropertyBaseColor materialColor, [ReadOnly] in MaxHealth maxHealth, [ReadOnly] in GridPosition gridPosition)
    {
        var gridPositionKey = GridPositionHash.GetKey(gridPosition.Value.x,  gridPosition.Value.z);
        if (!DamageAmountHashMap.TryGetFirstValue(gridPositionKey, out var damage, out var it))
            return;

        var myHealth = health.Value - damage;
        while (DamageAmountHashMap.TryGetNextValue(out damage, ref it))
            myHealth -= damage;

        health.Value = myHealth;

        // Update health color
        var lerp = math.saturate((float)myHealth / maxHealth.Value);
        materialColor.Value = new float4(
            math.abs(FullHealthColor.x - 1.0f) < 0.001f ? lerp : 1.0f - lerp,
            math.abs(FullHealthColor.y - 1.0f) < 0.001f ? lerp : 1.0f - lerp,
            0.0f,
            materialColor.Value.w
        );
    }
}
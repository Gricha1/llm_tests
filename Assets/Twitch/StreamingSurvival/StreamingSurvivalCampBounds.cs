using UnityEngine;

/// <summary>
/// World XZ yard inside the presentation wooden fence. Sheep meadow is the south patch
/// (z &lt;= SheepMaxZ); north of that the west face is open in mesh but we keep a virtual line.
/// </summary>
public static class StreamingSurvivalCampBounds
{
    public const float MinX = -4.2f;
    public const float MaxX = 14.6f;
    public const float MinZ = 10.0f;
    public const float MaxZ = 33.0f;
    public const float SheepMaxZ = 16.8f;

    public static bool ContainsCamp(Vector3 world)
    {
        return world.x >= MinX && world.x <= MaxX
            && world.z >= MinZ && world.z <= MaxZ;
    }

    public static bool ContainsSheepMeadow(Vector3 world)
    {
        return world.x >= MinX && world.x <= MaxX
            && world.z >= MinZ && world.z <= SheepMaxZ;
    }

    public static Vector3 ClampToSheepMeadow(Vector3 world)
    {
        return new Vector3(
            Mathf.Clamp(world.x, MinX, MaxX),
            world.y,
            Mathf.Clamp(world.z, MinZ, SheepMaxZ));
    }
}

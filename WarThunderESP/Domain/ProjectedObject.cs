namespace WarThunderESP;

public readonly struct ProjectedObject
{
    public readonly long Address;
    public readonly Vec3 World;
    public readonly float ScreenX;
    public readonly float ScreenY;
    public readonly float BoxLeft;
    public readonly float BoxTop;
    public readonly float BoxRight;
    public readonly float BoxBottom;
    public readonly float Forward;
    public readonly float Distance;
    public readonly float ClipW;
    public readonly bool IsAircraft;
    public readonly string UnitName;

    public ProjectedObject(
        long address,
        Vec3 world,
        float screenX,
        float screenY,
        float boxLeft,
        float boxTop,
        float boxRight,
        float boxBottom,
        float forward,
        float distance,
        float clipW,
        bool isAircraft,
        string unitName)
    {
        Address = address;
        World = world;
        ScreenX = screenX;
        ScreenY = screenY;
        BoxLeft = boxLeft;
        BoxTop = boxTop;
        BoxRight = boxRight;
        BoxBottom = boxBottom;
        Forward = forward;
        Distance = distance;
        ClipW = clipW;
        IsAircraft = isAircraft;
        UnitName = unitName;
    }
}


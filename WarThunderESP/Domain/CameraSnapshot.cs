namespace WarThunderESP;

public readonly struct CameraSnapshot
{
    public readonly long BaseAddress;
    public readonly Vec3 Right;
    public readonly Vec3 Up;
    public readonly Vec3 Forward;
    public readonly Vec3 Position;

    public CameraSnapshot(long baseAddress, Vec3 right, Vec3 up, Vec3 forward, Vec3 position)
    {
        BaseAddress = baseAddress;
        Right = right;
        Up = up;
        Forward = forward;
        Position = position;
    }
}


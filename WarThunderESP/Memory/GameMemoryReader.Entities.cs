namespace WarThunderESP;

public sealed partial class GameMemoryReader
{
    public Vec3 GetCoordinates(long objectAddress)
    {
        long vt = ReadInt64(objectAddress);

        if (vt == _aircraftVtableAddr)
        {
            return new Vec3(
                ReadFloat(objectAddress + AircraftOffX),
                ReadFloat(objectAddress + AircraftOffY),
                ReadFloat(objectAddress + AircraftOffZ)
            );
        }

        return new Vec3(
            ReadFloat(objectAddress + GroundOffX),
            ReadFloat(objectAddress + GroundOffY),
            ReadFloat(objectAddress + GroundOffZ)
        );
    }

    private bool IsAircraftObject(long objectAddress)
    {
        return ReadInt64(objectAddress) == _aircraftVtableAddr;
    }

    public (float x, float z) GetSelfPosition()
    {
        float x = ReadFloat(_moduleBase + SelfPosOffset + 0);
        float z = ReadFloat(_moduleBase + SelfPosOffset + 4);
        return (x, z);
    }

    private static float DistanceXZ(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static bool IsSelfObject(in Vec3 pos, float selfX, float selfZ)
    {
        float dx = pos.X - selfX;
        float dz = pos.Z - selfZ;

        return dx * dx + dz * dz <= SelfExcludeRadiusSq;
    }

    // Reads unit names from descriptor string slots.
    private const int UnitDescriptorOffset = 0x0FF0;
    private static readonly int[] UnitNameOffsets = { 0x20, 0x08, 0x10, 0x40, 0x348 };

    public string ReadUnitName(long objectAddress)
    {
        long descriptor = ReadInt64(objectAddress + UnitDescriptorOffset);
        if (!IsReasonablePointer(descriptor))
            return "unknown";

        foreach (int nameOffset in UnitNameOffsets)
        {
            long stringPtr = ReadInt64(descriptor + nameOffset);
            string name = ReadAsciiString(stringPtr, 96);

            if (LooksLikeUnitName(name))
                return name;
        }

        return "unknown";
    }

    private string ReadAsciiString(long address, int maxLength)
    {
        if (!IsReasonablePointer(address) || maxLength <= 0)
            return string.Empty;

        byte[] buffer = new byte[maxLength];

        if (!ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out int read) || read <= 0)
            return string.Empty;

        int length = 0;
        while (length < read && buffer[length] != 0)
            length++;

        if (length < 3)
            return string.Empty;

        for (int i = 0; i < length; i++)
        {
            byte b = buffer[i];
            bool ok =
                b >= 0x20 && b <= 0x7E;

            if (!ok)
                return string.Empty;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static bool LooksLikeUnitName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Length < 3 || value.Length > 96)
            return false;

        // Accept display names and known internal resource paths.
        if (value.IndexOf("tankModels/", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (value.IndexOf("flightModels/", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (value.IndexOf("exp_fighter", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (value.IndexOf("gameData/", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (value.IndexOf('_') >= 0)
            return true;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '/' || c == '.';
            if (!ok)
                return false;
        }

        return true;
    }

    // Object metadata offsets used by entity filters.
    private const int TeamOffset = 0xFE0;
    private const int UnitIdOffset = 0xFE8;
    private const int DeadFlagOffset = 0x1860;

    private short ReadUnitId(long objectAddress)
    {
        return ReadI16(objectAddress, UnitIdOffset);
    }

    private bool IsCombatUnit(long objectAddress)
    {
        short id = ReadUnitId(objectAddress);

        return id >= 0 && id <= 64;
    }

    private bool IsAlive(long objectAddress)
    {
        // Dead flag is a byte marker; zero means alive.
        byte deadFlag = ReadByte(objectAddress + DeadFlagOffset);

        return deadFlag == 0;
    }

    private int ResolveSelfTeam(long selfObjectAddress)
    {
        int currentTeam = selfObjectAddress != 0 ? ReadTeam(selfObjectAddress) : int.MinValue;

        if (IsValidTeamMarker(currentTeam))
        {
            _cachedSelfTeam = currentTeam;
            _cachedSelfTeamAtUtc = DateTime.UtcNow;
            return currentTeam;
        }

        if (IsValidTeamMarker(_cachedSelfTeam))
        {
            double ageMs = (DateTime.UtcNow - _cachedSelfTeamAtUtc).TotalMilliseconds;
            if (ageMs >= 0 && ageMs <= SelfTeamCacheMs)
                return _cachedSelfTeam;
        }

        return int.MinValue;
    }

    private long FindSelfAircraftByView(long[] objects, float[] vp, float screenW, float screenH)
    {
        long bestAddress = 0;
        float bestScore = float.MaxValue;
        float centerX = screenW * 0.5f;
        float centerY = screenH * 0.5f;
        float maxCenterDistSq = AirSelfScreenRadiusPx * AirSelfScreenRadiusPx;

        foreach (long objectAddress in objects)
        {
            if (!IsAircraftObject(objectAddress))
                continue;

            if (!IsCombatUnit(objectAddress))
                continue;

            if (!IsAlive(objectAddress))
                continue;

            int team = ReadTeam(objectAddress);
            if (!IsValidTeamMarker(team))
                continue;

            var pos = GetCoordinates(objectAddress);
            if (!IsValidWorldPosition(pos))
                continue;

            if (!TryWorldToScreenViewProjection(pos, vp, screenW, screenH, out float sx, out float sy, out float clipW))
                continue;

            if (clipW <= 0.05f || clipW > AircraftSelfMaxClipW)
                continue;

            float centerDistSq = ScreenDistanceSq(sx, sy, centerX, centerY);
            if (centerDistSq > maxCenterDistSq)
                continue;

            // Resolve the local aircraft by preferring projected objects near screen center.
            float score = centerDistSq + clipW * 1.75f;
            if (score < bestScore)
            {
                bestScore = score;
                bestAddress = objectAddress;
            }
        }

        return bestAddress;
    }

    private long FindSelfObjectAddress(long[] objects, float selfX, float selfZ)
    {
        long bestAddress = 0;
        float bestDistSq = SelfResolveRadiusSq;

        foreach (long objectAddress in objects)
        {
            if (!IsCombatUnit(objectAddress))
                continue;

            if (!IsAlive(objectAddress))
                continue;

            var pos = GetCoordinates(objectAddress);
            if (!IsValidWorldPosition(pos))
                continue;

            float dx = pos.X - selfX;
            float dz = pos.Z - selfZ;
            float distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestAddress = objectAddress;
            }
        }

        return bestAddress;
    }

    private static bool IsValidTeamMarker(int team)
    {
        return team >= 0 && team <= 8;
    }

    private bool IsEnemyOfSelfTeam(long objectAddress, int selfTeam)
    {
        int team = ReadTeam(objectAddress);

        if (!IsValidTeamMarker(team))
            return false;

        return team != selfTeam;
    }

    public int ReadTeam(long objectAddress)
    {
        return ReadByte(objectAddress + TeamOffset);
    }

    public int ReadI32(long objectAddress, int offset)
    {
        return ReadInt32(objectAddress + offset);
    }

    public short ReadI16(long objectAddress, int offset)
    {
        byte[] buf = new byte[2];

        if (ReadProcessMemory(_handle, (IntPtr)(objectAddress + offset), buf, 2, out int read) && read == 2)
            return BitConverter.ToInt16(buf, 0);

        return short.MinValue;
    }

    public byte ReadU8(long objectAddress, int offset)
    {
        return ReadByte(objectAddress + offset);
    }
}


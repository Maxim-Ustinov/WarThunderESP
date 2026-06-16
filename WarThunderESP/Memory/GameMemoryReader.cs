namespace WarThunderESP;

public sealed partial class GameMemoryReader : IDisposable
{
    private const string ProcessName = "aces";

    private const long GroundVtableOffset = 0x62AC9F0;
    private const long AircraftVtableOffset = 0x6358720;

    private const int GroundOffX = 0xD08;
    private const int GroundOffY = 0xD0C;
    private const int GroundOffZ = 0xD10;

    // Fury Mk.I aircraft object found from height anchors:
    // vtable = aces.exe+0x6358720. The area around +0x2480 contains several
    // transform snapshots. +0x249C/+0x24A0/+0x24A4 lags behind on fast aircraft.
    // Use the first/newest slot to reduce ESP delay/teleporting.
    private const int AircraftOffX = 0x2480;
    private const int AircraftOffY = 0x2484;
    private const int AircraftOffZ = 0x2488;

    // Visual box tuning. Ground anchor was +2.0f; lower it by ~1.25m.
    private const float GroundEspYOffset = 0.75f;
    private const float GroundBoxBottomY = 0.10f;
    private const float GroundBoxTopY = 2.70f;
    private const float GroundBoxHalfWidth = 2.20f;
    private const float GroundBoxHalfLength = 3.60f;

    private const float AircraftBoxHalfWidth = 7.50f;
    private const float AircraftBoxHalfLength = 8.50f;
    private const float AircraftBoxHalfHeight = 1.60f;

    private const long SelfPosOffset = 0x6ECAA38;
    private const float SelfExcludeRadius = 35.0f;
    private const float SelfExcludeRadiusSq = SelfExcludeRadius * SelfExcludeRadius;
    private const float SelfResolveRadius = 12.0f;
    private const float SelfResolveRadiusSq = SelfResolveRadius * SelfResolveRadius;
    private const int SelfTeamCacheMs = 15000;
    // Р’ Р°РІРёР°-СЂРµР¶РёРјРµ СЃС‚Р°СЂС‹Р№ SelfPosOffset РјРѕР¶РµС‚ РЅРµ СѓРєР°Р·С‹РІР°С‚СЊ РЅР° СЃР°РјРѕР»С‘С‚ РёРіСЂРѕРєР°.
    // РџРѕСЌС‚РѕРјСѓ, РµСЃР»Рё ground/self-position resolve РЅРµ СЃСЂР°Р±РѕС‚Р°Р», Р±РµСЂС‘Рј Р±Р»РёР¶Р°Р№С€РёР№ Рє РєР°РјРµСЂРµ Р¶РёРІРѕР№ aircraft РєР°Рє self.
    private const float AircraftSelfMaxClipW = 420.0f;

    // cameraBase = ResolvePointer(aces.exe + 0x06ED7C28, [0x110, 0x298, 0xD8, 0x0])
    private const long CameraPtrBaseOffset = 0x06ED7C28;
    private static readonly int[] CameraOffsets = { 0x110, 0x298, 0xD8, 0x0 };

    private const int CamRightX = 0x50;
    private const int CamRightY = 0x54;
    private const int CamRightZ = 0x58;

    private const int CamUpX = 0x5C;
    private const int CamUpY = 0x60;
    private const int CamUpZ = 0x64;

    private const int CamForwardX = 0x68;
    private const int CamForwardY = 0x6C;
    private const int CamForwardZ = 0x70;

    private const int CamPosX = 0x74;
    private const int CamPosY = 0x78;
    private const int CamPosZ = 0x7C;

    private const int ScanPauseMs = 1500;
    private const int ScanBufferSize = 8 * 1024 * 1024;
    private const bool SkipFileBackedMapped = true;

    private IntPtr _handle = IntPtr.Zero;
    private readonly long _moduleBase;
    private readonly long _vtableAddr;
    private readonly long _aircraftVtableAddr;

    private readonly object _sync = new();
    private long[] _objects = Array.Empty<long>();
    private DateTime _lastScan = DateTime.MinValue;
    private long _scanMs;
    private volatile int _lastSelfTeam = int.MinValue;
    private int _cachedSelfTeam = int.MinValue;
    private DateTime _cachedSelfTeamAtUtc = DateTime.MinValue;
    private float[]? _cachedAirGlobtm;
    private DateTime _cachedAirGlobtmAtUtc = DateTime.MinValue;
    private const int AirGlobtmCacheMs = 95;

    // Aircraft GLOBTM РёРЅРѕРіРґР° РґР°С‘С‚ РѕРґРёРЅ РїР»РѕС…РѕР№ РєР°РґСЂ: Р±РѕРєСЃ РјРѕСЂРіР°РµС‚ РёР»Рё СѓР»РµС‚Р°РµС‚.
    // Р”РµСЂР¶РёРј РєРѕСЂРѕС‚РєРёР№ cache РїРѕСЃР»РµРґРЅРµР№ С…РѕСЂРѕС€РµР№ РїСЂРѕРµРєС†РёРё Рё СЃРіР»Р°Р¶РёРІР°РµРј С‚РѕР»СЊРєРѕ aircraft ESP.
    private readonly Dictionary<long, ProjectedObject> _airProjectionCache = new();
    private readonly Dictionary<long, DateTime> _airProjectionCacheAtUtc = new();

    // Р•СЃР»Рё aircraft-РїСЂРѕРµРєС†РёСЏ РЅР° РѕРґРёРЅ РєР°РґСЂ СѓР»РµС‚Р°РµС‚ РІ СЃР»СѓС‡Р°Р№РЅСѓСЋ С‚РѕС‡РєСѓ, РЅРµ РїСЂРёРЅРёРјР°РµРј РµС‘ СЃСЂР°Р·Сѓ.
    // РЎРЅР°С‡Р°Р»Р° РєР»Р°РґС‘Рј РµС‘ РІ pending. Р•СЃР»Рё СЃР»РµРґСѓСЋС‰РёР№ РєР°РґСЂ РїРѕРґС‚РІРµСЂР¶РґР°РµС‚ РїСЂРёРјРµСЂРЅРѕ С‚Сѓ Р¶Рµ РЅРѕРІСѓСЋ РїРѕР·РёС†РёСЋ вЂ” РїСЂРёРЅРёРјР°РµРј.
    // РўР°Рє СѓР±РёСЂР°СЋС‚СЃСЏ СЂР°Р·РѕРІС‹Рµ "СЂР°РЅРґРѕРјРЅС‹Рµ Р±РѕРєСЃС‹ РІ РЅРµР±Рµ", РЅРѕ РїСЂРё СЂРµР°Р»СЊРЅРѕРј РїРѕРІРѕСЂРѕС‚Рµ РєР°РјРµСЂС‹ Р·Р°РґРµСЂР¶РєР° РјР°РєСЃРёРјСѓРј 1-2 РєР°РґСЂР°.
    private readonly Dictionary<long, ProjectedObject> _airProjectionPending = new();
    private readonly Dictionary<long, DateTime> _airProjectionPendingAtUtc = new();

    private const int AirProjectionCacheMs = 450;
    private const float AirProjectionOutlierPixels = 360.0f;
    private const float AirProjectionConfirmPixels = 160.0f;
    private const int AirProjectionPendingMaxMs = 220;
    private const float AirProjectionSmoothAlpha = 1.0f;
    private const float AirSelfScreenRadiusPx = 300.0f;

    private Thread? _scannerThread;
    private volatile bool _running = true;

    public event Action? OnObjectsUpdated;

    public long ModuleBase => _moduleBase;
    public long VtableAddress => _vtableAddr;
    public long AircraftVtableAddress => _aircraftVtableAddr;

    public int ObjectCount
    {
        get
        {
            lock (_sync)
                return _objects.Length;
        }
    }

    public DateTime LastScan
    {
        get
        {
            lock (_sync)
                return _lastScan;
        }
    }

    public long LastScanMs
    {
        get
        {
            lock (_sync)
                return _scanMs;
        }
    }

    public int LastSelfTeam => _lastSelfTeam;

    public GameMemoryReader()
    {
        var proc = FindProcess();
        if (proc == null)
            throw new Exception($"РџСЂРѕС†РµСЃСЃ {ProcessName}.exe РЅРµ РЅР°Р№РґРµРЅ");

        _moduleBase = proc.MainModule!.BaseAddress.ToInt64();
        _vtableAddr = _moduleBase + GroundVtableOffset;
        _aircraftVtableAddr = _moduleBase + AircraftVtableOffset;

        _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
        if (_handle == IntPtr.Zero)
            throw new Exception($"OpenProcess РЅРµ СѓРґР°Р»СЃСЏ. РљРѕРґ: {Marshal.GetLastWin32Error()}");
    }
}


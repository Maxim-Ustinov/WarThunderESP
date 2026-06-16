namespace WarThunderESP;

public sealed partial class GameMemoryReader
{
    // #02 из AOB-скана: тот же результат, что STORED_GLOBTM, но читаем VIEW+PROJ
    // и сами считаем VP = PROJ * VIEW. Это менее чувствительно к pass-specific globtm.
    private const long DebugViewOffset = 0x6EE2770;
    private const long DebugProjOffset = 0x6EE27B0;
    // Stored Dagor globtm from FrameStateTM: direct world->clip matrix.
    // In air battles VIEW+PROJ can be a stale/wrong render pass, so aircraft W2S tries this first.
    private const long DebugGlobtmOffset = 0x71827B0;

    public long ViewOffset => DebugViewOffset;
    public long ProjOffset => DebugProjOffset;

    private bool TryReadViewProjSnapshot(out float[] view, out float[] proj)
    {
        view = new float[16];
        proj = new float[16];

        // VIEW и PROJ лежат подряд: VIEW at +0x40, PROJ at +0x80.
        // Читаем 0x80 одним snapshot, чтобы не получить VIEW от одного кадра, а PROJ от другого.
        byte[] buffer = new byte[0x80];
        long address = _moduleBase + DebugViewOffset;

        if (!ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out int bytesRead))
            return false;

        if (bytesRead != buffer.Length)
            return false;

        for (int i = 0; i < 16; i++)
        {
            view[i] = BitConverter.ToSingle(buffer, i * 4);
            proj[i] = BitConverter.ToSingle(buffer, 0x40 + i * 4);

            if (!IsFinite(view[i]) || !IsFinite(proj[i]))
                return false;
        }

        return true;
    }

    private static float[] MulMat44ColumnMajor(float[] a, float[] b)
    {
        // Dagor mat44f: column-major, column-vector convention.
        // result = a * b.
        var r = new float[16];

        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                r[col * 4 + row] =
                    a[0 * 4 + row] * b[col * 4 + 0] +
                    a[1 * 4 + row] * b[col * 4 + 1] +
                    a[2 * 4 + row] * b[col * 4 + 2] +
                    a[3 * 4 + row] * b[col * 4 + 3];
            }
        }

        return r;
    }

    private bool TryReadViewProjection(out float[] vp)
    {
        vp = new float[16];

        if (!TryReadViewProjSnapshot(out float[] view, out float[] proj))
            return false;

        vp = MulMat44ColumnMajor(proj, view);
        return true;
    }

    private bool TryReadGlobtmRaw(out float[] globtm)
    {
        globtm = new float[16];

        byte[] buffer = new byte[0x40];
        long address = _moduleBase + DebugGlobtmOffset;

        if (!ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out int bytesRead))
            return false;

        if (bytesRead != buffer.Length)
            return false;

        for (int i = 0; i < 16; i++)
        {
            globtm[i] = BitConverter.ToSingle(buffer, i * 4);
            if (!IsFinite(globtm[i]))
                return false;
        }

        return true;
    }

    private bool TryReadGlobtm(out float[] globtm)
    {
        globtm = new float[16];

        // NoLag + AntiFlicker:
        // берём свежую GLOBTM каждый кадр, но не принимаем snapshot, если он пойман
        // прямо во время записи render-pass. Два быстрых чтения почти не добавляют лаг,
        // зато убирают одиночные битые кадры, из-за которых aircraft boxes моргают.
        if (TryReadGlobtmRaw(out float[] a) && TryReadGlobtmRaw(out float[] b))
        {
            if (MatricesClose(a, b, 0.12f))
            {
                globtm = b;
                _cachedAirGlobtm = (float[])b.Clone();
                _cachedAirGlobtmAtUtc = DateTime.UtcNow;
                return true;
            }
        }

        // Если попали в момент обновления матрицы, один-два кадра держим последнюю хорошую.
        // Cache короткий, чтобы при повороте камеры бокс не начинал заметно догонять модель.
        if (_cachedAirGlobtm != null)
        {
            double ageMs = (DateTime.UtcNow - _cachedAirGlobtmAtUtc).TotalMilliseconds;
            if (ageMs >= 0 && ageMs <= AirGlobtmCacheMs)
            {
                globtm = (float[])_cachedAirGlobtm.Clone();
                return true;
            }
        }

        return false;
    }

    private static bool MatricesClose(float[] a, float[] b, float maxDelta)
    {
        if (a.Length != 16 || b.Length != 16)
            return false;

        for (int i = 0; i < 16; i++)
        {
            if (!IsFinite(a[i]) || !IsFinite(b[i]))
                return false;

            if (Math.Abs(a[i] - b[i]) > maxDelta)
                return false;
        }

        return true;
    }

    private bool TryWorldToScreenViewProjection(
        in Vec3 world,
        float[] vp,
        float screenW,
        float screenH,
        out float screenX,
        out float screenY,
        out float clipW)
    {
        screenX = 0;
        screenY = 0;
        clipW = 0;

        float clipX =
            vp[0] * world.X +
            vp[4] * world.Y +
            vp[8] * world.Z +
            vp[12];

        float clipY =
            vp[1] * world.X +
            vp[5] * world.Y +
            vp[9] * world.Z +
            vp[13];

        float clipZ =
            vp[2] * world.X +
            vp[6] * world.Y +
            vp[10] * world.Z +
            vp[14];

        clipW =
            vp[3] * world.X +
            vp[7] * world.Y +
            vp[11] * world.Z +
            vp[15];

        if (clipW <= 0.001f || !IsFinite(clipW))
            return false;

        // Reverse-Z near-plane reject. Если будет пропадание на близких объектах — можно ослабить.
        if (clipZ >= clipW)
            return false;

        float ndcX = clipX / clipW;
        float ndcY = clipY / clipW;

        if (!IsFinite(ndcX) || !IsFinite(ndcY))
            return false;

        screenX = screenW * (0.5f * ndcX + 0.5f);
        screenY = screenH * (0.5f - 0.5f * ndcY);

        return true;
    }

    private bool TryBuildProjectedBox(
        in Vec3 pos,
        bool isAircraft,
        float[] vp,
        float screenW,
        float screenH,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        left = float.MaxValue;
        top = float.MaxValue;
        right = float.MinValue;
        bottom = float.MinValue;

        float halfWidth = isAircraft ? AircraftBoxHalfWidth : GroundBoxHalfWidth;
        float halfLength = isAircraft ? AircraftBoxHalfLength : GroundBoxHalfLength;
        float yMin = isAircraft ? -AircraftBoxHalfHeight : GroundBoxBottomY;
        float yMax = isAircraft ? AircraftBoxHalfHeight : GroundBoxTopY;

        int projected = 0;

        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iy = 0; iy <= 1; iy++)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    var corner = new Vec3(
                        pos.X + ix * halfWidth,
                        pos.Y + (iy == 0 ? yMin : yMax),
                        pos.Z + iz * halfLength);

                    if (!TryWorldToScreenViewProjection(corner, vp, screenW, screenH, out float sx, out float sy, out _))
                        continue;

                    left = Math.Min(left, sx);
                    top = Math.Min(top, sy);
                    right = Math.Max(right, sx);
                    bottom = Math.Max(bottom, sy);
                    projected++;
                }
            }
        }

        if (projected < (isAircraft ? 4 : 2))
            return false;

        float boxWidth = right - left;
        float boxHeight = bottom - top;

        if (!IsFinite(boxWidth) || !IsFinite(boxHeight) || boxWidth <= 1.0f || boxHeight <= 1.0f)
            return false;

        // Защита от гигантских прямоугольников, когда объект совсем рядом/частично за камерой.
        float maxW = screenW * 0.85f;
        float maxH = screenH * 0.85f;
        if (boxWidth > maxW || boxHeight > maxH)
            return false;

        // Минимальный размер, чтобы дальние цели не превращались в пиксель.
        float minW = isAircraft ? 16.0f : 18.0f;
        float minH = isAircraft ? 10.0f : 14.0f;
        if (boxWidth < minW)
        {
            float c = (left + right) * 0.5f;
            left = c - minW * 0.5f;
            right = c + minW * 0.5f;
        }

        if (boxHeight < minH)
        {
            float c = (top + bottom) * 0.5f;
            top = c - minH * 0.5f;
            bottom = c + minH * 0.5f;
        }

        return true;
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private static float ScreenDistanceSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx;
        float dy = ay - by;
        return dx * dx + dy * dy;
    }

    private bool TryStabilizeAircraftProjection(ProjectedObject raw, out ProjectedObject stable)
    {
        stable = raw;

        if (!raw.IsAircraft)
            return true;

        DateTime now = DateTime.UtcNow;

        if (_airProjectionCache.TryGetValue(raw.Address, out ProjectedObject prev) &&
            _airProjectionCacheAtUtc.TryGetValue(raw.Address, out DateTime prevAt))
        {
            double ageMs = (now - prevAt).TotalMilliseconds;

            if (ageMs >= 0 && ageMs <= AirProjectionCacheMs)
            {
                float jumpSq = ScreenDistanceSq(raw.ScreenX, raw.ScreenY, prev.ScreenX, prev.ScreenY);
                float outlierSq = AirProjectionOutlierPixels * AirProjectionOutlierPixels;

                if (jumpSq > outlierSq)
                {
                    // ВАЖНО: bad-frame больше НИКОГДА не принимается по timeout.
                    // Иначе одиночный мусорный GLOBTM/позиционный slot успевает записаться в cache,
                    // и появляется "рандомный" бокс с правильным названием самолёта где-то в небе.
                    if (_airProjectionPending.TryGetValue(raw.Address, out ProjectedObject pending) &&
                        _airProjectionPendingAtUtc.TryGetValue(raw.Address, out DateTime pendingAt))
                    {
                        double pendingAgeMs = (now - pendingAt).TotalMilliseconds;
                        float confirmSq = AirProjectionConfirmPixels * AirProjectionConfirmPixels;
                        float pendingJumpSq = ScreenDistanceSq(raw.ScreenX, raw.ScreenY, pending.ScreenX, pending.ScreenY);

                        if (pendingAgeMs >= 0 && pendingAgeMs <= AirProjectionPendingMaxMs && pendingJumpSq <= confirmSq)
                        {
                            // Только два похожих подряд новых кадра считаем реальным смещением.
                            _airProjectionPending.Remove(raw.Address);
                            _airProjectionPendingAtUtc.Remove(raw.Address);
                            _airProjectionCache[raw.Address] = raw;
                            _airProjectionCacheAtUtc[raw.Address] = now;
                            stable = raw;
                            return true;
                        }
                    }

                    _airProjectionPending[raw.Address] = raw;
                    _airProjectionPendingAtUtc[raw.Address] = now;
                    stable = prev;
                    return true;
                }
            }
        }
        else
        {
            // Первый кадр для aircraft тоже не рисуем сразу.
            // Нужно второе похожее чтение, иначе случайный первый bad-frame появится как отдельный бокс в небе.
            if (_airProjectionPending.TryGetValue(raw.Address, out ProjectedObject pending) &&
                _airProjectionPendingAtUtc.TryGetValue(raw.Address, out DateTime pendingAt))
            {
                double pendingAgeMs = (now - pendingAt).TotalMilliseconds;
                float confirmSq = AirProjectionConfirmPixels * AirProjectionConfirmPixels;
                float pendingJumpSq = ScreenDistanceSq(raw.ScreenX, raw.ScreenY, pending.ScreenX, pending.ScreenY);

                if (pendingAgeMs >= 0 && pendingAgeMs <= AirProjectionPendingMaxMs && pendingJumpSq <= confirmSq)
                {
                    _airProjectionPending.Remove(raw.Address);
                    _airProjectionPendingAtUtc.Remove(raw.Address);
                    _airProjectionCache[raw.Address] = raw;
                    _airProjectionCacheAtUtc[raw.Address] = now;
                    stable = raw;
                    return true;
                }
            }

            _airProjectionPending[raw.Address] = raw;
            _airProjectionPendingAtUtc[raw.Address] = now;
            return false;
        }

        _airProjectionPending.Remove(raw.Address);
        _airProjectionPendingAtUtc.Remove(raw.Address);
        _airProjectionCache[raw.Address] = raw;
        _airProjectionCacheAtUtc[raw.Address] = now;
        stable = raw;
        return true;
    }

    private void AddRecentCachedAircraft(List<ProjectedObject> result, HashSet<long> seen, long selfObjectAddress)
    {
        DateTime now = DateTime.UtcNow;
        var stale = new List<long>();

        foreach (var kv in _airProjectionCache)
        {
            long address = kv.Key;

            if (address == selfObjectAddress || seen.Contains(address))
                continue;

            if (!_airProjectionCacheAtUtc.TryGetValue(address, out DateTime at))
            {
                stale.Add(address);
                continue;
            }

            double ageMs = (now - at).TotalMilliseconds;
            if (ageMs < 0 || ageMs > AirProjectionCacheMs)
            {
                stale.Add(address);
                continue;
            }

            result.Add(kv.Value);
            seen.Add(address);
        }

        foreach (long address in stale)
        {
            _airProjectionCache.Remove(address);
            _airProjectionCacheAtUtc.Remove(address);
            _airProjectionPending.Remove(address);
            _airProjectionPendingAtUtc.Remove(address);
        }
    }

    private void DropAircraftProjectionCache(long address)
    {
        if (address == 0)
            return;

        _airProjectionCache.Remove(address);
        _airProjectionCacheAtUtc.Remove(address);
        _airProjectionPending.Remove(address);
        _airProjectionPendingAtUtc.Remove(address);
    }

    public bool TryWorldToScreenBasis(
in Vec3 world,
in CameraSnapshot camera,
float screenW,
float screenH,
float scaleX,
float scaleY,
out float screenX,
out float screenY,
out float forward,
out float distance)
    {
        screenX = 0;
        screenY = 0;
        forward = 0;
        distance = 0;

        var delta = world - camera.Position;

        float camRight = Vec3.Dot(delta, camera.Right);
        float camUp = Vec3.Dot(delta, camera.Up);
        float camForward = Vec3.Dot(delta, camera.Forward);

        if (camForward <= 1.0f || float.IsNaN(camForward) || float.IsInfinity(camForward))
            return false;

        distance = Distance3D(camera.Position, world);
        if (float.IsNaN(distance) || float.IsInfinity(distance))
            return false;

        screenX = screenW / 2.0f + (camRight / camForward) * scaleX;
        screenY = screenH / 2.0f - (camUp / camForward) * scaleY;

        forward = camForward;
        return true;
    }

    public List<ProjectedObject> GetProjectedObjects(
        float screenW,
        float screenH,
        float scaleX,
        float scaleY)
    {
        var result = new List<ProjectedObject>();
        var seenAircraft = new HashSet<long>();

        bool hasViewProj = TryReadViewProjection(out float[] viewProj);
        bool hasGlobtm = TryReadGlobtm(out float[] globtm);

        if (!hasViewProj && !hasGlobtm)
            return result;

        float[] defaultVp = hasViewProj ? viewProj : globtm;
        // Для авиации используем только стабильный GLOBTM. VIEW+PROJ в лётном режиме даёт другой/stale pass.

        var objects = GetObjectAddresses();
        var self = GetSelfPosition();
        bool selfOk = IsFinite(self.x) && IsFinite(self.z);

        // Собственную команду нельзя считать "по умолчанию".
        // В TAB/после смерти/при выборе нового танка self-object может временно не находиться.
        // Старый код в этот момент отключал team-фильтр, поэтому на 5-15 секунд появлялись союзники.
        long selfObjectAddress = selfOk ? FindSelfObjectAddress(objects, self.x, self.z) : 0;

        // В танковом бою self ищется через старый SelfPosOffset.
        // В лётном бою этот offset часто не обновляется, поэтому selfTeam не находился
        // и ESP возвращал пустой список. Fallback: ближайший к камере aircraft = свой самолёт.
        if (selfObjectAddress == 0 && hasGlobtm)
        {
            selfObjectAddress = FindSelfAircraftByView(objects, globtm, screenW, screenH);
        }

        int selfTeam = ResolveSelfTeam(selfObjectAddress);

        if (!IsValidTeamMarker(selfTeam))
        {
            _lastSelfTeam = int.MinValue;
            return result;
        }

        _lastSelfTeam = selfTeam;

        foreach (long objectAddress in objects)
        {
            var pos = GetCoordinates(objectAddress);
            if (!IsValidWorldPosition(pos))
                continue;

            // Убираем только собственную машину по адресу, а не все объекты в радиусе 35м.
            if (selfObjectAddress != 0 && objectAddress == selfObjectAddress)
                continue;

            // Убираем мусор/props: у небоевых объектов FE8 обычно = -1.
            // FE8 читаем как short, потому что в debug-подписи он был I16.
            if (!IsCombatUnit(objectAddress))
                continue;

            // Убираем трупы. По live/dead diff чистый кандидат: object+0x1860 byte/int 0 -> 1 после смерти.
            if (!IsAlive(objectAddress))
                continue;

            // FE0 используем только когда selfTeam уже разрешён или взят из свежего cache.
            // Если selfTeam неизвестен — выше возвращаем пустой список, а не рисуем союзников.
            if (!IsEnemyOfSelfTeam(objectAddress, selfTeam))
                continue;

            bool isAircraft = IsAircraftObject(objectAddress);

            if (isAircraft && !hasGlobtm)
                continue;

            var espPos = isAircraft
                ? pos
                : new Vec3(pos.X, pos.Y + GroundEspYOffset, pos.Z);

            float[] primaryVp = isAircraft ? globtm : defaultVp;
            float[] usedVp = primaryVp;

            bool projectedOk = TryWorldToScreenViewProjection(
                espPos,
                primaryVp,
                screenW,
                screenH,
                out float sx,
                out float sy,
                out float clipW);

            if (!projectedOk && !isAircraft && hasViewProj && hasGlobtm)
            {
                float[] fallbackVp = ReferenceEquals(primaryVp, globtm) ? viewProj : globtm;
                projectedOk = TryWorldToScreenViewProjection(
                    espPos,
                    fallbackVp,
                    screenW,
                    screenH,
                    out sx,
                    out sy,
                    out clipW);

                if (projectedOk)
                    usedVp = fallbackVp;
            }

            if (projectedOk)
            {
                bool boxOk = TryBuildProjectedBox(pos, isAircraft, usedVp, screenW, screenH, out float left, out float top, out float right, out float bottom);

                // В авиации иногда один corner-box строится по соседнему render pass и улетает от anchor-точки.
                // Если центр прямоугольника далеко от projected center, отбрасываем 3D-box и рисуем стабильный 2D fallback.
                if (boxOk && isAircraft)
                {
                    float bw = Math.Max(1.0f, right - left);
                    float bh = Math.Max(1.0f, bottom - top);
                    float cx = (left + right) * 0.5f;
                    float cy = (top + bottom) * 0.5f;
                    float maxShift = Math.Max(160.0f, Math.Max(bw, bh) * 2.5f);

                    if (Math.Abs(cx - sx) > maxShift || Math.Abs(cy - sy) > maxShift)
                        boxOk = false;
                }

                if (!boxOk)
                {
                    // Fallback на старую дистанционную формулу, если 3D box частично за камерой.
                    float h = isAircraft
                        ? Clamp(2600.0f / Math.Max(Math.Abs(clipW), 1.0f), 18.0f, 70.0f)
                        : Clamp(4200.0f / Math.Max(Math.Abs(clipW), 1.0f), 24.0f, 120.0f);
                    float w = isAircraft ? h * 1.80f : h * 1.35f;

                    left = sx - w * 0.5f;
                    top = sy - h * 0.5f;
                    right = sx + w * 0.5f;
                    bottom = sy + h * 0.5f;
                }

                float depthForBox = Math.Abs(clipW);
                float distanceMeters = selfOk
                    ? DistanceXZ(self.x, self.z, pos.X, pos.Z)
                    : depthForBox;

                var projectedObject = new ProjectedObject(
                    objectAddress,
                    pos,
                    sx,
                    sy,
                    left,
                    top,
                    right,
                    bottom,
                    depthForBox,
                    distanceMeters,
                    clipW,
                    isAircraft,
                    ReadUnitName(objectAddress)
                );

                if (isAircraft)
                {
                    if (!TryStabilizeAircraftProjection(projectedObject, out projectedObject))
                        continue;

                    seenAircraft.Add(objectAddress);
                }

                result.Add(projectedObject);
            }
        }

        if (selfObjectAddress != 0)
            DropAircraftProjectionCache(selfObjectAddress);

        AddRecentCachedAircraft(result, seenAircraft, selfObjectAddress);

        return result;
    }
}


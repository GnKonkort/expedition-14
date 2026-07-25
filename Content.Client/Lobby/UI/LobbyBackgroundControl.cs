using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Timing;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Lobby.UI;

public enum LobbyBgMode : byte
{
    Server = 0,
    BlackHole = 1
}

/// <summary>
/// Dual-mode lobby background: server room and black hole, crossfaded on a 180s timer.
/// Renders into a capped low-res CPU buffer at ~24 FPS.
/// </summary>
public sealed class LobbyBackgroundControl : Control
{
    private const int PixelScale = 4;
    private const float TargetFrameSeconds = 1f / 24f;
    private const float SimScale = 60f * TargetFrameSeconds;
    private const float BgDuration = 180f;
    private const float BgFade = 3f;

    private const int RingDebrisCount = 80;
    private const int MaxFlybys = 12;
    private const int MaxBigHalfPx = 36;
    private const int RackUnit = 6;

    [Dependency] private readonly IClyde _clyde = default!;

    public event Action<LobbyBgMode>? ModeChanged;

    public LobbyBgMode CurrentMode => _bgBlend >= 0.5f ? LobbyBgMode.BlackHole : LobbyBgMode.Server;

    public void SwitchBackground()
    {
        _bgTarget = _bgTarget < 0.5f ? 1f : 0f;
        _bgTimer = 0f;
    }

    private OwnedTexture? _texture;
    private Rgba32[] _buffer = Array.Empty<Rgba32>();
    private Rgba32[] _scratchBuffer = Array.Empty<Rgba32>();
    private Rgba32[] _writeBuffer = null!;
    private Vector2i _bufferSize;
    private float _accum;
    private float _t;
    private int _frameCount;
    private int _lastBigSpawn = -200;
    private int _randState = 1337;

    private float _bgBlend;
    private float _bgTarget;
    private float _bgTimer;
    private bool _lastNotifiedIsBh;

    private readonly List<(float X, float Y, float B, float Tw)> _stars = new();
    private readonly RingDebris[] _ring = new RingDebris[RingDebrisCount];
    private readonly List<FlybyDebris> _flybys = new();

    private readonly List<RackLayer> _rackLayers = new();
    private readonly List<Blinker> _blinkers = new();
    private readonly float[] _cablePulseTs = new float[9];
    private readonly float[] _cableSpeeds =
    {
        0.0048f, 0.0062f, 0.0058f, 0.0044f,
        0.0055f, 0.0080f, 0.0100f, 0.0072f, 0.0060f
    };

    private GazeState _gazeState = GazeState.Scanning;
    private float _gazeTimer;
    private float _nextStareIn = 20f;
    private float _stareDur;
    private float _pupilX;
    private float _pupilY;
    private float _pupilMax;

    public LobbyBackgroundControl()
    {
        IoCManager.InjectDependencies(this);
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;

        for (var i = 0; i < 320; i++)
            _stars.Add((RandomFloat(), RandomFloat(), RandomFloat(), RandomFloat() * MathF.PI * 2f));

        for (var i = 0; i < RingDebrisCount; i++)
        {
            _ring[i] = new RingDebris
            {
                OrbitAngle = SeededRand(i * 7) * MathF.PI * 2f,
                OrbitR = 0.85f + SeededRand(i * 13 + 1) * 0.4f,
                OrbitSpeed = (0.018f + SeededRand(i * 17 + 2) * 0.022f) * (SeededRand(i * 3) > 0.5f ? 1f : -1f),
                Size = 1 + (int) (SeededRand(i * 11 + 3) * 3f),
                Seed = i * 31
            };
        }

        _nextStareIn = 14f + RandomFloat() * 16f;
        for (var i = 0; i < _cablePulseTs.Length; i++)
            _cablePulseTs[i] = i / (float) _cablePulseTs.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _texture?.Dispose();
            _texture = null;
        }

        base.Dispose(disposing);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (!Visible || PixelWidth < PixelScale || PixelHeight < PixelScale)
            return;

        var dt = args.DeltaSeconds;

        _bgTimer += dt;
        if (_bgTimer >= BgDuration)
        {
            _bgTimer = 0f;
            _bgTarget = _bgTarget < 0.5f ? 1f : 0f;
        }

        var fadeRate = dt / BgFade;
        if (_bgBlend < _bgTarget)
            _bgBlend = Math.Min(_bgTarget, _bgBlend + fadeRate);
        else
            _bgBlend = Math.Max(_bgTarget, _bgBlend - fadeRate);

        var isBh = _bgBlend >= 0.5f;
        if (isBh != _lastNotifiedIsBh)
        {
            _lastNotifiedIsBh = isBh;
            ModeChanged?.Invoke(isBh ? LobbyBgMode.BlackHole : LobbyBgMode.Server);
        }

        if (_bgBlend < 0.99f)
        {
            UpdateGaze(dt);
            for (var i = 0; i < _cablePulseTs.Length; i++)
                _cablePulseTs[i] = (_cablePulseTs[i] + _cableSpeeds[i] * dt * 60f) % 1f;
        }

        _accum += dt;
        if (_accum < TargetFrameSeconds)
            return;

        _accum = 0f;

        try
        {
            EnsureBuffer();
            if (_texture == null || _buffer.Length == 0)
                return;

            RenderFrame();
            _texture.SetSubImage<Rgba32>(Vector2i.Zero, _bufferSize, _buffer.AsSpan());
            _t += TargetFrameSeconds;
            _frameCount++;
        }
        catch
        {
            // Never take down the client over a decorative background.
        }
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (_texture == null)
            return;

        handle.DrawTextureRect(_texture, PixelSizeBox);
    }

    private void EnsureBuffer()
    {
        var w = Math.Max(1, PixelWidth / PixelScale);
        var h = Math.Max(1, PixelHeight / PixelScale);

        w = Math.Min(w, 480);
        h = Math.Min(h, 270);

        if (_bufferSize.X == w && _bufferSize.Y == h && _texture != null)
            return;

        _texture?.Dispose();
        _texture = null;
        _bufferSize = new Vector2i(w, h);
        _buffer = new Rgba32[w * h];
        _scratchBuffer = new Rgba32[w * h];
        _texture = _clyde.CreateBlankTexture<Rgba32>(_bufferSize, "lobby-background");

        _pupilMax = MathF.Min(w, h) * 0.155f * 0.78f * 0.38f;
        RebuildRacks(w, h);
    }

    private void RenderFrame()
    {
        if (_bgBlend < 0.01f)
        {
            RenderServer(_buffer);
            return;
        }

        if (_bgBlend > 0.99f)
        {
            RenderBlackHole(_buffer);
            return;
        }

        RenderServer(_buffer);
        RenderBlackHole(_scratchBuffer);
        CompositeBuffers(_buffer, _scratchBuffer, _bgBlend);
    }

    private static void CompositeBuffers(Rgba32[] server, Rgba32[] blackHole, float blend)
    {
        var t = Math.Clamp(blend, 0f, 1f);
        var inv = 1f - t;
        for (var i = 0; i < server.Length; i++)
        {
            ref var dst = ref server[i];
            var bh = blackHole[i];
            dst.R = (byte) (dst.R * inv + bh.R * t);
            dst.G = (byte) (dst.G * inv + bh.G * t);
            dst.B = (byte) (dst.B * inv + bh.B * t);
            dst.A = 255;
        }
    }

    private void UpdateGaze(float dt)
    {
        _gazeTimer += dt;

        if (_gazeState == GazeState.Scanning)
        {
            if (_gazeTimer >= _nextStareIn)
            {
                _gazeState = GazeState.Staring;
                _gazeTimer = 0f;
                _stareDur = 2.5f + RandomFloat() * 2.5f;
            }
        }
        else if (_gazeTimer >= _stareDur)
        {
            _gazeState = GazeState.Scanning;
            _gazeTimer = 0f;
            _nextStareIn = 14f + RandomFloat() * 16f;
        }

        float targetX;
        float targetY;
        if (_gazeState == GazeState.Scanning)
        {
            targetX = MathF.Sin(_t * 0.26f) * _pupilMax * 0.88f;
            targetY = MathF.Sin(_t * 0.16f + 1.1f) * _pupilMax * 0.52f;
        }
        else
        {
            targetX = 0f;
            targetY = 0f;
        }

        var ease = _gazeState == GazeState.Staring ? 0.12f : 0.04f;
        _pupilX += (targetX - _pupilX) * ease;
        _pupilY += (targetY - _pupilY) * ease;
    }

    #region Server room

    private void RenderServer(Rgba32[] target)
    {
        _writeBuffer = target;
        var w = _bufferSize.X;
        var h = _bufferSize.Y;

        DrawServerRoomBg(w, h);
        DrawServerRacks(w, h);
        DrawRobot(w, h);
        DrawVents(w, h); // fog in front of lower scene (Figma layering)
    }

    private void DrawServerRoomBg(int w, int h)
    {
        Array.Fill(_writeBuffer, new Rgba32(2, 0, 1, 255));

        for (var y = 0; y < h; y += 2)
        {
            for (var x = 0; x < w; x++)
                Blend(x, y, 0, 0, 0, 0.22f);
        }

        // Soft ambient — smooth falloff, no hard disk edge
        var cx = w * 0.5f;
        var cy = h * 0.50f;
        var maxR = w * 0.62f;
        const int step = 2;

        for (var y = 0; y < h; y += step)
        for (var x = 0; x < w; x += step)
        {
            var dx = x - cx;
            var dy = (y - cy) * 1.15f;
            var dist = MathF.Sqrt(dx * dx + dy * dy) / maxR;
            if (dist >= 1f)
                continue;

            // Smooth cubic fade — no banding / hard ring
            var fall = 1f - dist;
            fall = fall * fall * (3f - 2f * fall);
            var a = 0.28f * fall * fall;
            if (a <= 0.008f)
                continue;

            var r = (byte) (20 + (int) (95 * fall));
            var g = (byte) (1 + (int) (5 * fall));
            var b = (byte) (2 + (int) (6 * fall));

            for (var oy = 0; oy < step; oy++)
            for (var ox = 0; ox < step; ox++)
                Blend(x + ox, y + oy, r, g, b, a);
        }

        // Very soft side ambient (no vertical slabs)
        var wallW = (int) (w * 0.14f);
        var wallTop = (int) (h * 0.14f);
        var wallBot = (int) (h * 0.82f);

        for (var x = 0; x < wallW; x++)
        {
            var t = 1f - x / (float) wallW;
            var a = 0.06f * t * t * t;
            for (var y = wallTop; y < wallBot; y++)
                Blend(x, y, 70, 3, 4, a);
        }

        var rightStart = (int) (w * 0.86f);
        for (var x = rightStart; x < w; x++)
        {
            var t = (x - rightStart) / (float) Math.Max(1, w - rightStart);
            var a = 0.06f * (1f - t) * (1f - t) * (1f - t);
            for (var y = wallTop; y < wallBot; y++)
                Blend(x, y, 70, 3, 4, a);
        }

        var floorStart = (int) (h * 0.78f);
        for (var y = floorStart; y < h; y++)
        {
            var t = (y - floorStart) / (float) Math.Max(1, h - floorStart);
            var a = 0.88f * t;
            for (var x = 0; x < w; x++)
                Blend(x, y, 8, 1, 1, a);
        }

        for (var fl = 0; fl < 10; fl++)
        {
            var fy = (int) (h * 0.82f + fl * h * 0.015f);
            if ((uint) fy >= (uint) h)
                continue;
            for (var x = 0; x < w; x++)
                Blend(x, fy, 36, 6, 6, 0.22f);
        }

        for (var vl = -5; vl <= 5; vl++)
        {
            var x0 = (int) (w * 0.5f + vl * w * 0.07f);
            var x1 = (int) (w * 0.5f + vl * w * 0.45f);
            DrawLine(x0, (int) (h * 0.82f), x1, h - 1, 36, 6, 6, 0.14f);
        }

        var ceilH = (int) (h * 0.18f);
        for (var y = 0; y < ceilH; y++)
        {
            var t = 1f - y / (float) ceilH;
            var a = 0.90f * t * t;
            for (var x = 0; x < w; x++)
                Blend(x, y, 6, 1, 1, a);
        }
    }

    private void DrawLine(int x0, int y0, int x1, int y1, byte r, byte g, byte b, float a)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;
        var x = x0;
        var y = y0;

        while (true)
        {
            Blend(x, y, r, g, b, a);
            if (x == x1 && y == y1)
                break;

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static float SmoothNoise(float x, float y, int seed)
    {
        var ix = (int) MathF.Floor(x);
        var iy = (int) MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        static float Hash(int n)
        {
            unchecked
            {
                n = (n ^ 61) ^ (n >> 16);
                n *= 9;
                n ^= n >> 4;
                n *= 0x27d4eb2d;
                n ^= n >> 15;
                return (n & 0xFFFF) / 65535f;
            }
        }

        var a = Hash(seed + ix * 374761 + iy * 668265);
        var b = Hash(seed + (ix + 1) * 374761 + iy * 668265);
        var c = Hash(seed + ix * 374761 + (iy + 1) * 668265);
        var d = Hash(seed + (ix + 1) * 374761 + (iy + 1) * 668265);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    private void DrawPuff(int cx, int cy, float rx, float ry, float alpha, byte rv, byte gv, byte bv, int seed)
    {
        if (alpha <= 0.004f || rx < 0.8f || ry < 0.8f)
            return;

        var rMax = (int) MathF.Ceiling(MathF.Max(rx, ry) * 1.3f);
        for (var y = cy - rMax; y <= cy + rMax; y++)
        for (var x = cx - rMax; x <= cx + rMax; x++)
        {
            var nx = (x - cx) / rx;
            var ny = (y - cy) / ry;
            var ang = MathF.Atan2(ny, nx);
            var warp = 0.70f
                       + 0.20f * MathF.Sin(ang * 3.0f + seed * 0.37f)
                       + 0.14f * MathF.Sin(ang * 5.0f - seed * 0.21f)
                       + 0.12f * SmoothNoise(x * 0.18f, y * 0.18f, seed);
            var dist = MathF.Sqrt(nx * nx + ny * ny) / warp;
            if (dist > 1f)
                continue;

            var fall = (1f - dist) * (1f - dist * 0.5f);
            var a = alpha * fall;
            if (a <= 0.004f)
                continue;

            Blend(x, y, rv, gv, bv, MathF.Min(1f, a));
        }
    }

    /// <summary>Cheap elliptical mist blob — no per-pixel noise (for wide floor coverage).</summary>
    private void DrawFogBlob(int cx, int cy, float rx, float ry, float alpha, byte rv, byte gv, byte bv)
    {
        if (alpha <= 0.004f || rx < 0.8f || ry < 0.8f)
            return;

        var x0 = Math.Max(0, (int) MathF.Floor(cx - rx));
        var x1 = Math.Min(_bufferSize.X - 1, (int) MathF.Ceiling(cx + rx));
        var y0 = Math.Max(0, (int) MathF.Floor(cy - ry));
        var y1 = Math.Min(_bufferSize.Y - 1, (int) MathF.Ceiling(cy + ry));
        var invRx2 = 1f / (rx * rx);
        var invRy2 = 1f / (ry * ry);

        for (var y = y0; y <= y1; y++)
        {
            var dy = y - cy;
            var ty = dy * dy * invRy2;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx;
                var d2 = dx * dx * invRx2 + ty;
                if (d2 > 1f)
                    continue;

                var a = alpha * (1f - d2);
                if (a > 0.004f)
                    Blend(x, y, rv, gv, bv, a);
            }
        }
    }

    private void DrawVents(int w, int h)
    {
        // Exactly two ceiling ducts — steam falls from them and sheets along the floor.
        (float xf, float wf)[] vents =
        {
            (0.22f, 0.090f), // left
            (0.69f, 0.090f), // right
        };

        var floorY = h * 0.84f;
        var eyeX = w * 0.5f;
        var eyeY = h * 0.50f;
        var ventMids = new float[vents.Length];
        var ventBot = (int) (h * 0.055f);

        // Falling columns from each duct, then horizontal floor spread
        const int puffsPerVent = 8;
        for (var vi = 0; vi < vents.Length; vi++)
        {
            var v = vents[vi];
            var vx = (int) (w * v.xf);
            var vw = (int) (w * v.wf);
            var midX = vx + vw * 0.5f;
            ventMids[vi] = midX;
            var seed = vi * 100;

            for (var pi = 0; pi < puffsPerVent; pi++)
            {
                var birthOffset = SeededRand(seed + pi * 7);
                var driftSeed = SeededRand(seed + pi * 13 + 1);
                var sizeSeed = SeededRand(seed + pi * 17 + 2);
                var speedSeed = 0.032f + SeededRand(seed + pi * 11 + 3) * 0.045f;
                var age = (_t * speedSeed + birthOffset) % 1f;
                var inFall = age < 0.42f;
                var fallF = age / 0.42f;
                var spreadF = (age - 0.42f) / 0.58f;

                var jitterX = (driftSeed - 0.5f) * vw * 1.1f;
                var driftDir = driftSeed > 0.5f ? 1f : -1f;
                var driftAmt = inFall
                    ? 0f
                    : spreadF * w * (0.14f + sizeSeed * 0.12f) * driftDir;
                var wobble = MathF.Sin(_t * (0.45f + sizeSeed * 0.3f) + pi * 2.1f) * w * 0.008f;

                float pcx;
                float pcy;
                float prx;
                float pry;
                float pa;

                if (inFall)
                {
                    pcx = midX + jitterX * fallF * 0.35f + wobble * 0.4f;
                    pcy = ventBot + fallF * (floorY - ventBot);
                    var baseR = w * (0.014f + sizeSeed * 0.008f) + fallF * w * 0.016f;
                    prx = baseR * (0.40f + sizeSeed * 0.18f);
                    pry = baseR * (1.55f + sizeSeed * 0.4f);
                    pa = fallF * (0.07f + sizeSeed * 0.03f) * (1f - fallF * 0.25f);
                }
                else
                {
                    pcx = midX + jitterX * 0.5f + driftAmt + wobble;
                    pcy = floorY + spreadF * h * 0.06f + MathF.Sin(pi * 1.3f + _t * 0.2f) * h * 0.012f;
                    var baseR = w * (0.035f + sizeSeed * 0.025f) + spreadF * w * 0.10f;
                    prx = baseR * (1.8f + sizeSeed * 0.55f);
                    pry = baseR * (0.42f + sizeSeed * 0.25f);
                    pa = (0.09f + sizeSeed * 0.04f) * (1f - spreadF * spreadF);
                }

                var lit = MathF.Max(0f, 1f - MathF.Abs(pcx - eyeX) / (w * 0.45f));
                var rv = (byte) Math.Clamp((int) (175 + lit * 45), 0, 255);
                var gv = (byte) Math.Clamp((int) (155 + lit * 15), 0, 255);
                var bv = (byte) Math.Clamp((int) (148 + lit * 10), 0, 255);

                if (inFall)
                    DrawPuff((int) pcx, (int) pcy, MathF.Max(1.2f, prx), MathF.Max(1.4f, pry), pa, rv, gv, bv, seed + pi * 97);
                else
                    DrawFogBlob((int) pcx, (int) pcy, MathF.Max(1.2f, prx), MathF.Max(1.4f, pry), pa, rv, gv, bv);
            }
        }

        // Full-width floor fog — subsampled + 2 cheap noise samples (was 10 FBM octaves/pixel)
        var fogTop = (int) (h * 0.62f);
        var midL = ventMids[0];
        var midR = ventMids[1];
        var spreadR = w * 0.42f;
        var tSlow = _t * 0.10f;
        var tDrift = _t * 0.04f;

        for (var y = fogTop; y < h; y += 2)
        {
            var yh = (y - fogTop) / (float) Math.Max(1, h - fogTop);
            var heightMask = MathF.Pow(yh, 0.95f) * (0.65f + 0.35f * MathF.Sin(yh * MathF.PI));
            var ny1 = y * 0.042f - _t * 0.065f;
            var ny2 = y * 0.02f - tDrift;

            for (var x = 0; x < w; x += 2)
            {
                var dL = MathF.Abs(x - midL) / spreadR;
                var dR = MathF.Abs(x - midR) / spreadR;
                var nearVent = MathF.Max(0f, 1f - MathF.Min(dL, dR));
                var floorMask = 0.45f + 0.55f * nearVent * nearVent;

                var n1 = SmoothNoise(x * 0.028f + tSlow, ny1, 42);
                var n2 = SmoothNoise(x * 0.012f - tDrift, ny2, 17);
                var cloud = (n1 * 0.7f + n2 * 0.3f - 0.28f) / 0.52f;
                if (cloud <= 0.02f)
                    continue;

                cloud = MathF.Min(1f, cloud);
                cloud = cloud * cloud * (3f - 2f * cloud);

                var dx = x - eyeX;
                var dy = y - eyeY;
                var dist = MathF.Sqrt(dx * dx + dy * dy) / (w * 0.58f);
                var lit = MathF.Max(0f, 1f - dist);
                lit *= lit;

                var a = cloud * heightMask * floorMask * (0.20f + lit * 0.14f);
                if (a < 0.008f)
                    continue;

                a = MathF.Min(0.58f, a);
                var rv = (byte) Math.Clamp((int) (160 + lit * 75 + cloud * 22), 0, 255);
                var gv = (byte) Math.Clamp((int) (145 + lit * 22 + cloud * 12), 0, 255);
                var bv = (byte) Math.Clamp((int) (140 + lit * 14 + cloud * 8), 0, 255);

                // Stamp 2×2 so subsampled fog still covers the floor
                Blend(x, y, rv, gv, bv, a);
                if (x + 1 < w)
                    Blend(x + 1, y, rv, gv, bv, a);
                if (y + 1 < h)
                {
                    Blend(x, y + 1, rv, gv, bv, a * 0.92f);
                    if (x + 1 < w)
                        Blend(x + 1, y + 1, rv, gv, bv, a * 0.92f);
                }
            }
        }

        // Extra low ground blobs across the full floor (cheap ellipses)
        const int floorPuffs = 12;
        for (var pi = 0; pi < floorPuffs; pi++)
        {
            var birth = SeededRand(pi * 41 + 7);
            var sizeSeed = SeededRand(pi * 19 + 3);
            var xSeed = SeededRand(pi * 29 + 5);
            var age = (_t * (0.018f + sizeSeed * 0.012f) + birth) % 1f;
            var fade = age < 0.2f ? age / 0.2f : age > 0.75f ? (1f - age) / 0.25f : 1f;
            var pcx = xSeed * w + MathF.Sin(_t * 0.25f + pi * 1.4f) * w * 0.04f;
            var pcy = floorY + MathF.Sin(pi * 2.1f + _t * 0.15f) * h * 0.02f + h * 0.02f;
            var prx = w * (0.055f + sizeSeed * 0.06f);
            var pry = h * (0.032f + sizeSeed * 0.025f);
            var pa = (0.06f + sizeSeed * 0.04f) * fade;
            var lit = MathF.Max(0f, 1f - MathF.Abs(pcx - eyeX) / (w * 0.5f));
            var rv = (byte) Math.Clamp((int) (170 + lit * 40), 0, 255);
            var gv = (byte) Math.Clamp((int) (155 + lit * 12), 0, 255);
            var bv = (byte) Math.Clamp((int) (148 + lit * 8), 0, 255);
            DrawFogBlob((int) pcx, (int) pcy, MathF.Max(2f, prx), MathF.Max(1.5f, pry), pa, rv, gv, bv);
        }

        // Duct housings on top
        for (var vi = 0; vi < vents.Length; vi++)
        {
            var v = vents[vi];
            var vx = (int) (w * v.xf);
            var vw = (int) (w * v.wf);
            var vh = ventBot;
            var midX = vx + vw / 2;

            for (var dy = 0; dy < vh; dy++)
            for (var dx = 0; dx < vw; dx++)
            {
                var isFrame = dx == 0 || dx == vw - 1 || dy == 0 || dy == vh - 1;
                var isSlat = dy >= 2 && dy % 3 == 0;
                byte sh, sg, sb;
                if (isFrame) { sh = 42; sg = 18; sb = 18; }
                else if (isSlat) { sh = 24; sg = 11; sb = 11; }
                else
                {
                    sh = (byte) (6 + (int) (dy / (float) vh * 6));
                    sg = sh;
                    sb = (byte) Math.Clamp((int) (sh * 1.4f), 0, 255);
                }

                Set(vx + dx, dy, new Rgba32(sh, sg, sb, 255));
            }

            for (var dx = 1; dx < vw - 1; dx++)
            {
                Blend(vx + dx, vh, 0, 0, 0, 0.55f);
                if (vh + 1 < h)
                    Blend(vx + dx, vh + 1, 0, 0, 0, 0.45f);
            }

            var ga = 0.10f + 0.05f * MathF.Sin(_t * 1.7f + vi * 1.4f);
            var glowR = (int) (vw * 0.7f);
            for (var gy = vh - 1; gy < vh + (int) (h * 0.07f); gy++)
            for (var gx = (int) (midX - vw); gx < (int) (midX + vw); gx++)
            {
                var dx = gx - midX;
                var dy = gy - (vh + 1);
                var dist = MathF.Sqrt(dx * dx + dy * dy) / glowR;
                if (dist > 1f)
                    continue;

                Blend(gx, gy, 170, 200, 220, ga * (1f - dist) * (1f - dist));
            }
        }
    }

    private void RebuildRacks(int w, int h)
    {
        _rackLayers.Clear();
        _blinkers.Clear();

        _rackLayers.AddRange(new[]
        {
            new RackLayer(0.17f, 0.07f, 0.22f, 0.55f, 0f, true, 10),
            new RackLayer(0.26f, 0.06f, 0.24f, 0.50f, 0f, true, 20),
            new RackLayer(0.68f, 0.06f, 0.24f, 0.50f, 0f, false, 30),
            new RackLayer(0.77f, 0.07f, 0.22f, 0.55f, 0f, false, 40),
            new RackLayer(0.02f, 0.12f, 0.18f, 0.66f, 0.4f, true, 1),
            new RackLayer(0.86f, 0.12f, 0.18f, 0.66f, 0.4f, false, 2),
            new RackLayer(-0.06f, 0.14f, 0.08f, 0.92f, 1f, true, 5),
            new RackLayer(0.92f, 0.14f, 0.08f, 0.92f, 1f, false, 6),
        });

        foreach (var layer in _rackLayers)
        {
            var rx = (int) (w * layer.XFrac);
            var rw = Math.Max(4, (int) (w * layer.WFrac));
            var ry = (int) (h * layer.YFrac);
            var rh = (int) (h * layer.HFrac);
            var slotX = rx + 2;
            var slotW = Math.Max(1, rw - 4);

            for (var row = 0; row * RackUnit < rh; row++)
            {
                var numLights = 3 + (int) (SeededRand(row * 7 + layer.Seed * 100) * 3f);
                for (var li = 0; li < numLights; li++)
                {
                    var offX = 1 + (int) (SeededRand(row * 31 + li * 13 + layer.Seed) * Math.Max(1, slotW - 2));
                    _blinkers.Add(new Blinker
                    {
                        SlotX = slotX,
                        Gy = ry + row * RackUnit + 2 + (int) (SeededRand(row * 5 + li + layer.Seed) * 2f),
                        OffX = offX,
                        Phase = SeededRand(row * 19 + li + layer.Seed) * MathF.PI * 2f,
                        Speed = 0.3f + SeededRand(row * 23 + li + layer.Seed) * 1.8f,
                        Col = (byte) (li % 3 == 2 ? 2 : li % 2 == 0 ? 0 : 1)
                    });
                }
            }
        }
    }

    private void DrawServerRacks(int w, int h)
    {
        foreach (var layer in _rackLayers)
            DrawRack(layer, w, h);
    }

    private void DrawRack(RackLayer layer, int w, int h)
    {
        var rx = (int) (w * layer.XFrac);
        var rw = Math.Max(4, (int) (w * layer.WFrac));
        var ry = (int) (h * layer.YFrac);
        var rh = (int) (h * layer.HFrac);
        var ds = layer.DepthScale;
        var darkness = 1f - ds * 0.55f;
        var alphaFade = 0.55f + ds * 0.45f;
        var slotX = rx + 2;

        for (var dx = 0; dx < rw; dx++)
        for (var dy = 0; dy < rh; dy++)
        {
            var isOuter = dx == 0 || dx == rw - 1 || dy == 0 || dy == rh - 1;
            var rowInUnit = dy % RackUnit;
            var isSlotBorder = rowInUnit == 0 || rowInUnit == RackUnit - 1;
            var isInset = dx == 1 || dx == rw - 2;
            var isInterior = dx >= 2 && dx <= rw - 3;

            int sh;
            if (isOuter)
                sh = (int) (22 * (1f - darkness * 0.3f));
            else if (isSlotBorder)
                sh = (int) (28 * (1f - darkness * 0.3f));
            else if (isInset)
            {
                var isShadow = layer.FacingRight ? dx == 1 : dx == rw - 2;
                sh = isShadow
                    ? (int) (12 * (1f - darkness * 0.4f))
                    : (int) (32 * (1f - darkness * 0.2f));
            }
            else if (isInterior)
            {
                var depthFrac = (rowInUnit - 1) / (float) (RackUnit - 3);
                sh = (int) ((14 + depthFrac * 8) * (1f - darkness * 0.5f));
            }
            else
                sh = (int) (17 * (1f - darkness * 0.4f));

            var g = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
            // Neutral dark metal — slight warm only on the face toward the eye
            var towardEye = layer.FacingRight
                ? dx / (float) Math.Max(1, rw - 1)
                : 1f - dx / (float) Math.Max(1, rw - 1);
            var warm = towardEye * towardEye * (0.12f + ds * 0.10f);
            var rr = (byte) Math.Clamp(sh + (int) (warm * 40), 0, 255);
            Blend(rx + dx, ry + dy, rr, g, g, alphaFade);
        }

        for (var row = 0; row * RackUnit < rh; row++)
        {
            var topY = ry + row * RackUnit + 1;
            for (var x = rx + 2; x < rx + rw - 2; x++)
                Blend(x, topY, 0, 0, 0, 0.5f * alphaFade);

            var botY = ry + row * RackUnit + RackUnit - 2;
            for (var x = rx + 2; x < rx + rw - 2; x++)
                Blend(x, botY, 40, 6, 6, 0.12f * alphaFade);
        }

        // Soft rim light on the edge facing the core — not a tall red slab
        if (ds > 0.35f)
        {
            var rimX = layer.FacingRight ? rx + rw - 1 : rx;
            var ga = 0.04f + ds * 0.05f;
            for (var y = ry; y < ry + rh; y++)
            {
                var yf = (y - ry) / (float) Math.Max(1, rh);
                var midBoost = 1f - MathF.Abs(yf - 0.45f) * 1.6f;
                if (midBoost <= 0f)
                    continue;

                Blend(rimX, y, 160, 20, 20, ga * midBoost);
                if (layer.FacingRight)
                    Blend(rimX - 1, y, 120, 12, 12, ga * midBoost * 0.45f);
                else
                    Blend(rimX + 1, y, 120, 12, 12, ga * midBoost * 0.45f);
            }
        }

        foreach (var b in _blinkers)
        {
            if (b.SlotX != slotX)
                continue;

            var on = MathF.Sin(_t * b.Speed + b.Phase) > 0.15f;
            byte lr;
            byte lg;
            byte lb;
            byte br;
            byte bg;
            byte bb;
            switch (b.Col)
            {
                case 0:
                    lr = 48; lg = 6; lb = 6;
                    br = 255; bg = 51; bb = 51;
                    break;
                case 1:
                    lr = 26; lg = 12; lb = 4;
                    br = 221; bg = 102; bb = 0;
                    break;
                default:
                    lr = 7; lg = 16; lb = 7;
                    br = 34; bg = 221; bb = 34;
                    break;
            }

            var gx = b.SlotX + b.OffX;
            if (on)
            {
                Set(gx, b.Gy, new Rgba32(br, bg, bb, 255));
                if (ds > 0.15f)
                {
                    var bloomA = 0.22f + ds * 0.18f;
                    var bloomR = b.Col == 0 ? (byte) 255 : b.Col == 1 ? (byte) 220 : (byte) 40;
                    var bloomG = b.Col == 0 ? (byte) 40 : b.Col == 1 ? (byte) 100 : (byte) 220;
                    var bloomB = b.Col == 0 ? (byte) 40 : b.Col == 1 ? (byte) 20 : (byte) 40;
                    for (var oy = -1; oy <= 1; oy++)
                    for (var ox = -1; ox <= 1; ox++)
                        Blend(gx + ox, b.Gy + oy, bloomR, bloomG, bloomB, bloomA);
                }
            }
            else
            {
                Set(gx, b.Gy, new Rgba32(lr, lg, lb, 255));
            }
        }
    }

    private void DrawRobot(int w, int h)
    {
        var sway = MathF.Sin(_t * 0.38f) * w * 0.022f;
        var mountCx = w * 0.5f;
        var eyeCx = mountCx + sway;
        var eyeCy = h * 0.52f;
        var eyeR = MathF.Min(w, h) * 0.155f;
        var stare = _gazeState == GazeState.Staring;

        var gCx = (int) MathF.Round(eyeCx);
        var gCy = (int) MathF.Round(eyeCy);
        var gMx = (int) MathF.Floor(mountCx);

        var housePx = (int) MathF.Ceiling(eyeR * 1.32f);
        var bezPx = (int) MathF.Ceiling(eyeR * 1.10f);
        var irisPx = (int) MathF.Floor(eyeR * 0.78f);
        var pupilPx = (int) MathF.Floor(eyeR * 0.30f);
        var holePx = Math.Max(1, (int) MathF.Floor(eyeR * 0.16f));
        var hRx = housePx + 5;
        var hRy = (int) MathF.Floor(housePx * 0.84f) + 4;

        var pGx = (int) MathF.Round(_pupilX);
        var pGy = (int) MathF.Round(_pupilY);

        var swayTotal = eyeCx - mountCx;
        int Sw(float f) => (int) MathF.Round(swayTotal * f);

        var j0 = (X: gMx + Sw(0.12f), Y: (int) MathF.Floor(h * 0.05f));
        var j1 = (X: gMx + Sw(0.35f), Y: (int) MathF.Floor(h * 0.175f));
        var j2 = (X: gMx + Sw(0.60f), Y: (int) MathF.Floor(h * 0.295f));
        var j3 = (X: gMx + Sw(0.80f), Y: (int) MathF.Floor(h * 0.385f));
        var j4 = (X: gCx, Y: (int) MathF.Floor(h * 0.445f));

        var mountHw = (int) MathF.Floor(eyeR * 1.25f);

        // Soft bloom around eye — smooth falloff, no hard disk
        var glowR = MathF.Min(w, h) * 0.58f;
        const int glowStep = 2;
        for (var y = 0; y < h; y += glowStep)
        for (var x = 0; x < w; x += glowStep)
        {
            var dx = x - eyeCx;
            var dy = y - eyeCy;
            var dist = MathF.Sqrt(dx * dx + dy * dy) / glowR;
            if (dist >= 1f)
                continue;

            var fall = 1f - dist;
            fall = fall * fall * (3f - 2f * fall); // smoothstep
            var core = stare ? 0.38f : 0.22f;
            var a = core * fall * fall;
            if (a <= 0.008f)
                continue;

            var r = (byte) (40 + (int) (170 * fall));
            var g = (byte) (2 + (int) (6 * fall));
            var b = (byte) (4 + (int) (8 * fall));

            for (var oy = 0; oy < glowStep; oy++)
            for (var ox = 0; ox < glowStep; ox++)
                Blend(x + ox, y + oy, r, g, b, a);
        }

        // 9 thick power cables
        var aL2 = (int) (w * 0.14f);
        var aL1 = (int) (w * 0.32f);
        var aR1 = (int) (w * 0.58f);
        var aR2 = (int) (w * 0.80f);
        (int x0, int x1, int droop, int thick)[] cables =
        {
            (aL2, gCx - 6, 55, 3),
            (aL1, gCx - 3, 38, 3),
            (aR1, gCx + 3, 38, 3),
            (aR2, gCx + 6, 55, 3),
            (gMx - 22, gCx - 10, 32, 2),
            (gMx - 10, gCx - 4, 22, 3),
            (gMx, gCx, 14, 4),
            (gMx + 10, gCx + 4, 22, 3),
            (gMx + 22, gCx + 10, 32, 2),
        };
        var headGy = gCy;

        (int gx, int gy) CablePt((int x0, int x1, int droop, int thick) c, float f)
        {
            return (
                (int) MathF.Round(c.x0 + (c.x1 - c.x0) * f),
                (int) MathF.Round(headGy * f + c.droop * 4f * f * (1f - f))
            );
        }

        foreach (var c in cables)
        {
            var bracketW = Math.Max(2, c.thick + 1);
            for (var dy = 0; dy <= 3; dy++)
            for (var gx = c.x0 - bracketW; gx <= c.x0 + bracketW; gx++)
            {
                var isEdge = Math.Abs(gx - c.x0) >= bracketW;
                byte sh;
                if (dy == 0)
                    sh = 42;
                else if (isEdge)
                    sh = 32;
                else
                    sh = 23;

                var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                Set(gx, dy, new Rgba32(sh, sg, sg, 255));
            }
        }

        const int steps = 130;
        foreach (var c in cables)
        {
            for (var s = 0; s <= steps; s++)
            {
                var f = s / (float) steps;
                var pt = CablePt(c, f);
                for (var tx = 0; tx < c.thick; tx++)
                    Set(pt.gx + tx, pt.gy, new Rgba32(22, 6, 6, 255));

                Blend(pt.gx, pt.gy, 38, 14, 14, 0.7f);
                Blend(pt.gx + c.thick - 1, pt.gy, 0, 0, 0, 0.4f);
            }
        }

        const int trail = 12;
        for (var ci = 0; ci < cables.Length; ci++)
        {
            var c = cables[ci];
            var pulseT = _cablePulseTs[ci];
            for (var tr = trail; tr >= 0; tr--)
            {
                var ft = MathF.Max(0f, pulseT - tr * 0.016f);
                if (ft <= 0.001f)
                    continue;

                var pt = CablePt(c, ft);
                var frac = 1f - tr / (float) trail;
                if (tr == 0)
                {
                    for (var tx = 0; tx < c.thick; tx++)
                        Blend(pt.gx + tx, pt.gy, 255, 80, 0, 0.95f);

                    for (var ox = -1; ox <= c.thick; ox++)
                    for (var oy = -1; oy <= 1; oy++)
                        Blend(pt.gx + ox, pt.gy + oy, 255, 50, 0, 0.4f);

                    for (var ox = -2; ox <= c.thick + 1; ox++)
                    for (var oy = -2; oy <= 2; oy++)
                        Blend(pt.gx + ox, pt.gy + oy, 200, 30, 0, 0.18f);
                }
                else
                {
                    var pr = (byte) Math.Clamp(80 + frac * 175, 0, 255);
                    var pg = (byte) Math.Clamp(frac * 40, 0, 255);
                    for (var tx = 0; tx < c.thick; tx++)
                        Blend(pt.gx + tx, pt.gy, pr, pg, 0, frac * 0.75f);
                }
            }
        }

        // Ceiling mount plate
        const int plateH = 9;
        for (var gy = 0; gy <= plateH; gy++)
        {
            var hw = gy == 0 ? mountHw : gy <= 2 ? mountHw : Math.Max(1, (int) (mountHw * 0.52f));
            for (var gx = gMx - hw; gx <= gMx + hw; gx++)
            {
                var isEdge = Math.Abs(gx - gMx) >= hw - 1 || gy == 0 || gy == plateH;
                byte sh;
                if (isEdge)
                    sh = 34;
                else if (gy % 3 == 1)
                    sh = 28;
                else
                    sh = 23;

                var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                Set(gx, gy, new Rgba32(sh, sg, sg, 255));
            }
        }

        for (var bi = -2; bi <= 2; bi += 2)
            Set(gMx + bi, 4, new Rgba32(42, 20, 20, 255));

        int SpineCx(int gy)
        {
            if (gy <= j1.Y)
            {
                var f = j1.Y > j0.Y ? (gy - j0.Y) / (float) (j1.Y - j0.Y) : 0f;
                return (int) MathF.Round(j0.X + (j1.X - j0.X) * f);
            }

            if (gy <= j2.Y)
            {
                var f = j2.Y > j1.Y ? (gy - j1.Y) / (float) (j2.Y - j1.Y) : 0f;
                return (int) MathF.Round(j1.X + (j2.X - j1.X) * f);
            }

            var f2 = j3.Y > j2.Y ? (gy - j2.Y) / (float) (j3.Y - j2.Y) : 0f;
            return (int) MathF.Round(j2.X + (j3.X - j2.X) * f2);
        }

        void DrawRingClamp(int jx, int jy, int hw)
        {
            var cH = Math.Max(5, (int) MathF.Floor(eyeR * 0.15f));
            for (var dy = 0; dy < cH; dy++)
            {
                var litFrac = 1f - dy / (float) cH;
                for (var gx = jx - hw; gx <= jx + hw; gx++)
                {
                    var xf = hw > 0 ? Math.Abs(gx - jx) / (float) hw : 0f;
                    var isEdge = xf >= 0.92f;
                    var sh = dy == 0 ? 36 : isEdge ? 22 : (int) MathF.Floor(16 + litFrac * 14);
                    var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                    Set(gx, jy + dy, new Rgba32((byte) sh, sg, sg, 255));
                }
            }

            var by2 = jy + cH / 2;
            var nb = Math.Max(4, (int) MathF.Floor(hw * 1.2f));
            for (var bi = 0; bi <= nb; bi++)
            {
                var bx = jx - hw + (int) MathF.Round(bi * (hw * 2f) / nb);
                Set(bx, by2, new Rgba32(60, 28, 28, 255));
            }
        }

        // Central spine tube + helical cables + clamps
        var bodyTopY = j0.Y;
        var bodyBotY = j3.Y;
        var bodyLen = Math.Max(1, bodyBotY - bodyTopY);
        var tubeHw = (int) MathF.Floor(eyeR * 0.16f);
        var cableOrbit = tubeHw + (int) MathF.Floor(eyeR * 0.22f);
        const int numHelix = 3;
        var wrapFreq = MathF.PI * 2f * 5f / bodyLen;
        var clampHw1 = cableOrbit + (int) MathF.Floor(eyeR * 0.12f);
        var clampHw2 = cableOrbit + (int) MathF.Floor(eyeR * 0.08f);

        for (var gy = bodyTopY; gy <= bodyBotY; gy++)
        {
            var tcx = SpineCx(gy);

            for (var ci = 0; ci < numHelix; ci++)
            {
                var phase = ci / (float) numHelix * MathF.PI * 2f;
                var angle = gy * wrapFreq + phase;
                var cosV = MathF.Cos(angle);
                if (cosV >= -0.05f)
                    continue;

                var sinV = MathF.Sin(angle);
                var cx2 = (int) MathF.Round(tcx + sinV * cableOrbit);
                var sh = (int) MathF.Floor(10 + MathF.Abs(cosV) * 5);
                var sg = (byte) Math.Clamp((int) (sh * 0.22f), 0, 255);
                Set(cx2, gy, new Rgba32((byte) sh, sg, sg, 255));
                if (cx2 + 1 < w)
                    Set(cx2 + 1, gy, new Rgba32((byte) sh, sg, sg, 255));
            }

            for (var gx = tcx - tubeHw; gx <= tcx + tubeHw; gx++)
            {
                var xf = tubeHw > 0 ? Math.Abs(gx - tcx) / (float) tubeHw : 0f;
                var isEdge = xf >= 0.82f;
                var sh = isEdge ? 28 : (int) MathF.Floor(18 - xf * 5);
                var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                Set(gx, gy, new Rgba32((byte) sh, sg, sg, 255));
            }

            for (var ci = 0; ci < numHelix; ci++)
            {
                var phase = ci / (float) numHelix * MathF.PI * 2f;
                var angle = gy * wrapFreq + phase;
                var cosV = MathF.Cos(angle);
                if (cosV <= 0.05f)
                    continue;

                var sinV = MathF.Sin(angle);
                var cx2 = (int) MathF.Round(tcx + sinV * cableOrbit);
                var twist = (gy + ci * 5) % 4 == 0;
                var bright = 0.38f + 0.62f * cosV;
                var sh = (int) MathF.Floor((18 + bright * 18) * (twist ? 0.78f : 1f));
                var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                Set(cx2, gy, new Rgba32((byte) sh, sg, sg, 255));
                if (cx2 + 1 < w)
                    Set(cx2 + 1, gy, new Rgba32((byte) sh, sg, sg, 255));
            }
        }

        var clampOffY = (int) MathF.Floor(eyeR * 0.07f);
        DrawRingClamp(j1.X, j1.Y - clampOffY, clampHw1);
        DrawRingClamp(j2.X, j2.Y - clampOffY, clampHw2);

        // Neck + massive head-mount collar
        var collarTopY = j3.Y;
        var collarBotY = j4.Y + (int) MathF.Floor(eyeR * 0.20f);
        var colSpanY = collarBotY - collarTopY;
        var s3BottomHw = (int) MathF.Floor(eyeR * 0.30f);
        var collarHwTop = s3BottomHw + (int) MathF.Floor(eyeR * 0.22f);
        var collarHwBot = (int) MathF.Floor(eyeR * 0.82f);

        for (var gy = collarTopY; gy <= collarBotY; gy++)
        {
            var f = colSpanY == 0 ? 0f : (gy - collarTopY) / (float) colSpanY;
            var fEase = f * f * (3f - 2f * f);
            var hw = (int) MathF.Round(collarHwTop + (collarHwBot - collarHwTop) * fEase);
            var ccx = (int) MathF.Round(j3.X + (j4.X - j3.X) * f);

            for (var gx = ccx - hw; gx <= ccx + hw; gx++)
            {
                var xFrac = hw > 0 ? Math.Abs(gx - ccx) / (float) hw : 0f;
                var isEdge = xFrac >= 0.93f;
                var isBevel = xFrac >= 0.76f && xFrac < 0.93f;
                var isHLine = (gy - collarTopY) % 5 == 0;
                var isTopBot = gy == collarTopY || gy == collarBotY;

                byte sh;
                byte sg;
                if (isTopBot || isEdge)
                {
                    sh = 42;
                    sg = 18;
                }
                else if (isBevel)
                {
                    sh = (byte) Math.Clamp((int) MathF.Floor(24 + (1f - xFrac) * 10), 0, 255);
                    sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                }
                else if (isHLine)
                {
                    sh = 32;
                    sg = 14;
                }
                else
                {
                    sh = (byte) Math.Clamp(15 + (int) MathF.Floor(f * 5), 0, 255);
                    sg = (byte) Math.Clamp((int) (sh * 0.26f), 0, 255);
                }

                Set(gx, gy, new Rgba32(sh, sg, sg, 255));
            }
        }

        var capBotY = collarTopY + colSpanY * 26 / 100;
        for (var gy = collarTopY; gy <= capBotY; gy++)
        {
            var f = colSpanY == 0 ? 0f : (gy - collarTopY) / (float) colSpanY;
            var fE = f * f * (3f - 2f * f);
            var hw = (int) MathF.Round(collarHwTop + (collarHwBot - collarHwTop) * fE);
            var ccx = (int) MathF.Round(j3.X + (j4.X - j3.X) * f);
            for (var gx = ccx - hw + 1; gx <= ccx + hw - 1; gx++)
            {
                var xFrac = hw > 1 ? Math.Abs(gx - ccx) / (float) (hw - 1) : 0f;
                if (xFrac >= 0.78f)
                    continue;

                var sh = (byte) Math.Clamp(20 + (int) MathF.Floor((1f - xFrac) * 8), 0, 255);
                var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                Set(gx, gy, new Rgba32(sh, sg, sg, 255));
            }
        }

        const float boltFrac = 0.52f;
        var boltFE = boltFrac * boltFrac * (3f - 2f * boltFrac);
        var boltHw = (int) MathF.Round(collarHwTop + (collarHwBot - collarHwTop) * boltFE);
        var boltY = collarTopY + colSpanY * 52 / 100;
        var boltCx = (int) MathF.Round(j3.X + (j4.X - j3.X) * boltFrac);
        const int numBolts = 10;
        for (var gx = boltCx - boltHw; gx < boltCx + boltHw; gx++)
        {
            Blend(gx, boltY - 1, 44, 18, 18, 0.85f);
            Blend(gx, boltY + 2, 44, 18, 18, 0.85f);
        }

        for (var bi = 0; bi < numBolts; bi++)
        {
            var bxOff = (int) MathF.Round((bi / (float) (numBolts - 1) - 0.5f) * 2 * boltHw * 0.85f);
            Set(boltCx + bxOff, boltY, new Rgba32(58, 26, 26, 255));
            Set(boltCx + bxOff, boltY + 1, new Rgba32(58, 26, 26, 255));
        }

        const float earFrac = 0.18f;
        var earFE = earFrac * earFrac * (3f - 2f * earFrac);
        var earHw = (int) MathF.Round(collarHwTop + (collarHwBot - collarHwTop) * earFE);
        var earY = collarTopY + colSpanY * 18 / 100;
        var earCx = (int) MathF.Round(j3.X + (j4.X - j3.X) * earFrac);
        var earProj = (int) MathF.Floor(eyeR * 0.28f);
        var earH = (int) MathF.Floor(eyeR * 0.18f);
        for (var side = -1; side <= 1; side += 2)
        {
            var earBaseX = earCx + side * earHw;
            for (var dx = 0; dx < earProj; dx++)
            {
                var decayHw = earH - dx * earH / Math.Max(1, earProj);
                for (var dy = -decayHw; dy <= decayHw; dy++)
                {
                    var isEdge2 = Math.Abs(dy) >= decayHw || dx == earProj - 1;
                    var sh = isEdge2 ? 22 : 18 + (int) MathF.Floor((1f - dx / (float) earProj) * 6);
                    var sg = (byte) Math.Clamp((int) (sh * 0.28f), 0, 255);
                    Set(earBaseX + side * dx, earY + dy, new Rgba32((byte) sh, sg, sg, 255));
                }
            }

            Set(earBaseX + side * (earProj / 2), earY, new Rgba32(58, 24, 24, 255));
        }

        var flangeY = collarBotY - 3;
        var flangeCx = j4.X;
        for (var dy = 0; dy <= 4; dy++)
        {
            var fhw = dy == 0 || dy == 4 ? collarHwBot - 1 : collarHwBot + 1;
            for (var gx = flangeCx - fhw; gx <= flangeCx + fhw; gx++)
            {
                var sh = dy == 1 ? (byte) 32 : dy == 2 ? (byte) 22 : (byte) 16;
                var sg = (byte) Math.Clamp((int) (sh * 0.3f), 0, 255);
                Set(gx, flangeY + dy, new Rgba32(sh, sg, sg, 255));
            }
        }

        // Head oval + eye module
        var scanX = hRx + 2;
        var scanY = hRy + 2;

        for (var ddx = -scanX; ddx <= scanX; ddx++)
        for (var ddy = -scanY; ddy <= scanY; ddy++)
        {
            var ellD = ddx * ddx / (float) (hRx * hRx) + ddy * ddy / (float) (hRy * hRy);
            if (ellD > 1f)
                continue;

            var px = gCx + ddx;
            var py = gCy + ddy;
            var dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            var ang = MathF.Atan2(ddy, ddx);
            var pdx = ddx - pGx;
            var pdy = ddy - pGy;
            var pd = MathF.Sqrt(pdx * pdx + pdy * pdy);

            if (dist <= housePx + 0.5f)
            {
                if (dist <= irisPx)
                {
                    if (pd <= holePx)
                    {
                        Set(px, py, new Rgba32(6, 1, 3, 255));
                        continue;
                    }

                    if (pd <= pupilPx)
                    {
                        var ringFrac = (pd - holePx) / (float) (pupilPx - holePx);
                        var isOuter = ringFrac > 0.72f;
                        var isInner = ringFrac < 0.28f;
                        var isNotch = MathF.Min(MathF.Abs(pdx), MathF.Abs(pdy)) == 0
                            && pd > holePx + 1 && pd < pupilPx - 1;
                        if (!isNotch)
                        {
                            byte pr;
                            byte pg;
                            byte pb;
                            if (isOuter)
                            {
                                pr = stare ? (byte) 255 : (byte) 204;
                                pg = stare ? (byte) 68 : (byte) 32;
                                pb = stare ? (byte) 68 : (byte) 32;
                            }
                            else if (isInner)
                            {
                                pr = stare ? (byte) 153 : (byte) 102;
                                pg = 32;
                                pb = 32;
                            }
                            else
                            {
                                pr = stare ? (byte) 187 : (byte) 136;
                                pg = 34;
                                pb = 34;
                            }

                            Set(px, py, new Rgba32(pr, pg, pb, 255));
                        }

                        continue;
                    }

                    var irisDepth = 1f - dist / irisPx;
                    var ringTier = (int) (dist * 3.5f) % 3;
                    var rb = ringTier == 0 ? 1.08f : ringTier == 1 ? 0.88f : 1f;
                    var boost = stare ? 1.65f : 1.2f;
                    var limbal = dist > irisPx - 2 ? 0.65f : 1f;
                    var rv = (byte) Math.Clamp((165 + irisDepth * 100) * rb * boost * limbal, 0, 255);
                    Set(px, py, new Rgba32(rv, (byte) (6 * irisDepth), (byte) (5 * irisDepth), 255));
                    continue;
                }

                if (dist <= bezPx)
                {
                    var t2 = (dist - irisPx) / (float) (bezPx - irisPx);
                    var sh = (byte) Math.Clamp(16 - t2 * 8 + (stare ? 6 : 0), 0, 255);
                    Set(px, py, new Rgba32(sh, 3, 3, 255));
                    continue;
                }

                var depth = (dist - bezPx) / (housePx - bezPx);
                const int segN = 18;
                var segFrac = ((ang + MathF.PI) / (MathF.PI * 2f)) * segN;
                var isGap = segFrac % 1f < 0.035f;
                var isBolt = false;
                for (var bi = 0; bi < 8; bi++)
                {
                    var ba = bi / 8f * MathF.PI * 2f;
                    var diff = MathF.Abs(((ang - ba + MathF.PI * 3f) % (MathF.PI * 2f)) - MathF.PI);
                    if (diff < 0.10f && MathF.Abs(dist - (bezPx + (housePx - bezPx) * 0.42f)) < 1.6f)
                    {
                        isBolt = true;
                        break;
                    }
                }

                var isRim = depth > 0.84f;
                var flicker = 0.45f + 0.42f * MathF.Sin(_t * 2.1f + ang * 5.5f);
                var isVent = depth > 0.60f && segFrac % 1f > 0.46f && segFrac % 1f < 0.49f;
                var pIdx = (int) segFrac;

                if (isGap || isVent)
                {
                    Set(px, py, new Rgba32(5, 1, 2, 255));
                }
                else if (isBolt)
                {
                    Set(px, py, new Rgba32(48, 18, 18, 255));
                }
                else if (isRim)
                {
                    var ga = (0.32f + 0.38f * flicker) * (stare ? 1.8f : 1f);
                    Blend(px, py, 210, 14, 14, MathF.Min(1f, ga));
                }
                else
                {
                    var baseSh = pIdx % 2 == 0 ? 24 : 17;
                    var shade = (byte) Math.Clamp(baseSh + (int) MathF.Floor(depth * 10), 0, 255);
                    var sg = (byte) Math.Clamp((int) (shade * 0.28f), 0, 255);
                    Set(px, py, new Rgba32(shade, sg, sg, 255));
                }

                continue;
            }

            var isOuterShell = ellD > 0.86f;
            var rowOff = ((gCy + ddy) % 5 + 5) % 5;
            if (isOuterShell)
            {
                Set(px, py, new Rgba32(34, 14, 14, 255));
            }
            else
            {
                var baseSh = rowOff == 0 ? (byte) 17 : (byte) 12;
                var sg = (byte) Math.Clamp((int) (baseSh * 0.28f), 0, 255);
                Set(px, py, new Rgba32(baseSh, sg, sg, 255));
            }
        }

        for (var pl = -2; pl <= 2; pl++)
        {
            var py = gCy + (int) MathF.Round(pl * hRy * 0.28f);
            var fy = pl / (hRy * 0.28f);
            var hw = (int) MathF.Floor(hRx * MathF.Sqrt(MathF.Max(0f, 1f - fy * fy * 0.28f)));
            if (hw > housePx)
            {
                Set(gCx - hw, py, new Rgba32(30, 10, 10, 255));
                Set(gCx + hw - 1, py, new Rgba32(30, 10, 10, 255));
            }
        }

        var hlY = gCy + (int) MathF.Floor(hRy * 0.55f);
        var hlX = (int) MathF.Floor(housePx * 0.78f);
        for (var si = -1; si <= 1; si += 2)
        {
            var hlOn = MathF.Sin(_t * 2.4f + si * 1.5f) > 0.2f;
            if (hlOn)
            {
                Set(gCx + si * hlX, hlY, new Rgba32(255, 34, 34, 255));
                for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                    Blend(gCx + si * hlX + ox, hlY + oy, 255, 30, 30, 0.25f);
            }
            else
            {
                Set(gCx + si * hlX, hlY, new Rgba32(26, 5, 5, 255));
            }
        }

        // Stare flash
        if (stare && _gazeTimer < 0.3f)
        {
            var fa = (0.3f - _gazeTimer) / 0.3f * 0.45f;
            for (var i = 0; i < _writeBuffer.Length; i++)
            {
                ref var px = ref _writeBuffer[i];
                px.R = (byte) Math.Clamp(px.R * (1f - fa) + 210 * fa, 0, 255);
                px.G = (byte) Math.Clamp(px.G * (1f - fa) + 15 * fa, 0, 255);
                px.B = (byte) Math.Clamp(px.B * (1f - fa) + 15 * fa, 0, 255);
            }
        }
    }

    #endregion

    #region Black hole

    private void RenderBlackHole(Rgba32[] target)
    {
        _writeBuffer = target;
        var w = _bufferSize.X;
        var h = _bufferSize.Y;
        Array.Fill(_writeBuffer, new Rgba32(2, 3, 10, 255));

        var cx = w * 0.32f;
        var cy = h * 0.5f;

        DrawNebula(cx, cy, h);
        DrawStars(w, h);

        if (_frameCount % 36 == 0 && CountActiveFlybys() < MaxFlybys)
            SpawnFlyby(forceBig: false);

        if (_frameCount - _lastBigSpawn > 480 + (int) (RandomFloat() * 240))
        {
            if (RandomFloat() < 0.02f)
            {
                SpawnFlyby(forceBig: true);
                _lastBigSpawn = _frameCount;
            }
        }

        UpdateFlybys();
        foreach (var d in _flybys)
        {
            if (!d.Active || d.Layer != FlybyLayer.Background)
                continue;

            DrawSmallDebris(
                (int) (d.X * w),
                (int) (d.Y * h),
                Math.Max(2, d.Size),
                d.Seed,
                d.Alpha * 0.75f,
                lit: true);
        }

        var tilt = 0.42f;
        var diskR = h * 0.2f;
        var twist = 0.26f;
        var debrisRingR = diskR * 1.55f;
        const int rings = 28;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < _ring.Length; i++)
            {
                ref var d = ref _ring[i];
                if (pass == 0)
                    d.OrbitAngle += d.OrbitSpeed * 0.016f * SimScale;

                var angle = d.OrbitAngle;
                var r = debrisRingR * d.OrbitR;
                var dx3 = MathF.Cos(angle) * r;
                var dz3 = MathF.Sin(angle) * r;
                var screenY = -dz3 * MathF.Sin(tilt);
                var finalX = dx3 * MathF.Cos(twist) - screenY * MathF.Sin(twist);
                var finalY = dx3 * MathF.Sin(twist) + screenY * MathF.Cos(twist);
                var isBehind = dz3 > 0f;

                if (pass == 0 && !isBehind)
                    continue;
                if (pass == 1 && isBehind)
                    continue;

                var px = (int) (cx + finalX);
                var py = (int) (cy + finalY);
                var glowAlpha = isBehind ? 0.55f : 0.85f;
                DrawSmallDebris(px, py, d.Size, d.Seed, glowAlpha, lit: !isBehind);

                if (!isBehind && d.Size >= 2)
                {
                    var glint = 0.4f + 0.4f * MathF.Sin(_t * 2f + d.Seed);
                    Blend(px - 1, py - 1, 255, 200, 60, glint * 0.6f);
                }
            }

            if (pass != 0)
                continue;

            for (var ring = rings; ring >= 0; ring--)
            {
                var frac = ring / (float) rings;
                var r = diskR * (0.28f + frac * 0.72f);
                var segments = Math.Max(28, (int) (r * 2.5f));
                var heat = 1f - frac;

                for (var seg = 0; seg < segments; seg++)
                {
                    var angle = seg / (float) segments * MathF.PI * 2f + _t * (0.12f + frac * 0.25f);
                    var dx3 = MathF.Cos(angle) * r;
                    var dz3 = MathF.Sin(angle) * r;
                    var screenY = -dz3 * MathF.Sin(tilt);
                    var finalX = dx3 * MathF.Cos(twist) - screenY * MathF.Sin(twist);
                    var finalY = dx3 * MathF.Sin(twist) + screenY * MathF.Cos(twist);
                    var flicker = 0.82f + 0.18f * MathF.Sin(_t * 3.5f + seg * 0.6f + ring * 0.4f);

                    byte rr;
                    byte gg;
                    byte bb;
                    float aa;
                    if (heat > 0.78f)
                    {
                        rr = 255;
                        gg = (byte) Math.Clamp((int) (200 + 55 * heat), 0, 255);
                        bb = (byte) Math.Clamp((int) (60 + 80 * heat), 0, 255);
                        aa = 0.95f * flicker;
                    }
                    else if (heat > 0.5f)
                    {
                        rr = 255;
                        gg = (byte) Math.Clamp((int) (160 + 60 * heat), 0, 255);
                        bb = 20;
                        aa = 0.85f * flicker;
                    }
                    else if (heat > 0.25f)
                    {
                        rr = 220;
                        gg = (byte) Math.Clamp((int) (100 + 60 * heat), 0, 255);
                        bb = 10;
                        aa = 0.65f * flicker;
                    }
                    else
                    {
                        rr = (byte) Math.Clamp((int) (120 + 60 * frac), 0, 255);
                        gg = (byte) Math.Clamp((int) (40 + 30 * frac), 0, 255);
                        bb = 5;
                        aa = 0.35f * flicker;
                    }

                    Blend((int) (cx + finalX), (int) (cy + finalY), rr, gg, bb, aa);
                }
            }

            var bhR = h * 0.105f;
            var bhRx = Math.Max(1, (int) bhR);
            var bhRy = Math.Max(1, (int) (bhR * MathF.Abs(MathF.Cos(tilt)) * 0.85f));

            for (var dx = -bhRx - 2; dx <= bhRx + 2; dx++)
            for (var dy = -bhRy - 2; dy <= bhRy + 2; dy++)
            {
                var ex = dx / (bhRx + 2f);
                var ey = dy / (bhRy + 2f);
                var d2 = ex * ex + ey * ey;
                if (d2 > 0.85f && d2 <= 1.15f)
                    Blend((int) cx + dx, (int) cy + dy, 255, 180, 30, (1.15f - d2) / 0.3f * 0.7f);
            }

            for (var dx = -bhRx; dx <= bhRx; dx++)
            for (var dy = -bhRy; dy <= bhRy; dy++)
            {
                var ex = dx / (float) bhRx;
                var ey = dy / (float) bhRy;
                if (ex * ex + ey * ey <= 1f)
                    Set((int) cx + dx, (int) cy + dy, new Rgba32(0, 0, 0, 255));
            }

            var lensR = bhR * 1.14f;
            for (var i = 0; i < 120; i++)
            {
                var ang = i / 120f * MathF.PI * 2f;
                var ldx = MathF.Cos(ang) * lensR;
                var ldz = MathF.Sin(ang) * lensR;
                var lsx = ldx * MathF.Cos(twist) + -ldz * MathF.Sin(tilt) * -MathF.Sin(twist);
                var lsy = ldx * MathF.Sin(twist) + -ldz * MathF.Sin(tilt) * MathF.Cos(twist);
                var glow = 0.55f + 0.45f * MathF.Sin(ang * 4f + _t * 2.5f);
                var facing = MathF.Sin(ang) < 0 ? 1f : 0.5f;
                Blend((int) (cx + lsx), (int) (cy + lsy), 255, 210, 60, glow * facing);
            }

            for (var j = 0; j < 18; j++)
            {
                var jt = (_t * 0.35f + j / 18f) % 1f;
                var jLen = bhR * (1.6f + jt * 3.5f);
                var jitterX = MathF.Sin(j * 2.3f + _t) * 1.5f;
                var jdx = MathF.Sin(tilt) * jLen * 0.3f;
                Blend((int) (cx + jitterX + jdx), (int) (cy - jLen), 200, 140, 20, (1f - jt) * 0.5f);
                Blend((int) (cx + jitterX - jdx), (int) (cy + jLen), 200, 140, 20, (1f - jt) * 0.5f);
            }

            var bloomR = (int) (bhR * 2.2f);
            for (var y = Math.Max(0, (int) cy - bloomR); y < Math.Min(h, (int) cy + bloomR); y += 2)
            for (var x = Math.Max(0, (int) cx - bloomR); x < Math.Min(w, (int) cx + bloomR); x += 2)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < bhR * 0.8f || dist > bloomR)
                    continue;

                var tt = (dist - bhR * 0.8f) / (bloomR - bhR * 0.8f);
                var a = tt < 0.5f ? 0.07f * (1f - tt * 2f) : 0.03f * (1f - (tt - 0.5f) * 2f);
                Blend(x, y, 255, 160, 20, a);
            }
        }

        foreach (var d in _flybys)
        {
            if (!d.Active || d.Layer != FlybyLayer.Foreground)
                continue;

            var dpx = (int) (d.X * w);
            var dpy = (int) (d.Y * h);

            if (d.IsBig && d.Poly is { Length: > 0 })
            {
                DrawLargeDebris(dpx, dpy, d.HalfPx, d.Seed, d.Alpha, d.Poly);
                DrawDebrisHalo(dpx, dpy, d.HalfPx, d.Alpha);
            }
            else
            {
                DrawSmallDebris(dpx, dpy, Math.Max(2, d.Size), d.Seed, d.Alpha, lit: true);
            }
        }

        for (var i = _flybys.Count - 1; i >= 0; i--)
        {
            if (!_flybys[i].Active)
                _flybys.RemoveAt(i);
        }
    }

    private void DrawNebula(float cx, float cy, int h)
    {
        var maxR = h * 0.6f;
        const int step = 3;
        for (var y = 0; y < _bufferSize.Y; y += step)
        for (var x = 0; x < _bufferSize.X; x += step)
        {
            var dx = x - cx;
            var dy = y - cy;
            var dist = MathF.Sqrt(dx * dx + dy * dy) / maxR;
            if (dist > 1f)
                continue;

            float a;
            byte r;
            byte g;
            byte b;
            if (dist < 0.25f)
            {
                a = dist / 0.25f * 0.14f;
                r = 120;
                g = 80;
                b = 10;
            }
            else if (dist < 0.5f)
            {
                a = 0.14f + (dist - 0.25f) / 0.25f * (0.1f - 0.14f);
                r = 60;
                g = 30;
                b = 5;
            }
            else
            {
                a = 0.1f * (1f - (dist - 0.5f) / 0.5f);
                r = 40;
                g = 20;
                b = 5;
            }

            if (a <= 0.01f)
                continue;

            for (var oy = 0; oy < step; oy++)
            for (var ox = 0; ox < step; ox++)
                Blend(x + ox, y + oy, r, g, b, a);
        }
    }

    private void DrawStars(int w, int h)
    {
        foreach (var (sx, sy, b, tw) in _stars)
        {
            var x = (int) (sx * w);
            var y = (int) (sy * h);
            var twink = 0.5f + 0.5f * MathF.Sin(_t * 0.8f + tw);
            var a = b * 0.7f * twink + 0.1f;
            Blend(x, y, 220, 210, 180, a);
            if (b > 0.82f)
                Blend(x + 1, y, 220, 210, 180, a * 0.7f);
        }
    }

    private void UpdateFlybys()
    {
        foreach (var d in _flybys)
        {
            if (!d.Active)
                continue;

            d.X += d.Vx * SimScale;
            d.Y += d.Vy * SimScale;
            d.Alpha = Math.Min(1f, d.Alpha + 0.04f * SimScale);

            if (d.X < -0.2f || d.X > 1.2f || d.Y < -0.2f || d.Y > 1.2f)
                d.Active = false;
        }
    }

    private int CountActiveFlybys()
    {
        var n = 0;
        foreach (var d in _flybys)
        {
            if (d.Active)
                n++;
        }

        return n;
    }

    private void SpawnFlyby(bool forceBig)
    {
        var edge = (int) (RandomFloat() * 4f);
        float sx;
        float sy;
        var speed = forceBig
            ? 0.0006f + RandomFloat() * 0.0005f
            : 0.003f + RandomFloat() * 0.005f;
        var targetX = 0.2f + RandomFloat() * 0.7f;
        var targetY = 0.2f + RandomFloat() * 0.6f;

        if (edge == 0) { sx = RandomFloat(); sy = -0.12f; }
        else if (edge == 1) { sx = 1.12f; sy = RandomFloat(); }
        else if (edge == 2) { sx = RandomFloat(); sy = 1.12f; }
        else { sx = -0.12f; sy = RandomFloat(); }

        var dx = targetX - sx;
        var dy = targetY - sy;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f)
            len = 0.001f;

        var seed = (int) (RandomFloat() * 99999f);
        int size;
        int halfPx;
        Vector2[]? poly = null;

        if (forceBig)
        {
            halfPx = Math.Min(MaxBigHalfPx, 20 + (int) (RandomFloat() * 22f));
            size = halfPx * 2;
            poly = BuildDebrisPolygon(seed, halfPx);
        }
        else
        {
            size = 2 + (int) (RandomFloat() * 4f);
            halfPx = Math.Max(1, size / 2);
        }

        _flybys.Add(new FlybyDebris
        {
            X = sx,
            Y = sy,
            Vx = dx / len * speed,
            Vy = dy / len * speed,
            Size = size,
            HalfPx = halfPx,
            Seed = seed,
            Alpha = 0f,
            Layer = forceBig ? FlybyLayer.Foreground : RandomFloat() > 0.4f ? FlybyLayer.Foreground : FlybyLayer.Background,
            Active = true,
            IsBig = forceBig,
            Poly = poly
        });
    }

    private void DrawSmallDebris(int px, int py, int size, int seed, float alpha, bool lit)
    {
        var halfPx = Math.Max(1, size / 2);
        var tint = SeededRand(seed + 88);
        var baseR = 18 + tint * 14f;
        var baseG = 14 + tint * 9f;
        var baseB = 12 + (1f - tint) * 10f;

        for (var ddx = -halfPx; ddx <= halfPx; ddx++)
        for (var ddy = -halfPx; ddy <= halfPx; ddy++)
        {
            var dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            var ang = MathF.Atan2(ddy, ddx);
            var noiseSeed = seed + (int) MathF.Floor(ang * 5f + 1f);
            var r = halfPx * (0.58f + 0.42f * SeededRand(noiseSeed));
            if (dist > r)
                continue;

            var edgeFrac = 1f - dist / halfPx;
            var litFactor = lit ? Math.Max(0f, (-ddx * 0.6f - ddy * 0.4f) / halfPx) : 0f;
            var shade = 0.28f + 0.38f * edgeFrac + 0.34f * litFactor;
            Blend(
                px + ddx,
                py + ddy,
                (byte) Math.Clamp((int) (baseR * shade), 0, 255),
                (byte) Math.Clamp((int) (baseG * shade), 0, 255),
                (byte) Math.Clamp((int) (baseB * shade), 0, 255),
                alpha);
        }
    }

    private void DrawLargeDebris(int px, int py, int halfPx, int seed, float alpha, Vector2[] poly)
    {
        halfPx = Math.Min(halfPx, MaxBigHalfPx);
        var tint = SeededRand(seed + 88);
        var baseR = 10 + tint * 12f;
        var baseG = 8 + tint * 8f;
        var baseB = 7 + (1f - tint) * 7f;

        for (var ddx = -halfPx; ddx <= halfPx; ddx++)
        for (var ddy = -halfPx; ddy <= halfPx; ddy++)
        {
            if (!PointInPoly(ddx, ddy, poly))
                continue;

            var litFactor = Math.Max(0f, (ddx * 0.5f + ddy * 0.3f) / halfPx);
            var detail = SeededRand(seed + ddx * 19 + ddy * 37) > 0.96f;
            var shade = detail ? 0.55f : 0.18f + 0.22f * litFactor;
            var rMul = detail ? 2.5f : 1f;
            Blend(
                px + ddx,
                py + ddy,
                (byte) Math.Clamp((int) (baseR * rMul * shade + (detail ? 8 : 0)), 0, 255),
                (byte) Math.Clamp((int) (baseG * shade), 0, 255),
                (byte) Math.Clamp((int) (baseB * shade), 0, 255),
                alpha);
        }

        for (var ddx = -halfPx - 1; ddx <= halfPx + 1; ddx++)
        for (var ddy = -halfPx - 1; ddy <= halfPx + 1; ddy++)
        {
            if (PointInPoly(ddx, ddy, poly))
                continue;

            var adjacent =
                PointInPoly(ddx - 1, ddy, poly) ||
                PointInPoly(ddx + 1, ddy, poly) ||
                PointInPoly(ddx, ddy - 1, poly) ||
                PointInPoly(ddx, ddy + 1, poly);
            if (!adjacent)
                continue;

            var rimLit = (ddx * 0.55f + ddy * 0.35f) / halfPx;
            if (rimLit < 0.2f)
                continue;

            Blend(px + ddx, py + ddy, 160, 90, 15, alpha * rimLit * 0.45f);
        }
    }

    private void DrawDebrisHalo(int px, int py, int halfPx, float alpha)
    {
        var haloR = halfPx * 1.6f;
        const int step = 2;
        for (var y = (int) -haloR; y <= haloR; y += step)
        for (var x = (int) -haloR; x <= haloR; x += step)
        {
            var dist = MathF.Sqrt(x * x + y * y);
            if (dist > haloR || dist < halfPx * 0.3f)
                continue;

            var t = (dist - halfPx * 0.3f) / (haloR - halfPx * 0.3f);
            var a = t < 0.5f
                ? alpha * 0.55f * (1f - t * 2f)
                : alpha * 0.25f * (1f - (t - 0.5f) * 2f);
            Blend(px + x, py + y, 0, 0, 0, a);
        }
    }

    private static Vector2[] BuildDebrisPolygon(int seed, int halfPx)
    {
        var numVerts = 7 + (int) (SeededRand(seed + 200) * 12f);
        var scaleX = 0.35f + SeededRand(seed + 201) * 0.95f;
        var scaleY = 0.35f + SeededRand(seed + 202) * 0.95f;
        var angles = new float[numVerts];
        var a = 0f;
        for (var v = 0; v < numVerts; v++)
        {
            a += MathF.PI * 2f / numVerts * (0.4f + SeededRand(seed + v * 11 + 203) * 1.2f);
            angles[v] = a;
        }

        var verts = new Vector2[numVerts];
        for (var v = 0; v < numVerts; v++)
        {
            var ang = angles[v];
            var r = halfPx * (0.20f + SeededRand(seed + v * 17 + 204) * 0.80f);
            verts[v] = new Vector2(MathF.Cos(ang) * r * scaleX, MathF.Sin(ang) * r * scaleY);
        }

        return verts;
    }

    private static bool PointInPoly(int px, int py, Vector2[] verts)
    {
        var inside = false;
        for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
        {
            var xi = verts[i].X;
            var yi = verts[i].Y;
            var xj = verts[j].X;
            var yj = verts[j].Y;
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi + 0.0001f) + xi)
                inside = !inside;
        }

        return inside;
    }

    #endregion

    #region Pixel helpers

    private void Set(int x, int y, Rgba32 color)
    {
        if ((uint) x >= (uint) _bufferSize.X || (uint) y >= (uint) _bufferSize.Y)
            return;
        _writeBuffer[y * _bufferSize.X + x] = color;
    }

    private void Blend(int x, int y, byte r, byte g, byte b, float a)
    {
        if (a <= 0f || (uint) x >= (uint) _bufferSize.X || (uint) y >= (uint) _bufferSize.Y)
            return;

        a = Math.Clamp(a, 0f, 1f);
        ref var dst = ref _writeBuffer[y * _bufferSize.X + x];
        var inv = 1f - a;
        dst.R = (byte) (dst.R * inv + r * a);
        dst.G = (byte) (dst.G * inv + g * a);
        dst.B = (byte) (dst.B * inv + b * a);
        dst.A = 255;
    }

    private float RandomFloat()
    {
        _randState = unchecked(_randState * 1103515245 + 12345);
        return ((_randState >> 16) & 0x7FFF) / (float) 0x7FFF;
    }

    private static float SeededRand(int seed)
    {
        var x = MathF.Sin(seed + 1f) * 43758.5453f;
        return x - MathF.Floor(x);
    }

    #endregion

    private enum GazeState : byte
    {
        Scanning,
        Staring
    }

    private enum FlybyLayer : byte
    {
        Background,
        Foreground
    }

    private readonly record struct RackLayer(
        float XFrac,
        float WFrac,
        float YFrac,
        float HFrac,
        float DepthScale,
        bool FacingRight,
        int Seed);

    private struct Blinker
    {
        public int SlotX;
        public int Gy;
        public int OffX;
        public float Phase;
        public float Speed;
        public byte Col;
    }

    private struct RingDebris
    {
        public float OrbitAngle;
        public float OrbitR;
        public float OrbitSpeed;
        public int Size;
        public int Seed;
    }

    private sealed class FlybyDebris
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Size;
        public int HalfPx;
        public int Seed;
        public float Alpha;
        public FlybyLayer Layer;
        public bool Active;
        public bool IsBig;
        public Vector2[]? Poly;
    }
}

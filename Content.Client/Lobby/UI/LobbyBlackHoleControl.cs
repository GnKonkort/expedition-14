using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Timing;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Pixel black-hole background ported from the Figma Make mock:
/// accretion disk, debris ring, small flybys, and rare large foreground rocks.
/// Renders into a capped low-res buffer at ~24 FPS.
/// </summary>
public sealed class LobbyBlackHoleControl : Control
{
    private const int PixelScale = 4;
    private const float TargetFrameSeconds = 1f / 24f;
    private const float SimScale = 60f * TargetFrameSeconds; // Figma logic assumes ~60 FPS steps
    private const int RingDebrisCount = 80;
    private const int MaxFlybys = 12;
    private const int MaxBigHalfPx = 36;

    [Dependency] private readonly IClyde _clyde = default!;

    private OwnedTexture? _texture;
    private Rgba32[] _buffer = Array.Empty<Rgba32>();
    private Vector2i _bufferSize;
    private float _accum;
    private float _t;
    private int _frameCount;
    private int _lastBigSpawn = -200;
    private int _randState = 1337;

    private readonly List<(float X, float Y, float B, float Tw)> _stars = new();
    private readonly RingDebris[] _ring = new RingDebris[RingDebrisCount];
    private readonly List<FlybyDebris> _flybys = new();

    public LobbyBlackHoleControl()
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

        _accum += args.DeltaSeconds;
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

        // Cap work so ultra-wide resolutions stay cheap.
        w = Math.Min(w, 480);
        h = Math.Min(h, 270);

        if (_bufferSize.X == w && _bufferSize.Y == h && _texture != null)
            return;

        _texture?.Dispose();
        _texture = null;
        _bufferSize = new Vector2i(w, h);
        _buffer = new Rgba32[w * h];
        _texture = _clyde.CreateBlankTexture<Rgba32>(_bufferSize, "lobby-blackhole");
    }

    private void RenderFrame()
    {
        var w = _bufferSize.X;
        var h = _bufferSize.Y;
        Array.Fill(_buffer, new Rgba32(2, 3, 10, 255));

        var cx = w * 0.32f;
        var cy = h * 0.5f;

        DrawNebula(cx, cy, h);
        DrawStars(w, h);

        // Spawn cadence scaled from Figma's 60 FPS frame counts.
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

        // Advance + draw background flybys (behind the hole).
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

        // Pass 0: ring debris behind → disk/hole → pass 1: ring debris in front.
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

            // Accretion disk
            for (var ring = rings; ring >= 0; ring--)
            {
                var frac = ring / (float) rings;
                var r = diskR * (0.28f + frac * 0.72f);
                var segments = Math.Max(28, (int) (r * 2.5f));
                var heat = 1f - frac;

                for (var seg = 0; seg < segments; seg++)
                {
                    var angle = (seg / (float) segments) * MathF.PI * 2f + _t * (0.12f + frac * 0.25f);
                    var dx3 = MathF.Cos(angle) * r;
                    var dz3 = MathF.Sin(angle) * r;
                    var screenY = -dz3 * MathF.Sin(tilt);
                    var finalX = dx3 * MathF.Cos(twist) - screenY * MathF.Sin(twist);
                    var finalY = dx3 * MathF.Sin(twist) + screenY * MathF.Cos(twist);
                    var flicker = 0.82f + 0.18f * MathF.Sin(_t * 3.5f + seg * 0.6f + ring * 0.4f);

                    byte rr, gg, bb;
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

            // Event horizon
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
                    Blend((int) cx + dx, (int) cy + dy, 255, 180, 30, ((1.15f - d2) / 0.3f) * 0.7f);
            }

            for (var dx = -bhRx; dx <= bhRx; dx++)
            for (var dy = -bhRy; dy <= bhRy; dy++)
            {
                var ex = dx / (float) bhRx;
                var ey = dy / (float) bhRy;
                if (ex * ex + ey * ey <= 1f)
                    Set((int) cx + dx, (int) cy + dy, new Rgba32(0, 0, 0, 255));
            }

            // Photon ring
            var lensR = bhR * 1.14f;
            for (var i = 0; i < 120; i++)
            {
                var ang = (i / 120f) * MathF.PI * 2f;
                var ldx = MathF.Cos(ang) * lensR;
                var ldz = MathF.Sin(ang) * lensR;
                var lsx = ldx * MathF.Cos(twist) + (-ldz * MathF.Sin(tilt)) * (-MathF.Sin(twist));
                var lsy = ldx * MathF.Sin(twist) + (-ldz * MathF.Sin(tilt)) * MathF.Cos(twist);
                var glow = 0.55f + 0.45f * MathF.Sin(ang * 4f + _t * 2.5f);
                var facing = MathF.Sin(ang) < 0 ? 1f : 0.5f;
                Blend((int) (cx + lsx), (int) (cy + lsy), 255, 210, 60, glow * facing);
            }

            // Jets
            for (var j = 0; j < 18; j++)
            {
                var jt = (_t * 0.35f + j / 18f) % 1f;
                var jLen = bhR * (1.6f + jt * 3.5f);
                var jitterX = MathF.Sin(j * 2.3f + _t) * 1.5f;
                var jdx = MathF.Sin(tilt) * jLen * 0.3f;
                Blend((int) (cx + jitterX + jdx), (int) (cy - jLen), 200, 140, 20, (1f - jt) * 0.5f);
                Blend((int) (cx + jitterX - jdx), (int) (cy + jLen), 200, 140, 20, (1f - jt) * 0.5f);
            }

            // Soft local bloom
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

        // Foreground flybys (including large rocks).
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

        // Compact inactive flybys
        for (var i = _flybys.Count - 1; i >= 0; i--)
        {
            if (!_flybys[i].Active)
                _flybys.RemoveAt(i);
        }
    }

    private void DrawNebula(float cx, float cy, int h)
    {
        var maxR = h * 0.6f;
        var step = 3;
        for (var y = 0; y < _bufferSize.Y; y += step)
        for (var x = 0; x < _bufferSize.X; x += step)
        {
            var dx = x - cx;
            var dy = y - cy;
            var dist = MathF.Sqrt(dx * dx + dy * dy) / maxR;
            if (dist > 1f)
                continue;

            float a;
            byte r, g, b;
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
        float sx, sy;
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
        // Sizes are in buffer pixels (buffer is already ~1/4 screen).
        int size;
        int halfPx;
        Vector2[]? poly = null;

        if (forceBig)
        {
            // ~150–330 screen px → ~38–82 buffer px radius, then capped.
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
            Layer = forceBig ? FlybyLayer.Foreground : (RandomFloat() > 0.4f ? FlybyLayer.Foreground : FlybyLayer.Background),
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

        // Golden rim on lit edge
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
        var step = 2;
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
            a += (MathF.PI * 2f / numVerts) * (0.4f + SeededRand(seed + v * 11 + 203) * 1.2f);
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

    private void Set(int x, int y, Rgba32 color)
    {
        if ((uint) x >= (uint) _bufferSize.X || (uint) y >= (uint) _bufferSize.Y)
            return;
        _buffer[y * _bufferSize.X + x] = color;
    }

    private void Blend(int x, int y, byte r, byte g, byte b, float a)
    {
        if (a <= 0f || (uint) x >= (uint) _bufferSize.X || (uint) y >= (uint) _bufferSize.Y)
            return;

        a = Math.Clamp(a, 0f, 1f);
        ref var dst = ref _buffer[y * _bufferSize.X + x];
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

    private struct RingDebris
    {
        public float OrbitAngle;
        public float OrbitR;
        public float OrbitSpeed;
        public int Size;
        public int Seed;
    }

    private enum FlybyLayer : byte
    {
        Background,
        Foreground
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

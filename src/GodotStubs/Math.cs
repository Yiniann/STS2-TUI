namespace Godot;

public struct Vector2
{
    public float X;
    public float Y;

    public static Vector2 Zero { get; } = new(0, 0);
    public static Vector2 One { get; } = new(1, 1);
    public static Vector2 Up { get; } = new(0, -1);
    public static Vector2 Down { get; } = new(0, 1);
    public static Vector2 Left { get; } = new(-1, 0);
    public static Vector2 Right { get; } = new(1, 0);

    public Vector2(float x, float y) { X = x; Y = y; }

    public float Length() => MathF.Sqrt(X * X + Y * Y);
    public float LengthSquared() => X * X + Y * Y;
    public Vector2 Normalized()
    {
        float len = Length();
        return len > 0 ? new Vector2(X / len, Y / len) : Zero;
    }
    public float DistanceTo(Vector2 other) => (this - other).Length();
    public Vector2 Lerp(Vector2 to, float weight) => new(X + (to.X - X) * weight, Y + (to.Y - Y) * weight);
    public float Dot(Vector2 other) => X * other.X + Y * other.Y;
    public Vector2 Abs() => new(MathF.Abs(X), MathF.Abs(Y));
    public Vector2 Clamp(Vector2 min, Vector2 max) => new(Math.Clamp(X, min.X, max.X), Math.Clamp(Y, min.Y, max.Y));

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator *(Vector2 a, float b) => new(a.X * b, a.Y * b);
    public static Vector2 operator *(float a, Vector2 b) => new(a * b.X, a * b.Y);
    public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
    public static Vector2 operator /(Vector2 a, float b) => new(a.X / b, a.Y / b);
    public static Vector2 operator /(Vector2 a, int b) => new(a.X / b, a.Y / b);
    public static Vector2 operator -(Vector2 v) => new(-v.X, -v.Y);
    public static bool operator ==(Vector2 a, Vector2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
    public override bool Equals(object? obj) => obj is Vector2 v && this == v;
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X}, {Y})";
}

public struct Vector2I
{
    public int X;
    public int Y;
    public Vector2I(int x, int y) { X = x; Y = y; }
    public static Vector2I Zero { get; } = new(0, 0);
    public static Vector2I One { get; } = new(1, 1);
    public static implicit operator Vector2(Vector2I v) => new(v.X, v.Y);
}

public struct Vector3
{
    public float X, Y, Z;
    public static Vector3 Zero { get; } = new(0, 0, 0);
    public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
}

public struct Color
{
    public float R, G, B, A;
    public static Color White { get; } = new(1, 1, 1, 1);
    public static Color Black { get; } = new(0, 0, 0, 1);
    public static Color Transparent { get; } = new(0, 0, 0, 0);

    public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
    public Color(string htmlColor) { R = 1; G = 1; B = 1; A = 1; }
    // Godot's Color(Color, float) — recolor with a new alpha. Used by VFX helpers
    // (e.g. Liquid Memories' discard-pile selection); missing overload throws
    // MissingMethodException in headless (issue #72).
    public Color(Color c, float alpha) { R = c.R; G = c.G; B = c.B; A = alpha; }

    public Color Lerp(Color to, float weight) => new(
        R + (to.R - R) * weight, G + (to.G - G) * weight,
        B + (to.B - B) * weight, A + (to.A - A) * weight);

    public static Color operator *(Color c, float f) => new(c.R * f, c.G * f, c.B * f, c.A * f);
    public static bool operator ==(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    public static bool operator !=(Color a, Color b) => !(a == b);
    public override bool Equals(object? obj) => obj is Color c && this == c;
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
}

public struct Rect2
{
    private Vector2 _position;
    private Vector2 _size;
    public Vector2 Position { get => _position; set => _position = value; }
    public Vector2 Size { get => _size; set => _size = value; }
    public float X { get => _position.X; set => _position = new Vector2(value, _position.Y); }
    public float Y { get => _position.Y; set => _position = new Vector2(_position.X, value); }
    public float W { get => _size.X; set => _size = new Vector2(value, _size.Y); }
    public float H { get => _size.Y; set => _size = new Vector2(_size.X, value); }

    public Rect2(float x, float y, float w, float h)
    {
        _position = new Vector2(x, y);
        _size = new Vector2(w, h);
    }
    public Rect2(Vector2 pos, Vector2 size) { _position = pos; _size = size; }
}

public struct Transform2D
{
    public Vector2 X, Y, Origin;
    public static Transform2D Identity { get; } = new();
    public Transform2D SampleBakedWithRotation() => this;
}

public static class Mathf
{
    public const float Pi = MathF.PI;
    public const float Tau = MathF.Tau;
    public const float Inf = float.PositiveInfinity;
    public const float Epsilon = 1e-6f;

    public static float Sin(float s) => MathF.Sin(s);
    public static float Cos(float s) => MathF.Cos(s);
    public static float Tan(float s) => MathF.Tan(s);
    public static float Sqrt(float s) => MathF.Sqrt(s);
    public static float Pow(float @base, float exp) => MathF.Pow(@base, exp);
    public static float Abs(float s) => MathF.Abs(s);
    public static float Floor(float s) => MathF.Floor(s);
    public static float Ceil(float s) => MathF.Ceiling(s);
    public static float Round(float s) => MathF.Round(s);
    public static int FloorToInt(float s) => (int)MathF.Floor(s);
    public static int CeilToInt(float s) => (int)MathF.Ceiling(s);
    public static int CeilToInt(double s) => (int)Math.Ceiling(s);
    public static int FloorToInt(double s) => (int)Math.Floor(s);
    public static int RoundToInt(double s) => (int)Math.Round(s);
    public static int RoundToInt(float s) => (int)MathF.Round(s);
    public static float Min(float a, float b) => MathF.Min(a, b);
    public static int Min(int a, int b) => Math.Min(a, b);
    public static float Max(float a, float b) => MathF.Max(a, b);
    public static int Max(int a, int b) => Math.Max(a, b);
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    public static float Lerp(float from, float to, float weight) => from + (to - from) * weight;
    public static float DegToRad(float deg) => deg * (Pi / 180f);
    public static float RadToDeg(float rad) => rad * (180f / Pi);
    public static float Sign(float s) => MathF.Sign(s);
    public static float Remap(float value, float istart, float istop, float ostart, float ostop)
    {
        return ostart + (ostop - ostart) * ((value - istart) / (istop - istart));
    }
    public static bool IsEqualApprox(float a, float b) => MathF.Abs(a - b) < Epsilon;
    public static float Snapped(float value, float step) => step != 0 ? MathF.Round(value / step) * step : value;
}

namespace SatisfactoryMapRenderer.Save;

public readonly record struct Vector3(double X, double Y, double Z)
{
    public static Vector3 ReadFloat(BinaryDataReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    public static Vector3 ReadDouble(BinaryDataReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public override string ToString() => $"({X:0.#}, {Y:0.#}, {Z:0.#})";
}

public readonly record struct Quaternion(double X, double Y, double Z, double W)
{
    public static readonly Quaternion Identity = new(0, 0, 0, 1);

    public static Quaternion ReadFloat(BinaryDataReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    public static Quaternion ReadDouble(BinaryDataReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());

    /// <summary>Rotation around the vertical (Z) axis in radians.</summary>
    public double Yaw => Math.Atan2(2 * (W * Z + X * Y), 1 - 2 * (Y * Y + Z * Z));

    public Vector3 Rotate(Vector3 v)
    {
        // v' = v + 2w(q × v) + 2(q × (q × v))
        double tx = 2 * (Y * v.Z - Z * v.Y);
        double ty = 2 * (Z * v.X - X * v.Z);
        double tz = 2 * (X * v.Y - Y * v.X);
        return new Vector3(
            v.X + W * tx + (Y * tz - Z * ty),
            v.Y + W * ty + (Z * tx - X * tz),
            v.Z + W * tz + (X * ty - Y * tx));
    }
}

public readonly record struct Transform(Quaternion Rotation, Vector3 Translation, Vector3 Scale)
{
    public static readonly Transform Identity = new(Quaternion.Identity, default, new Vector3(1, 1, 1));

    public static Transform ReadFloat(BinaryDataReader r) => new(Quaternion.ReadFloat(r), Vector3.ReadFloat(r), Vector3.ReadFloat(r));
    public static Transform ReadDouble(BinaryDataReader r) => new(Quaternion.ReadDouble(r), Vector3.ReadDouble(r), Vector3.ReadDouble(r));

    public Vector3 TransformPosition(Vector3 local) =>
        Rotation.Rotate(new Vector3(local.X * Scale.X, local.Y * Scale.Y, local.Z * Scale.Z)) + Translation;
}

/// <summary>A reference to another object in the save: level name + full path name.</summary>
public readonly record struct ObjectReference(string LevelName, string PathName)
{
    public static ObjectReference Read(BinaryDataReader r) => new(r.ReadString(), r.ReadString());
    public bool IsEmpty => string.IsNullOrEmpty(PathName);
    public override string ToString() => PathName;
}

public readonly record struct LinearColor(float R, float G, float B, float A);

public readonly record struct Color(byte B, byte G, byte R, byte A);

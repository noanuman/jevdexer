namespace Jevdexer.Content.Models;

/// <summary>Vector helpers. Stored vectors are int8 (a quarter of the size, same ranking in practice).</summary>
public static class Vectors
{
    public static void Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Unit vector to int8 (as bytes, two's complement, so JSON stores it as compact base64): each component times 127, rounded.</summary>
    public static byte[] Quantize(float[] v) => v.Select(x => (byte)(sbyte)Math.Clamp(MathF.Round(x * 127f), -127, 127)).ToArray();

    public static float[] Dequantize(ReadOnlySpan<byte> q)
    {
        var v = new float[q.Length];
        for (var i = 0; i < q.Length; i++) v[i] = (sbyte)q[i] / 127f;
        return v;
    }
}

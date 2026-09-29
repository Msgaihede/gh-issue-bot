namespace DiscordGithubBot.CodeContext.Retrieval;

/// <summary>
/// Embedding vectors as bytes for a SQLite BLOB column: float32s in machine order. Kept as plain
/// <c>byte[]</c> on the entity (which EF compares by content out of the box) rather than a converted
/// <c>float[]</c>, which needed a value converter and a value comparer to track changes correctly.
/// </summary>
public static class VectorBytes
{
    public static byte[] From(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] To(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}

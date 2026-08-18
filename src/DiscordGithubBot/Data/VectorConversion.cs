using System.Runtime.InteropServices;

namespace DiscordGithubBot.Data;

/// <summary>Converts embedding vectors to and from their little-endian BLOB representation.</summary>
public static class VectorConversion
{
    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes<float>(vector).ToArray();

    public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}

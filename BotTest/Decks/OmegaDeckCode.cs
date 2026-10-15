using System.IO.Compression;

namespace BotTest.Decks
{
    /// <summary>
    /// Omega's deck code format: base64 of a raw-deflated byte array
    /// [main+extra count, side count] followed by every card ID as a little-endian uint32
    /// (main, then extra, then side).
    /// </summary>
    public static class OmegaDeckCode
    {
        public static string Encode(IReadOnlyCollection<int> main, IReadOnlyCollection<int> extra, IReadOnlyCollection<int> side)
        {
            using var raw = new MemoryStream();
            raw.WriteByte((byte)(main.Count + extra.Count));
            raw.WriteByte((byte)side.Count);
            foreach (int id in main.Concat(extra).Concat(side))
                raw.Write(BitConverter.GetBytes((uint)id));

            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(raw.ToArray());

            return Convert.ToBase64String(compressed.ToArray());
        }
    }
}

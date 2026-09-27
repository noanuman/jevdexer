using System.Buffers.Binary;
using System.Text;

namespace Jevdexer.Discovery.Enrichment;

/// <summary>
/// Reader for Steam's binary `appcache\appinfo.vdf`: per app, the `appinfo.common` section.
/// Handles format 29 (keys are indexes into a string table at the end of the file, magic
/// 0x07564429) and 28 (keys inline, magic 0x07564428).
/// </summary>
internal static class SteamAppInfo
{
    public sealed record Common(string? Type, IReadOnlyList<int> Genres, IReadOnlyList<int> StoreTags);

    private const uint Magic28 = 0x07564428;
    private const uint Magic29 = 0x07564429;

    // Per entry after appid + size: infoState u32, lastUpdated u32, picsToken u64, sha1[20], changeNumber u32, binarySha1[20].
    private const int EntryHeader = 4 + 4 + 8 + 20 + 4 + 20;

    /// <summary>Parses the file; returns only the apps asked for, to skip the rest cheaply.</summary>
    public static Dictionary<uint, Common> Read(byte[] data, IReadOnlySet<uint> wanted)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic is not (Magic28 or Magic29)) throw new InvalidDataException($"appinfo.vdf magic 0x{magic:x8} not supported");

        string[]? strings = null;
        var pos = 8;
        if (magic == Magic29)
        {
            strings = ReadStringTable(data, BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)));
            pos = 16;
        }

        var result = new Dictionary<uint, Common>();
        while (pos + 8 <= data.Length)
        {
            var appId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            if (appId == 0) break;
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            var next = pos + 8 + size;

            if (wanted.Contains(appId))
            {
                var p = pos + 8 + EntryHeader;
                var root = ReadKv(data, ref p, strings);
                if (root.Child("appinfo")?.Child("common") is { } common)
                    result[appId] = new Common(common.Get("type") as string, Ints(common.Child("genres")), Ints(common.Child("store_tags")));
            }
            pos = next;
        }
        return result;
    }

    private static List<int> Ints(Node? n) => n?.Values.Values.OfType<int>().ToList() ?? [];

    private sealed class Node
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object? Get(string k) => Values.GetValueOrDefault(k);
        public Node? Child(string k) => Get(k) as Node;
    }

    private static Node ReadKv(byte[] d, ref int p, string[]? strings)
    {
        var node = new Node();
        while (true)
        {
            var type = d[p++];
            if (type == 8) return node;

            string key;
            if (strings is not null)
            {
                key = strings[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p))];
                p += 4;
            }
            else key = ReadCString(d, ref p);

            node.Values[key] = type switch
            {
                0 => ReadKv(d, ref p, strings),
                1 => ReadCString(d, ref p),
                2 => ReadAndAdvance(ref p, 4, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p))),
                3 => ReadAndAdvance(ref p, 4, BitConverter.ToSingle(d, p)),
                7 or 10 => ReadAndAdvance(ref p, 8, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p))),
                _ => throw new InvalidDataException($"appinfo.vdf value type {type} at {p}"),
            };
        }
    }

    private static object ReadAndAdvance(ref int p, int width, object value)
    {
        p += width;
        return value;
    }

    private static string ReadCString(byte[] d, ref int p)
    {
        var end = Array.IndexOf(d, (byte)0, p);
        var s = Encoding.UTF8.GetString(d, p, end - p);
        p = end + 1;
        return s;
    }

    private static string[] ReadStringTable(byte[] d, long offset)
    {
        var p = (int)offset;
        var count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        p += 4;
        var strings = new string[count];
        for (var i = 0; i < count; i++) strings[i] = ReadCString(d, ref p);
        return strings;
    }
}

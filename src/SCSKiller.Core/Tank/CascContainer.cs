using System.Runtime.InteropServices;

namespace SCSKiller.Core.Tank;

/// <summary>Blizzard's CASC storage, as Overwatch's Battle.net install has it: <c>.build.info</c> and
/// <c>data/casc/indices</c>, with the assets in <c>data/*.0xx</c>. The ROOT file names only the launcher's files, so the
/// content manifests (<c>Manifest/*.cmf</c>, per-build keys) are what name the rest; CascLib reads both. Every named file is
/// opened by its name (which for the game's assets is a content key's hex) and the shader assets are the ones that start with
/// the same eight 0xFF bytes as the Steam install's. Nothing here is written; the storage is opened read-only.</summary>
internal static unsafe class CascContainer
{
    public const string BuildInfo = ".build.info";
    const int MaxPath = 1024;   // CascPort.h: MAX_PATH is 1024 outside Windows
    const int Md5 = 16;
    const uint LocaleAll = 0xFFFFFFFF;
    const uint OpenByCKey = 1;   // CascLib.h: CASC_OPEN_BY_CKEY, the name is the 16-byte key itself
    const int MaxAsset = 64 << 20;

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    struct FindData
    {
        public fixed byte Name[MaxPath];
        public fixed byte CKey[Md5];
        public fixed byte EKey[Md5];
        public ulong TagBitMask;
        public ulong FileSize;
        public nint PlainName;
        public uint FileDataId;
        public uint LocaleFlags;
        public uint ContentFlags;
        public uint SpanCount;
        public uint Flags;   // bFileAvailable:1, the rest of the DWORD
        public uint NameType;
    }

    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascOpenStorage([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint locale, out nint storage);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascCloseStorage(nint storage);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    static extern nint CascFindFirstFile(nint storage, [MarshalAs(UnmanagedType.LPUTF8Str)] string mask, ref FindData data, nint listFile);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascFindNextFile(nint find, ref FindData data);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascFindClose(nint find);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascOpenFile(nint storage, byte* ckey, uint locale, uint flags, out nint file);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascCloseFile(nint file);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascGetFileSize64(nint file, out ulong size);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascSetFilePointer64(nint file, long distance, out ulong newPosition, uint method);
    [DllImport("libcasc.so", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CascReadFile(nint file, byte* buffer, uint toRead, out uint read);

    static bool OpenByKey(nint storage, ReadOnlySpan<byte> ckey, out nint file)
    {
        fixed (byte* ck = ckey) return CascOpenFile(storage, ck, LocaleAll, OpenByCKey, out file);
    }

    /// <summary>The game's root: the exe's own folder for a Battle.net registry entry, its parent when a launcher's
    /// entry names <c>_retail_</c>.</summary>
    static string? RootOf(string installDir)
    {
        if (!OperatingSystem.IsLinux()) return null;
        bool Has(string dir) => File.Exists(Path.Combine(dir, BuildInfo)) && Directory.Exists(Path.Combine(dir, "data", "casc", "indices"));
        if (Has(installDir)) return installDir;
        return Path.GetDirectoryName(installDir) is { } up && Has(up) ? up : null;
    }

    public static bool IsInstall(string installDir) => RootOf(installDir) != null;

    /// <summary>A .build.info header cell: "Product", or CascLib's typed "Product!STRING:0".</summary>
    static int Column(string[] columns, string name) =>
        Array.FindIndex(columns, c => c == name || c.StartsWith(name + "!", StringComparison.Ordinal));

    /// <summary>The build identity's Product and Version, from the second line of <c>.build.info</c>; null when it can't be read.</summary>
    public static (string Raw, string Product, string Version)? BuildInfoOf(string installDir)
    {
        try
        {
            if (RootOf(installDir) is not { } root) return null;
            var text = File.ReadAllText(Path.Combine(root, BuildInfo));
            var lines = text.Split('\n', 3);
            if (lines.Length < 2) return null;
            var columns = lines[0].TrimEnd('\r').Split('|');
            var values = lines[1].TrimEnd('\r').Split('|');
            var product = Column(columns, "Product");
            var version = Column(columns, "Version");
            if (product < 0 || version < 0 || product >= values.Length || version >= values.Length) return null;
            return (text, values[product], values[version]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Each locally available file whose content starts like a ShaderCode asset, keyed by its content key.
    /// The storage names about a million entries; an entry the install doesn't carry locally is skipped unopened.</summary>
    public static void Walk(string installDir, CancellationToken ct, Action<string, byte[]> asset, Action<int>? progress = null)
    {
        var path = Path.Combine(RootOf(installDir) ?? installDir, "data");
        if (!CascOpenStorage(path, 0, out var storage) || storage == 0)
            throw new IOException("the CASC storage couldn't be opened");
        try
        {
            var data = default(FindData);
            var find = CascFindFirstFile(storage, "*", ref data, 0);
            if (find == 0 || find == -1) throw new IOException("the CASC storage has no file list");
            try
            {
                Span<byte> head = stackalloc byte[8];
                var n = 0;
                do
                {
                    if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
                    if (data.FileSize is < 16 or > MaxAsset || (data.Flags & 1) == 0) continue;
                    progress?.Invoke(n);
                    var name = Convert.ToHexStringLower(new ReadOnlySpan<byte>(data.CKey, 16));
                    if (!OpenByKey(storage, new ReadOnlySpan<byte>(data.CKey, 16), out var file) || file == 0) continue;
                    try
                    {
                        if (CascGetFileSize64(file, out var size) && size is >= 16 and <= MaxAsset)
                        {
                            fixed (byte* h = head)
                                if (!CascReadFile(file, h, 8, out var got) || got != 8 || head.IndexOfAnyExcept((byte)0xFF) >= 0) continue;
                            var bytes = new byte[(int)size];
                            fixed (byte* p = bytes)
                                if (CascSetFilePointer64(file, 0, out _, 0) && CascReadFile(file, p, (uint)bytes.Length, out var read) && read == (uint)bytes.Length)
                                    asset(name, bytes);
                        }
                    }
                    finally { CascCloseFile(file); }
                } while (CascFindNextFile(find, ref data));
            }
            finally { CascFindClose(find); }
        }
        finally { CascCloseStorage(storage); }
    }

    /// <summary>Reads each named asset again, in one storage.</summary>
    public static void Read(string installDir, IEnumerable<string> names, CancellationToken ct, Action<string, byte[]> sink)
    {
        var path = Path.Combine(RootOf(installDir) ?? installDir, "data");
        if (!CascOpenStorage(path, 0, out var storage) || storage == 0)
            throw new IOException("the CASC storage couldn't be opened");
        try
        {
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                if (OpenByKey(storage, Convert.FromHexString(name), out var file) && file != 0)
                    try
                    {
                        if (CascGetFileSize64(file, out var size) && size is >= 16 and <= MaxAsset)
                        {
                            var bytes = new byte[(int)size];
                            fixed (byte* b = bytes)
                                if (CascReadFile(file, b, (uint)bytes.Length, out var read) && read == (uint)bytes.Length) sink(name, bytes);
                        }
                    }
                    finally { CascCloseFile(file); }
            }
        }
        finally { CascCloseStorage(storage); }
    }
}

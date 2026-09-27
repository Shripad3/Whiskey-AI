using System.Data.OleDb;
using System.Security.Principal;
using System.Text;
using Alfred.Core;

namespace Alfred.Windows.Platform;

/// <summary>
/// The Windows Search index (SystemIndex over OLE DB), metadata columns only
/// (spec §8). When the index is off or doesn't cover a folder, the bounded
/// directory walk answers instead.
/// </summary>
internal sealed class WindowsFileSearch : IFileSearch
{
    private readonly DirectoryFileSearch _fallback = new();

    public async Task<IReadOnlyList<FoundFile>> SearchAsync(FileQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        try
        {
            var found = await Task.Run(() => Index(query, roots), cancellationToken);
            if (found.Count > 0) return found;
        }
        catch (Exception e) when (e is OleDbException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Windows Search unavailable ({e.Message}); walking the folders instead");
        }
        // Nothing indexed can also mean "not indexed": look for real.
        return await _fallback.SearchAsync(query, roots, cancellationToken);
    }

    private static List<FoundFile> Index(FileQuery query, IReadOnlyList<string> roots)
    {
        using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        connection.Open();
        using var command = new OleDbCommand(WindowsSearchSql.Build(query, roots), connection);
        using var reader = command.ExecuteReader();
        var found = new List<FoundFile>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var path = reader.GetString(0);
            var name = reader.IsDBNull(1) ? Path.GetFileName(path) : reader.GetString(1);
            var date = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2).ToLocalTime();
            found.Add(new FoundFile(path, name, date));
        }
        return found;
    }
}

/// <summary>
/// The Recycle Bin (spec §8, §11): items go there with the shell's own undo
/// flag, and nothing here can empty it. Where each item landed is read from
/// the bin's $I index files, so undo can put it back.
/// </summary>
internal sealed class WindowsRecycleBin : IRecycleBin
{
    private static readonly string Sid = WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static string BinFolder(string path) => Path.Combine(Path.GetPathRoot(Path.GetFullPath(path))!, "$Recycle.Bin", Sid);

    public string Recycle(string path)
    {
        var full = Path.GetFullPath(path);
        var before = DateTime.UtcNow.AddSeconds(-2);
        var operation = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = full + "\0\0",
            // FOF_WANTNUKEWARNING: if the drive has no Recycle Bin, Windows asks
            // before deleting for good, and "no" leaves the file alone.
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI | Native.FOF_WANTNUKEWARNING),
        };
        var result = 0;
        // The shell wants an STA thread, and may show its "delete permanently?" dialog.
        var thread = new Thread(() => result = Native.SHFileOperationW(ref operation));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (result != 0 || operation.fAnyOperationsAborted != 0 || File.Exists(full) || Directory.Exists(full))
            throw new ToolException($"Windows didn't move {Path.GetFileName(full)} to the Recycle Bin");
        return Find(full, before) ?? throw new ToolException($"{Path.GetFileName(full)} is in the Recycle Bin, but I couldn't find it there to undo later");
    }

    /// <summary>The $R item for the newest $I entry recording this original path.</summary>
    private static string? Find(string original, DateTime notBefore)
    {
        var folder = BinFolder(original);
        if (!Directory.Exists(folder)) return null;
        var best = (Path: (string?)null, Deleted: DateTime.MinValue);
        foreach (var index in Directory.EnumerateFiles(folder, "$I*"))
        {
            if (ReadIndex(index) is not { } entry || !string.Equals(entry.Original, original, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Deleted < notBefore || entry.Deleted < best.Deleted) continue;
            var item = Path.Combine(folder, "$R" + Path.GetFileName(index)[2..]);
            if (File.Exists(item) || Directory.Exists(item)) best = (item, entry.Deleted);
        }
        return best.Path;
    }

    /// <summary>
    /// A $I file: version (8 bytes, 2 on Windows 10+), size (8), deletion time
    /// (8, FILETIME), then the original path's length in characters (4) and
    /// the path in UTF-16.
    /// </summary>
    private static (string Original, DateTime Deleted)? ReadIndex(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 28 || BitConverter.ToInt64(bytes, 0) != 2) return null;
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
            var length = BitConverter.ToInt32(bytes, 24);
            if (length <= 0 || 28 + length * 2 > bytes.Length) return null;
            return (Encoding.Unicode.GetString(bytes, 28, length * 2).TrimEnd('\0'), deleted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Restore(string inBin, string original)
    {
        if (!Contains(inBin)) throw new ToolException("That item isn't in the Recycle Bin");
        if (Directory.Exists(inBin)) Directory.Move(inBin, original);
        else File.Move(inBin, original, overwrite: false);
        // Its $I index entry now points at nothing; remove it so the bin doesn't list a ghost.
        var index = Path.Combine(Path.GetDirectoryName(inBin)!, "$I" + Path.GetFileName(inBin)[2..]);
        try { File.Delete(index); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public bool Contains(string path) =>
        Path.GetFullPath(path).Contains($"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}

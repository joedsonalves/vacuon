using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Core.Index;

namespace Vacuon.Core.Scan;

/// <summary>
/// Estratégia de fallback (PRD §7.1, S3): travessia pela API do Windows.
/// <para>
/// Funciona em exFAT, FAT32, ReFS, unidade de rede e sem elevação — ao custo de ser
/// 10 a 40× mais lenta que a MFT. Também é o caminho quando o usuário pede o escopo
/// de uma pasta específica em vez do volume inteiro.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32Walker(MftScanOptions? options = null)
{
    private readonly MftScanOptions _options = options ?? new MftScanOptions();

    public VolumeIndex Scan(string rootPath, CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);
        char drive = char.ToUpperInvariant(rootPath[0]);

        var entries = new List<FileEntry>(1 << 16);
        var names = new NameBlob();

        // Índice 0 é a raiz do escopo; os demais apontam para o pai pelo índice na lista.
        entries.Add(new FileEntry
        {
            RecordNumber = 0,
            ParentIndex = 0,
            NameOffset = names.Append(rootPath.AsSpan()),
            NameLength = (ushort)rootPath.Length,
            Flags = EntryFlags.Directory,
        });

        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        int files = 0;

        var queue = new Stack<(string Path, int Index)>();
        queue.Push((rootPath, 0));

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string dir, int parentIndex) = queue.Pop();

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumOptions);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (FileSystemInfo item in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attrs;
                try { attrs = item.Attributes; }
                catch (IOException) { continue; }

                var flags = EntryFlags.None;
                if ((attrs & FileAttributes.Hidden) != 0) flags |= EntryFlags.Hidden;
                if ((attrs & FileAttributes.System) != 0) flags |= EntryFlags.System;
                if ((attrs & FileAttributes.Compressed) != 0) flags |= EntryFlags.Compressed;
                if ((attrs & FileAttributes.SparseFile) != 0) flags |= EntryFlags.Sparse;
                if ((attrs & FileAttributes.Encrypted) != 0) flags |= EntryFlags.Encrypted;
                if ((attrs & FileAttributes.ReparsePoint) != 0) flags |= EntryFlags.ReparsePoint;

                // ⚠️ Cloud placeholders, on this path too. The MFT scanner has set this flag
                // since it was written; the walk had not, so on an unelevated session every
                // reader downstream — the duplicate search, the fingerprints — would happily
                // read a OneDrive placeholder and make Windows fetch the file. FileAttributes
                // has no name for these two, so the values are spelled out: RECALL_ON_OPEN
                // and RECALL_ON_DATA_ACCESS, from winnt.h.
                const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
                const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

                if ((attrs & (RecallOnOpen | RecallOnDataAccess)) != 0)
                    flags |= EntryFlags.CloudPlaceholder;

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                if (isDir) flags |= EntryFlags.Directory;

                long size = 0;
                if (!isDir && item is FileInfo fi)
                {
                    try { size = fi.Length; }
                    catch (IOException) { }
                }

                var entry = new FileEntry
                {
                    RecordNumber = (uint)entries.Count,
                    ParentIndex = (uint)parentIndex,
                    NameOffset = names.Append(item.Name.AsSpan()),
                    NameLength = (ushort)item.Name.Length,
                    Flags = flags,
                    LogicalSize = size,
                    AllocatedSize = size, // sem MFT não há AllocatedSize real; aproximação honesta
                    CreatedUtc = SafeFileTime(() => item.CreationTimeUtc),
                    LastWriteUtc = SafeFileTime(() => item.LastWriteTimeUtc),
                    LastAccessUtc = SafeFileTime(() => item.LastAccessTimeUtc),
                    HardLinkCount = 1,
                };

                entries.Add(entry);

                // Reparse point nunca é atravessado: junctions criam ciclos
                // (C:\Documents and Settings -> C:\Users) e contam espaço duas vezes.
                if (isDir && (attrs & FileAttributes.ReparsePoint) == 0)
                {
                    queue.Push((item.FullName, entries.Count - 1));
                }
                else if (!isDir)
                {
                    files++;
                }

                if (_options.Progress is not null && sw.Elapsed - lastReport >= _options.ProgressInterval)
                {
                    lastReport = sw.Elapsed;
                    _options.Progress.Report(new ScanProgress(0, 0, entries.Count, files, sw.Elapsed));
                }
            }
        }

        _options.Progress?.Report(new ScanProgress(0, 0, entries.Count, files, sw.Elapsed));

        VolumeInfo info = VolumeProbe.Describe(new DriveInfo($"{drive}:\\"));
        return new VolumeIndex([.. entries], names, info, ScanStrategy.Win32Walk);
    }

    private static long SafeFileTime(Func<DateTime> get)
    {
        try { return get().ToFileTimeUtc(); }
        catch { return 0; }
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;
using Vacuon.Native.Ntfs;

namespace Vacuon.Core.Scan;

public sealed record ScanProgress(
    long BytesRead,
    long TotalBytes,
    int RecordsParsed,
    int EntriesFound,
    TimeSpan Elapsed)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Min(100.0, BytesRead * 100.0 / TotalBytes);
    public double RecordsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : RecordsParsed / Elapsed.TotalSeconds;
    public double MegabytesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : BytesRead / 1048576.0 / Elapsed.TotalSeconds;
}

public sealed class MftScanOptions
{
    /// <summary>Tamanho do bloco de leitura. Blocos grandes são o que dá o throughput sequencial.</summary>
    public int ReadBlockBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Frequência do callback de progresso. Reportar por registro trava a UI.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(120);

    public IProgress<ScanProgress>? Progress { get; init; }
}

/// <summary>
/// Varredura por leitura bruta da MFT — a estratégia primária (PRD §7.1, S1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftScanner(MftScanOptions? options = null)
{
    private readonly MftScanOptions _options = options ?? new MftScanOptions();

    public VolumeIndex Scan(char driveLetter, CancellationToken cancellationToken = default)
    {
        using VolumeDevice device = VolumeDevice.Open(driveLetter);
        NtfsVolumeData vd = device.VolumeData;

        uint bytesPerRecord = vd.BytesPerFileRecordSegment;
        if (bytesPerRecord is 0 or > 65536)
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                $"Tamanho de registro da MFT inválido: {bytesPerRecord}.");

        List<DataRun> runs = ReadMftDataRuns(device, vd, bytesPerRecord);
        if (runs.Count == 0)
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                "A $MFT não expôs data runs legíveis (possível $ATTRIBUTE_LIST na própria $MFT).");

        var stream = new MftStream(device, runs, vd.BytesPerCluster, vd.MftValidDataLength);

        long recordCapacity = vd.MftValidDataLength / bytesPerRecord;
        if (recordCapacity is <= 0 or > int.MaxValue)
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                $"Capacidade da MFT fora de faixa: {recordCapacity} registros.");

        var entries = new FileEntry[recordCapacity];
        // ~40 chars por nome é o que se observa na prática; o blob cresce sozinho se errar.
        var names = new NameBlob((int)Math.Min(recordCapacity * 24L + 1024, 1 << 28));
        var adsBytes = new Dictionary<int, long>();

        Sweep(stream, entries, names, adsBytes, bytesPerRecord, vd.BytesPerSector, cancellationToken);

        LinkOrphans(entries);

        VolumeInfo info = VolumeProbe.Describe(driveLetter, device);
        return new VolumeIndex(entries, names, info, ScanStrategy.Mft, adsBytes);
    }

    /// <summary>
    /// Lê o registro 0 (a própria $MFT) e decodifica seus data runs.
    /// <para>
    /// Este é o passo que quase todo tutorial erra: a MFT é fragmentada em disco usado,
    /// e tratá-la como bloco contíguo faz o scanner perder arquivos sem dar erro.
    /// </para>
    /// </summary>
    private static List<DataRun> ReadMftDataRuns(VolumeDevice device, NtfsVolumeData vd, uint bytesPerRecord)
    {
        byte[] buffer = new byte[bytesPerRecord];
        long offset = vd.MftStartLcn * vd.BytesPerCluster;

        if (device.ReadAt(offset, buffer) != bytesPerRecord)
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                "Não foi possível ler o registro 0 da MFT.");

        if (!MftRecordParser.ApplyFixups(buffer, (int)vd.BytesPerSector))
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                "Os fixups do registro 0 da MFT não conferem (Update Sequence Array inconsistente).");

        if (!MftAttributeFinder.TryFind(buffer, NtfsAttributeType.Data, out ReadOnlySpan<byte> data))
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                "O registro 0 da MFT não tem atributo $DATA sem nome.");

        if (data[NtfsLayout.AttrNonResident] == 0)
            throw new VolumeAccessException(VolumeAccessFailure.MftUnreadable,
                "O $DATA da $MFT veio residente, o que é impossível em um volume válido.");

        return DataRunList.FromNonResidentAttribute(data);
    }

    private void Sweep(MftStream stream, FileEntry[] entries, NameBlob names,
                       Dictionary<int, long> adsBytes,
                       uint bytesPerRecord, uint bytesPerSector, CancellationToken ct)
    {
        int blockSize = Math.Max((int)bytesPerRecord * 64, _options.ReadBlockBytes);
        blockSize -= blockSize % (int)bytesPerRecord; // blocos inteiros de registro

        byte[] block = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            int recordsParsed = 0;
            int entriesFound = 0;
            long recordIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int read = stream.Read(block.AsSpan(0, blockSize));
                if (read <= 0) break;

                int recordsInBlock = read / (int)bytesPerRecord;

                for (int r = 0; r < recordsInBlock; r++)
                {
                    Span<byte> record = block.AsSpan(r * (int)bytesPerRecord, (int)bytesPerRecord);
                    uint number = (uint)(recordIndex + r);

                    recordsParsed++;

                    if (!MftRecordParser.ApplyFixups(record, (int)bytesPerSector))
                        continue; // registro livre, zerado ou escrito pela metade

                    ParsedMftRecord parsed = MftRecordParser.Parse(record, number);
                    if (!parsed.IsValid || !parsed.InUse) continue;

                    if (Merge(entries, names, adsBytes, parsed, number)) entriesFound++;
                }

                recordIndex += recordsInBlock;

                if (_options.Progress is not null && sw.Elapsed - lastReport >= _options.ProgressInterval)
                {
                    lastReport = sw.Elapsed;
                    _options.Progress.Report(new ScanProgress(
                        stream.Position, stream.Length, recordsParsed, entriesFound, sw.Elapsed));
                }
            }

            _options.Progress?.Report(new ScanProgress(
                stream.Length, stream.Length, recordsParsed, entriesFound, sw.Elapsed));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    /// <summary>
    /// Grava o registro parseado no índice.
    /// <para>
    /// Registros de extensão (<c>BaseRecordNumber != 0</c>, produzidos por $ATTRIBUTE_LIST
    /// em arquivos muito fragmentados) escrevem no índice do registro <b>base</b> e só
    /// preenchem campos ainda vazios — assim tanto faz se o base veio antes ou depois.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> se criou uma entrada nova (não uma fusão).</returns>
    private static bool Merge(FileEntry[] entries, NameBlob names, Dictionary<int, long> adsBytes,
                              in ParsedMftRecord parsed, uint number)
    {
        uint target = parsed.BaseRecordNumber != 0 ? parsed.BaseRecordNumber : number;
        if (target >= entries.Length) return false;

        ref FileEntry e = ref entries[(int)target];
        bool isNew = e.NameLength == 0;

        e.RecordNumber = target;

        if (parsed.HasName && e.NameLength == 0)
        {
            e.NameOffset = names.Append(parsed.Name);
            e.NameLength = (ushort)parsed.Name.Length;
            e.ParentIndex = parsed.ParentRecordNumber;
        }

        if (parsed.HasStandardInfo && e.LastWriteUtc == 0)
        {
            e.CreatedUtc = parsed.CreationTime;
            e.LastWriteUtc = parsed.ModifiedTime;
            e.LastAccessUtc = parsed.AccessTime;
            e.Flags |= TranslateAttributes(parsed.FileAttributes);
        }

        if (parsed.HasData && e.LogicalSize == 0 && e.AllocatedSize == 0)
        {
            e.LogicalSize = parsed.LogicalSize;
            e.AllocatedSize = parsed.AllocatedSize;
            if (parsed.IsCompressed) e.Flags |= EntryFlags.Compressed;
            if (parsed.IsSparse) e.Flags |= EntryFlags.Sparse;
            if (parsed.IsEncrypted) e.Flags |= EntryFlags.Encrypted;
        }

        if (parsed.HasAds)
        {
            e.Flags |= EntryFlags.HasAds;
            adsBytes.TryGetValue((int)target, out long previous);
            adsBytes[(int)target] = previous + parsed.AdsBytes;
        }

        if (parsed.IsDirectory) e.Flags |= EntryFlags.Directory;
        if (parsed.IsReparsePoint) e.Flags |= EntryFlags.ReparsePoint;

        if (parsed.BaseRecordNumber == 0)
        {
            e.HardLinkCount = parsed.HardLinkCount;
            if (parsed.HardLinkCount > 1) e.Flags |= EntryFlags.HardLinked;
        }

        return isNew && e.NameLength > 0;
    }

    private static EntryFlags TranslateAttributes(NtfsFileAttributes a)
    {
        EntryFlags f = EntryFlags.None;
        if ((a & NtfsFileAttributes.Directory) != 0) f |= EntryFlags.Directory;
        if ((a & NtfsFileAttributes.Hidden) != 0) f |= EntryFlags.Hidden;
        if ((a & NtfsFileAttributes.System) != 0) f |= EntryFlags.System;
        if ((a & NtfsFileAttributes.Compressed) != 0) f |= EntryFlags.Compressed;
        if ((a & NtfsFileAttributes.SparseFile) != 0) f |= EntryFlags.Sparse;
        if ((a & NtfsFileAttributes.Encrypted) != 0) f |= EntryFlags.Encrypted;
        if ((a & NtfsFileAttributes.ReparsePoint) != 0) f |= EntryFlags.ReparsePoint;

        // Placeholder de nuvem: ler hidrata (enche o disco em vez de liberar) e
        // apagar remove da nuvem. Nunca pode entrar em regra automática.
        if ((a & (NtfsFileAttributes.RecallOnDataAccess | NtfsFileAttributes.RecallOnOpen)) != 0)
            f |= EntryFlags.CloudPlaceholder;

        return f;
    }

    /// <summary>
    /// Amarra pontas soltas: entradas cujo pai não existe mais (deleção em andamento,
    /// registro reciclado) são reancoradas na raiz e marcadas, em vez de sumirem do índice.
    /// </summary>
    private static void LinkOrphans(FileEntry[] entries)
    {
        int root = NtfsLayout.RootDirectoryRecord;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry e = ref entries[i];
            if (!e.IsInUse) continue;

            if (i == root)
            {
                e.ParentIndex = (uint)root;
                e.Flags |= EntryFlags.Directory;
                continue;
            }

            if (e.ParentIndex >= entries.Length || !entries[(int)e.ParentIndex].IsInUse)
            {
                e.ParentIndex = (uint)root;
                e.Flags |= EntryFlags.Orphan;
            }
        }
    }
}

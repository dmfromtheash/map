using System.Buffers.Binary;

namespace Map.Persistence;

/// <summary>
/// Reads only the bounded classic ZIP end record and fixed central-directory headers before
/// ZipArchive is allowed to materialize entry objects and names. ZIP64 is unnecessary inside the
/// project limits and is rejected at this trust boundary.
/// </summary>
internal static class MapProjectZipAdmission
{
    private const uint EndSignature = 0x06054b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const int EndRecordBytes = 22;
    private const int CentralHeaderBytes = 46;
    private const int MaxCommentBytes = ushort.MaxValue;

    public static void Validate(FileStream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new MapProjectCorruptedException("The project container is not seekable.");
        }

        if (stream.Length is < EndRecordBytes or > MapProjectFormat.MaxContainerFileBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The project file is outside the {MapProjectFormat.MaxContainerFileBytes}-byte container budget.");
        }

        var tailLength = (int)Math.Min(stream.Length, EndRecordBytes + MaxCommentBytes);
        var tail = GC.AllocateUninitializedArray<byte>(tailLength);
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);

        var endIndex = FindEndRecord(tail);
        if (endIndex < 0)
        {
            throw new MapProjectCorruptedException("The project has no canonical ZIP end record.");
        }

        var end = tail.AsSpan(endIndex);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(20, 2));
        if (endIndex + EndRecordBytes + commentLength != tail.Length)
        {
            throw new MapProjectCorruptedException("The ZIP end record length is inconsistent.");
        }

        var disk = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(4, 2));
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(6, 2));
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(8, 2));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(end.Slice(10, 2));
        var centralBytes = BinaryPrimitives.ReadUInt32LittleEndian(end.Slice(12, 4));
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end.Slice(16, 4));
        if (disk != 0 || centralDisk != 0 || entriesOnDisk != entryCount
            || entryCount == ushort.MaxValue
            || centralBytes == uint.MaxValue
            || centralOffset == uint.MaxValue)
        {
            throw new MapProjectResourceLimitException(
                "Multi-disk and ZIP64 containers are outside the map-project limits.");
        }

        if (entryCount > MapProjectFormat.MaxEntries
            || centralBytes > MapProjectFormat.MaxCentralDirectoryBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The ZIP central directory or its entries exceed the project metadata budget "
                + $"({entryCount}/{MapProjectFormat.MaxEntries} entries).");
        }

        var absoluteEnd = stream.Length - tailLength + endIndex;
        if ((long)centralOffset + centralBytes != absoluteEnd)
        {
            throw new MapProjectCorruptedException("The ZIP central-directory bounds are inconsistent.");
        }

        ScanCentralDirectory(stream, entryCount, centralOffset, centralBytes);
        stream.Position = 0;
    }

    private static int FindEndRecord(ReadOnlySpan<byte> tail)
    {
        for (var index = tail.Length - EndRecordBytes; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(index, 4)) == EndSignature
                && index + EndRecordBytes
                    + BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(index + 20, 2))
                    == tail.Length)
            {
                return index;
            }
        }

        return -1;
    }

    private static void ScanCentralDirectory(
        FileStream stream, int entryCount, long centralOffset, long centralBytes)
    {
        Span<byte> header = stackalloc byte[CentralHeaderBytes];
        var centralEnd = checked(centralOffset + centralBytes);
        long totalNameBytes = 0;
        stream.Position = centralOffset;
        for (var index = 0; index < entryCount; index++)
        {
            if (stream.Position > centralEnd - CentralHeaderBytes)
            {
                throw new MapProjectCorruptedException("The ZIP central directory is truncated.");
            }

            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != CentralHeaderSignature)
            {
                throw new MapProjectCorruptedException("The ZIP central directory has an invalid entry header.");
            }

            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
            var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30, 2));
            var commentBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
            if (nameBytes is 0 or > MapProjectFormat.MaxEntryNameBytes)
            {
                throw new MapProjectResourceLimitException(
                    "A ZIP entry name exceeds the project metadata budget.");
            }

            totalNameBytes = checked(totalNameBytes + nameBytes);
            if (totalNameBytes > MapProjectFormat.MaxEntryNamePayloadBytes)
            {
                throw new MapProjectResourceLimitException(
                    "ZIP entry names exceed the aggregate project metadata budget.");
            }

            var variableBytes = checked((long)nameBytes + extraBytes + commentBytes);
            if (stream.Position > centralEnd - variableBytes)
            {
                throw new MapProjectCorruptedException("The ZIP central-directory entry is truncated.");
            }

            stream.Position += variableBytes;
        }

        if (stream.Position != centralEnd)
        {
            throw new MapProjectCorruptedException(
                "The ZIP central-directory entry count does not match its encoded size.");
        }
    }
}

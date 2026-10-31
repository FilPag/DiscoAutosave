using BytePool = System.Buffers.ArrayPool<byte>;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;

namespace DiscoAutosave;

public sealed class FlatDialogueSnapshot : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static byte[]? reusableBuffer;
    private byte[]? buffer = Interlocked.Exchange(ref reusableBuffer, null) ?? BytePool.Shared.Rent(16 * 1024 * 1024);
    private int length;
    private int roots;
    internal int Size => length;
    internal int Capacity => buffer?.Length ?? 0;

    internal static void WarmUp() { using var snapshot = new FlatDialogueSnapshot(); }

    public void BeginRoot() => roots++;
    internal void Reset() { length = roots = 0; }
    internal void Append(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;
    }
    internal byte[] CopyFrom(int start) => buffer.AsSpan(start, length - start).ToArray();
    public void Byte(byte value) { Ensure(1); buffer![length++] = value; }
    public void Int(int value) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length), value); length += 4; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolean(bool value)
    {
        Ensure(2);
        buffer![length++] = (byte)'B';
        buffer[length++] = value ? (byte)1 : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNumber(double value)
    {
        Ensure(9);
        buffer![length++] = (byte)'N';
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(length), BitConverter.DoubleToInt64Bits(value));
        length += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteText(ReadOnlySpan<char> value)
    {
        int size = checked(value.Length * 2);
        Ensure(checked(5 + size));
        buffer![length++] = (byte)'S';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length), value.Length);
        length += 4;
        MemoryMarshal.AsBytes(value).CopyTo(buffer.AsSpan(length));
        length += size;
    }

    public void BeginTable(int items)
    {
        Ensure(5);
        buffer![length++] = (byte)'T';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length), items);
        length += 4;
    }

    public void BeginDictionaryTable(int pairs)
    {
        Ensure(9);
        buffer![length++] = (byte)'T';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length + 4), pairs);
        length += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int count)
    {
        if (buffer != null && count <= buffer.Length - length) return;
        Grow(count);
    }

    private void Grow(int count)
    {
        if (buffer == null) throw new ObjectDisposedException(nameof(FlatDialogueSnapshot));
        int required = checked(length + count);
        if (required <= buffer!.Length) return;
        if (required > 128 * 1024 * 1024) throw new InvalidDataException("Flat dialogue snapshot exceeds size limit.");
        var next = BytePool.Shared.Rent(Math.Max(required, buffer.Length * 2));
        buffer.AsSpan(0, length).CopyTo(next);
        BytePool.Shared.Return(buffer);
        buffer = next;
    }

    public byte[] Encode(byte[] tail)
    {
        using var output = new MemoryStream();
        WriteTo(output, tail);
        return output.ToArray();
    }

    public void WriteTo(Stream output, byte[] tail)
    {
        if (buffer == null) throw new ObjectDisposedException(nameof(FlatDialogueSnapshot));
        using var writer = new BinaryWriter(output, Utf8, true);
        byte[] scratch = BytePool.Shared.Rent(4096);
        try
        {
            int position = 0;
            for (int i = 0; i < roots; i++) EncodeValue(writer, ref position, 0, ref scratch);
            if (position != length) throw new InvalidDataException("Flat dialogue snapshot has trailing tokens.");
            writer.Write(tail);
            writer.Flush();
        }
        finally { BytePool.Shared.Return(scratch); }
    }

    private void EncodeValue(BinaryWriter writer, ref int position, int depth, ref byte[] scratch)
    {
        if (depth > 256 || position >= length) throw new InvalidDataException("Invalid flat dialogue snapshot.");
        byte tag = buffer![position++];
        writer.Write(tag);
        switch ((char)tag)
        {
            case 'X': return;
            case 'B': writer.Write(Take(ref position, 1)); return;
            case 'N': writer.Write(Take(ref position, 8)); return;
            case 'S':
                int chars = ReadInt(ref position);
                var text = MemoryMarshal.Cast<byte, char>(Take(ref position, checked(chars * 2)));
                int bytes = Utf8.GetByteCount(text);
                if (bytes > scratch.Length)
                {
                    byte[] next = BytePool.Shared.Rent(bytes);
                    BytePool.Shared.Return(scratch);
                    scratch = next;
                }
                writer.Write7BitEncodedInt(bytes);
                Utf8.GetBytes(text, scratch);
                writer.Write(scratch.AsSpan(0, bytes));
                return;
            case 'T':
                int items = ReadInt(ref position);
                writer.Write(items);
                for (int i = 0; i < items; i++) EncodeValue(writer, ref position, depth + 1, ref scratch);
                int pairs = ReadInt(ref position);
                writer.Write(pairs);
                for (int i = 0; i < pairs; i++)
                {
                    EncodeValue(writer, ref position, depth + 1, ref scratch);
                    EncodeValue(writer, ref position, depth + 1, ref scratch);
                }
                return;
            default: throw new InvalidDataException("Unknown flat dialogue token.");
        }
    }

    private int ReadInt(ref int position)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(Take(ref position, 4));
        if (value < 0) throw new InvalidDataException("Negative flat dialogue count.");
        return value;
    }

    private ReadOnlySpan<byte> Take(ref int position, int count)
    {
        if (count < 0 || count > length - position) throw new InvalidDataException("Truncated flat dialogue snapshot.");
        var result = buffer.AsSpan(position, count);
        position += count;
        return result;
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref buffer, null);
        if (owned == null) return;
        // The producer and disposing worker have different ArrayPool thread-local caches.
        var previous = Interlocked.Exchange(ref reusableBuffer, owned);
        if (previous != null) BytePool.Shared.Return(previous);
    }
}

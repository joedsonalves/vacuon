namespace Vacuon.Core.Index;

/// <summary>
/// Todos os nomes de arquivo em um único buffer de <see cref="char"/>.
/// <para>
/// Cada <see cref="FileEntry"/> guarda só (offset, length). Um milhão de objetos
/// <c>string</c> custaria ~40 MB só de cabeçalho de objeto, além de fragmentar o heap.
/// </para>
/// </summary>
public sealed class NameBlob
{
    private char[] _buffer;
    private int _length;

    public NameBlob(int initialCapacity = 1 << 20)
    {
        _buffer = new char[initialCapacity];
    }

    internal NameBlob(char[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public int Length => _length;
    public ReadOnlySpan<char> Buffer => _buffer.AsSpan(0, _length);
    internal char[] RawBuffer => _buffer;

    public int Append(ReadOnlySpan<char> name)
    {
        EnsureCapacity(_length + name.Length);
        int offset = _length;
        name.CopyTo(_buffer.AsSpan(offset));
        _length += name.Length;
        return offset;
    }

    public ReadOnlySpan<char> Get(int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > _length) return default;
        return _buffer.AsSpan(offset, length);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;

        int capacity = _buffer.Length;
        while (capacity < required) capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }
}

using System;
using System.IO;
namespace Promul
{
    public class CompositeReader : BinaryReader
    {
        private readonly ArraySegment<byte> _data;
        private readonly MemoryStream _ms;

        private CompositeReader(MemoryStream memory, ArraySegment<byte> data) : base(memory)
        {
            _ms = memory;
            _data = data;
        }

        public ArraySegment<byte> ReadRemainingBytes()
        {
            return new ArraySegment<byte>(_data.Array, _data.Offset + (int)_ms.Position,
                (int)(_data.Count - _ms.Position));
        }

        internal static CompositeReader Create(ArraySegment<byte> data)
        {
            return new CompositeReader(new MemoryStream(data.Array, data.Offset, data.Count), data);
        }

        public static implicit operator ArraySegment<byte>(CompositeReader cmpw)
        {
            return cmpw._data;
        }
    }
}
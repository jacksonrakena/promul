using System;
using System.IO;
using System.Text;

namespace Promul.Common.Networking.Data
{
    public class CompositeReader : BinaryReader
    {
        readonly MemoryStream _ms;
        private CompositeReader(MemoryStream memory) : base(memory)
        {
            _ms = memory;
        }

        public ArraySegment<byte> ReadRemainingBytes()
        {
            return new ArraySegment<byte>(_ms.GetBuffer(), (int)_ms.Position, (int)(_ms.Capacity - _ms.Position));
        }

        public static CompositeReader Create()
        {
            return new CompositeReader(new MemoryStream());
        }

        public static implicit operator ArraySegment<byte>(CompositeReader cmpw)
        {
            return cmpw._ms.GetBuffer();
        }
    }
}
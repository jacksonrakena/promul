﻿using System;
using System.IO;
namespace Promul.Common.Networking.Data
{
    public class CompositeWriter : BinaryWriter
    {
        readonly MemoryStream _ms;
        private CompositeWriter(MemoryStream memory) : base(memory)
        {
            _ms = memory;
        }

        public static CompositeWriter Create()
        {
            return new CompositeWriter(new MemoryStream());
        }

        public static implicit operator ArraySegment<byte>(CompositeWriter cmpw)
        {
            return cmpw._ms.GetBuffer();
        }
    }
}
using System.Collections.Generic;
using System.Net;

namespace Promul.Common.Networking
{
    class IPEndPointComparer : IEqualityComparer<IPEndPoint>
    {
        public bool Equals(IPEndPoint x, IPEndPoint y)
        {
            return x.Address.Equals(y.Address) && x.Port == y.Port;
        }

        public int GetHashCode(IPEndPoint obj)
        {
            return obj.GetHashCode();
        }
    }
}
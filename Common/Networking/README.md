# Promul Networking

Promul's included networking solution is a very heavily modified version of the amazing [LibNetLib](https://github.com/RevenantX/LiteNetLib) by Ruslan Pyrch.  
  
Promul is *probably* compatible with LibNetLib clients and servers. I don't know, and I don't check.  
Compatibility with LibNetLib is not a goal of Promul.

### Differences from LibNetLib
Among other differences:
- The library requires .NET Standard 2.1.
  - This limits the Unity versions that can use the library, but also
allows the library to use newer constructs such as `ArraySegment<T>`.
- The `NetDataReader` and `NetDataWriter` classes have been removed in favour of `BinaryReader` and `BinaryWriter` from `System.IO`.
  - This means support for `INetSerializable` has been removed. Clients are expected to create extension overloads for `BinaryWriter.Write` and `BinaryReader.Read{Type}`.
- The logic and receive threads have been removed, in favour of a long-running `Task` spawned by the `NetManager`.
- Most of the library's blocking methods have been replaced with Task-based Asynchronous Programming (TAP) methods, using `Task`. This requires
a significant rewrite of most parts of the library.
- NAT punch-through and manual mode have both been removed.
  - The transport will now put transport events onto a queue,
and `NetworkTransport#PollEvents` will dequeue events and pass them to Unity Netcode.
- `NetManager#Start` has been replaced by `PromulManager#ListenAsync`, which will begin infinitely looping, waiting for incoming messages.
  - This method will block, so in the Unity transport, it is spawned on an un-awaited `Task`.
  - Servers are expected to use a paradigm such as `BackgroundService` to run the long-running `Task`.
- Documentation has been improved.
- `NetPacket` no longer has jurisdiction over its data. It is a wrapper around an `ArraySegment<byte>`.
- Most (if not all) usages of `byte[] data, int offset, int count` have been replaced with `ArraySegment<byte>`.
- `INetBasedListener` and related classes have been removed. All events
are invoked on the `PromulManager` class as
regular CLR `event` types.
- In many classes, extraneous overloads have been removed in favour of default parameters.
### Licenses
Promul and its networking library is &copy; 2023 Firework Eyes Studio (NZBN 9429048922678) under the MIT License.
```
MIT License

Copyright (c) 2023 Jackson Rakena

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
LibNetLib is copyright (c) 2020 Ruslan Pyrch.  
```
MIT License

Copyright (c) 2020 Ruslan Pyrch

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
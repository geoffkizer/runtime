// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net
{
    internal sealed class SmartWriteBuffer : IDisposable
    {
        private readonly Stream _stream;
        private byte[]? _writeBuffer;
        private int _writeBufferCapacity;
        private int _writeLength;

        public const int DefaultWriteBufferCapacity = 4096;

        public SmartWriteBuffer(Stream stream, int writeBufferCapacity = DefaultWriteBufferCapacity)
        {
            if (!stream.CanWrite)
            {
                throw new ArgumentException(nameof(stream));
            }

            if (writeBufferCapacity < 0)
            {
                throw new ArgumentException(nameof(writeBufferCapacity));
            }

            _stream = stream;
            _writeBuffer = (writeBufferCapacity == 0 ? null : ArrayPool<byte>.Shared.Rent(writeBufferCapacity));
            _writeBufferCapacity = writeBufferCapacity;
            _writeLength = 0;
        }

        public Memory<byte> WriteBuffer => new Memory<byte>(_writeBuffer, _writeLength, _writeBufferCapacity - _writeLength);

        public int WriteBufferCapacity => _writeBufferCapacity;

        public bool IsWriteBufferFull => _writeLength == _writeBufferCapacity;

        private ReadOnlyMemory<byte> BufferedWriteBytes => new ReadOnlyMemory<byte>(_writeBuffer, 0, _writeLength);

        public async ValueTask WriteFromBufferAsync(CancellationToken cancellationToken = default)
        {
            if (_writeLength > 0)
            {
                await _stream.WriteAsync(BufferedWriteBytes, cancellationToken).ConfigureAwait(false);

                _writeLength = 0;
            }
        }

        public void WriteFromBuffer()
        {
            if (_writeLength > 0)
            {
                _stream.Write(BufferedWriteBytes.Span);

                _writeLength = 0;
            }
        }

        public void Advance(int bytesToAdvance)
        {
            if (bytesToAdvance < 0 || bytesToAdvance > WriteBuffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bytesToAdvance));
            }

            _writeLength += bytesToAdvance;
        }

        public void SetWriteBufferCapacity(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentException(nameof(capacity));
            }

            if (capacity < _writeLength)
            {
                throw new InvalidOperationException("new buffer capacity can't hold existing buffered data");
            }

            if (capacity != _writeBufferCapacity)
            {
                if (capacity == 0)
                {
                    Debug.Assert(_writeBuffer is not null);
                    ArrayPool<byte>.Shared.Return(_writeBuffer!);
                    _writeBuffer = null;
                }
                else
                {
                    byte[] newWriteBuffer = ArrayPool<byte>.Shared.Rent(capacity);
                    if (_writeLength != 0)
                    {
                        BufferedWriteBytes.CopyTo(newWriteBuffer);
                    }
                    _writeBuffer = newWriteBuffer;
                }

                _writeBufferCapacity = capacity;
            }
        }

        private int WriteIntoBuffer(ReadOnlySpan<byte> buffer)
        {
            Span<byte> writeBuffer = WriteBuffer.Span;

            int bytesToWrite = Math.Min(writeBuffer.Length, buffer.Length);
            buffer.Slice(0, bytesToWrite).CopyTo(writeBuffer);
            Advance(bytesToWrite);

            return bytesToWrite;
        }

        public void Dispose()
        {
            if (_writeBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_writeBuffer);
                _writeBuffer = null;
                _writeLength = 0;
            }
        }
    }
}

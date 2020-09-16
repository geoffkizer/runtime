// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal sealed class SmartWriteBufferStream : HttpBaseStream
    {
        private readonly Stream _innerStream;
        private byte[]? _writeBuffer;
        private int _writeBufferCapacity;
        private int _writeLength;

        public const int DefaultWriteBufferCapacity = 4096;

        public SmartWriteBufferStream(Stream innerStream, int writeBufferCapacity = DefaultWriteBufferCapacity)
        {
            if (!innerStream.CanWrite)
            {
                throw new ArgumentException(nameof(innerStream));
            }

            if (writeBufferCapacity < 0)
            {
                throw new ArgumentException(nameof(writeBufferCapacity));
            }

            _innerStream = innerStream;
            _writeBuffer = (writeBufferCapacity == 0 ? null : ArrayPool<byte>.Shared.Rent(writeBufferCapacity));
            _writeBufferCapacity = writeBufferCapacity;
            _writeLength = 0;
        }

        public override bool CanRead => false;
        public override bool CanWrite => true;

        public Memory<byte> WriteBuffer => new Memory<byte>(_writeBuffer, _writeLength, _writeBufferCapacity - _writeLength);

        public int WriteBufferCapacity => _writeBufferCapacity;

        public bool IsWriteBufferFull => _writeLength == _writeBufferCapacity;

        private ReadOnlyMemory<byte> BufferedWriteBytes => new ReadOnlyMemory<byte>(_writeBuffer, 0, _writeLength);

        public async ValueTask WriteFromBufferAsync(CancellationToken cancellationToken = default)
        {
            if (_writeLength > 0)
            {
                await _innerStream.WriteAsync(BufferedWriteBytes, cancellationToken).ConfigureAwait(false);

                _writeLength = 0;
            }
        }

        public void WriteFromBuffer()
        {
            if (_writeLength > 0)
            {
                _innerStream.Write(BufferedWriteBytes.Span);

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

        // The general idea for write should be as follows:
        // If there's data in the buffer currently, then write as much of the data as we can into the buffer.
        // If this handles all the data, we're done.
        // If not, then we need to flush the buffer and then continue.
        // If the remaining data fits in the buffer, then just put it in the buffer.
        // Otherwise, write it directly to the underlying stream.

        private int WriteIntoBuffer(ReadOnlySpan<byte> buffer)
        {
            Span<byte> writeBuffer = WriteBuffer.Span;

            int bytesToWrite = Math.Min(writeBuffer.Length, buffer.Length);
            buffer.Slice(0, bytesToWrite).CopyTo(writeBuffer);
            Advance(bytesToWrite);

            return bytesToWrite;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public override int Read(Span<byte> buffer)
        {
            throw new InvalidOperationException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_writeBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_writeBuffer);
                    _writeBuffer = null;
                    _writeLength = 0;
                }

                _innerStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

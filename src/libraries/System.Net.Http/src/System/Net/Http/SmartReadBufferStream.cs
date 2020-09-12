// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    // CONSIDER: Might simply to keep start/end instead of start/length
    // CONSIDER: Calls to Read/ReadAsync with small buffers should read into read buffer instead
    internal sealed class SmartReadBufferStream : HttpBaseStream
    {
        private readonly Stream _innerStream;
        private byte[]? _readBuffer;
        private int _readBufferSize;
        private int _readStart;
        private int _readLength;

        public const int DefaultReadBufferSize = 4096;

        public SmartReadBufferStream(Stream innerStream, int readBufferSize = DefaultReadBufferSize)
        {
            if (!innerStream.CanRead)
            {
                throw new InvalidOperationException("Can't read");
            }

            if (readBufferSize < 0)
            {
                throw new ArgumentException(nameof(readBufferSize));
            }

            _innerStream = innerStream;
            _readBuffer = (readBufferSize == 0 ? null : ArrayPool<byte>.Shared.Rent(readBufferSize));
            _readBufferSize = readBufferSize;
            _readStart = 0;
            _readLength = 0;
        }

        public override bool CanRead => true;
        public override bool CanWrite => false;

        public ReadOnlyMemory<byte> ReadBuffer => new ReadOnlyMemory<byte>(_readBuffer, _readStart, _readLength);

        // CONSIDER: Perhaps change to ReadBufferCapacity?
        public int ReadBufferSize => _readBufferSize;

        public bool IsReadBufferFull => _readLength == _readBufferSize;

        private Memory<byte> AdjustBufferForRead()
        {
            Debug.Assert(_readStart >= 0, $"_readStart={_readStart}??");
            Debug.Assert(_readStart <= _readBufferSize, $"_readStart={_readStart}, beyond _readBufferSize={_readBufferSize}");
            Debug.Assert(_readLength >= 0, $"_readLength={_readLength}??");
            Debug.Assert(_readStart + _readLength <= _readBufferSize, $"_readStart={_readStart}, _readLength={_readLength}, beyond _readBufferSize={_readBufferSize}");

            if (_readStart != 0)
            {
                if (_readLength != 0)
                {
                    // Shift remaining buffered bytes down to maximize read space.
                    ReadBuffer.CopyTo(_readBuffer);
                }

                _readStart = 0;
            }
            else if (_readLength == _readBufferSize)
            {
                throw new InvalidOperationException("buffer already full");
            }

            return new Memory<byte>(_readBuffer, _readLength, _readBufferSize - _readLength);
        }

        public async ValueTask<bool> ReadIntoBufferAsync()
        {
            Memory<byte> availableReadBuffer = AdjustBufferForRead();

            int bytesRead = await _innerStream.ReadAsync(availableReadBuffer).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return false;
            }

            _readLength += bytesRead;
            return true;
        }

        public bool ReadIntoBuffer()
        {
            Memory<byte> availableReadBuffer = AdjustBufferForRead();

            int bytesRead = _innerStream.Read(availableReadBuffer.Span);
            if (bytesRead == 0)
            {
                return false;
            }

            _readLength += bytesRead;
            return true;
        }

        public void Consume(int bytesConsumed)
        {
            if (bytesConsumed < 0)
            {
                throw new ArgumentException(nameof(bytesConsumed));
            }

            if (bytesConsumed > _readLength)
            {
                throw new InvalidOperationException("consume more than buffer size??");
            }

            _readStart += bytesConsumed;
            _readLength -= bytesConsumed;
        }

        public void SetReadBufferSize(int size)
        {
            if (size < 0)
            {
                throw new ArgumentException(nameof(size));
            }

            if (size < _readLength)
            {
                throw new InvalidOperationException("new buffer size can't hold existing buffered data");
            }

            if (size == 0)
            {
                _readBuffer = null;
            }
            else
            {
                byte[] newReadBuffer = ArrayPool<byte>.Shared.Rent(size);
                if (_readLength != 0)
                {
                    ReadBuffer.CopyTo(newReadBuffer);
                }
                _readBuffer = newReadBuffer;
            }

            _readBufferSize = size;
            _readStart = 0;
        }

        private int TryReadFromBuffer(Span<byte> buffer)
        {
            ReadOnlySpan<byte> readBuffer = ReadBuffer.Span;
            if (readBuffer.Length == 0)
            {
                return 0;
            }

            int bytesToRead = Math.Min(readBuffer.Length, buffer.Length);
            readBuffer.Slice(0, bytesToRead).CopyTo(buffer);
            Consume(bytesToRead);

            return bytesToRead;
        }

        public override int Read(Span<byte> buffer)
        {
            int bytesRead = TryReadFromBuffer(buffer);
            if (bytesRead > 0)
            {
                return bytesRead;
            }

            return _innerStream.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int bytesRead = TryReadFromBuffer(buffer.Span);
            if (bytesRead > 0)
            {
                return new ValueTask<int>(bytesRead);
            }

            return _innerStream.ReadAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("can't write");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_readBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_readBuffer);
                    _readBuffer = null;
                }

                _innerStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal sealed class SmartReadBufferStream : HttpBaseStream
    {
        private readonly Stream _innerStream;
        private byte[]? _readBuffer;
        private int _readBufferCapacity;
        private int _readStart;
        private int _readLength;

        public const int DefaultReadBufferCapacity = 4096;

        public SmartReadBufferStream(Stream innerStream, int readBufferCapacity = DefaultReadBufferCapacity)
        {
            if (!innerStream.CanRead)
            {
                throw new ArgumentException(nameof(innerStream));
            }

            if (readBufferCapacity < 0)
            {
                throw new ArgumentException(nameof(readBufferCapacity));
            }

            _innerStream = innerStream;
            _readBuffer = (readBufferCapacity == 0 ? null : ArrayPool<byte>.Shared.Rent(readBufferCapacity));
            _readBufferCapacity = readBufferCapacity;
            _readStart = 0;
            _readLength = 0;
        }

        public override bool CanRead => true;
        public override bool CanWrite => false;

        public ReadOnlyMemory<byte> ReadBuffer => new ReadOnlyMemory<byte>(_readBuffer, _readStart, _readLength);

        public int ReadBufferCapacity => _readBufferCapacity;

        public bool IsReadBufferFull => _readLength == _readBufferCapacity;

        private Memory<byte> AdjustBufferForRead()
        {
            Debug.Assert(_readStart >= 0, $"_readStart={_readStart}??");
            Debug.Assert(_readStart <= _readBufferCapacity, $"_readStart={_readStart}, beyond _readBufferSize={_readBufferCapacity}");
            Debug.Assert(_readLength >= 0, $"_readLength={_readLength}??");
            Debug.Assert(_readStart + _readLength <= _readBufferCapacity, $"_readStart={_readStart}, _readLength={_readLength}, beyond _readBufferSize={_readBufferCapacity}");

            if (_readStart != 0)
            {
                if (_readLength != 0)
                {
                    // Shift remaining buffered bytes down to maximize read space.
                    ReadBuffer.CopyTo(_readBuffer);
                }

                _readStart = 0;
            }
            else if (_readLength == _readBufferCapacity)
            {
                throw new InvalidOperationException("buffer already full");
            }

            return new Memory<byte>(_readBuffer, _readLength, _readBufferCapacity - _readLength);
        }

        public async ValueTask<bool> ReadIntoBufferAsync(CancellationToken cancellationToken = default)
        {
            Memory<byte> availableReadBuffer = AdjustBufferForRead();

            int bytesRead = await _innerStream.ReadAsync(availableReadBuffer, cancellationToken).ConfigureAwait(false);
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

        public void Consume(int bytesToConsume)
        {
            if (bytesToConsume < 0 || bytesToConsume > _readLength)
            {
                throw new ArgumentOutOfRangeException(nameof(bytesToConsume));
            }

            _readStart += bytesToConsume;
            _readLength -= bytesToConsume;
        }

        public void SetReadBufferCapacity(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentException(nameof(capacity));
            }

            if (capacity < _readLength)
            {
                throw new InvalidOperationException("new buffer capacity can't hold existing buffered data");
            }

            if (capacity == 0)
            {
                _readBuffer = null;
            }
            else
            {
                byte[] newReadBuffer = ArrayPool<byte>.Shared.Rent(capacity);
                if (_readLength != 0)
                {
                    ReadBuffer.CopyTo(newReadBuffer);
                }
                _readBuffer = newReadBuffer;
            }

            _readBufferCapacity = capacity;
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

            if (buffer.Length < _readBufferCapacity)
            {
                if (ReadIntoBuffer())
                {
                    bytesRead = TryReadFromBuffer(buffer);
                    Debug.Assert(bytesRead > 0);
                    return bytesRead;
                }
                else
                {
                    return 0;
                }
            }

            return _innerStream.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int bytesRead = TryReadFromBuffer(buffer.Span);
            if (bytesRead > 0)
            {
                return bytesRead;
            }

            if (buffer.Length < _readBufferCapacity)
            {
                if (await ReadIntoBufferAsync(cancellationToken).ConfigureAwait(false))
                {
                    bytesRead = TryReadFromBuffer(buffer.Span);
                    Debug.Assert(bytesRead > 0);
                    return bytesRead;
                }
                else
                {
                    return 0;
                }
            }

            return await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
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

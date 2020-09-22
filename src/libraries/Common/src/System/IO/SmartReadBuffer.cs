// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net
{
    internal sealed class SmartReadBuffer
    {
        private readonly Stream _innerStream;
        private byte[]? _readBuffer;
        private int _readBufferCapacity;
        private int _readStart;
        private int _readLength;

        public const int DefaultReadBufferCapacity = 4096;

        public SmartReadBuffer(Stream innerStream, int readBufferCapacity = DefaultReadBufferCapacity)
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

        public async ValueTask<bool> ReadAtLeastAsync(int bytesNeeded, CancellationToken cancellationToken = default)
        {
            if (ReadBuffer.Length >= bytesNeeded)
            {
                return true;
            }

            if (bytesNeeded > ReadBufferCapacity)
            {
                throw new InvalidOperationException("buffer capacity too small for requested bytes");
            }

            do
            {
                if (!await ReadIntoBufferAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            while (ReadBuffer.Length < bytesNeeded);

            return true;
        }

        public bool ReadAtLeast(int bytesNeeded)
        {
            if (ReadBuffer.Length >= bytesNeeded)
            {
                return true;
            }

            if (bytesNeeded > ReadBufferCapacity)
            {
                throw new InvalidOperationException("buffer capacity too small for requested bytes");
            }

            do
            {
                if (!ReadIntoBuffer())
                {
                    return false;
                }
            }
            while (ReadBuffer.Length < bytesNeeded);

            return true;
        }

        public async ValueTask<int> ReadUntilAsync(byte b, CancellationToken cancellationToken = default)
        {
            int scanned = 0;
            while (true)
            {
                int index = ReadBuffer.Span.Slice(scanned).IndexOf(b);
                if (index != -1)
                {
                    return scanned + index;
                }

                if (IsReadBufferFull || !await ReadIntoBufferAsync(cancellationToken).ConfigureAwait(false))
                {
                    return -1;
                }

                scanned = ReadBuffer.Length;
            }
        }

        public int ReadUntil(byte b)
        {
            int scanned = 0;
            while (true)
            {
                int index = ReadBuffer.Span.Slice(scanned).IndexOf(b);
                if (index != -1)
                {
                    return scanned + index;
                }

                if (IsReadBufferFull || !ReadIntoBuffer())
                {
                    return -1;
                }

                scanned = ReadBuffer.Length;
            }
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

            if (capacity != _readBufferCapacity)
            {
                if (capacity == 0)
                {
                    Debug.Assert(_readBuffer is not null);
                    ArrayPool<byte>.Shared.Return(_readBuffer!);
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
        }

        public void Dispose()
        {
            if (_readBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_readBuffer);
                _readBuffer = null;
                _readStart = 0;
                _readLength = 0;
            }
        }
    }
}

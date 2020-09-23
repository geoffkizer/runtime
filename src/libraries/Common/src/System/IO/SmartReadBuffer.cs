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

        /// <summary>
        /// Read more data into the read buffer from the underlying Stream.
        /// </summary>
        /// <returns>True on success; false if no data could be read because the underlying Stream is at EOF.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public async ValueTask<bool> FillAsync(CancellationToken cancellationToken = default)
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

        /// <summary>
        /// Read more data into the read buffer from the underlying Stream.
        /// </summary>
        /// <returns>True on success; false if no data could be read because the underlying Stream is at EOF.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public bool Fill()
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

        /// <summary>
        /// Read into the buffer until it contains at least the specified number of bytes.
        /// If the buffer already contains at least the specified number of bytes, this does nothing.
        /// </summary>
        /// <returns>True on success; false there was not enough data because the underlying Stream is at EOF.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public async ValueTask<bool> FillToAsync(int bytesNeeded, CancellationToken cancellationToken = default)
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
                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            while (ReadBuffer.Length < bytesNeeded);

            return true;
        }

        /// <summary>
        /// Read into the buffer until it contains at least the specified number of bytes.
        /// If the buffer already contains at least the specified number of bytes, this does nothing.
        /// </summary>
        /// <returns>True on success; false there was not enough data because the underlying Stream is at EOF.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public bool FillTo(int bytesNeeded)
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
                if (!Fill())
                {
                    return false;
                }
            }
            while (ReadBuffer.Length < bytesNeeded);

            return true;
        }

        /// <summary>
        /// Read into the buffer until the specified byte is found.
        /// </summary>
        /// <returns>On success, the index of the specified byte within the buffer. If the underlying stream is at EOF and the buffer does not contain the specified byte, returns -1.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public async ValueTask<int> FillUntilAsync(byte b, CancellationToken cancellationToken = default)
        {
            int scanned = 0;
            while (true)
            {
                int index = ReadBuffer.Span.Slice(scanned).IndexOf(b);
                if (index != -1)
                {
                    return scanned + index;
                }

                if (IsReadBufferFull || !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return -1;
                }

                scanned = ReadBuffer.Length;
            }
        }

        /// <summary>
        /// Read into the buffer until the specified byte is found.
        /// </summary>
        /// <returns>On success, the index of the specified byte within the buffer. If the underlying stream is at EOF and the buffer does not contain the specified byte, returns -1.</returns>
        /// <exception cref="InvalidOperationException">No more room in the buffer.</exception>
        public int FillUntil(byte b)
        {
            int scanned = 0;
            while (true)
            {
                int index = ReadBuffer.Span.Slice(scanned).IndexOf(b);
                if (index != -1)
                {
                    return scanned + index;
                }

                if (IsReadBufferFull || !Fill())
                {
                    return -1;
                }

                scanned = ReadBuffer.Length;
            }
        }

        /// <summary>
        /// Consume bytes from the buffer that are no longer needed.
        /// </summary>
        public void Consume(int bytesToConsume)
        {
            if (bytesToConsume < 0 || bytesToConsume > _readLength)
            {
                throw new ArgumentOutOfRangeException(nameof(bytesToConsume));
            }

            _readStart += bytesToConsume;
            _readLength -= bytesToConsume;
        }

        /// <summary>
        /// Set the read buffer capacity.
        /// </summary>
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

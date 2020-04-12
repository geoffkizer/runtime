using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;

namespace System.Net.Quic.Implementations.Managed.Internal.Buffers
{
    /// <summary>
    ///     Structure for containing outbound stream data, represents sending direction of the stream.
    /// </summary>
    internal sealed class OutboundBuffer
    {
        /// <summary>
        ///     Ranges of bytes acked by the peer.
        /// </summary>
        private readonly RangeSet _acked = new RangeSet();

        /// <summary>
        ///     Ranges of bytes currently in-flight.
        /// </summary>
        private readonly RangeSet _checkedOut = new RangeSet();

        /// <summary>
        ///     Ranges of bytes awaiting to be sent.
        /// </summary>
        private readonly RangeSet _pending = new RangeSet();

        /// <summary>
        ///     Channel of incoming chunks of memory from the user.
        /// </summary>
        private readonly Channel<StreamChunk> _boundaryChannel =
            Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions
            {
                SingleReader = true, SingleWriter = true
            });

        /// <summary>
        ///     Individual chunks of the stream to be sent.
        /// </summary>
        private readonly List<StreamChunk> _chunks = new List<StreamChunk>();

        /// <summary>
        ///     Number of bytes dequeued from the _boundaryChannel.
        /// </summary>
        private long _dequedBytes;

        public OutboundBuffer(long maxData)
        {
            UpdateMaxData(maxData);
        }

        /// <summary>
        ///     Total number of bytes written into this stream.
        /// </summary>
        internal long WrittenBytes { get; private set; }

        /// <summary>
        ///     Number of bytes present in <see cref="_boundaryChannel" />
        /// </summary>
        private long BytesInChannel => WrittenBytes - _dequedBytes;

        /// <summary>
        ///     Number of contiguous bytes that were acknowledged by the peer.
        /// </summary>
        internal long SentBytes => _acked.Count > 0 ? _acked[0].End + 1 : 0;

        /// <summary>
        ///     Total number of bytes allowed to transport in this stream.
        /// </summary>
        internal long MaxData { get; private set; }

        /// <summary>
        ///     True if the stream is closed for further writing (no more data can be added).
        /// </summary>
        internal bool SizeKnown { get; private set; }

        /// <summary>
        ///     True if all data has been transmitted and acknowledged.
        /// </summary>
        internal bool Finished => SizeKnown && SentBytes == WrittenBytes;

        /// <summary>
        ///     Returns true if buffer contains any sendable data below <see cref="MaxData" /> limit.
        /// </summary>
        // TODO-RZ: this is not threadsafe
        internal bool IsFlushable => _pending.Count > 0 && _pending[0].Start < MaxData ||
                                     _dequedBytes < MaxData && BytesInChannel > 0;

        /// <summary>
        ///     True if there is data that has not been confirmed received.
        /// </summary>
        internal bool HasUnackedData => _pending.Count + _checkedOut.Count != 0;

        /// <summary>
        ///     Updates the <see cref="MaxData" /> parameter.
        /// </summary>
        /// <param name="value">Value of the parameter.</param>
        internal void UpdateMaxData(long value)
        {
            MaxData = Math.Max(MaxData, value);
        }

        /// <summary>
        ///     Copies given memory to the outbound stream to be sent.
        /// </summary>
        /// <param name="data">Data to be sent.</param>
        internal void Enqueue(ReadOnlySpan<byte> data)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(buffer);

            EnqueueInternal(buffer.AsMemory(0, data.Length), buffer);
        }

        /// <summary>
        ///     Schedules given memory to the outbound stream to be sent. The memory must be unchanged until it is acked.
        /// </summary>
        /// <param name="data">Memory to be sent.</param>
        internal void Enqueue(ReadOnlyMemory<byte> data)
        {
            // EnqueueInternal(data, null);
            throw new NotImplementedException("zero-copy sending not implemented");
        }

        private void EnqueueInternal(ReadOnlyMemory<byte> memory, byte[] buffer)
        {
            Debug.Assert(!SizeKnown, "Trying to add data to finished OutboundBuffer");

            long offset = WrittenBytes;

            _boundaryChannel.Writer.TryWrite(new StreamChunk(offset, memory, buffer));

            WrittenBytes += memory.Length;
        }

        internal void DrainIncomingChunks()
        {
            var reader = _boundaryChannel.Reader;
            while (reader.TryRead(out var chunk))
            {
                Debug.Assert(_dequedBytes == chunk.StreamOffset);
                _pending.Add(chunk.StreamOffset, chunk.StreamOffset + chunk.Length - 1);
                _chunks.Add(chunk);
                _dequedBytes += chunk.Length;
            }
        }

        /// <summary>
        ///     Returns length of the next contiguous range of data that can be checked out, respecting the
        ///     <see cref="BufferBase.MaxData" /> parameter.
        /// </summary>
        /// <returns></returns>
        internal (long offset, long count) GetNextSendableRange()
        {
            DrainIncomingChunks();
            if (_pending.Count == 0) return (WrittenBytes, 0);

            long sendableLength = MaxData - _pending[0].Start;
            long count = Math.Min(sendableLength, _pending[0].Length);
            Debug.Assert(count > 0);
            return (_pending[0].Start, count);
        }

        /// <summary>
        ///     Reads data from the stream into provided span.
        /// </summary>
        /// <param name="destination">Destination memory for the data.</param>
        internal void CheckOut(Span<byte> destination)
        {
            if (destination.IsEmpty)
                return;

            Debug.Assert(destination.Length <= GetNextSendableRange().count);

            long start = _pending.GetMin();
            long end = start + destination.Length - 1;

            _pending.Remove(start, end);
            _checkedOut.Add(start, end);

            int copied = 0;
            int i = _chunks.FindIndex(c => c.StreamOffset + c.Length >= start);
            while (copied < destination.Length)
            {
                int inChunkStart = (int)(start - _chunks[i].StreamOffset) + copied;
                int inChunkCount = Math.Min((int)_chunks[i].Length - inChunkStart, destination.Length - copied);
                _chunks[i].Memory.Span.Slice(inChunkStart, inChunkCount).CopyTo(destination.Slice(copied));

                copied += inChunkCount;
                i++;
            }
        }

        /// <summary>
        ///     Marks the stream as finished, no more data can be added to the stream.
        /// </summary>
        internal void MarkEndOfData()
        {
            SizeKnown = true;
        }

        /// <summary>
        ///     Called to inform the buffer that transmission of given range was not successful.
        /// </summary>
        /// <param name="offset">Start of the range.</param>
        /// <param name="count">Number of bytes lost.</param>
        internal void OnLost(long offset, long count)
        {
            long end = offset + count - 1;

            Debug.Assert(_checkedOut.Includes(offset, end));
            Debug.Assert(!_pending.Includes(offset, end));

            _checkedOut.Remove(offset, end);
            _pending.Add(offset, end);
        }

        /// <summary>
        ///     Called to inform the buffer that transmission of given range was successful.
        /// </summary>
        /// <param name="offset">Start of the range.</param>
        /// <param name="count">Number of bytes acked.</param>
        internal void OnAck(long offset, long count)
        {
            long end = offset + count - 1;
            Debug.Assert(_checkedOut.Includes(offset, end));

            _checkedOut.Remove(offset, end);
            _acked.Add(offset, end);

            // release unneeded data
            long processed = _acked[0].End + 1;

            // index of first chunk with unsent data is the same as count of unneeded chunks that are before
            int toRemove = _chunks.FindIndex(c => c.StreamOffset + c.Length > processed);
            if (toRemove == -1)
            {
                toRemove = _chunks.Count;
            }

            for (int i = 0; i < toRemove; i++)
            {
                if (_chunks[i].Buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_chunks[i].Buffer!);
                }
            }

            _chunks.RemoveRange(0, toRemove);
        }
    }
}

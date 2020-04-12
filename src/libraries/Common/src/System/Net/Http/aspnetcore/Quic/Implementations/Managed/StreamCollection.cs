#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.Buffers;
using System.Threading.Channels;

namespace System.Net.Quic.Implementations.Managed
{
    /// <summary>
    ///     Collection of Quic streams.
    /// </summary>
    internal sealed class StreamCollection
    {
        /// <summary>
        ///     All streams organized by the stream type.
        /// </summary>
        private readonly List<ManagedQuicStream?>[] _streams =
        {
            new List<ManagedQuicStream?>(),
            new List<ManagedQuicStream?>(),
            new List<ManagedQuicStream?>(),
            new List<ManagedQuicStream?>()
        };

        /// <summary>
        ///     All streams which are flushable (have data to send).
        /// </summary>
        private readonly HashSet<ManagedQuicStream> _flushable = new HashSet<ManagedQuicStream>();

        /// <summary>
        ///     Channel of streams that were opened by the peer but not yet accepted by this endpoint.
        /// </summary>
        internal Channel<ManagedQuicStream> IncomingStreams { get; } =
            Channel.CreateUnbounded<ManagedQuicStream>(new UnboundedChannelOptions()
            {
                SingleReader = true, SingleWriter = true
            });

        internal ManagedQuicStream this[long streamId] =>
            _streams[(int)StreamHelpers.GetStreamType(streamId)][(int)StreamHelpers.GetStreamIndex(streamId)]!;


        internal ManagedQuicStream? GetFirstFlushableStream()
        {
            lock (_flushable)
            {
                return _flushable.FirstOrDefault();
            }
        }

        internal ManagedQuicStream GetOrCreateStream(long streamId, in TransportParameters localParams,
            in TransportParameters remoteParams, bool isServer)
        {
            var type = StreamHelpers.GetStreamType(streamId);
            // TODO-RZ: allow for long indices
            int index = (int) StreamHelpers.GetStreamIndex(streamId);
            bool unidirectional = !StreamHelpers.IsBidirectional(streamId);
            bool isLocal = isServer && StreamHelpers.IsServerInitiated(streamId);

            var streamList = _streams[(int)type];

            // reserve space in the list
            while (streamList.Count <= index)
            {
                streamList.Add(null);
            }

            var stream = streamList[index];
            if (stream == null)
            {
                stream = streamList[index] ??= CreateStream(streamId, isLocal, unidirectional, localParams, remoteParams);

                if (!isLocal)
                {
                    bool success = IncomingStreams.Writer.TryWrite(stream);
                    // reserving space should be assured by connection stream limits
                    Debug.Assert(success, "Failed to write into IncomingStreams");
                }
            }

            return stream;
        }

        private ManagedQuicStream CreateStream(long streamId,
            bool isLocal, bool unidirectional, TransportParameters localParams, TransportParameters remoteParams)
        {
            // use initial flow control limits
            (long? maxDataInbound, long? maxDataOutbound) = (isLocal, unidirectional) switch
            {
                // local unidirectional
                (true, true) => ((long?)null, (long?)remoteParams.InitialMaxStreamDataUni),
                // local bidirectional
                (true, false) => ((long?)localParams.InitialMaxStreamDataBidiLocal, (long?)remoteParams.InitialMaxStreamDataBidiRemote),
                // remote unidirectional
                (false, true) => ((long?)localParams.InitialMaxStreamDataUni, (long?)null),
                // remote bidirectional
                (false, false) => ((long?)localParams.InitialMaxStreamDataBidiRemote, (long?)remoteParams.InitialMaxStreamDataBidiLocal),
            };

            InboundBuffer? inboundBuffer = maxDataInbound != null
                ? new InboundBuffer(maxDataInbound.Value)
                : null;

            OutboundBuffer? outboundBuffer = maxDataOutbound != null
                ? new OutboundBuffer(maxDataOutbound.Value)
                : null;

            return new ManagedQuicStream(streamId, inboundBuffer, outboundBuffer, this);
        }

        internal ManagedQuicStream CreateOutboundStream(StreamType type, in TransportParameters localParams, in TransportParameters remoteParams)
        {
            var streamList = _streams[(int)type];
            long streamId = StreamHelpers.ComposeStreamId(type, streamList.Count);

            var stream = CreateStream(streamId, true, !StreamHelpers.IsBidirectional(type), localParams, remoteParams);
            streamList.Add(stream);
            return stream;
        }

        internal void MarkFlushable(ManagedQuicStream stream, bool flushable)
        {
            // TODO-RZ: remove the need for this lock
            lock (_flushable)
            {
                Debug.Assert(stream.CanWrite);
                if (flushable)
                    _flushable.Add(stream);
                else
                    _flushable.Remove(stream);
            }
        }

        internal long GetStreamCount(StreamType type)
        {
            return _streams[(int)type].Count;
        }
    }
}

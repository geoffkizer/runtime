#nullable enable

using System.Diagnostics;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.Frames;

namespace System.Net.Quic.Implementations.Managed
{
    internal partial class ManagedQuicConnection
    {
        /// <summary>
        ///     Returns true if the frame type requires receiver to sent acknowledgement before the maximum ack delay.
        /// </summary>
        /// <param name="frameType">The frame type.</param>
        private static bool IsAckEliciting(FrameType frameType)
        {
            return frameType switch
            {
                FrameType.Padding => false,
                FrameType.Ack => false,
                FrameType.ConnectionCloseQuic => false,
                FrameType.ConnectionCloseApplication => false,
                // all other frame types are ack eliciting
                _ => true
            };
        }

        /// <summary>
        ///     Returns true if the QUIC protocol allows given frame type to be sent in the given packet type.
        /// </summary>
        /// <param name="frameType">The frame type.</param>
        /// <param name="packetType">The packet type.</param>
        private bool IsFrameAllowed(FrameType frameType, PacketType packetType)
        {
            return packetType switch
            {
                // 1-RTT packets may contain any frame, but HANDSHAKE_DONE may only be sent by server
                PacketType.OneRtt => frameType != FrameType.HandshakeDone || !_isServer,

                PacketType.Initial => frameType switch
                {
                    FrameType.Padding => true,
                    FrameType.Ping => true,
                    FrameType.Ack => true,
                    FrameType.AckWithEcn => true,
                    FrameType.Crypto => true,
                    FrameType.ConnectionCloseQuic => true,
                    _ => false
                },

                PacketType.ZeroRtt => frameType switch
                {
                    FrameType.Ack => false,
                    FrameType.AckWithEcn => false,
                    FrameType.Crypto => false,
                    FrameType.NewToken => false,
                    FrameType.ConnectionCloseQuic => false,
                    FrameType.ConnectionCloseApplication => false,
                    FrameType.HandshakeDone => false,
                    _ => true
                },

                PacketType.Handshake => frameType switch
                {
                    FrameType.Padding => true,
                    FrameType.Ping => true,
                    FrameType.Ack => true,
                    FrameType.AckWithEcn => true,
                    FrameType.Crypto => true,
                    FrameType.ConnectionCloseQuic => true,
                    _ => false
                },

                // these two types do not carry frames, and should never be passed to this function
                // PacketType.Retry,
                // PacketType.VersionNegotiation,
                _ => throw new ArgumentOutOfRangeException(nameof(packetType), packetType, null)
            };
        }

        /// <summary>
        ///     Processes the sequence of frames in the given reader.
        /// </summary>
        /// <param name="reader">Reader with the packet payload (frames).</param>
        /// <param name="packetType">The type of the packet in which the payload was sent.</param>
        /// <param name="context">Contextual data for the current receive operation.</param>
        private ProcessPacketResult ProcessFrames(QuicReader reader, PacketType packetType, QuicSocketContext.RecvContext context)
        {
            while (reader.BytesLeft > 0)
            {
                var frameType = reader.PeekFrameType();

                if (!IsFrameAllowed(frameType, packetType))
                {
                    return CloseConnection(TransportErrorCode.ProtocolViolation, "Frame type not allowed", frameType);
                }

                if (IsAckEliciting(frameType))
                {
                    GetPacketNumberSpace(GetEncryptionLevel(packetType)).AckElicited = true;
                }

                // TODO-RZ: don't process anything except connection close frames when closing
                ProcessPacketResult result = frameType switch
                {
                    FrameType.Padding => DiscardPadding(reader),
                    FrameType.Ping => DiscardFrameType(reader),
                    FrameType.Ack => ProcessAckFrame(reader, packetType, context),
                    FrameType.AckWithEcn => ProcessAckFrame(reader, packetType, context),
                    FrameType.StopSending => throw new NotImplementedException(),
                    FrameType.Crypto => ProcessCryptoFrame(reader, packetType, context),
                    FrameType.NewToken => throw new NotImplementedException(),
                    FrameType.MaxData => ProcessMaxDataFrame(reader),
                    FrameType.MaxStreamData => ProcessMaxStreamDataFrame(reader),
                    FrameType.MaxStreamsBidirectional => ProcessMaxStreamsFrame(reader),
                    FrameType.MaxStreamsUnidirectional => ProcessMaxStreamsFrame(reader),
                    FrameType.DataBlocked => throw new NotImplementedException(),
                    FrameType.StreamDataBlocked => throw new NotImplementedException(),
                    FrameType.StreamsBlockedBidirectional => throw new NotImplementedException(),
                    FrameType.StreamsBlockedUnidirectional => throw new NotImplementedException(),
                    FrameType.NewConnectionId => ProcessNewConnectionIdFrame(reader),
                    FrameType.RetireConnectionId => throw new NotImplementedException(),
                    FrameType.PathChallenge => throw new NotImplementedException(),
                    FrameType.PathResponse => throw new NotImplementedException(),
                    FrameType.ConnectionCloseQuic => ProcessConnectionClose(reader, context),
                    FrameType.ConnectionCloseApplication => ProcessConnectionClose(reader, context),
                    FrameType.HandshakeDone => ProcessHandshakeDoneFrame(reader),
                    _ when (frameType & FrameType.StreamMask) == frameType => ProcessStreamFrame(reader),
                    _ => CloseConnection(TransportErrorCode.FrameEncodingError, QuicError.UnknownFrameType, frameType)
                };

                switch (result)
                {
                    case ProcessPacketResult.Ok:
                        continue;
                    case ProcessPacketResult.Error when _outboundError == null:
                        _outboundError = new QuicError(TransportErrorCode.FrameEncodingError,
                            QuicError.UnableToDeserialize, frameType);
                        break;
                }

                return result;
            }

            // do handshake to set encryption secrets (to be able to process coalesced packets)
            if (context.HandshakeWanted)
            {
                DoHandshake();
            }

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult DiscardFrameType(QuicReader reader)
        {
            reader.ReadFrameType();
            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult DiscardPadding(QuicReader reader)
        {
            while (reader.BytesLeft > 0 && reader.Peek() == 0)
            {
                reader.ReadUInt8();
            }

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessHandshakeDoneFrame(QuicReader reader)
        {
            // frame not being allowed to be sent by client is handled in IsPacketAllowed
            Debug.Assert(!_isServer);

            reader.ReadFrameType(); // there are no more data, just the frame type identifier.
            _handshakeDoneReceived = true;

            // An endpoint MUST discard handshake keys when TLS handshake is complete.
            DropPacketNumberSpace(PacketSpace.Handshake);

            SignalConnected();

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessMaxStreamDataFrame(QuicReader reader)
        {
            if (!MaxStreamDataFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            if (!StreamHelpers.IsReadable(_isServer, frame.StreamId))
                // TODO-RZ: check stream state
                return CloseConnection(TransportErrorCode.StreamStateError,
                    QuicError.NotInRecvState, FrameType.MaxStreamData);

            if (!TryGetOrCreateStream(frame.StreamId, out var stream))
                return CloseConnection(TransportErrorCode.StreamLimitError,
                    QuicError.StreamsLimitViolated, FrameType.MaxStreamData);

            var buffer = stream!.OutboundBuffer!;
            buffer.UpdateMaxData(frame.MaximumStreamData);

            if (buffer.IsFlushable)
            {
                _streams.MarkFlushable(stream!);
            }

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessMaxDataFrame(QuicReader reader)
        {
            if (!MaxDataFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            _peerLimits.UpdateMaxData(frame.MaximumData);
            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessMaxStreamsFrame(QuicReader reader)
        {
            if (!MaxStreamsFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            if (frame.Bidirectional)
                _peerLimits.UpdateMaxStreamsBidi(frame.MaximumStreams);
            else
                _peerLimits.UpdateMaxStreamsUni(frame.MaximumStreams);

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessNewConnectionIdFrame(QuicReader reader)
        {
            if (!NewConnectionIdFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            if (DestinationConnectionId!.Data.Length == 0)
            {
                return CloseConnection(TransportErrorCode.ProtocolViolation,
                    QuicError.NewConnectionIdFrameWhenZeroLengthCIDUsed, FrameType.NewConnectionId);
            }

            // RFC: If an endpoint receives a NEW_CONNECTION_ID frame that repeats a
            // previously issued connection ID with a different Stateless Reset
            // Token or a different sequence number, or if a sequence number is used
            // for different connection IDs, the endpoint MAY treat that receipt as
            // a connection error of type PROTOCOL_VIOLATION.

            var existingCid = _remoteConnectionIdCollection.FindBySequenceNumber(frame.SequenceNumber);

            if (!ReferenceEquals(_remoteConnectionIdCollection.Find(frame.ConnectionId), existingCid) ||
                 existingCid != null && existingCid.StatelessResetToken != frame.StatelessResetToken)
            {
                return CloseConnection(TransportErrorCode.ProtocolViolation,
                    QuicError.InconsistentNewConnectionIdFrame, FrameType.NewConnectionId);
            }

            if (existingCid == null)
            {
                var connectionId = new ConnectionId(
                    frame.ConnectionId.ToArray(),
                    frame.SequenceNumber,
                    frame.StatelessResetToken);

                _remoteConnectionIdCollection.Add(connectionId);
                if (NetEventSource.IsEnabled) NetEventSource.NewConnectionIdReceived(this, connectionId.Data);
            }

            if (frame.RetirePriorTo > 0)
                throw new NotImplementedException("Retiring connection ids is not implemented");

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessConnectionClose(QuicReader reader, QuicSocketContext.RecvContext context)
        {
            if (!ConnectionCloseFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            // keep only the first error
            if (_inboundError == null)
            {
                _inboundError = new QuicError((TransportErrorCode)frame.ErrorCode, frame.ReasonPhrase,
                    frame.FrameType, frame.IsQuicError);

                if (_closingPeriodEnd == null)
                {
                    StartClosing(context.Timestamp);
                }

                if (_outboundError == null)
                {
                    // From RFC: An endpoint that receives a CONNECTION_CLOSE frame MAY send a single packet containing a
                    // CONNECTION_CLOSE frame before entering the draining state, using NO_ERROR code if appropriate.
                    _outboundError = new QuicError(TransportErrorCode.NoError);
                    // draining state will be entered once the error is sent.
                }
                else
                {
                    StartDraining();
                }

                // connection will not succeed
                _connectTcs.TryCompleteException(new QuicErrorException(_inboundError));
            }

            return ProcessPacketResult.Ok; //TODO-RZ: Draining/closing state management
        }


        private ProcessPacketResult ProcessAckFrame(QuicReader reader, PacketType packetType, QuicSocketContext.RecvContext context)
        {
            if (!AckFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            PacketNumberSpace pnSpace = GetPacketNumberSpace(GetEncryptionLevel(packetType));

            if (frame.LargestAcknowledged >= pnSpace.NextPacketNumber || // acking future packet
                frame.LargestAcknowledged < frame.FirstAckRange) // acking negative PN
                return CloseConnection(TransportErrorCode.ProtocolViolation, QuicError.InvalidAckRange, FrameType.Ack);

            RangeSet ranges = new RangeSet();
            ranges.Add(frame.LargestAcknowledged - frame.FirstAckRange, frame.LargestAcknowledged);

            int read = 0;

            long prevSmallestAcked = ranges[^1].Start;

            // read the ranges in reverse order, so the `ranges` are in ascending order
            for (int i = (int)frame.AckRangeCount; i > 0; i--)
            {
                read += QuicPrimitives.TryReadVarInt(frame.AckRangesRaw.Slice(read), out long gap);
                read += QuicPrimitives.TryReadVarInt(frame.AckRangesRaw.Slice(read), out long acked);

                // the numbers are always encoded as one lesser, meaning sending 0 in gap means actually 1,
                // implying that     nextLargestAcked = prevSmallestAck - gap - 2

                long nextLargestAcked = prevSmallestAcked - gap - 2;
                long nextSmallestAcked = nextLargestAcked - acked;

                if (nextLargestAcked < 0)
                {
                    return CloseConnection(TransportErrorCode.FrameEncodingError,
                        QuicError.InvalidAckRange, frame.HasEcnCounts ? FrameType.AckWithEcn : FrameType.Ack);
                }

                ranges.Add(nextSmallestAcked, nextLargestAcked);
                prevSmallestAcked = nextSmallestAcked;
            }

            var space = GetPacketSpace(packetType);
            long ackDelay =
                Timestamp.FromMicroseconds(frame.AckDelay * (1 << (int)_peerTransportParameters.AckDelayExponent));
            Recovery.OnAckReceived(space, ranges, ackDelay, frame, context.Timestamp, _tls.IsHandshakeComplete);

            var ackedPackets = Recovery.GetPacketNumberSpace(space).AckedPackets;
            while (ackedPackets.TryDequeue(out var packet))
            {
                OnPacketAcked(packet, pnSpace);
                context.ReturnPacket(packet);
            }

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessCryptoFrame(QuicReader reader, PacketType packetType, QuicSocketContext.RecvContext context)
        {
            if (!CryptoFrame.Read(reader, out var crypto)) return ProcessPacketResult.Error;

            EncryptionLevel level = GetEncryptionLevel(packetType);
            var stream = GetPacketNumberSpace(level).CryptoInboundBuffer;

            // TODO-RZ: don't buffer if not needed
            stream.Receive(crypto.Offset, crypto.CryptoData);

            // process also buffered data received earlier
            if (stream.BytesAvailable > 0)
            {
                // define a copy of level variable with smaller scope to prevent allocations in common case
                EncryptionLevel level2 = level;
                stream.Deliver(segment => { _tls.OnDataReceived(level2, segment.Span); });
                context.HandshakeWanted = true;
            }

            return ProcessPacketResult.Ok;
        }

        private ProcessPacketResult ProcessStreamFrame(QuicReader reader)
        {
            var frameType = reader.PeekFrameType();
            if (!StreamFrame.Read(reader, out var frame))
                return ProcessPacketResult.Error;

            if (!TryGetOrCreateStream(frame.StreamId, out var stream))
            {
                // Flow control violated
                return CloseConnection(TransportErrorCode.StreamLimitError, QuicError.StreamsLimitViolated,
                    frameType);
            }

            if (!stream!.CanRead)
            {
                // Flow trying to write into receive only stream
                return CloseConnection(TransportErrorCode.StreamStateError, QuicError.StreamNotWritable,
                    frameType);
            }

            var buffer = stream.InboundBuffer!;
            long writtenOffset = frame.Offset + frame.StreamData.Length;

            if (writtenOffset > buffer.EstimatedSize)
            {
                // receiving data on largest offset yet, check also connection-level control flow
                ReceivedData += writtenOffset - buffer.EstimatedSize;
                if (ReceivedData > _peerLimits.MaxData)
                {
                    return CloseConnection(TransportErrorCode.FlowControlError, QuicError.MaxDataViolated, frameType);
                }
            }

            // check stream-level flow control
            if (writtenOffset > buffer.MaxData)
            {
                return CloseConnection(TransportErrorCode.FlowControlError, QuicError.StreamMaxDataViolated, frameType);
            }

            if (frame.Fin)
            {
                // if trying to change final size, or setting too small final size, report error.
                if (buffer.FinalSize != null && buffer.FinalSize != writtenOffset ||
                    writtenOffset < buffer.EstimatedSize)
                {
                    return CloseConnection(TransportErrorCode.FinalSizeError, QuicError.InconsistentFinalSize, frameType);
                }
            }

            // close if writing past known stream end
            if (buffer.FinalSize != null && frame.Offset + frame.StreamData.Length > buffer.FinalSize)
            {
                return CloseConnection(TransportErrorCode.FinalSizeError, QuicError.WritingPastFinalSize, frameType);
            }

            buffer.Receive(frame.Offset, frame.StreamData, frame.Fin);

            return ProcessPacketResult.Ok;
        }

        private void WriteFrames(QuicWriter writer, PacketType packetType, EncryptionLevel level, QuicSocketContext.SendContext context)
        {
            var pnSpace = GetPacketNumberSpace(level);

            // TODO-RZ other frames

            // start by non ack-eliciting frames
            WriteAckFrame(writer, pnSpace, context);
            WriteConnectionCloseFrame(writer, context);

            // we can simply track if this packet by tracking the written offset.
            int writtenAfterNonAckEliciting = writer.BytesWritten;

            if (writer.BytesAvailable > 0 && _isServer && !_handshakeDoneSent && packetType == PacketType.OneRtt &&
                _tls.IsHandshakeComplete)
            {
                writer.WriteFrameType(FrameType.HandshakeDone);
                // no data

                SignalConnected();
                context.SentPacket.HandshakeDoneSent = true;
                _handshakeDoneSent = true;

                // handshake is done
                DropPacketNumberSpace(PacketSpace.Handshake);
            }

            if (writer.BytesAvailable > 0 && _pingWanted)
            {
                writer.WriteFrameType(FrameType.Ping);
                // no data
                _pingWanted = false;
            }

            WriteAckFrame(writer, pnSpace, context);
            WriteCryptoFrames(writer, pnSpace, context);

            if (packetType == PacketType.OneRtt)
            {
                WriteStreamMaxDataFrames(writer, context);
                WriteMaxDataFrame(writer, context);
                WriteStreamFrames(writer, context);
            }

            if (writer.BytesWritten > writtenAfterNonAckEliciting)
            {
                // ack-eliciting frame was definitely sent.
                context.SentPacket.InFlight = true;
                context.SentPacket.AckEliciting = true;
            }
        }

        private void WriteConnectionCloseFrame(QuicWriter writer, QuicSocketContext.SendContext context)
        {
            if (_outboundError == null)
            {
                // nothing to send
                return;
            }

            if (_closingPeriodEnd == null)
            {
                // After sending a CONNECTION_CLOSE frame, an endpoint immediately enters the closing state.
                StartClosing(context.Timestamp);
            }

            // TODO-RZ: During the closing period, an endpoint SHOULD limit the number of packets it generates
            // containing a CONNECTION_CLOSE frame. For instance, wait progressively increasing number of packets or
            // amount of time before responding.
            if (_lastConnectionCloseSent >= context.Timestamp)
            {
                return;
            }

            var frame = new ConnectionCloseFrame((long)_outboundError.ErrorCode,
                _outboundError.IsQuicError,
                _outboundError.FrameType,
                _outboundError.ReasonPhrase);

            if (frame.GetSerializedLength() > writer.BytesAvailable)
            {
                // we can't fit the frame into the packet, wait for next time
                return;
            }

            ConnectionCloseFrame.Write(writer, frame);
            _lastConnectionCloseSent = context.Timestamp;

            if (_inboundError != null)
            {
                // RFC allows sending one packet to hasten up the closing, but otherwise we should be draining
                StartDraining();
            }
        }

        private static void WriteCryptoFrames(QuicWriter writer, PacketNumberSpace pnSpace, QuicSocketContext.SendContext context)
        {
            // assume 2 * 2 bytes for offset and length and 1 B for type
            const int minSize = 5;
            while (writer.BytesAvailable > minSize)
            {
                if (!pnSpace.CryptoOutboundStream.IsFlushable)
                    return;

                (long offset, long count) = pnSpace.CryptoOutboundStream.GetNextSendableRange();

                count = Math.Min(count, (long)writer.BytesAvailable - minSize);
                pnSpace.CryptoOutboundStream.CheckOut(CryptoFrame.ReservePayloadBuffer(writer, offset, count));

                context.SentPacket.StreamFrames.Add(
                    SentPacket.StreamFrameHeader.ForCryptoStream(offset, (int) count));
            }
        }

        private void WriteAckFrame(QuicWriter writer, PacketNumberSpace pnSpace, QuicSocketContext.SendContext context)
        {
            var ranges = pnSpace.UnackedPacketNumbers;

            if (ranges.Count == 0)
            {
                return; // no need for ack now
            }

            if (!pnSpace.AckElicited && context.Timestamp - pnSpace.LastAckSent <= Recovery.LatestRtt / 2)
            {
                return;
            }

            pnSpace.LastAckSent = context.Timestamp;

            Debug.Assert(ranges.Count > 0); // implied by AckElicited
            Debug.Assert(pnSpace.LargestReceivedPacketTimestamp != 0);

            // TODO-RZ check max ack delay to avoid sending acks every packet?
            long ackDelay = Timestamp.GetMicroseconds(context.Timestamp - pnSpace.LargestReceivedPacketTimestamp) >>
                            (int)_localTransportParameters.AckDelayExponent;
            // sanity check
            ackDelay = Math.Max(0, ackDelay);

            long largest = ranges.GetMax();
            var firstRange = ranges[^1];

            int written = 0;
            int lengthEstimate = ranges.Count * 2 * 4; // assume worst case encoding

            Span<byte> ackRangesRaw = lengthEstimate <= 512
                ? stackalloc byte[lengthEstimate]
                : new byte[lengthEstimate];

            long prevSmallestAcked = firstRange.Start;
            int overhead = AckFrame.GetOverhead(largest, ackDelay, ranges.Count, firstRange.Length - 1);

            // write as many ranges as possible
            int rangesSent = 0;
            for (int i = ranges.Count - 2; i >= 0; i--)
            {
                var range = ranges[i];

                long nextLargestAcked = range.End;

                // the numbers are always encoded as one lesser, meaning sending 0 in gap means actually 1,
                // implying that     nextLargestAcked = prevSmallestAck - gap - 2

                long gap = prevSmallestAcked - nextLargestAcked - 2;
                long ack = range.Length - 1;

                int rangeWireLength = 0;
                rangeWireLength += QuicPrimitives.WriteVarInt(ackRangesRaw.Slice(written + rangeWireLength), gap);
                rangeWireLength += QuicPrimitives.WriteVarInt(ackRangesRaw.Slice(written + rangeWireLength), ack);

                if (written + overhead + rangeWireLength > writer.BytesAvailable)
                {
                    // cannot fit more
                    break;
                }

                prevSmallestAcked = ranges[i].Start;
                // record that the range has been sent
                context.SentPacket.AckedRanges.Add(range.Start, range.End);
                rangesSent++;
                written += rangeWireLength;
            }

            if (written + overhead <= writer.BytesAvailable)
            {
                context.SentPacket.AckedRanges.Add(firstRange.Start, firstRange.End);

                // TODO-RZ implement ECN counts
                AckFrame.Write(writer,
                    new AckFrame(largest, ackDelay, rangesSent,
                        firstRange.Length - 1, ackRangesRaw.Slice(0, written),
                        false, 0, 0, 0));

                pnSpace.AckElicited = false;
            }
        }

        private void WriteStreamMaxDataFrames(QuicWriter writer, QuicSocketContext.SendContext context)
        {
            ManagedQuicStream? stream;
            while (writer.BytesAvailable > StreamFrame.MinSize && (stream = _streams.GetFirstStreamForFlowControlUpdate()) != null)
            {
                var buffer = stream!.InboundBuffer!;

                if (buffer.MaxData == buffer.RemoteMaxData)
                {
                    // nothing to update, this may happen due to a race condition, should not happen terribly often
                    continue;
                }

                var frame = new MaxStreamDataFrame(stream.StreamId, buffer.MaxData);
                if (writer.BytesAvailable < frame.GetSerializedLength())
                {
                    // cannot fit the frame into packet, be sure to try next time
                    _streams.MarkForFlowControlUpdate(stream);
                    return;
                }

                context.SentPacket.MaxStreamDataFrames.Add(frame);
                MaxStreamDataFrame.Write(writer, frame);
            }
        }

        private void WriteMaxDataFrame(QuicWriter writer, QuicSocketContext.SendContext context)
        {
            // TODO-RZ: strategy for deciding whether the frame should be sent.
            if (MaxDataFrameSent ||
                _localLimits.MaxData - _peerReceivedLocalLimits.MaxData < 1024 * 32 )
            {
                return;
            }

            var frame = new MaxDataFrame(_localLimits.MaxData);

            if (writer.BytesAvailable <= frame.GetSerializedLength())
            {
                return;
            }

            MaxDataFrame.Write(writer, frame);
            context.SentPacket.MaxDataFrame = frame;
            MaxDataFrameSent = true;
        }

        private void WriteStreamFrames(QuicWriter writer, QuicSocketContext.SendContext context)
        {
            ManagedQuicStream? stream;
            while (writer.BytesAvailable > StreamFrame.MinSize && (stream = _streams.GetFirstFlushableStream()) != null)
            {
                var buffer = stream!.OutboundBuffer!;

                if (!buffer.IsFlushable && !buffer.SizeKnown)
                {
                    // race condition, should not happen terribly often
                    continue;
                }

                (long offset, long count) = buffer.GetNextSendableRange();

                // send only as much data as can fit into the packet
                int overhead = StreamFrame.GetOverheadLength(stream.StreamId, offset, count);
                count = Math.Min(count, writer.BytesAvailable - overhead);

                // respect connection-level control flow
                long flowControlAvailable = _peerLimits.MaxData - SentData;
                count = Math.Min(count, buffer.SentBytes + flowControlAvailable - offset);

                // if size is known, WrittenBytes is no longer mutable
                bool fin = buffer.SizeKnown && buffer.WrittenBytes == offset + count;

                if (count > 0 || fin)
                {
                    var payloadDestination = StreamFrame.ReservePayloadBuffer(writer, stream!.StreamId, offset, (int)count, fin);

                    if (count > 0)
                    {
                        // add the newly sent data to the flow control counter
                        SentData += Math.Max(0, offset + count - buffer.SentBytes);

                        buffer.CheckOut(payloadDestination);
                    }

                    // record sent data
                    context.SentPacket.StreamFrames.Add(
                        new SentPacket.StreamFrameHeader(stream!.StreamId, offset, (int) count, fin));
                }

                // if there is more data to sent, put the stream back to queue
                if (buffer.IsFlushable)
                {
                    _streams.MarkFlushable(stream!);
                }

                if (count <= 0)
                {
                    // no more data can fit into this packet.
                    break;
                }
            }
        }
    }
}

#nullable enable

using System.Net.Quic.Implementations.Managed.Internal.Crypto;
using System.Runtime.CompilerServices;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     Class for aggregating all connection data for a single epoch.
    /// </summary>
    internal class EpochData
    {
        /// <summary>
        ///     Largest packet number received by the peer.
        /// </summary>
        internal ulong LargestTransportedPacketNumber { get; set; }

        /// <summary>
        ///     Timestamp when packet with <see cref="LargestTransportedPacketNumber"/> was received.
        /// </summary>
        internal DateTime LargestTransportedPacketTimestamp { get; set; }

        /// <summary>
        ///     Number for the next packet to be send with.
        /// </summary>
        internal ulong NextPacketNumber { get; set; }

        /// <summary>
        ///     Received packet numbers which an ack frame needs to be sent to the peer.
        /// </summary>
        internal RangeSet UnackedPacketNumbers { get; } = new RangeSet();

        /// <summary>
        ///     Set of all received packet numbers.
        /// </summary>
        internal PacketNumberWindow ReceivedPacketNumbers { get; } = new PacketNumberWindow();

        /// <summary>
        ///     Flag that next time packets for sending are requested, an ack frame should be added, because an ack eliciting frame was received meanwhile.
        /// </summary>
        internal bool AckElicited { get; set; }

        /// <summary>
        ///     CryptoSeal for encryption of the outbound data.
        /// </summary>
        internal CryptoSeal? SendCryptoSeal { get; set; }

        /// <summary>
        ///     CryptoSeal for decryption of inbound data.
        /// </summary>
        internal CryptoSeal? RecvCryptoSeal { get; set; }

        /// <summary>
        ///     Stream of outbound messages to be carried in CRYPTO frames.
        /// </summary>
        internal CryptoStream CryptoStream { get; set; } = new CryptoStream();

        /// <summary>
        ///     Gets packet number and it's minimum safe encoding length for the next packet sent.
        /// </summary>
        /// <returns>Truncated packet number and it's length.</returns>
        internal (uint truncatedPn, int pnLength) GetNextPacketNumber()
        {
            int pnLength = QuicPrimitives.GetPacketNumberByteCount(LargestTransportedPacketNumber, NextPacketNumber);
            return ((uint) NextPacketNumber, pnLength);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal abstract class BaseStream : Stream
    {
        public sealed override bool CanSeek => false;

        public sealed override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count, default), callback, state);

        public sealed override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        public sealed override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(WriteAsync(buffer, offset, count, default), callback, state);

        public sealed override void EndWrite(IAsyncResult asyncResult) =>
            TaskToApm.End(asyncResult);

        public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public sealed override void SetLength(long value) => throw new NotSupportedException();

        public sealed override long Length => throw new NotSupportedException();

        public sealed override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        protected static void ValidateBufferArgs(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if ((uint)offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if ((uint)count > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }

        public sealed override int ReadByte()
        {
            byte b = 0;
            return Read(MemoryMarshal.CreateSpan(ref b, 1)) == 1 ? b : -1;
        }

        public sealed override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArgs(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public sealed override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArgs(buffer, offset, count);
            return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public sealed override void WriteByte(byte value) =>
            Write(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

        public sealed override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArgs(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public sealed override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArgs(buffer, offset, count);
            return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        protected static Task NopAsync(CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            Task.CompletedTask;

        //
        // Methods which can be overridden by derived classes
        //

        public override int Read(Span<byte> buffer) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => NopAsync(cancellationToken);
    }
}

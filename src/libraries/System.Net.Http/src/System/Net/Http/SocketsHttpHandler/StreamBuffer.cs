// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Http
{
    internal sealed class StreamBuffer : IDisposable
    {
        private ArrayBuffer _buffer; // mutable struct, do not make this readonly
        private readonly int _maxSize;
        private bool _writeEnded;
        private bool _readAborted;
        private readonly ResettableValueTaskSource _readTaskSource;
        private readonly ResettableValueTaskSource _writeTaskSource;
        private readonly object _syncObject = new object();

        public const int DefaultInitialBufferSize = 4 * 1024;
        public const int DefaultMaxBufferSize = 32 * 1024;

        public StreamBuffer(int initialSize = DefaultInitialBufferSize, int maxSize = DefaultMaxBufferSize)
        {
            _buffer = new ArrayBuffer(initialSize, usePool: true);
            _maxSize = maxSize;
            _readTaskSource = new ResettableValueTaskSource();
            _writeTaskSource = new ResettableValueTaskSource();
        }

        private object SyncObject => _syncObject;

        public bool IsComplete
        {
            get
            {
                Debug.Assert(!Monitor.IsEntered(SyncObject));
                lock (SyncObject)
                {
                    return (_writeEnded && _buffer.ActiveLength == 0);
                }
            }
        }

        public bool IsAborted
        {
            get
            {
                Debug.Assert(!Monitor.IsEntered(SyncObject));
                lock (SyncObject)
                {
                    return _readAborted;
                }
            }
        }

        public int ReadBytesAvailable
        {
            get
            {
                Debug.Assert(!Monitor.IsEntered(SyncObject));
                lock (SyncObject)
                {
                    return _buffer.ActiveLength;
                }
            }
        }

        public int WriteBytesAvailable => (_maxSize - ReadBytesAvailable);

        private (bool wait, int bytesWritten) TryWriteToBuffer(ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer.Length > 0);

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_writeEnded)
                {
                    throw new InvalidOperationException();
                }

                if (_readAborted)
                {
                    return (false, buffer.Length);
                }

                _buffer.TryEnsureAvailableSpaceUpToLimit(buffer.Length, _maxSize);

                int bytesWritten = Math.Min(buffer.Length, _buffer.AvailableLength);
                if (bytesWritten > 0)
                {
                    buffer.Slice(0, bytesWritten).CopyTo(_buffer.AvailableSpan);
                    _buffer.Commit(bytesWritten);

                    _readTaskSource.SignalWaiter();
                }

                buffer = buffer.Slice(bytesWritten);
                if (buffer.Length == 0)
                {
                    return (false, bytesWritten);
                }

                _writeTaskSource.Reset();

                return (true, bytesWritten);
            }
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            while (true)
            {
                (bool wait, int bytesWritten) = TryWriteToBuffer(buffer);
                if (!wait)
                {
                    Debug.Assert(bytesWritten == buffer.Length);
                    break;
                }

                buffer = buffer.Slice(bytesWritten);
                _writeTaskSource.Wait();
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            while (true)
            {
                (bool wait, int bytesWritten) = TryWriteToBuffer(buffer.Span);
                if (!wait)
                {
                    Debug.Assert(bytesWritten == buffer.Length);
                    break;
                }

                buffer = buffer.Slice(bytesWritten);
                await _writeTaskSource.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void EndWrite()
        {
            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_writeEnded)
                {
                    return;
                }

                _writeEnded = true;

                _readTaskSource.SignalWaiter();
            }
        }

        private (bool wait, int bytesRead) TryReadFromBuffer(Span<byte> buffer)
        {
            Debug.Assert(buffer.Length > 0);

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_readAborted)
                {
                    return (false, 0);
                }

                if (_buffer.ActiveLength > 0)
                {
                    int bytesRead = Math.Min(buffer.Length, _buffer.ActiveLength);
                    _buffer.ActiveSpan.Slice(0, bytesRead).CopyTo(buffer);
                    _buffer.Discard(bytesRead);

                    _writeTaskSource.SignalWaiter();

                    return (false, bytesRead);
                }
                else if (_writeEnded)
                {
                    return (false, 0);
                }

                _readTaskSource.Reset();

                return (true, 0);
            }
        }

        public int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            (bool wait, int bytesRead) = TryReadFromBuffer(buffer);
            if (wait)
            {
                Debug.Assert(bytesRead == 0);
                _readTaskSource.Wait();
                (wait, bytesRead) = TryReadFromBuffer(buffer);
                Debug.Assert(!wait);
            }

            return bytesRead;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            (bool wait, int bytesRead) = TryReadFromBuffer(buffer.Span);
            if (wait)
            {
                Debug.Assert(bytesRead == 0);
                await _readTaskSource.WaitAsync(cancellationToken).ConfigureAwait(false);
                (wait, bytesRead) = TryReadFromBuffer(buffer.Span);
                Debug.Assert(!wait);
            }

            return bytesRead;
        }

        // Note, this can be called while a read is in progress, and will cause it to return 0 bytes.
        // Caller can then check IsAborted if appropriate to distinguish between EOF and abort.
        public void AbortRead()
        {
            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_readAborted)
                {
                    return;
                }

                _readAborted = true;
                if (_buffer.ActiveLength != 0)
                {
                    _buffer.Discard(_buffer.ActiveLength);
                }

                _readTaskSource.SignalWaiter();
                _writeTaskSource.SignalWaiter();
            }
        }

        public void Dispose()
        {
            AbortRead();
            EndWrite();

            _buffer.Dispose();
        }

        private sealed class ResettableValueTaskSource : IValueTaskSource
        {
            private ManualResetValueTaskSourceCore<bool> _waitSource = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true }; // mutable struct, do not make this readonly
            private CancellationToken _waitSourceCancellationToken;
            private CancellationTokenRegistration _waitSourceCancellation;
            private int _hasWaiter;

            // This object is itself usable as a backing source for ValueTask.  Since there's only ever one awaiter
            // for this object's state transitions at a time, we allow the object to be awaited directly. All functionality
            // associated with the implementation is just delegated to the ManualResetValueTaskSourceCore.
            ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);
            void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _waitSource.OnCompleted(continuation, state, token, flags);
            void IValueTaskSource.GetResult(short token)
            {
                Debug.Assert(_hasWaiter == 0);

                // Clean up the registration.  It's important to Dispose rather than Unregister, so that we wait
                // for any in-flight cancellation to complete.
                _waitSourceCancellation.Dispose();
                _waitSourceCancellation = default;
                _waitSourceCancellationToken = default;

                // Propagate any exceptions if there were any.
                _waitSource.GetResult(token);
            }

            public void SignalWaiter()
            {
                if (Interlocked.Exchange(ref _hasWaiter, 0) == 1)
                {
                    _waitSource.SetResult(true);
                }
            }

            private void CancelWaiter()
            {
                if (Interlocked.Exchange(ref _hasWaiter, 0) == 1)
                {
                    Debug.Assert(_waitSourceCancellationToken != default);
                    _waitSource.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new OperationCanceledException(_waitSourceCancellationToken)));
                }
            }

            public void Reset()
            {
                Debug.Assert(_hasWaiter == 0);

                _waitSource.Reset();
                Volatile.Write(ref _hasWaiter, 1);
            }

            public void Wait()
            {
                _waitSource.RunContinuationsAsynchronously = false;
                new ValueTask(this, _waitSource.Version).AsTask().GetAwaiter().GetResult();
            }

            public ValueTask WaitAsync(CancellationToken cancellationToken)
            {
                _waitSource.RunContinuationsAsynchronously = true;

                _waitSourceCancellationToken = cancellationToken;
                _waitSourceCancellation = cancellationToken.UnsafeRegister(static s => ((ResettableValueTaskSource)s!).CancelWaiter(), this);

                return new ValueTask(this, _waitSource.Version);
            }
        }
    }
}

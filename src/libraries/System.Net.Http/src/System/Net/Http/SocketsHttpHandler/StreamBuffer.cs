// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Http
{
    // Note this buffer is unbounded.
    // TODO: Make bounded
    internal sealed class StreamBuffer : IDisposable
    {
        private ArrayBuffer _buffer; // mutable struct, do not make this readonly
        private readonly int _maxSize;
        private bool _writeEnded;
        private bool _readAborted;
        private readonly ResettableValueTaskSource _taskSource;
        private readonly object _syncObject = new object();

        public const int DefaultInitialBufferSize = 4 * 1024;
        public const int DefaultMaxBufferSize = 32 * 1024;

        public StreamBuffer(int initialSize = DefaultInitialBufferSize, int maxSize = DefaultMaxBufferSize)
        {
            _buffer = new ArrayBuffer(initialSize, usePool: true);
            _maxSize = maxSize;
            _taskSource = new ResettableValueTaskSource();
        }

        private object SyncObject => _syncObject;

        public bool IsComplete
        {
            get
            {
                Debug.Assert(!Monitor.IsEntered(SyncObject));
                lock (SyncObject)
                {
                    // CONSIDER: What should this do when _readAborted is true?
                    return _writeEnded && _buffer.ActiveLength == 0;
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

                _taskSource.SignalWaiter();
            }
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_writeEnded)
                {
                    throw new InvalidOperationException();
                }

                if (_readAborted)
                {
                    return;
                }

                _buffer.EnsureAvailableSpace(buffer.Length);
                buffer.CopyTo(_buffer.AvailableSpan);
                _buffer.Commit(buffer.Length);

                _taskSource.SignalWaiter();
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

                _taskSource.SignalWaiter();
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

                    return (false, bytesRead);
                }
                else if (_writeEnded)
                {
                    return (false, 0);
                }

                _taskSource.Reset();

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
                // Synchronously block waiting for data to be produced.
                Debug.Assert(bytesRead == 0);
                _taskSource.Wait();
                (wait, bytesRead) = TryReadFromBuffer(buffer);
                Debug.Assert(!wait);
            }

            return bytesRead;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            (bool wait, int bytesRead) = TryReadFromBuffer(buffer.Span);
            if (wait)
            {
                Debug.Assert(bytesRead == 0);
                await _taskSource.WaitAsync(cancellationToken).ConfigureAwait(false);
                (wait, bytesRead) = TryReadFromBuffer(buffer.Span);
                Debug.Assert(!wait);
            }

            return bytesRead;
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

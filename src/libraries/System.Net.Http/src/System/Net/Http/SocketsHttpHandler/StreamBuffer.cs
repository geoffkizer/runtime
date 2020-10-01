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
    internal sealed class StreamBuffer : IValueTaskSource, IDisposable
    {
        private ArrayBuffer _buffer; // mutable struct, do not make this readonly

        /// <summary>
        /// The core logic for the IValueTaskSource implementation.
        ///
        /// Thread-safety:
        /// _waitSource is used to coordinate between a producer indicating that something is available to process (either the connection's event loop
        /// or a cancellation request) and a consumer doing that processing.  There must only ever be a single consumer, namely this stream reading
        /// data associated with the response.  Because there is only ever at most one consumer, producers can trust that if _hasWaiter is true,
        /// until the _waitSource is then set, no consumer will attempt to reset the _waitSource.  A producer must still take SyncObj in order to
        /// coordinate with other producers (e.g. a race between data arriving from the event loop and cancellation being requested), but while holding
        /// the lock it can check whether _hasWaiter is true, and if it is, set _hasWaiter to false, exit the lock, and then set the _waitSource. Another
        /// producer coming along will then see _hasWaiter as false and will not attempt to concurrently set _waitSource (which would violate _waitSource's
        /// thread-safety), and no other consumer could come along in the interim, because _hasWaiter being true means that a consumer is already waiting
        /// for _waitSource to be set, and legally there can only be one consumer.  Once this producer sets _waitSource, the consumer could quickly loop
        /// around to wait again, but invariants have all been maintained in the interim, and the consumer would need to take the SyncObj lock in order to
        /// Reset _waitSource.
        /// </summary>
        private ManualResetValueTaskSourceCore<bool> _waitSource = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true }; // mutable struct, do not make this readonly
        private CancellationToken _waitSourceCancellationToken;
        /// <summary>Cancellation registration used to cancel the <see cref="_waitSource"/>.</summary>
        private CancellationTokenRegistration _waitSourceCancellation;

        /// <summary>
        /// Whether code has requested or is about to request a wait be performed and thus requires a call to SetResult to complete it.
        /// This is read and written while holding the lock so that most operations on _waitSource don't need to be.
        /// </summary>
        private bool _hasWaiter;
        private bool _writeEnded;
        private bool _readAborted;

        public StreamBuffer(int initialSize = 4096)
        {
            _buffer = new ArrayBuffer(initialSize, usePool: true);
        }

        private object SyncObject => this; // this isn't handed out to code that may lock on it

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

        public int BufferedByteCount
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

        public void AbortRead()
        {
            bool signalWaiter;

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_readAborted)
                {
                    Debug.Assert(!_hasWaiter);
                    return;
                }

                _readAborted = true;
                if (_buffer.ActiveLength != 0)
                {
                    Debug.Assert(!_hasWaiter);
                    _buffer.Discard(_buffer.ActiveLength);
                }

                signalWaiter = _hasWaiter;
                _hasWaiter = false;
            }

            if (signalWaiter)
            {
                _waitSource.SetResult(true);
            }
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            bool signalWaiter;

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

                signalWaiter = _hasWaiter;
                _hasWaiter = false;
            }

            if (signalWaiter)
            {
                _waitSource.SetResult(true);
            }
        }

        public void EndWrite()
        {
            bool signalWaiter;

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                if (_writeEnded)
                {
                    Debug.Assert(!_hasWaiter);
                    return;
                }

                _writeEnded = true;

                signalWaiter = _hasWaiter;
                _hasWaiter = false;
            }

            if (signalWaiter)
            {
                _waitSource.SetResult(true);
            }
        }

        private (bool wait, int bytesRead) TryReadFromBuffer(Span<byte> buffer)
        {
            Debug.Assert(buffer.Length > 0);

            Debug.Assert(!Monitor.IsEntered(SyncObject));
            lock (SyncObject)
            {
                Debug.Assert(!_hasWaiter);

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

                _hasWaiter = true;
                _waitSource.Reset();
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
                WaitForData();
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
                await WaitForDataAsync(cancellationToken).ConfigureAwait(false);
                (wait, bytesRead) = TryReadFromBuffer(buffer.Span);
                Debug.Assert(!wait);
            }

            return bytesRead;
        }

        // This object is itself usable as a backing source for ValueTask.  Since there's only ever one awaiter
        // for this object's state transitions at a time, we allow the object to be awaited directly. All functionality
        // associated with the implementation is just delegated to the ManualResetValueTaskSourceCore.
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _waitSource.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token)
        {
            Debug.Assert(!Monitor.IsEntered(SyncObject));

            // Clean up the registration.  It's important to Dispose rather than Unregister, so that we wait
            // for any in-flight cancellation to complete.
            _waitSourceCancellation.Dispose();
            _waitSourceCancellation = default;
            _waitSourceCancellationToken = default;

            // Propagate any exceptions if there were any.
            _waitSource.GetResult(token);
        }

        private void WaitForData()
        {
            // See comments in WaitAsync.
            _waitSource.RunContinuationsAsynchronously = false;
            new ValueTask(this, _waitSource.Version).AsTask().GetAwaiter().GetResult();
        }

        private ValueTask WaitForDataAsync(CancellationToken cancellationToken)
        {
            _waitSource.RunContinuationsAsynchronously = true;

            // No locking is required here to access _waitSource.  To be here, we've already updated _hasWaiter (while holding the lock)
            // to indicate that we would be creating this waiter, and at that point the only code that could be await'ing _waitSource or
            // Reset'ing it is this code here.  It's possible for this to race with the _waitSource being completed, but that's ok and is
            // handled by _waitSource as one of its primary purposes.  We can't assert _hasWaiter here, though, as once we released the
            // lock, a producer could have seen _hasWaiter as true and both set it to false and signaled _waitSource.

            // With HttpClient, the supplied cancellation token will always be cancelable, as HttpClient supplies a token that
            // will have cancellation requested if CancelPendingRequests is called (or when a non-infinite Timeout expires).
            // However, this could still be non-cancelable if HttpMessageInvoker was used, at which point this will only be
            // cancelable if the caller's token was cancelable.

            _waitSourceCancellationToken = cancellationToken;
            _waitSourceCancellation = cancellationToken.UnsafeRegister(static s =>
            {
                var thisRef = (StreamBuffer)s!;

                bool signalWaiter;
                Debug.Assert(!Monitor.IsEntered(thisRef.SyncObject));
                lock (thisRef.SyncObject)
                {
                    signalWaiter = thisRef._hasWaiter;
                    thisRef._hasWaiter = false;
                }

                if (signalWaiter)
                {
                    // Wake up the wait.  It will then immediately check whether cancellation was requested and throw if it was.
                    thisRef._waitSource.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(
                        CancellationHelper.CreateOperationCanceledException(null, thisRef._waitSourceCancellationToken)));
                }
            }, this);

            return new ValueTask(this, _waitSource.Version);
        }

        public void Dispose()
        {
            AbortRead();
            EndWrite();

            _buffer.Dispose();
        }
    }
}

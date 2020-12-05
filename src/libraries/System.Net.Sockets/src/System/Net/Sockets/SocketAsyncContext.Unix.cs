// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// Disable warning about accesing ValueTask directly
#pragma warning disable CA2012

namespace System.Net.Sockets
{
    // Note on asynchronous behavior here:

    // The asynchronous socket operations here generally do the following:
    // (1) If the operation queue is Ready (queue is empty), try to perform the operation immediately, non-blocking.
    // If this completes (i.e. does not return EWOULDBLOCK), then we return the results immediately
    // for both success (SocketError.Success) or failure.
    // No callback will happen; callers are expected to handle these synchronous completions themselves.
    // (2) If EWOULDBLOCK is returned, or the queue is not empty, then we enqueue an operation to the
    // appropriate queue and return SocketError.IOPending.
    // Enqueuing itself may fail because the socket is closed before the operation can be enqueued;
    // in this case, we return SocketError.OperationAborted (which matches what Winsock would return in this case).
    // (3) When we receive an epoll notification for the socket, we post a work item to the threadpool
    // to perform the I/O and invoke the callback with the I/O result.

    // Synchronous operations generally do the same, except that instead of returning IOPending,
    // they block on an event handle until the operation is processed by the queue.

    // See comments on OperationQueue below for more details of how the queue coordination works.

    internal sealed class SocketAsyncContext
    {
        // Cached operation instances for operations commonly repeated on the same socket instance,
        // e.g. async accepts, sends/receives with single and multiple buffers.  More can be
        // added in the future if necessary, at the expense of extra fields here.  With a larger
        // refactoring, these could also potentially be moved to SocketAsyncEventArgs, which
        // would be more invasive but which would allow them to be reused across socket instances
        // and also eliminate the interlocked necessary to rent the instances.
        private AcceptOperation? _cachedAcceptOperation;
        private BufferMemoryReceiveOperation? _cachedBufferMemoryReceiveOperation;
        private BufferListReceiveOperation? _cachedBufferListReceiveOperation;
        private BufferMemorySendOperation? _cachedBufferMemorySendOperation;
        private BufferListSendOperation? _cachedBufferListSendOperation;

        private void ReturnOperation(AcceptOperation operation)
        {
            operation.Reset();
            operation.Callback = null;
            operation.SocketAddress = null;
            Volatile.Write(ref _cachedAcceptOperation, operation); // benign race condition
        }

        private void ReturnOperation(BufferMemoryReceiveOperation operation)
        {
            operation.Reset();
            operation.Buffer = default;
            operation.Callback = null;
            operation.SocketAddress = null;
            Volatile.Write(ref _cachedBufferMemoryReceiveOperation, operation); // benign race condition
        }

        private void ReturnOperation(BufferListReceiveOperation operation)
        {
            operation.Reset();
            operation.Buffers = null;
            operation.Callback = null;
            operation.SocketAddress = null;
            Volatile.Write(ref _cachedBufferListReceiveOperation, operation); // benign race condition
        }

        private void ReturnOperation(BufferMemorySendOperation operation)
        {
            operation.Reset();
            operation.Buffer = default;
            operation.Callback = null;
            operation.SocketAddress = null;
            Volatile.Write(ref _cachedBufferMemorySendOperation, operation); // benign race condition
        }

        private void ReturnOperation(BufferListSendOperation operation)
        {
            operation.Reset();
            operation.Buffers = null;
            operation.Callback = null;
            operation.SocketAddress = null;
            Volatile.Write(ref _cachedBufferListSendOperation, operation); // benign race condition
        }

        private AcceptOperation RentAcceptOperation() =>
            Interlocked.Exchange(ref _cachedAcceptOperation, null) ??
            new AcceptOperation(this);

        private BufferMemoryReceiveOperation RentBufferMemoryReceiveOperation() =>
            Interlocked.Exchange(ref _cachedBufferMemoryReceiveOperation, null) ??
            new BufferMemoryReceiveOperation(this);

        private BufferListReceiveOperation RentBufferListReceiveOperation() =>
            Interlocked.Exchange(ref _cachedBufferListReceiveOperation, null) ??
            new BufferListReceiveOperation(this);

        private BufferMemorySendOperation RentBufferMemorySendOperation() =>
            Interlocked.Exchange(ref _cachedBufferMemorySendOperation, null) ??
            new BufferMemorySendOperation(this);

        private BufferListSendOperation RentBufferListSendOperation() =>
            Interlocked.Exchange(ref _cachedBufferListSendOperation, null) ??
            new BufferListSendOperation(this);

        private abstract class AsyncOperation : IThreadPoolWorkItem
        {
            private enum State
            {
                Waiting = 0,
                Running = 1,
                Complete = 2,
                Cancelled = 3
            }

            private int _state; // Actually AsyncOperation.State.

#if DEBUG
            private int _callbackQueued; // When non-zero, the callback has been queued.
#endif

            public readonly SocketAsyncContext AssociatedContext;
            public AsyncOperation Next = null!; // initialized by helper called from ctor
            public SocketError ErrorCode;
            public byte[]? SocketAddress;
            public int SocketAddressLen;
            public CancellationTokenRegistration CancellationRegistration;

            public ManualResetEventSlim? Event { get; set; }
            public TaskCompletionSource? CompletionSource { get; set; }

            public AsyncOperation(SocketAsyncContext context)
            {
                AssociatedContext = context;
                Reset();
            }

            public void Reset()
            {
                _state = (int)State.Waiting;
                Event = null;
                CompletionSource = null;
                Next = this;
#if DEBUG
                _callbackQueued = 0;
#endif
            }

            public bool TryComplete(SocketAsyncContext context)
            {
                // TODO: Remove parameter?
                Debug.Assert(context == AssociatedContext);

                TraceWithContext(context, "Enter");

                bool result = DoTryComplete(context);

                TraceWithContext(context, $"Exit, result={result}");

                return result;
            }

            public bool TrySetRunning()
            {
                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has already been cancelled, and had its completion processed.
                    // Simply return false to indicate no further processing is needed.
                    return false;
                }

                Debug.Assert(oldState == (int)State.Waiting);
                return true;
            }

            public void SetComplete()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Complete);
            }

            public void SetWaiting()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Waiting);
            }

            public bool TryCancel()
            {
                Trace("Enter");

                // We're already canceling, so we don't need to still be hooked up to listen to cancellation.
                // The cancellation request could also be caused by something other than the token, so it's
                // important we clean it up, regardless.
                CancellationRegistration.Dispose();

                // Try to transition from Waiting to Cancelled
                SpinWait spinWait = default;
                bool keepWaiting = true;
                while (keepWaiting)
                {
                    int state = Interlocked.CompareExchange(ref _state, (int)State.Cancelled, (int)State.Waiting);
                    switch ((State)state)
                    {
                        case State.Running:
                            // A completion attempt is in progress. Keep busy-waiting.
                            Trace("Busy wait");
                            spinWait.SpinOnce();
                            break;

                        case State.Complete:
                            // A completion attempt succeeded. Consider this operation as having completed within the timeout.
                            Trace("Exit, previously completed");
                            return false;

                        case State.Waiting:
                            // This operation was successfully cancelled.
                            // Break out of the loop to handle the cancellation
                            keepWaiting = false;
                            break;

                        case State.Cancelled:
                            // Someone else cancelled the operation.
                            // The previous canceller will have fired the completion, etc.
                            Trace("Exit, previously cancelled");
                            return false;
                    }
                }

                Trace("Cancelled, processing completion");

                // The operation successfully cancelled.
                // It's our responsibility to set the error code and queue the completion.
                DoAbort();

                ManualResetEventSlim? e = Event;
                if (e != null)
                {
                    e.Set();
                }
                else if (CompletionSource is not null)
                {
                    CompletionSource.TrySetResult();
                }
                else
                {
#if DEBUG
                    Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif
                    // We've marked the operation as canceled, and so should invoke the callback, but
                    // we can't pool the object, as ProcessQueue may still have a reference to it, due to
                    // using a pattern whereby it takes the lock to grab an item, but then releases the lock
                    // to do further processing on the item that's still in the list.
                    ThreadPool.UnsafeQueueUserWorkItem(o => ((AsyncOperation)o!).InvokeCallback(allowPooling: false), this);
                }

                Trace("Exit");

                // Note, we leave the operation in the OperationQueue.
                // When we get around to processing it, we'll see it's cancelled and skip it.
                return true;
            }

            public void Dispatch()
            {
                ManualResetEventSlim? e = Event;
                if (e != null)
                {
                    // Sync operation.  Signal waiting thread to continue processing.
                    e.Set();
                }
                else if (CompletionSource is not null)
                {
                    CompletionSource.TrySetResult();
                }
                else
                {
                    // Async operation.
                    Schedule();
                }
            }

            public void Schedule()
            {
                Debug.Assert(Event == null);
                Debug.Assert(CompletionSource is null);

                // Async operation.  Process the IO on the threadpool.
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }

            public void Process() => ((IThreadPoolWorkItem)this).Execute();

            void IThreadPoolWorkItem.Execute()
            {
                // ReadOperation and WriteOperation, the only two types derived from
                // AsyncOperation, implement IThreadPoolWorkItem.Execute to call
                // ProcessAsyncOperation(this) on the appropriate receive or send queue.
                // However, this base class needs to be able to queue them without
                // additional allocation, so it also implements the interface in order
                // to pass the compiler's static checking for the interface, but then
                // when the runtime queries for the interface, it'll use the derived
                // type's interface implementation.  We could instead just make this
                // an abstract and have the derived types override it, but that adds
                // "Execute" as a public method, which could easily be misunderstood.
                // We could also add an abstract method that the base interface implementation
                // invokes, but that adds an extra virtual dispatch.
                Debug.Fail("Expected derived type to implement IThreadPoolWorkItem");
                throw new InvalidOperationException();
            }

            // Called when op is not in the queue yet, so can't be otherwise executing
            public void DoAbort()
            {
                Abort();
                ErrorCode = SocketError.OperationAborted;
            }

            protected abstract void Abort();

            protected abstract bool DoTryComplete(SocketAsyncContext context);

            public abstract void InvokeCallback(bool allowPooling);

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void Trace(string message, [CallerMemberName] string? memberName = null)
            {
                OutputTrace($"{IdOf(this)}.{memberName}: {message}");
            }

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void TraceWithContext(SocketAsyncContext context, string message, [CallerMemberName] string? memberName = null)
            {
                OutputTrace($"{IdOf(context)}, {IdOf(this)}.{memberName}: {message}");
            }
        }

        private abstract class AsyncOperation2<T> : AsyncOperation
            where T : AsyncOperation
        {
            public AsyncOperation2(SocketAsyncContext context) : base(context) { }

            public abstract ref OperationQueue<T> OperationQueue { get; }
        }

        // These two abstract classes differentiate the operations that go in the
        // read queue vs the ones that go in the write queue.
        private abstract class ReadOperation : AsyncOperation2<ReadOperation>, IThreadPoolWorkItem
        {
            public ReadOperation(SocketAsyncContext context) : base(context) { }

            void IThreadPoolWorkItem.Execute() => AssociatedContext.ProcessAsyncReadOperation(this);

            public sealed override ref OperationQueue<ReadOperation> OperationQueue => ref AssociatedContext._receiveQueue;
        }

        private abstract class WriteOperation : AsyncOperation2<WriteOperation>, IThreadPoolWorkItem
        {
            public WriteOperation(SocketAsyncContext context) : base(context) { }

            void IThreadPoolWorkItem.Execute() => AssociatedContext.ProcessAsyncWriteOperation(this);

            public sealed override ref OperationQueue<WriteOperation> OperationQueue => ref AssociatedContext._sendQueue;
        }

        private abstract class SendOperation : WriteOperation
        {
            public SocketFlags Flags;
            public int BytesTransferred;
            public int Offset;
            public int Count;

            public SendOperation(SocketAsyncContext context) : base(context) { }

            protected sealed override void Abort() { }

            public Action<int, byte[]?, int, SocketFlags, SocketError>? Callback { get; set; }

            public override void InvokeCallback(bool allowPooling) =>
                Callback!(BytesTransferred, SocketAddress, SocketAddressLen, SocketFlags.None, ErrorCode);
        }

        private sealed class BufferMemorySendOperation : SendOperation
        {
            public Memory<byte> Buffer;

            public BufferMemorySendOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                int bufferIndex = 0;
                return SocketPal.TryCompleteSendTo(context._socket, Buffer.Span, null, ref bufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }

            public override void InvokeCallback(bool allowPooling)
            {
                var cb = Callback!;
                int bt = BytesTransferred;
                byte[]? sa = SocketAddress;
                int sal = SocketAddressLen;
                SocketError ec = ErrorCode;

                if (allowPooling)
                {
                    AssociatedContext.ReturnOperation(this);
                }

                cb(bt, sa, sal, SocketFlags.None, ec);
            }
        }

        private sealed class BufferListSendOperation : SendOperation
        {
            public IList<ArraySegment<byte>>? Buffers;
            public int BufferIndex;

            public BufferListSendOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteSendTo(context._socket, default(ReadOnlySpan<byte>), Buffers, ref BufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }

            public override void InvokeCallback(bool allowPooling)
            {
                var cb = Callback!;
                int bt = BytesTransferred;
                byte[]? sa = SocketAddress;
                int sal = SocketAddressLen;
                SocketError ec = ErrorCode;

                if (allowPooling)
                {
                    AssociatedContext.ReturnOperation(this);
                }

                cb(bt, sa, sal, SocketFlags.None, ec);
            }
        }

        private abstract class ReceiveOperation : ReadOperation
        {
            public SocketFlags Flags;
            public SocketFlags ReceivedFlags;
            public int BytesTransferred;

            public ReceiveOperation(SocketAsyncContext context) : base(context) { }

            protected sealed override void Abort() { }

            public Action<int, byte[]?, int, SocketFlags, SocketError>? Callback { get; set; }

            public override void InvokeCallback(bool allowPooling) =>
                Callback!(BytesTransferred, SocketAddress, SocketAddressLen, ReceivedFlags, ErrorCode);
        }

        private sealed class BufferMemoryReceiveOperation : ReceiveOperation
        {
            public Memory<byte> Buffer;
            public bool SetReceivedFlags;

            public BufferMemoryReceiveOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                // Zero byte read is performed to know when data is available.
                // We don't have to call receive, our caller is interested in the event.
                if (Buffer.Length == 0 && Flags == SocketFlags.None && SocketAddress == null)
                {
                    BytesTransferred = 0;
                    ReceivedFlags = SocketFlags.None;
                    ErrorCode = SocketError.Success;
                    return true;
                }
                else
                {
                    if (!SetReceivedFlags)
                    {
                        Debug.Assert(SocketAddress == null);

                        ReceivedFlags = SocketFlags.None;
                        return SocketPal.TryCompleteReceive(context._socket, Buffer.Span, Flags, out BytesTransferred, out ErrorCode);
                    }
                    else
                    {
                        return SocketPal.TryCompleteReceiveFrom(context._socket, Buffer.Span, null, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);
                    }
                }
            }

            public override void InvokeCallback(bool allowPooling)
            {
                var cb = Callback!;
                int bt = BytesTransferred;
                byte[]? sa = SocketAddress;
                int sal = SocketAddressLen;
                SocketFlags rf = ReceivedFlags;
                SocketError ec = ErrorCode;

                if (allowPooling)
                {
                    AssociatedContext.ReturnOperation(this);
                }

                cb(bt, sa, sal, rf, ec);
            }
        }

        // Note, these aren't specific to sync anymore. Basically just dummy operations.

        private sealed class DumbSyncReceiveOperation : ReceiveOperation
        {
            public DumbSyncReceiveOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                Debug.Assert(false);
                return true;
            }

            public override void InvokeCallback(bool allowPooling)
            {
                Debug.Assert(false);
            }
        }

        private sealed class DumbSyncSendOperation : SendOperation
        {
            public DumbSyncSendOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                Debug.Assert(false);
                return true;
            }

            public override void InvokeCallback(bool allowPooling)
            {
                Debug.Assert(false);
            }
        }

        private sealed class BufferListReceiveOperation : ReceiveOperation
        {
            public IList<ArraySegment<byte>>? Buffers;

            public BufferListReceiveOperation(SocketAsyncContext context) : base(context) { }

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveFrom(context._socket, default(Span<byte>), Buffers, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);

            public override void InvokeCallback(bool allowPooling)
            {
                var cb = Callback!;
                int bt = BytesTransferred;
                byte[]? sa = SocketAddress;
                int sal = SocketAddressLen;
                SocketFlags rf = ReceivedFlags;
                SocketError ec = ErrorCode;

                if (allowPooling)
                {
                    AssociatedContext.ReturnOperation(this);
                }

                cb(bt, sa, sal, rf, ec);
            }
        }

        private sealed class ReceiveMessageFromOperation : ReadOperation
        {
            public Memory<byte> Buffer;
            public SocketFlags Flags;
            public int BytesTransferred;
            public SocketFlags ReceivedFlags;
            public IList<ArraySegment<byte>>? Buffers;

            public bool IsIPv4;
            public bool IsIPv6;
            public IPPacketInformation IPPacketInformation;

            public ReceiveMessageFromOperation(SocketAsyncContext context) : base(context) { }

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError>? Callback { get; set; }

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveMessageFrom(context._socket, Buffer.Span, Buffers, Flags, SocketAddress!, ref SocketAddressLen, IsIPv4, IsIPv6, out BytesTransferred, out ReceivedFlags, out IPPacketInformation, out ErrorCode);

            public override void InvokeCallback(bool allowPooling) =>
                Callback!(BytesTransferred, SocketAddress!, SocketAddressLen, ReceivedFlags, IPPacketInformation, ErrorCode);
        }

        private sealed class AcceptOperation : ReadOperation
        {
            public IntPtr AcceptedFileDescriptor;

            public AcceptOperation(SocketAsyncContext context) : base(context) { }

            public Action<IntPtr, byte[], int, SocketError>? Callback { get; set; }

            protected override void Abort() =>
                AcceptedFileDescriptor = (IntPtr)(-1);

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool completed = SocketPal.TryCompleteAccept(context._socket, SocketAddress!, ref SocketAddressLen, out AcceptedFileDescriptor, out ErrorCode);
                Debug.Assert(ErrorCode == SocketError.Success || AcceptedFileDescriptor == (IntPtr)(-1), $"Unexpected values: ErrorCode={ErrorCode}, AcceptedFileDescriptor={AcceptedFileDescriptor}");
                return completed;
            }

            public override void InvokeCallback(bool allowPooling)
            {
                var cb = Callback!;
                IntPtr fd = AcceptedFileDescriptor;
                byte[] sa = SocketAddress!;
                int sal = SocketAddressLen;
                SocketError ec = ErrorCode;

                if (allowPooling)
                {
                    AssociatedContext.ReturnOperation(this);
                }

                cb(fd, sa, sal, ec);
            }
        }

        private sealed class ConnectOperation : WriteOperation
        {
            public ConnectOperation(SocketAsyncContext context) : base(context) { }

            public Action<SocketError>? Callback { get; set; }

            protected override void Abort() { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool result = SocketPal.TryCompleteConnect(context._socket, SocketAddressLen, out ErrorCode);
                context._socket.RegisterConnectResult(ErrorCode);
                return result;
            }

            public override void InvokeCallback(bool allowPooling) =>
                Callback!(ErrorCode);
        }

        private sealed class SendFileOperation : WriteOperation
        {
            public SafeFileHandle FileHandle = null!; // always set when constructed
            public long Offset;
            public long Count;
            public long BytesTransferred;

            public SendFileOperation(SocketAsyncContext context) : base(context) { }

            protected override void Abort() { }

            public Action<long, SocketError>? Callback { get; set; }

            public override void InvokeCallback(bool allowPooling) =>
                Callback!(BytesTransferred, ErrorCode);

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteSendFile(context._socket, FileHandle, ref Offset, ref Count, ref BytesTransferred, out ErrorCode);
        }

        // In debug builds, this struct guards against:
        // (1) Unexpected lock reentrancy, which should never happen
        // (2) Deadlock, by setting a reasonably large timeout
        private readonly struct LockToken : IDisposable
        {
            private readonly object _lockObject;

            public LockToken(object lockObject)
            {
                Debug.Assert(lockObject != null);

                _lockObject = lockObject;

                Debug.Assert(!Monitor.IsEntered(_lockObject));

#if DEBUG
                bool success = Monitor.TryEnter(_lockObject, 10000);
                Debug.Assert(success, "Timed out waiting for queue lock");
#else
                Monitor.Enter(_lockObject);
#endif
            }

            public void Dispose()
            {
                Debug.Assert(Monitor.IsEntered(_lockObject));
                Monitor.Exit(_lockObject);
            }
        }

        private struct OperationQueue<TOperation>
            where TOperation : AsyncOperation
        {
            // Quick overview:
            //
            // When attempting to perform an IO operation, the caller first checks IsReady,
            // and if true, attempts to perform the operation itself.
            // If this returns EWOULDBLOCK, or if the queue was not ready, then the operation
            // is enqueued by calling StartAsyncOperation and the state becomes Waiting.
            // When an epoll notification is received, we check if the state is Waiting,
            // and if so, change the state to Processing and enqueue a workitem to the threadpool
            // to try to perform the enqueued operations.
            // If an operation is successfully performed, we remove it from the queue,
            // enqueue another threadpool workitem to process the next item in the queue (if any),
            // and call the user's completion callback.
            // If we successfully process all enqueued operations, then the state becomes Ready;
            // otherwise, the state becomes Waiting and we wait for another epoll notification.

            private enum QueueState : int
            {
                Ready = 0,          // Indicates that data MAY be available on the socket.
                                    // Queue must be empty.
                Waiting = 1,        // Indicates that data is definitely not available on the socket.
                                    // Queue must not be empty.
                Processing = 2,     // Indicates that a thread pool item has been scheduled (and may
                                    // be executing) to process the IO operations in the queue.
                                    // Queue must not be empty.
                Stopped = 3,        // Indicates that the queue has been stopped because the
                                    // socket has been closed.
                                    // Queue must be empty.
            }

            // These fields define the queue state.

            private QueueState _state;      // See above
            private bool _isNextOperationSynchronous;
            private int _sequenceNumber;    // This sequence number is updated when we receive an epoll notification.
                                            // It allows us to detect when a new epoll notification has arrived
                                            // since the last time we checked the state of the queue.
                                            // If this happens, we MUST retry the operation, otherwise we risk
                                            // "losing" the notification and causing the operation to pend indefinitely.
            private AsyncOperation? _tail;   // Queue of pending IO operations to process when data becomes available.

            // The _queueLock is used to ensure atomic access to the queue state above.
            // The lock is only ever held briefly, to read and/or update queue state, and
            // never around any external call, e.g. OS call or user code invocation.
            private object _queueLock;

            private LockToken Lock() => new LockToken(_queueLock);

            public bool IsNextOperationSynchronous_Speculative => _isNextOperationSynchronous;

            public void Init()
            {
                Debug.Assert(_queueLock == null);
                _queueLock = new object();

                _state = QueueState.Ready;
                _sequenceNumber = 0;
            }

            // IsReady returns whether an operation can be executed immediately.
            // observedSequenceNumber must be passed to StartAsyncOperation.
            public bool IsReady(SocketAsyncContext context, out int observedSequenceNumber)
            {
                // It is safe to read _state and _sequence without using Lock.
                // - The return value is soley based on Volatile.Read of _state.
                // - The Volatile.Read of _sequenceNumber ensures we read a value before executing the operation.
                //   This is needed to retry the operation in StartAsyncOperation in case the _sequenceNumber incremented.
                // - Because no Lock is taken, it is possible we observe a sequence number increment before the state
                //   becomes Ready. When that happens, observedSequenceNumber is decremented, and StartAsyncOperation will
                //   execute the operation because the sequence number won't match.

                Debug.Assert(sizeof(QueueState) == sizeof(int));
                QueueState state = (QueueState)Volatile.Read(ref Unsafe.As<QueueState, int>(ref _state));
                observedSequenceNumber = Volatile.Read(ref _sequenceNumber);

                bool isReady = state == QueueState.Ready || state == QueueState.Stopped;
                if (!isReady)
                {
                    observedSequenceNumber--;
                }

                Trace(context, $"{isReady}");

                return isReady;
            }

            // Return true for pending, false for completed synchronously (including failure and abort)
            public bool StartAsyncOperation(SocketAsyncContext context, TOperation operation, int observedSequenceNumber, CancellationToken cancellationToken = default)
            {
                Trace(context, $"Enter");

                if (!context.IsRegistered)
                {
                    context.Register();
                }

                while (true)
                {
                    bool doAbort = false;
                    using (Lock())
                    {
                        switch (_state)
                        {
                            case QueueState.Ready:
                                if (observedSequenceNumber != _sequenceNumber)
                                {
                                    // The queue has become ready again since we previously checked it.
                                    // So, we need to retry the operation before we enqueue it.
                                    Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                    observedSequenceNumber = _sequenceNumber;
                                    break;
                                }

                                // Caller tried the operation and got an EWOULDBLOCK, so we need to transition.
                                _state = QueueState.Waiting;
                                goto case QueueState.Waiting;

                            case QueueState.Waiting:
                            case QueueState.Processing:
                                // Enqueue the operation.
                                Debug.Assert(operation.Next == operation, "Expected operation.Next == operation");

                                if (_tail == null)
                                {
                                    Debug.Assert(!_isNextOperationSynchronous);
                                    _isNextOperationSynchronous = operation.Event != null;
                                }
                                else
                                {
                                    operation.Next = _tail.Next;
                                    _tail.Next = operation;
                                }

                                _tail = operation;
                                Trace(context, $"Leave, enqueued {IdOf(operation)}");

                                // Now that the object is enqueued, hook up cancellation.
                                // Note that it's possible the call to register itself could
                                // call TryCancel, so we do this after the op is fully enqueued.
                                if (cancellationToken.CanBeCanceled)
                                {
                                    operation.CancellationRegistration = cancellationToken.UnsafeRegister(s => ((TOperation)s!).TryCancel(), operation);
                                }

                                return true;

                            case QueueState.Stopped:
                                Debug.Assert(_tail == null);
                                doAbort = true;
                                break;

                            default:
                                Environment.FailFast("unexpected queue state");
                                break;
                        }
                    }

                    if (doAbort)
                    {
                        operation.DoAbort();
                        Trace(context, $"Leave, queue stopped");
                        return false;
                    }

                    // Retry the operation.
                    if (operation.TryComplete(context))
                    {
                        Trace(context, $"Leave, retry succeeded");
                        return false;
                    }
                }
            }

            // TODO: This is a modified version of above for sync operations.
            // It doesn't actually invoke the operation....
            // Returns aborted: true if the op was aborted due to queue being stopped
            // Returns retry: true if we need to retry due to updated seq number
            // Returns retry: false if we enqueued and will be signalled later.
            public (bool aborted, bool retry, int observedSequenceNumber) StartSyncOperation(SocketAsyncContext context, TOperation operation, int observedSequenceNumber, CancellationToken cancellationToken = default)
            {
                Trace(context, $"Enter");

                // TODO: This could probably be popped up a level, or handled differently...
                if (!context.IsRegistered)
                {
                    context.Register();
                }

                bool doAbort = false;
                using (Lock())
                {
                    switch (_state)
                    {
                        case QueueState.Ready:
                            if (observedSequenceNumber != _sequenceNumber)
                            {
                                // The queue has become ready again since we previously checked it.
                                // So, we need to retry the operation before we enqueue it.
                                Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                observedSequenceNumber = _sequenceNumber;
                                break;
                            }

                            // Caller tried the operation and got an EWOULDBLOCK, so we need to transition.
                            _state = QueueState.Waiting;
                            goto case QueueState.Waiting;

                        case QueueState.Waiting:
                        case QueueState.Processing:
                            // Enqueue the operation.
                            Debug.Assert(operation.Next == operation, "Expected operation.Next == operation");

                            if (_tail == null)
                            {
                                Debug.Assert(!_isNextOperationSynchronous);
                                _isNextOperationSynchronous = operation.Event != null;
                            }
                            else
                            {
                                operation.Next = _tail.Next;
                                _tail.Next = operation;
                            }

                            _tail = operation;
                            Trace(context, $"Leave, enqueued {IdOf(operation)}");

                            // Now that the object is enqueued, hook up cancellation.
                            // Note that it's possible the call to register itself could
                            // call TryCancel, so we do this after the op is fully enqueued.
                            if (cancellationToken.CanBeCanceled)
                            {
                                operation.CancellationRegistration = cancellationToken.UnsafeRegister(s => ((TOperation)s!).TryCancel(), operation);
                            }

                            return (aborted: false, retry: false, observedSequenceNumber: 0);

                        case QueueState.Stopped:
                            Debug.Assert(_tail == null);
                            doAbort = true;
                            break;

                        default:
                            Environment.FailFast("unexpected queue state");
                            break;
                    }
                }

                if (doAbort)
                {
                    operation.DoAbort();
                    Trace(context, $"Leave, queue stopped");
                    return (aborted: true, retry: false, observedSequenceNumber: 0);
                }

                // Tell the caller to retry the operation.
                Trace(context, $"Leave, signal retry");
                return (aborted: false, retry: true, observedSequenceNumber: observedSequenceNumber);
            }

            // Note, I changed the default of processAsyncEvents to false.
            // I believe this is more correct, but it's still a change in behavior...
            public AsyncOperation? ProcessSyncEventOrGetAsyncEvent(SocketAsyncContext context, bool skipAsyncEvents = false, bool processAsyncEvents = false)
            {
                // This path is hacked out for now
                Debug.Assert(!processAsyncEvents);

                AsyncOperation op;
                using (Lock())
                {
                    Trace(context, $"Enter");

                    switch (_state)
                    {
                        case QueueState.Ready:
                            Debug.Assert(_tail == null, "State == Ready but queue is not empty!");
                            _sequenceNumber++;
                            Trace(context, $"Exit (previously ready)");
                            return null;

                        case QueueState.Waiting:
                            Debug.Assert(_tail != null, "State == Waiting but queue is empty!");
                            op = _tail.Next;
                            Debug.Assert(_isNextOperationSynchronous == (op.Event != null));
                            if (skipAsyncEvents && !_isNextOperationSynchronous)
                            {
                                Debug.Assert(!processAsyncEvents);
                                // Return the operation to indicate that the async operation was not processed, without making
                                // any state changes because async operations are being skipped
                                return op;
                            }

                            _state = QueueState.Processing;
                            // Break out and release lock
                            break;

                        case QueueState.Processing:
                            Debug.Assert(_tail != null, "State == Processing but queue is empty!");
                            _sequenceNumber++;
                            Trace(context, $"Exit (currently processing)");
                            return null;

                        case QueueState.Stopped:
                            Debug.Assert(_tail == null);
                            Trace(context, $"Exit (stopped)");
                            return null;

                        default:
                            Environment.FailFast("unexpected queue state");
                            return null;
                    }
                }

                ManualResetEventSlim? e = op.Event;
                if (e != null)
                {
                    // Sync operation.  Signal waiting thread to continue processing.
                    e.Set();
                    return null;
                }
                else if (op.CompletionSource is not null)
                {
                    op.CompletionSource.TrySetResult();
                    return null;
                }
                else
                {
                    // Async operation.  The caller will figure out how to process the IO.
                    Debug.Assert(!skipAsyncEvents);
                    if (processAsyncEvents)
                    {
                        op.Process();
                        return null;
                    }
                    return op;
                }
            }

            // Here's what I basically want to do.
            // I want to do something similar to the sync logic I have now.
            // Except, I want to make the wait asynchronous, via a TaskCompletionSource type of thing
            // Don't worry about the expense for now...

            // My plan is to invoke ops similarly to the sync path, but using a TCS
            // and singalling that TCS in all the places where we signal the Event today
            // So, if that works, this code path ends up unused
            // But, it's doing cancellation registration dispose, so that's probably worth understanding more....

            // Re cancellation registration, we really should see how the cancellation token is used in the caller... may be unnecessary at this point...

            // This is called on a thread pool thread when we think we are ready to process an operation.
            // It's invoked from the epoll thread queuing work to the thread pool.

            /// NOTE:
            // Eventually this code shouldn't be invoked anymore, because the TCS logic will supersede it.
            // However, let's at least keep it around for now as it's instructive, if nothing else.

            internal void ProcessAsyncOperation(TOperation op)
            {
                OperationResult result = ProcessQueuedOperation(op);

                Debug.Assert(op.Event == null, "Sync operation encountered in ProcessAsyncOperation");

                if (result == OperationResult.Completed)
                {
                    // At this point, the operation has completed and it's no longer
                    // in the queue / no one else has a reference to it.  We can invoke
                    // the callback and let it pool the object if appropriate. This is
                    // also a good time to unregister from cancellation; we must do
                    // so before the object is returned to the pool (or else a cancellation
                    // request for a previous operation could affect a subsequent one)
                    // and here we know the operation has completed.
                    op.CancellationRegistration.Dispose();
                    op.InvokeCallback(allowPooling: true);
                }
            }

            public enum OperationResult
            {
                Pending = 0,
                Completed = 1,
                Cancelled = 2
            }

            public OperationResult ProcessQueuedOperation(TOperation op)
            {
                bool cancelled;
                int observedSequenceNumber;
                (cancelled, observedSequenceNumber) = GetQueuedOperationStatus(op);
                if (cancelled)
                {
                    return OperationResult.Cancelled;
                }

                while (true)
                {
                    // Try to perform the IO
                    // TODO: Why does TryComplete take a context if the op already has it?
                    if (op.TryComplete(op.AssociatedContext))
                    {
                        CompleteQueuedOperation(op);
                        return OperationResult.Completed;
                    }

                    bool retry;
                    (cancelled, retry, observedSequenceNumber) = PendQueuedOperation(op, observedSequenceNumber);
                    if (cancelled)
                    {
                        return OperationResult.Cancelled;
                    }

                    if (!retry)
                    {
                        return OperationResult.Pending;
                    }
                }
            }

            // Returns true if cancelled or queue stopped, false if op should be tried
            public (bool cancelled, int observedSequenceNumber) GetQueuedOperationStatus(TOperation op)
            {
                int observedSequenceNumber;
                using (Lock())
                {
                    Trace(op.AssociatedContext, $"Enter");

                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        Trace(op.AssociatedContext, $"Exit (stopped)");
                        return (true, 0);
                    }
                    else
                    {
                        Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");
                        Debug.Assert(_tail != null, "Unexpected empty queue while processing I/O");
                        Debug.Assert(op == _tail.Next, "Operation is not at head of queue???");
                        observedSequenceNumber = _sequenceNumber;
                    }
                }

                // Try to change the op state to Running.
                // If this fails, it means the operation was previously cancelled,
                // and we should just remove it from the queue without further processing.
                if (!op.TrySetRunning())
                {
                    RemoveQueuedOperation(op);
                    return (true, 0);
                }

                return (false, observedSequenceNumber);
            }

            public void CompleteQueuedOperation(TOperation op)
            {
                op.SetComplete();
                RemoveQueuedOperation(op);
            }

            // We tried the op and it didn't complete.
            // Set it to pend again, unless we need to retry again
            public (bool cancelled, bool retry, int observedSequenceNumber) PendQueuedOperation(TOperation op, int observedSequenceNumber)
            {
                op.SetWaiting();

                // Check for retry and reset queue state.

                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        Trace(op.AssociatedContext, $"Exit (stopped)");
                        return (true, false, 0);
                    }
                    else
                    {
                        Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");

                        if (observedSequenceNumber != _sequenceNumber)
                        {
                            // We received another epoll notification since we previously checked it.
                            // So, we need to retry the operation.
                            Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                            observedSequenceNumber = _sequenceNumber;
                        }
                        else
                        {
                            _state = QueueState.Waiting;
                            Trace(op.AssociatedContext, $"Exit (received EAGAIN)");
                            return (false, false, 0);
                        }
                    }
                }

                // Try to change the op state to Running.
                // If this fails, it means the operation was previously cancelled,
                // and we should just remove it from the queue without further processing.
                if (!op.TrySetRunning())
                {
                    RemoveQueuedOperation(op);
                    return (true, false, 0);
                }

                return (false, true, observedSequenceNumber);
            }

            public void RemoveQueuedOperation(TOperation op)
            {
                // Remove the op from the queue and see if there's more to process.
                AsyncOperation? nextOp = null;
                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        Trace(op.AssociatedContext, $"Exit (stopped)");
                    }
                    else
                    {
                        Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");
                        Debug.Assert(_tail!.Next == op, "Queue modified while processing queue");

                        if (op == _tail)
                        {
                            // No more operations to process
                            _tail = null;
                            _isNextOperationSynchronous = false;
                            _state = QueueState.Ready;
                            _sequenceNumber++;
                            Trace(op.AssociatedContext, $"Exit (finished queue)");
                        }
                        else
                        {
                            // Pop current operation and advance to next
                            nextOp = _tail.Next = op.Next;
                            _isNextOperationSynchronous = nextOp.Event != null;
                        }
                    }
                }

                nextOp?.Dispatch();
            }

            public void CancelAndContinueProcessing(TOperation op)
            {
                // Note, only sync operations use this method.
                Debug.Assert(op.Event != null);

                // Remove operation from queue.
                // Note it must be there since it can only be processed and removed by the caller.
                AsyncOperation? nextOp = null;
                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                    }
                    else
                    {
                        Debug.Assert(_tail != null, "Unexpected empty queue in CancelAndContinueProcessing");

                        if (_tail.Next == op)
                        {
                            // We're the head of the queue
                            if (op == _tail)
                            {
                                // No more operations
                                _tail = null;
                                _isNextOperationSynchronous = false;
                            }
                            else
                            {
                                // Pop current operation and advance to next
                                _tail.Next = op.Next;
                                _isNextOperationSynchronous = op.Next.Event != null;
                            }

                            // We're the first op in the queue.
                            if (_state == QueueState.Processing)
                            {
                                // The queue has already handed off execution responsibility to us.
                                // We need to dispatch to the next op.
                                if (_tail == null)
                                {
                                    _state = QueueState.Ready;
                                    _sequenceNumber++;
                                }
                                else
                                {
                                    nextOp = _tail.Next;
                                }
                            }
                            else if (_state == QueueState.Waiting)
                            {
                                if (_tail == null)
                                {
                                    _state = QueueState.Ready;
                                    _sequenceNumber++;
                                }
                            }
                        }
                        else
                        {
                            // We're not the head of the queue.
                            // Just find this op and remove it.
                            AsyncOperation current = _tail.Next;
                            while (current.Next != op)
                            {
                                current = current.Next;
                            }

                            if (current.Next == _tail)
                            {
                                _tail = current;
                            }
                            current.Next = current.Next.Next;
                        }
                    }
                }

                nextOp?.Dispatch();
            }

            // Called when the socket is closed.
            public bool StopAndAbort(SocketAsyncContext context)
            {
                bool aborted = false;

                // We should be called exactly once, by SafeSocketHandle.
                Debug.Assert(_state != QueueState.Stopped);

                using (Lock())
                {
                    Trace(context, $"Enter");

                    Debug.Assert(_state != QueueState.Stopped);

                    _state = QueueState.Stopped;

                    if (_tail != null)
                    {
                        AsyncOperation op = _tail;
                        do
                        {
                            aborted |= op.TryCancel();
                            op = op.Next;
                        } while (op != _tail);
                    }

                    _tail = null;
                    _isNextOperationSynchronous = false;

                    Trace(context, $"Exit");
                }

                return aborted;
            }

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void Trace(SocketAsyncContext context, string message, [CallerMemberName] string? memberName = null)
            {
                string queueType =
                    typeof(TOperation) == typeof(ReadOperation) ? "recv" :
                    typeof(TOperation) == typeof(WriteOperation) ? "send" :
                    "???";

                OutputTrace($"{IdOf(context)}-{queueType}.{memberName}: {message}, {_state}-{_sequenceNumber}, {((_tail == null) ? "empty" : "not empty")}");
            }
        }

        private readonly SafeSocketHandle _socket;
        private OperationQueue<ReadOperation> _receiveQueue;
        private OperationQueue<WriteOperation> _sendQueue;
        private SocketAsyncEngine? _asyncEngine;
        private bool IsRegistered => _asyncEngine != null;
        private bool _nonBlockingSet;

        private readonly object _registerLock = new object();

        public SocketAsyncContext(SafeSocketHandle socket)
        {
            _socket = socket;

            _receiveQueue.Init();
            _sendQueue.Init();
        }

        public bool PreferInlineCompletions
        {
            // Socket.PreferInlineCompletions is an experimental API with internal access modifier.
            // DynamicDependency ensures the setter is available externally using reflection.
            [DynamicDependency("set_PreferInlineCompletions", typeof(Socket))]
            get => _socket.PreferInlineCompletions;
        }

        private void Register()
        {
            Debug.Assert(_nonBlockingSet);
            lock (_registerLock)
            {
                if (_asyncEngine == null)
                {
                    bool addedRef = false;
                    try
                    {
                        _socket.DangerousAddRef(ref addedRef);
                        IntPtr handle = _socket.DangerousGetHandle();
                        Volatile.Write(ref _asyncEngine, SocketAsyncEngine.RegisterSocket(handle, this));

                        Trace("Registered");
                    }
                    finally
                    {
                        if (addedRef)
                        {
                            _socket.DangerousRelease();
                        }
                    }
                }
            }
        }

        public bool StopAndAbort()
        {
            bool aborted = false;

            // Drain queues
            aborted |= _sendQueue.StopAndAbort(this);
            aborted |= _receiveQueue.StopAndAbort(this);

            // We don't need to synchronize with Register.
            // This method is called when the handle gets released.
            // The Register method will throw ODE when it tries to use the handle at this point.
            _asyncEngine?.UnregisterSocket(_socket.DangerousGetHandle());

            return aborted;
        }

        public void SetNonBlocking()
        {
            //
            // Our sockets may start as blocking, and later transition to non-blocking, either because the user
            // explicitly requested non-blocking mode, or because we need non-blocking mode to support async
            // operations.  We never transition back to blocking mode, to avoid problems synchronizing that
            // transition with the async infrastructure.
            //
            // Note that there's no synchronization here, so we may set the non-blocking option multiple times
            // in a race.  This should be fine.
            //
            if (!_nonBlockingSet)
            {
                if (Interop.Sys.Fcntl.SetIsNonBlocking(_socket, 1) != 0)
                {
                    throw new SocketException((int)SocketPal.GetSocketErrorForErrorCode(Interop.Sys.GetLastError()));
                }

                _nonBlockingSet = true;
            }
        }

        private SyncOperationState2<ReadOperation> CreateReadOperationState(int timeout)
        {
            // TODO: Cache and/or defer operation

            return new SyncOperationState2<ReadOperation>(new DumbSyncReceiveOperation(this), timeout: timeout);
        }

        private SyncOperationState2<WriteOperation> CreateWriteOperationState(int timeout)
        {
            // TODO: Cache and/or defer operation

            return new SyncOperationState2<WriteOperation>(new DumbSyncSendOperation(this), timeout: timeout);
        }

        private SyncOperationState2<ReadOperation> CreateAsyncReadOperationState(CancellationToken cancellationToken)
        {
            // TODO: Cache and/or defer operation

            return new SyncOperationState2<ReadOperation>(new DumbSyncReceiveOperation(this), cancellationToken: cancellationToken);
        }

        private SyncOperationState2<WriteOperation> CreateAsyncWriteOperationState(CancellationToken cancellationToken)
        {
            // TODO: Cache and/or defer operation

            return new SyncOperationState2<WriteOperation>(new DumbSyncSendOperation(this), cancellationToken: cancellationToken);
        }

        // Note, this isn;t sync-specific anymore

        private struct SyncOperationState2<T>
            where T : AsyncOperation2<T>
        {
            private bool _isStarted;            // TODO: these should be combined into a single state? But I probably should look at stuff like StartSyncOperation in more detail
            private bool _isInQueue;
            private int _timeout;
            private CancellationToken _cancellationToken;
            private int _observedSequenceNumber;
            private T _operation;

            public SyncOperationState2(T operation, int timeout = -1, CancellationToken cancellationToken = default)
            {
                Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

                _timeout = timeout;
                _cancellationToken = cancellationToken;

                _isStarted = false;
                _isInQueue = false;
                _observedSequenceNumber = 0;

                _operation = operation;
            }

            // TODO: COuld be merged below?
            private bool WaitForSyncSignal()
            {
                DateTime waitStart = DateTime.UtcNow;

                if (!_operation.Event!.Wait(_timeout))
                {
                    _operation.ErrorCode = SocketError.TimedOut;
                    return false;
                }

                // Reset the event now to avoid lost notifications if the processing is unsuccessful.
                _operation.Event!.Reset();

                // Adjust timeout for next attempt.
                if (_timeout > 0)
                {
                    _timeout -= (DateTime.UtcNow - waitStart).Milliseconds;

                    if (_timeout <= 0)
                    {
                        _operation.ErrorCode = SocketError.TimedOut;
                        return false;
                    }
                }

                return true;
            }

            // Note we don't handle cancellation here, for now at least.
            // It will be handled by going through the AsyncOperation.TryCancel path.
            // We probably want to revist this, but not yet.

            private async ValueTask<bool> WaitForAsyncSignal()
            {
                Debug.Assert(_operation.CompletionSource is not null);

                await _operation.CompletionSource.Task.ConfigureAwait(false);

                // Reallocate the TCS now to avoid lost notifications if the processing is unsuccessful.
                // TODO: Obviously this is suboptimal, not clear what's better; revisit later

                _operation.CompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                return true;
            }

            // False means cancellation (or timeout); error is in [socketError]
            public (bool retry, SocketError socketError) WaitForSyncRetry()
            {
                bool cancelled;
                bool retry;

                if (!_isStarted)
                {
                    _isStarted = true;
                    if (_operation.OperationQueue.IsReady(_operation.AssociatedContext, out _observedSequenceNumber))
                    {
                        return (true, default);
                    }
                }

                if (!_isInQueue)
                {
                    // TODO: Look at this
                    SocketError errorCode;
                    if (!_operation.AssociatedContext.ShouldRetrySyncOperation(out errorCode))
                    {
                        return (false, errorCode);
                    }

                    // Allocate the event we will wait on
                    _operation.Event = new ManualResetEventSlim(false, 0);

                    (cancelled, retry, _observedSequenceNumber) = _operation.OperationQueue.StartSyncOperation(_operation.AssociatedContext, _operation, _observedSequenceNumber);
                    if (cancelled)
                    {
                        Cleanup();
                        return (false, _operation.ErrorCode);
                    }

                    if (retry)
                    {
                        Cleanup();
                        return (true, default);
                    }

                    _isInQueue = true;
                }
                else
                {
                    // We just tried to execute the queued operation, but it failed.
                    (cancelled, retry, _observedSequenceNumber) = _operation.OperationQueue.PendQueuedOperation(_operation, _observedSequenceNumber);
                    if (cancelled)
                    {
                        Cleanup();
                        return (false, _operation.ErrorCode);
                    }

                    if (retry)
                    {
                        return (true, default);
                    }
                }

                if (!WaitForSyncSignal())
                {
                    // Timeout occurred. Error code is set.
                    _operation.OperationQueue.CancelAndContinueProcessing(_operation);

                    Cleanup();
                    return (false, _operation.ErrorCode);
                }

                // We've been signalled to try to process the operation.
                (cancelled, _observedSequenceNumber) = _operation.OperationQueue.GetQueuedOperationStatus(_operation);
                if (cancelled)
                {
                    Cleanup();
                    return (false, _operation.ErrorCode);
                }

                return (true, default);
            }

            // TODO: This shares a lot of logic with the above sync routine, but
            // I'll wait to simplify/unify it until I have a better sense of how the queue simplfication shakes out.

            // TODO: This is calling StartSyncOperation below, does that matter? Not sure what the difference is here...
            // I don't think this matters, as it's really just a modified version of StartAsyncOperation. COnsider.


            // TODO: Add a CancellationToken argument

            // False means cancellation (or timeout); error is in [socketError]
            // TODO: Clarify, does this throw on CT cancellation or return appropriate error?
            public async ValueTask<(bool retry, SocketError socketError)> WaitForAsyncRetry()
            {
                bool cancelled;
                bool retry;

                // temporary -- should be unnecessary, revisit later

                try
                {
                    if (!_isStarted)
                    {
                        _isStarted = true;
                        if (_operation.OperationQueue.IsReady(_operation.AssociatedContext, out _observedSequenceNumber))
                        {
                            return (true, default);
                        }
                    }

                    if (!_isInQueue)
                    {
                        // TODO: This doesn't make any sense for async operations,
                        // but since it only acts when the socket is blocking, it shouldn't actually do any harm.
                        SocketError errorCode;
                        if (!_operation.AssociatedContext.ShouldRetrySyncOperation(out errorCode))
                        {
                            return (false, errorCode);
                        }

                        // Allocate the TCS we will wait on
                        _operation.CompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                        (cancelled, retry, _observedSequenceNumber) = _operation.OperationQueue.StartSyncOperation(_operation.AssociatedContext, _operation, _observedSequenceNumber, _cancellationToken);
                        if (cancelled)
                        {
                            Cleanup();
                            return (false, _operation.ErrorCode);
                        }

                        if (retry)
                        {
                            Cleanup();
                            return (true, default);
                        }

                        _isInQueue = true;
                    }
                    else
                    {
                        // We just tried to execute the queued operation, but it failed.
                        (cancelled, retry, _observedSequenceNumber) = _operation.OperationQueue.PendQueuedOperation(_operation, _observedSequenceNumber);
                        if (cancelled)
                        {
                            Cleanup();
                            return (false, _operation.ErrorCode);
                        }

                        if (retry)
                        {
                            return (true, default);
                        }
                    }

                    if (!await WaitForAsyncSignal().ConfigureAwait(false))
                    {
                        // Cancellation occurred. Error code is set.
                        _operation.OperationQueue.CancelAndContinueProcessing(_operation);

                        Cleanup();
                        return (false, _operation.ErrorCode);
                    }

                    // We've been signalled to try to process the operation.
                    (cancelled, _observedSequenceNumber) = _operation.OperationQueue.GetQueuedOperationStatus(_operation);
                    if (cancelled)
                    {
                        Cleanup();
                        return (false, _operation.ErrorCode);
                    }

                    return (true, default);
                }
                catch (Exception e)
                {
                    Debug.Fail($"Unexpected exception in WaitForAsyncRetry: {e}");
                    throw;
                }
            }

            public void Complete()
            {
                if (_isInQueue)
                {
                    _operation.OperationQueue.CompleteQueuedOperation(_operation);
                }

                Cleanup();
            }

            private void Cleanup()
            {
                _operation.Event?.Dispose();
                _operation.Event = null;

                _operation.CompletionSource = null;

                // Note: this used to happen in ProcessAsyncOperation, but this seems like the appropriate place to do it now.

                _operation.CancellationRegistration.Dispose();
            }
        }

        private bool ShouldRetrySyncOperation(out SocketError errorCode)
        {
            if (_nonBlockingSet)
            {
                errorCode = SocketError.Success;    // Will be ignored
                return true;
            }

            // We are in blocking mode, so the EAGAIN we received indicates a timeout.
            errorCode = SocketError.TimedOut;
            return false;
        }

        private void ProcessAsyncReadOperation(ReadOperation op) => _receiveQueue.ProcessAsyncOperation(op);

        private void ProcessAsyncWriteOperation(WriteOperation op) => _sendQueue.ProcessAsyncOperation(op);

        public SocketError Accept(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");

            SocketError errorCode;

            var state = CreateReadOperationState(-1);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    acceptedFd = default;
                    return errorCode;
                }

                if (SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
                {
                    state.Complete();
                    return errorCode;
                }
            }
        }

        public SocketError AcceptAsync(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, Action<IntPtr, byte[], int, SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
            {
                Debug.Assert(errorCode == SocketError.Success || acceptedFd == (IntPtr)(-1), $"Unexpected values: errorCode={errorCode}, acceptedFd={acceptedFd}");

                return errorCode;
            }

            AcceptOperation operation = RentAcceptOperation();
            operation.Callback = callback;
            operation.SocketAddress = socketAddress;
            operation.SocketAddressLen = socketAddressLen;

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                acceptedFd = operation.AcceptedFileDescriptor;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            acceptedFd = (IntPtr)(-1);
            return SocketError.IOPending;
        }

        public SocketError Connect(byte[] socketAddress, int socketAddressLen)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");

            SocketError errorCode;

            // Connect is different than the usual "readiness" pattern of other operations.
            // We need to call TryStartConnect to initiate the connect with the OS,
            // before we try to complete it via epoll notification.
            // Thus, always call TryStartConnect regardless of readiness.
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            var state = CreateWriteOperationState(-1);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    return errorCode;
                }

                if (SocketPal.TryCompleteConnect(_socket, socketAddressLen, out errorCode))
                {
                    state.Complete();
                    _socket.RegisterConnectResult(errorCode);
                    return errorCode;
                }
            }
        }

        public SocketError ConnectAsync(byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            // Connect is different than the usual "readiness" pattern of other operations.
            // We need to initiate the connect before we try to complete it.
            // Thus, always call TryStartConnect regardless of readiness.
            SocketError errorCode;
            int observedSequenceNumber;
            _sendQueue.IsReady(this, out observedSequenceNumber);
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            var operation = new ConnectOperation(this)
            {
                Callback = callback,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                return operation.ErrorCode;
            }

            return SocketError.IOPending;
        }

        public SocketError Receive(Memory<byte> buffer, SocketFlags flags, int timeout, out int bytesReceived)
        {
            int socketAddressLen = 0;
            return ReceiveFrom(buffer, ref flags, null, ref socketAddressLen, timeout, out bytesReceived);
        }

        public SocketError Receive(Span<byte> buffer, SocketFlags flags, int timeout, out int bytesReceived)
        {
            int socketAddressLen = 0;
            return ReceiveFrom(buffer, ref flags, null, ref socketAddressLen, timeout, out bytesReceived);
        }

        public SocketError ReceiveAsync(Memory<byte> buffer, SocketFlags flags, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken)
        {
            int socketAddressLen = 0;
            return ReceiveFromAsync(buffer, flags, null, ref socketAddressLen, out bytesReceived, out receivedFlags, callback, cancellationToken);
        }

        public SocketError ReceiveFrom(Memory<byte> buffer, ref SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, int timeout, out int bytesReceived)
        {
            return ReceiveFrom(buffer.Span, ref flags, socketAddress, ref socketAddressLen,  timeout, out bytesReceived);
        }

        public unsafe SocketError ReceiveFrom(Span<byte> buffer, ref SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, int timeout, out int bytesReceived)
        {
            SocketError errorCode;

            var state = CreateReadOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    bytesReceived = default;
                    return errorCode;
                }

                if (SocketPal.TryCompleteReceiveFrom(_socket, buffer, flags, socketAddress, ref socketAddressLen, out bytesReceived, out SocketFlags receivedFlags, out errorCode))
                {
                    state.Complete();
                    flags = receivedFlags;
                    return errorCode;
                }
            }
        }

#if false   // New async path, not working...
        private async ValueTask<(SocketError socketError, int bytesReceived)> InternalReceiveAsync(Memory<byte> buffer, SocketFlags flags, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode) = await state.WaitForAsyncRetry().ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default);
                }

                int bytesReceived;
                if (SocketPal.TryCompleteReceive(_socket, buffer.Span, flags, out bytesReceived, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesReceived);
                }
            }
        }

        public SocketError ReceiveAsync(Memory<byte> buffer, SocketFlags flags, out int bytesReceived, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            ValueTask<(SocketError socketError, int bytesReceived)> vt = InternalReceiveAsync(buffer, flags, cancellationToken);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesReceived) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesReceived;
                    (socketError, bytesReceived) = vt.GetAwaiter().GetResult();
                    callback(bytesReceived, null, 0, SocketFlags.None, socketError);
                });

                bytesReceived = default;
                return SocketError.IOPending;
            }
        }
#endif

        public SocketError ReceiveAsync(Memory<byte> buffer, SocketFlags flags, out int bytesReceived, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceive(_socket, buffer.Span, flags, out bytesReceived, out errorCode))
            {
                return errorCode;
            }

            BufferMemoryReceiveOperation operation = RentBufferMemoryReceiveOperation();
            operation.SetReceivedFlags = false;
            operation.Callback = callback;
            operation.Buffer = buffer;
            operation.Flags = flags;
            operation.SocketAddress = null;
            operation.SocketAddressLen = 0;

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber, cancellationToken))
            {
                bytesReceived = operation.BytesTransferred;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            bytesReceived = 0;
            return SocketError.IOPending;
        }
        public SocketError ReceiveFromAsync(Memory<byte> buffer, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffer.Span, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                return errorCode;
            }

            BufferMemoryReceiveOperation operation = RentBufferMemoryReceiveOperation();
            operation.SetReceivedFlags = true;
            operation.Callback = callback;
            operation.Buffer = buffer;
            operation.Flags = flags;
            operation.SocketAddress = socketAddress;
            operation.SocketAddressLen = socketAddressLen;

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber, cancellationToken))
            {
                receivedFlags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            bytesReceived = 0;
            receivedFlags = SocketFlags.None;
            return SocketError.IOPending;
        }

        public SocketError Receive(IList<ArraySegment<byte>> buffers, SocketFlags flags, int timeout, out int bytesReceived)
        {
            return ReceiveFrom(buffers, ref flags, null, 0, timeout, out bytesReceived);
        }

        public SocketError ReceiveAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return ReceiveFromAsync(buffers, flags, null, ref socketAddressLen, out bytesReceived, out receivedFlags, callback);
        }

        public SocketError ReceiveFrom(IList<ArraySegment<byte>> buffers, ref SocketFlags flags, byte[]? socketAddress, int socketAddressLen, int timeout, out int bytesReceived)
        {
            SocketError errorCode;

            var state = CreateReadOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    bytesReceived = default;
                    return errorCode;
                }

                if (SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out SocketFlags receivedFlags, out errorCode))
                {
                    state.Complete();
                    flags = receivedFlags;
                    return errorCode;
                }
            }
        }

        public SocketError ReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                // Synchronous success or failure
                return errorCode;
            }

            BufferListReceiveOperation operation = RentBufferListReceiveOperation();
            operation.Callback = callback;
            operation.Buffers = buffers;
            operation.Flags = flags;
            operation.SocketAddress = socketAddress;
            operation.SocketAddressLen = socketAddressLen;

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                receivedFlags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            receivedFlags = SocketFlags.None;
            bytesReceived = 0;
            return SocketError.IOPending;
        }

        public SocketError ReceiveMessageFrom(
            Memory<byte> buffer, IList<ArraySegment<byte>>? buffers, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, int timeout, out IPPacketInformation ipPacketInformation, out int bytesReceived)
        {
            SocketError errorCode;

            var state = CreateReadOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    bytesReceived = default;
                    ipPacketInformation = default;
                    return errorCode;
                }

                if (SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer.Span, buffers, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out SocketFlags receivedFlags, out ipPacketInformation, out errorCode))
                {
                    state.Complete();
                    flags = receivedFlags;
                    return errorCode;
                }
            }
        }

        public SocketError ReceiveMessageFromAsync(Memory<byte> buffer, IList<ArraySegment<byte>>? buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, out int bytesReceived, out SocketFlags receivedFlags, out IPPacketInformation ipPacketInformation, Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer.Span, buffers, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
            {
                return errorCode;
            }

            var operation = new ReceiveMessageFromOperation(this)
            {
                Callback = callback,
                Buffer = buffer,
                Buffers = buffers,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                IsIPv4 = isIPv4,
                IsIPv6 = isIPv6,
            };

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                receivedFlags = operation.ReceivedFlags;
                ipPacketInformation = operation.IPPacketInformation;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            ipPacketInformation = default(IPPacketInformation);
            bytesReceived = 0;
            receivedFlags = SocketFlags.None;
            return SocketError.IOPending;
        }

        public SocketError Send(ReadOnlySpan<byte> buffer, SocketFlags flags, int timeout, out int bytesSent) =>
            SendTo(buffer, flags, null, 0, timeout, out bytesSent);

        public SocketError Send(byte[] buffer, int offset, int count, SocketFlags flags, int timeout, out int bytesSent)
        {
            return SendTo(buffer, offset, count, flags, null, 0, timeout, out bytesSent);
        }

        public SocketError SendAsync(Memory<byte> buffer, int offset, int count, SocketFlags flags, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken)
        {
            int socketAddressLen = 0;
            return SendToAsync(buffer, offset, count, flags, null, ref socketAddressLen, out bytesSent, callback, cancellationToken);
        }

        public SocketError SendTo(byte[] buffer, int offset, int count, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            return SendTo(new ReadOnlySpan<byte>(buffer, offset, count), flags, socketAddress, socketAddressLen, timeout, out bytesSent);
        }

        public unsafe SocketError SendTo(ReadOnlySpan<byte> buffer, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            SocketError errorCode;

            bytesSent = 0;
            int bufferIndexIgnored = 0, offset = 0, count = buffer.Length;
            var state = CreateWriteOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    return errorCode;
                }

                if (SocketPal.TryCompleteSendTo(_socket, buffer, null, ref bufferIndexIgnored, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return errorCode;
                }
            }
        }

        public SocketError SendToAsync(Memory<byte> buffer, int offset, int count, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffer.Span, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            BufferMemorySendOperation operation = RentBufferMemorySendOperation();
            operation.Callback = callback;
            operation.Buffer = buffer;
            operation.Offset = offset;
            operation.Count = count;
            operation.Flags = flags;
            operation.SocketAddress = socketAddress;
            operation.SocketAddressLen = socketAddressLen;
            operation.BytesTransferred = bytesSent;

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber, cancellationToken))
            {
                bytesSent = operation.BytesTransferred;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            return SocketError.IOPending;
        }

        public SocketError Send(IList<ArraySegment<byte>> buffers, SocketFlags flags, int timeout, out int bytesSent)
        {
            return SendTo(buffers, flags, null, 0, timeout, out bytesSent);
        }

        public SocketError SendAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return SendToAsync(buffers, flags, null, ref socketAddressLen, out bytesSent, callback);
        }

        public SocketError SendTo(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            SocketError errorCode;

            bytesSent = 0;
            int bufferIndex = 0, offset = 0;
            var state = CreateWriteOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    return errorCode;
                }

                if (SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return errorCode;
                }
            }
        }

        public SocketError SendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            int bufferIndex = 0;
            int offset = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            BufferListSendOperation operation = RentBufferListSendOperation();
            operation.Callback = callback;
            operation.Buffers = buffers;
            operation.BufferIndex = bufferIndex;
            operation.Offset = offset;
            operation.Flags = flags;
            operation.SocketAddress = socketAddress;
            operation.SocketAddressLen = socketAddressLen;
            operation.BytesTransferred = bytesSent;

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                bytesSent = operation.BytesTransferred;
                errorCode = operation.ErrorCode;

                ReturnOperation(operation);
                return errorCode;
            }

            return SocketError.IOPending;
        }

        public SocketError SendFile(SafeFileHandle fileHandle, long offset, long count, int timeout, out long bytesSent)
        {
            SocketError errorCode;

            bytesSent = 0;
            var state = CreateWriteOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForSyncRetry();
                if (!retry)
                {
                    return errorCode;
                }

                if (SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return errorCode;
                }
            }
        }

        public SocketError SendFileAsync(SafeFileHandle fileHandle, long offset, long count, out long bytesSent, Action<long, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendFileOperation(this)
            {
                Callback = callback,
                FileHandle = fileHandle,
                Offset = offset,
                Count = count,
                BytesTransferred = bytesSent
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            return SocketError.IOPending;
        }

        // Called on the epoll thread, speculatively tries to process synchronous events and errors for synchronous events, and
        // returns any remaining events that remain to be processed. Taking a lock for each operation queue to deterministically
        // handle synchronous events on the epoll thread seems to significantly reduce throughput in benchmarks. On the other
        // hand, the speculative checks make it nondeterministic, where it would be possible for the epoll thread to think that
        // the next operation in a queue is not synchronous when it is (due to a race, old caches, etc.) and cause the event to
        // be scheduled instead. It's not functionally incorrect to schedule the release of a synchronous operation, just it may
        // lead to thread pool starvation issues if the synchronous operations are blocking thread pool threads (typically not
        // advised) and more threads are not immediately available to run work items that would release those operations.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Interop.Sys.SocketEvents HandleSyncEventsSpeculatively(Interop.Sys.SocketEvents events)
        {
        // NOTE: I moved this code up to caller. it's not necessary here anymore, and shouldn't really have been here in the first place.
            if ((events & Interop.Sys.SocketEvents.Error) != 0)
            {
                // Set the Read and Write flags; the processing for these events
                // will pick up the error.
                events ^= Interop.Sys.SocketEvents.Error;
                events |= Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write;
            }

            if ((events & Interop.Sys.SocketEvents.Read) != 0 &&
                _receiveQueue.IsNextOperationSynchronous_Speculative &&
                _receiveQueue.ProcessSyncEventOrGetAsyncEvent(this, skipAsyncEvents: true) == null)
            {
                events ^= Interop.Sys.SocketEvents.Read;
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0 &&
                _sendQueue.IsNextOperationSynchronous_Speculative &&
                _sendQueue.ProcessSyncEventOrGetAsyncEvent(this, skipAsyncEvents: true) == null)
            {
                events ^= Interop.Sys.SocketEvents.Write;
            }

            return events;
        }

#if false
        // Called on the epoll thread.
        public void HandleEventsInline(Interop.Sys.SocketEvents events)
        {
            if ((events & Interop.Sys.SocketEvents.Error) != 0)
            {
                // Set the Read and Write flags; the processing for these events
                // will pick up the error.
                events ^= Interop.Sys.SocketEvents.Error;
                events |= Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write;
            }

            if ((events & Interop.Sys.SocketEvents.Read) != 0)
            {
                _receiveQueue.ProcessSyncEventOrGetAsyncEvent(this, processAsyncEvents: true);
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0)
            {
                _sendQueue.ProcessSyncEventOrGetAsyncEvent(this, processAsyncEvents: true);
            }
        }
#endif

        // Called on ThreadPool thread.
        public unsafe void HandleEvents(Interop.Sys.SocketEvents events)
        {
            Debug.Assert((events & Interop.Sys.SocketEvents.Error) == 0);

            AsyncOperation? receiveOperation =
                (events & Interop.Sys.SocketEvents.Read) != 0 ? _receiveQueue.ProcessSyncEventOrGetAsyncEvent(this) : null;
            AsyncOperation? sendOperation =
                (events & Interop.Sys.SocketEvents.Write) != 0 ? _sendQueue.ProcessSyncEventOrGetAsyncEvent(this) : null;

            // This method is called from a thread pool thread. When we have only one operation to process, process it
            // synchronously to avoid an extra thread pool work item. When we have two operations to process, processing both
            // synchronously may delay the second operation, so schedule one onto the thread pool and process the other
            // synchronously. There might be better ways of doing this.
            if (sendOperation == null)
            {
                receiveOperation?.Process();
            }
            else
            {
                receiveOperation?.Schedule();
                sendOperation.Process();
            }
        }

        //
        // Tracing stuff
        //

        // To enabled tracing:
        // (1) Add reference to System.Console in the csproj
        // (2) #define SOCKETASYNCCONTEXT_TRACE

        [Conditional("SOCKETASYNCCONTEXT_TRACE")]
        public void Trace(string message, [CallerMemberName] string? memberName = null)
        {
            OutputTrace($"{IdOf(this)}.{memberName}: {message}");
        }

        [Conditional("SOCKETASYNCCONTEXT_TRACE")]
        public static void OutputTrace(string s)
        {
            // CONSIDER: Change to NetEventSource
#if SOCKETASYNCCONTEXT_TRACE
            Console.WriteLine(s);
#endif
        }

        public static string IdOf(object o) => o == null ? "(null)" : $"{o.GetType().Name}#{o.GetHashCode():X2}";
    }
}

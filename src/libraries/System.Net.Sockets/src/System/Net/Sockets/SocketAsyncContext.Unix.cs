// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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
        // TODO: The whole AsyncOperation infrastructure should die.
        // As far as I can tell, I really only need this for cancellation handling now, so look at how to get rid of that.
        private abstract class AsyncOperation
        {
            public readonly SocketAsyncContext AssociatedContext;

            public ManualResetEventSlim? Event { get; set; }
            public TaskCompletionSource<bool>? CompletionSource { get; set; }

            public AsyncOperation(SocketAsyncContext context)
            {
                AssociatedContext = context;
                Event = null;
                CompletionSource = null;
            }

            public void Signal()
            {
                ManualResetEventSlim? e = Event;
                TaskCompletionSource<bool>? tcs = CompletionSource;
                if (e != null)
                {
                    e.Set();
                }
                else if (tcs is not null)
                {
                    tcs.TrySetResult(false);
                }
                else
                {
                    Debug.Assert(false);
                }
            }
        }

        private abstract class AsyncOperation2<T> : AsyncOperation
            where T : AsyncOperation
        {
            public AsyncOperation2(SocketAsyncContext context) : base(context) { }

            public abstract ref OperationQueue OperationQueue { get; }
        }

        // These two abstract classes differentiate the operations that go in the
        // read queue vs the ones that go in the write queue.
        private abstract class ReadOperation : AsyncOperation2<ReadOperation>
        {
            public ReadOperation(SocketAsyncContext context) : base(context) { }

            public sealed override ref OperationQueue OperationQueue => ref AssociatedContext._receiveQueue;
        }

        private abstract class WriteOperation : AsyncOperation2<WriteOperation>
        {
            public WriteOperation(SocketAsyncContext context) : base(context) { }

            public sealed override ref OperationQueue OperationQueue => ref AssociatedContext._sendQueue;
        }


        // Note, these aren't specific to sync anymore. Basically just dummy operations.

        private sealed class DumbSyncReceiveOperation : ReadOperation
        {
            public DumbSyncReceiveOperation(SocketAsyncContext context) : base(context) { }
        }

        private sealed class DumbSyncSendOperation : WriteOperation
        {
            public DumbSyncSendOperation(SocketAsyncContext context) : base(context) { }
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

        private struct OperationQueue
        {
            // The ultimate goal here is to get rid of QueueState and the lock and sequence number.

            // This is new stuff:
            public SemaphoreSlim _semaphore;
            // TODO: This is not getting disposed currently.

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

            // This replaces the old sequence number.
            private bool _dataAvailable;

            private AsyncOperation? _currentOperation;

            // The _queueLock is used to ensure atomic access to the queue state above.
            // The lock is only ever held briefly, to read and/or update queue state, and
            // never around any external call, e.g. OS call or user code invocation.
            private object _queueLock;

            private LockToken Lock() => new LockToken(_queueLock);

            public void Init()
            {
                Debug.Assert(_queueLock == null);
                _queueLock = new object();

                _dataAvailable = true;

                _semaphore = new SemaphoreSlim(1, 1);
            }

            // IsReady returns whether an operation can be executed immediately.
            // To be clear, this always returns true, but it also resets _dataAvailable so we can check it later on.
            public bool IsReady(SocketAsyncContext context)
            {
                // We don't care if it's stopped.
                using (Lock())
                {
                    // Note, we really should check for readiness anyway; it's not really anymore expensive is it?

                    // We always return true from this.
                    // The semaphore will enforce serialization of operations.
                    _dataAvailable = false;
                    return true;
                }
            }

            public void OnReady()
            {
                AsyncOperation? toSignal = null;
                using (Lock())
                {
                    _dataAvailable = true;
                    if (_currentOperation is not null)
                    {
                        toSignal = _currentOperation;
                        _currentOperation = null;
                    }
                }

                toSignal?.Signal();
            }

            // This is called when we are about to try the operation, after a notification has been received.
            public void StartOperation()
            {
                using (Lock())
                {
                    Debug.Assert(_dataAvailable == true);
                    Debug.Assert(_currentOperation == null);

                    // Reset _dataAvailable for subsequent attempts
                    _dataAvailable = false;
                }
            }

            // TODO: "DataAvailable" is an inappropriate term for sending.
            // Consider changing this to something like "Ready"

            // This is called after an operation completes and we believe there's still more data available,
            // so that the next operation will proceed immediately without waiting for a data notification.
            public void SetDataAvailable()
            {
                using (Lock())
                {
                    Debug.Assert(_currentOperation == null);

                    _dataAvailable = true;
                }
            }

            // Return true if pending needed, false if not
            // If [true] is returned, [op] will not be signalled
            // If [false] is returned, [op] will be signalled on a callback-capable thread
            // TODO: Should this take Action, or Action<object> + state?
            public bool WaitForDataAvailable(AsyncOperation op)
            {
                using (Lock())
                {
                    Debug.Assert(_currentOperation == null);

                    if (_dataAvailable)
                    {
                        _dataAvailable = false;
                        return true;
                    }
                    else
                    {
                        _currentOperation = op;
                        return false;
                    }
                }
            }

            // This is only called in the case of sync timeout or async cancellation via CancellationToken.
            // All it really does now is set _dataAvailable =true; is that worthwhile?
            public void CancelAndContinueProcessing()
            {
                // Remove operation from queue.
                // Note it must be there since it can only be processed and removed by the caller.
                using (Lock())
                {
                    // Clear out current operation; it's been canceled
                    _currentOperation = null;

                    // Just assume there is data available.
                    _dataAvailable = true;
                }
            }

            // Called when the socket is closed.
            public bool StopAndAbort(SocketAsyncContext context)
            {
                bool aborted = false;

                AsyncOperation? toSignal = null;
                using (Lock())
                {
                    // TODO: This should just call Signal or whatever
                    if (_currentOperation != null)
                    {
                        toSignal = _currentOperation;
                        _currentOperation = null;

                        _dataAvailable = true;

                        aborted = true;
                    }
                }

                toSignal?.Signal();

                return aborted;
            }
        }

        private readonly SafeSocketHandle _socket;
        private OperationQueue _receiveQueue;
        private OperationQueue _sendQueue;
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

        private static ValueTask<(bool, SocketError, SyncOperationState2<ReadOperation>)> WaitForReadAsyncRetry(SyncOperationState2<ReadOperation> state) =>
            SyncOperationState2<ReadOperation>.WaitForDataAvailableAsync(state);

        private static ValueTask<(bool, SocketError, SyncOperationState2<WriteOperation>)> WaitForWriteAsyncRetry(SyncOperationState2<WriteOperation> state) =>
            SyncOperationState2<WriteOperation>.WaitForDataAvailableAsync(state);

        //private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("SOCKETTRACE") == "1";
        private const bool TraceEnabled = true;

        [System.Runtime.InteropServices.DllImport("libc")] private static extern int printf(string format, string arg);

        //private static void Print(string s) => printf("%s\r\n", s);
        private static void Print(string s)
        {
            if (TraceEnabled)
            {
                printf("%s\r\n", s);
            }
        }

        // Note, this isn;t sync-specific anymore

        private struct SyncOperationState2<T>
            where T : AsyncOperation2<T>
        {
            private bool _isStarted;    // TODO: Consider promoting the lock handling to callers
            private DateTime? _expiration;
            private CancellationToken _cancellationToken;
            private T _operation;

            public SyncOperationState2(T operation, int timeout = -1, CancellationToken cancellationToken = default)
            {
                Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

                _expiration = timeout == -1 ? null : DateTime.UtcNow.AddMilliseconds(timeout);

                _cancellationToken = cancellationToken;

                _isStarted = false;

                _operation = operation;
            }

            private int CurrentTimeout =>
                (_expiration is null) ? -1 :
                Math.Max((int)(_expiration.Value - DateTime.UtcNow).TotalMilliseconds, 0);

            // TODO: Consider separating the timeout adjustment into a helper, and use it for waiting on data signal too.

            private bool WaitForSemaphoreSync()
            {
                Print($"WaitForSemaphoreSync: CurrentTimeout = {CurrentTimeout}");

                return _operation.OperationQueue._semaphore.Wait(CurrentTimeout);
            }

            private async ValueTask<bool> WaitForSemaphoreAsync()
            {
                try
                {
                    await _operation.OperationQueue._semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                return true;
            }

            private void ReleaseSemaphore()
            {
                _operation.OperationQueue._semaphore.Release();
            }

            // TODO: COuld be merged below?
            private bool WaitForSyncSignal()
            {
                return _operation.Event!.Wait(CurrentTimeout);
            }

            private static async ValueTask<(bool, SyncOperationState2<T>)> WaitForAsyncSignal(SyncOperationState2<T> state)
            {
                Debug.Assert(state._operation.CompletionSource is not null);

                CancellationTokenRegistration registration = default;
                if (state._cancellationToken.CanBeCanceled)
                {
                    registration = state._cancellationToken.Register((tcs, tkn) => ((TaskCompletionSource<bool>)tcs!).TrySetResult(true), state._operation.CompletionSource);
                }

                bool cancelled = await state._operation.CompletionSource.Task.ConfigureAwait(false);
                registration.Dispose();

                if (cancelled)
                {
                    return (false, state);
                }

                return (true, state);
            }

            // False means cancellation (or timeout); error is in [socketError]
            public (bool retry, SocketError socketError) WaitForDataAvailable()
            {
                bool retry;

                if (!_isStarted)
                {
                    if (!WaitForSemaphoreSync())
                    {
                        // Timeout
                        return (false, SocketError.TimedOut);
                    }

                    _isStarted = true;
                    if (_operation.OperationQueue.IsReady(_operation.AssociatedContext))
                    {
                        return (true, default);
                    }
                }

                // This is a test to determine if the EWOULDBLOCK we received previously
                // was an actual timeout on a blocking socket, or just a regular EWOULDBLOCK on a non-blocking socket.
                // TODO: This works today because IsReady above never returns false. But it could, and we should handle this differently...
                SocketError errorCode;
                if (!_operation.AssociatedContext.ShouldRetrySyncOperation(out errorCode))
                {
                    Cleanup();
                    return (false, errorCode);
                }

                // TODO: This could go somewhere else
                if (!_operation.AssociatedContext.IsRegistered)
                {
                    _operation.AssociatedContext.Register();
                }

                // Allocate the event we will wait on
                // TODO: This is suboptimal, obviously...
                _operation.Event = new ManualResetEventSlim(false, 0);


                retry = _operation.OperationQueue.WaitForDataAvailable(_operation);
                if (retry)
                {
                    return (true, default);
                }

                if (!WaitForSyncSignal())
                {
                    // Timeout occurred.
                    _operation.OperationQueue.CancelAndContinueProcessing();

                    Cleanup();
                    return (false, SocketError.TimedOut);
                }

                // We've been signalled to try to process the operation.
                _operation.OperationQueue.StartOperation();

                return (true, default);
            }

            // TODO: This shares a lot of logic with the above sync routine, but
            // I'll wait to simplify/unify it until I have a better sense of how the queue simplfication shakes out.

            // TODO: This is calling StartSyncOperation below, does that matter? Not sure what the difference is here...
            // I don't think this matters, as it's really just a modified version of StartAsyncOperation. COnsider.


            // TODO: Add a CancellationToken argument

            // False means cancellation (or timeout); error is in [socketError]
            // TODO: Clarify, does this throw on CT cancellation or return appropriate error?

            // NOTE: This needs to be static because this is a struct.

            public static async ValueTask<(bool retry, SocketError socketError, SyncOperationState2<T> state)> WaitForDataAvailableAsync(SyncOperationState2<T> state)
            {
                bool retry;

                // TODO: This could go somewhere else
                if (!state._operation.AssociatedContext.IsRegistered)
                {
                    state._operation.AssociatedContext.Register();
                }

                try
                {
                    // CONSIDER: Move semaphore handling to caller
                    if (!state._isStarted)
                    {
                        if (!await state.WaitForSemaphoreAsync().ConfigureAwait(false))
                        {
                            // Cancellation occurred
                            return (false, SocketError.OperationAborted, state);
                        }

                        state._isStarted = true;
                        if (state._operation.OperationQueue.IsReady(state._operation.AssociatedContext))
                        {
                            return (true, default, state);
                        }
                    }

                    // Allocate the TCS we will wait on
                    // TODO: We don't always wait on this (i.e. retry) -- need to be better about how we allocate this.
                    state._operation.CompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    retry = state._operation.OperationQueue.WaitForDataAvailable(state._operation);
                    if (retry)
                    {
                        return (true, default, state);
                    }

                    bool success;
                    (success, state) = await WaitForAsyncSignal(state).ConfigureAwait(false);

                    if (!success)
                    {
                        // Cancellation occurred. Error code is set.
                        state._operation.OperationQueue.CancelAndContinueProcessing();

                        state.Cleanup();
                        return (false, SocketError.OperationAborted, state);
                    }

                    // We've been signalled to try to process the operation.
                    state._operation.OperationQueue.StartOperation();

                    return (true, default, state);
                }
                catch (Exception e)
                {
                    Debug.Fail($"Unexpected exception in WaitForAsyncRetry: {e}");
                    throw;
                }
            }

            public void Complete()
            {
                // We completed the operation. So we need to set data available so the next operation will try immediately.
                _operation.OperationQueue.SetDataAvailable();
                Cleanup();
            }

            private void Cleanup()
            {
                _operation.Event?.Dispose();
                _operation.Event = null;

                _operation.CompletionSource = null;

                if (_isStarted)
                {
                    ReleaseSemaphore();
                }
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

        public SocketError Accept(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");

            SocketError errorCode;

            var state = CreateReadOperationState(-1);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForDataAvailable();
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

        private async ValueTask<(SocketError socketError, int socketAddressLen, IntPtr acceptedFd)> InternalAcceptAsync(byte[] socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForReadAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default);
                }

                IntPtr acceptedFd;
                if (SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
                {
                    state.Complete();
                    return (errorCode, socketAddressLen, acceptedFd);
                }
            }
        }

        public SocketError AcceptAsync(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, Action<IntPtr, byte[], int, SocketError> callback)
        {
            ValueTask<(SocketError socketError, int socketAddressLen, IntPtr acceptedFd)> vt = InternalAcceptAsync(socketAddress, socketAddressLen, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, socketAddressLen, acceptedFd) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int socketAddressLen;
                    IntPtr acceptedFd;
                    (socketError, socketAddressLen, acceptedFd) = vt.GetAwaiter().GetResult();
                    callback(acceptedFd, socketAddress, socketAddressLen, socketError);
                });

                acceptedFd = default;
                return SocketError.IOPending;
            }
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
                (retry, errorCode) = state.WaitForDataAvailable();
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

        private async ValueTask<SocketError> InternalConnectAsync(byte[] socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            // Connect is different than the usual "readiness" pattern of other operations.
            // We need to initiate the connect before we try to complete it.
            // Thus, always call TryStartConnect regardless of readiness.
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            var state = CreateAsyncWriteOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForWriteAsyncRetry(state).ConfigureAwait(false);
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
            ValueTask<SocketError> vt = InternalConnectAsync(socketAddress, socketAddressLen, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                socketError = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    socketError = vt.GetAwaiter().GetResult();
                    callback(socketError);
                });

                return SocketError.IOPending;
            }
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
                (retry, errorCode) = state.WaitForDataAvailable();
                if (!retry)
                {
                    bytesReceived = default;
                    return errorCode;
                }

                try
                {
                    if (SocketPal.TryCompleteReceiveFrom(_socket, buffer, flags, socketAddress, ref socketAddressLen, out bytesReceived, out SocketFlags receivedFlags, out errorCode))
                    {
                        state.Complete();
                        flags = receivedFlags;
                        return errorCode;
                    }
                }
                catch (Exception e)
                {
                    Debug.Fail($"Caught exception: {e}");
                }
            }
        }

        private async ValueTask<(SocketError socketError, int bytesReceived)> InternalReceiveAsync(Memory<byte> buffer, SocketFlags flags, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForReadAsyncRetry(state).ConfigureAwait(false);
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

        private async ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags)>
            InternalReceiveFromAsync(Memory<byte> buffer, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForReadAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default, default);
                }

                int bytesReceived;
                SocketFlags receivedFlags;
                if (SocketPal.TryCompleteReceiveFrom(_socket, buffer.Span, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesReceived, socketAddressLen, receivedFlags);
                }
            }
        }


        public SocketError ReceiveFromAsync(Memory<byte> buffer, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags)> vt = InternalReceiveFromAsync(buffer, flags, socketAddress, socketAddressLen, cancellationToken);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesReceived, socketAddressLen, receivedFlags) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesReceived;
                    int socketAddressLen;
                    SocketFlags receivedFlags;
                    (socketError, bytesReceived, socketAddressLen, receivedFlags) = vt.GetAwaiter().GetResult();
                    callback(bytesReceived, socketAddress, socketAddressLen, receivedFlags, socketError);
                });

                bytesReceived = default;
                receivedFlags = default;
                return SocketError.IOPending;
            }
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
                (retry, errorCode) = state.WaitForDataAvailable();
                if (!retry)
                {
                    bytesReceived = default;
                    return errorCode;
                }

                try
                {
                    if (SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out SocketFlags receivedFlags, out errorCode))
                    {
                        state.Complete();
                        flags = receivedFlags;
                        return errorCode;
                    }
                }
                catch
                {
                    // We are throwing an ArgumentNullException way down in SocketPal.SysReceive, validating the supplied buffers
                    // I don't really think this worked properly before, but handle it here for now.
                    state.Complete();
                    throw;
                }
            }
        }

        private async ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags)>
            InternalReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForReadAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default, default);
                }

                int bytesReceived;
                SocketFlags receivedFlags;
                if (SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesReceived, socketAddressLen, receivedFlags);
                }
            }
        }


        public SocketError ReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags)> vt = InternalReceiveFromAsync(buffers, flags, socketAddress, socketAddressLen, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesReceived, socketAddressLen, receivedFlags) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesReceived;
                    int socketAddressLen;
                    SocketFlags receivedFlags;
                    (socketError, bytesReceived, socketAddressLen, receivedFlags) = vt.GetAwaiter().GetResult();
                    callback(bytesReceived, socketAddress, socketAddressLen, receivedFlags, socketError);
                });

                bytesReceived = default;
                receivedFlags = default;
                return SocketError.IOPending;
            }
        }

        public SocketError ReceiveMessageFrom(
            Memory<byte> buffer, IList<ArraySegment<byte>>? buffers, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, int timeout, out IPPacketInformation ipPacketInformation, out int bytesReceived)
        {
            SocketError errorCode;

            var state = CreateReadOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForDataAvailable();
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

        private async ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags, IPPacketInformation ipPacketInformation)>
            InternalReceiveMessageFromAsync(Memory<byte> buffer, IList<ArraySegment<byte>>? buffers, SocketFlags flags, byte[] socketAddress, int socketAddressLen, bool isIPv4, bool isIPv6, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            var state = CreateAsyncReadOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForReadAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default, default, default);
                }

                int bytesReceived;
                SocketFlags receivedFlags;
                IPPacketInformation ipPacketInformation;
                if (SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer.Span, buffers, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesReceived, socketAddressLen, receivedFlags, ipPacketInformation);
                }
            }
        }

        public SocketError ReceiveMessageFromAsync(Memory<byte> buffer, IList<ArraySegment<byte>>? buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, out int bytesReceived, out SocketFlags receivedFlags, out IPPacketInformation ipPacketInformation, Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError> callback)
        {
            ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen, SocketFlags receivedFlags, IPPacketInformation ipPacketInformation)> vt =
                InternalReceiveMessageFromAsync(buffer, buffers, flags, socketAddress, socketAddressLen, isIPv4, isIPv6, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesReceived, socketAddressLen, receivedFlags, ipPacketInformation) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesReceived;
                    int socketAddressLen;
                    SocketFlags receivedFlags;
                    IPPacketInformation ipPacketInformation;
                    (socketError, bytesReceived, socketAddressLen, receivedFlags, ipPacketInformation) = vt.GetAwaiter().GetResult();
                    callback(bytesReceived, socketAddress, socketAddressLen, receivedFlags, ipPacketInformation, socketError);
                });

                bytesReceived = default;
                receivedFlags = default;
                ipPacketInformation = default;
                return SocketError.IOPending;
            }
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
                (retry, errorCode) = state.WaitForDataAvailable();
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

        private async ValueTask<(SocketError socketError, int bytesSent, int socketAddressLen)> InternalSendToAsync(Memory<byte> buffer, int offset, int count, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            int bytesSent = 0;

            var state = CreateAsyncWriteOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForWriteAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default);
                }

                if (SocketPal.TryCompleteSendTo(_socket, buffer.Span, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesSent, socketAddressLen);
                }
            }
        }

        public SocketError SendToAsync(Memory<byte> buffer, int offset, int count, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback, CancellationToken cancellationToken = default)
        {
            ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen)> vt = InternalSendToAsync(buffer, offset, count, flags, socketAddress, socketAddressLen, cancellationToken);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesSent, socketAddressLen) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesSent;
                    int socketAddressLen;
                    (socketError, bytesSent, socketAddressLen) = vt.GetAwaiter().GetResult();
                    callback(bytesSent, socketAddress, socketAddressLen, SocketFlags.None, socketError);
                });

                bytesSent = default;
                return SocketError.IOPending;
            }
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
                (retry, errorCode) = state.WaitForDataAvailable();
                if (!retry)
                {
                    return errorCode;
                }

                try
                {
                    if (SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                    {
                        state.Complete();
                        return errorCode;
                    }
                }
                catch
                {
                    // See ReceiveFrom above
                    state.Complete();
                    throw;
                }
            }
        }

        private async ValueTask<(SocketError socketError, int bytesSent, int socketAddressLen)> InternalSendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, int socketAddressLen, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            int bytesSent = 0;
            int bufferIndex = 0;
            int offset = 0;

            var state = CreateAsyncWriteOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForWriteAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default, default);
                }

                if (SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesSent, socketAddressLen);
                }
            }
        }

        public SocketError SendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[]? socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[]?, int, SocketFlags, SocketError> callback)
        {
            ValueTask<(SocketError socketError, int bytesReceived, int socketAddressLen)> vt = InternalSendToAsync(buffers, flags, socketAddress, socketAddressLen, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesSent, socketAddressLen) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    int bytesSent;
                    int socketAddressLen;
                    (socketError, bytesSent, socketAddressLen) = vt.GetAwaiter().GetResult();
                    callback(bytesSent, socketAddress, socketAddressLen, SocketFlags.None, socketError);
                });

                bytesSent = default;
                return SocketError.IOPending;
            }
        }

        public SocketError SendFile(SafeFileHandle fileHandle, long offset, long count, int timeout, out long bytesSent)
        {
            SocketError errorCode;

            bytesSent = 0;
            var state = CreateWriteOperationState(timeout);
            while (true)
            {
                bool retry;
                (retry, errorCode) = state.WaitForDataAvailable();
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

        private async ValueTask<(SocketError socketError, long bytesSent)> InternalSendFileAsync(SafeFileHandle fileHandle, long offset, long count, CancellationToken cancellationToken)
        {
            SetNonBlocking();

            SocketError errorCode;

            long bytesSent = 0;

            var state = CreateAsyncWriteOperationState(cancellationToken);
            while (true)
            {
                bool retry;
                (retry, errorCode, state) = await WaitForWriteAsyncRetry(state).ConfigureAwait(false);
                if (!retry)
                {
                    return (errorCode, default);
                }

                if (SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
                {
                    state.Complete();
                    return (errorCode, bytesSent);
                }
            }
        }

        public SocketError SendFileAsync(SafeFileHandle fileHandle, long offset, long count, out long bytesSent, Action<long, SocketError> callback)
        {
            ValueTask<(SocketError socketError, long bytesSent)> vt = InternalSendFileAsync(fileHandle, offset, count, CancellationToken.None);
            bool completedSynchronously = vt.IsCompleted;

            if (completedSynchronously)
            {
                SocketError socketError;
                (socketError, bytesSent) = vt.GetAwaiter().GetResult();
                return socketError;
            }
            else
            {
                vt.GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Debug.Assert(vt.IsCompleted);

                    SocketError socketError;
                    long bytesSent;
                    (socketError, bytesSent) = vt.GetAwaiter().GetResult();
                    callback(bytesSent, socketError);
                });

                bytesSent = default;
                return SocketError.IOPending;
            }
        }

        // Called on the epoll thread, speculatively tries to process synchronous events and errors for synchronous events, and
        // returns any remaining events that remain to be processed. Taking a lock for each operation queue to deterministically
        // handle synchronous events on the epoll thread seems to significantly reduce throughput in benchmarks. On the other
        // hand, the speculative checks make it nondeterministic, where it would be possible for the epoll thread to think that
        // the next operation in a queue is not synchronous when it is (due to a race, old caches, etc.) and cause the event to
        // be scheduled instead. It's not functionally incorrect to schedule the release of a synchronous operation, just it may
        // lead to thread pool starvation issues if the synchronous operations are blocking thread pool threads (typically not
        // advised) and more threads are not immediately available to run work items that would release those operations.

        // Ignore above comment.
        // We are now handling all events on the epoll thread.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleEventsOnEpollThread(Interop.Sys.SocketEvents events)
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
                _receiveQueue.OnReady();
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0)
            {
                _sendQueue.OnReady();
            }
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

#if false
        // Called on ThreadPool thread.
        public unsafe void HandleEvents(Interop.Sys.SocketEvents events)
        {
            // This should not be called anymore. All events are handled on the epoll thread now.
            Debug.Assert(false);

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
#endif

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

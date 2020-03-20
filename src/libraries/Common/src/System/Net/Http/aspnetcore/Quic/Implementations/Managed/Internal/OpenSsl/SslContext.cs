﻿namespace System.Net.Quic.Implementations.Managed.Internal.OpenSsl
{
    internal struct SslContext
    {
        public static SslContext Null => default(SslContext);

        private readonly IntPtr _handle;

        private SslContext(IntPtr handle)
        {
            _handle = handle;
        }

        public static SslContext New(SslMethod method)
        {
            return new SslContext(Interop.OpenSslQuic.SslCtxNew(method));
        }

        public static void Free(SslContext ctx)
        {
            Interop.OpenSslQuic.SslCtxFree(ctx._handle);
        }

        public override string ToString()
        {
            return _handle.ToString("x");
        }
    }
}

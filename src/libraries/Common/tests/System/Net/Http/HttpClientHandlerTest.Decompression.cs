// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Test.Common;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    using Configuration = System.Net.Test.Common.Configuration;

#if WINHTTPHANDLER_TEST
    using HttpClientHandler = System.Net.Http.WinHttpClientHandler;
#endif

    public abstract class HttpClientHandler_Decompression_Test : HttpClientHandlerTestBase
    {
#if !NETFRAMEWORK
        private const DecompressionMethods AllMethods = DecompressionMethods.All;
#else
        private const DecompressionMethods AllMethods = DecompressionMethods.Deflate | DecompressionMethods.GZip;
#endif

        public HttpClientHandler_Decompression_Test(ITestOutputHelper output) : base(output) { }

        // Notes:
        // WinHttpHandler supports DecompressionMethods.Deflate incorrectly; it continues to use DeflateStream.
        // WinHttpHandler also does not support DecompressionMethods.Brotli.
        // So these variations are disabled for WinHttpHandler below.


        // Here's what I want in general.
        // I want to use the enum value in the InlineData.
        // I want helpers that will map this to a name and do compression on a byte[] -> byte[]. (Decompression too? Don't think I need it.)

        private static string GetEncodingName(DecompressionMethods method) =>
            method switch
            {
                DecompressionMethods.GZip => "gzip",
                DecompressionMethods.Deflate => "deflate",
                DecompressionMethods.Brotli => "br",
                _ => throw new InvalidOperationException($"unexpected DecompressionMethod {method}")
            };

        private static Stream GetCompressionStream(DecompressionMethods method, Stream s) =>
            method switch
            {
                DecompressionMethods.GZip => new GZipStream(s, CompressionLevel.Optimal, leaveOpen: true),
                DecompressionMethods.Brotli => new BrotliStream(s, CompressionLevel.Optimal, leaveOpen: true),
                DecompressionMethods.Deflate => new ZLibStream(s, CompressionLevel.Optimal, leaveOpen: true),
                _ => throw new InvalidOperationException($"unexpected DecompressionMethod {method}")
            };

        [Theory]
        [InlineData(DecompressionMethods.GZip, false)]
        [InlineData(DecompressionMethods.GZip, true)]
#if !NETFRAMEWORK
        [InlineData(DecompressionMethods.Deflate, false)]
        [InlineData(DecompressionMethods.Deflate, true)]
        [InlineData(DecompressionMethods.Brotli, false)]
        [InlineData(DecompressionMethods.Brotli, true)]
#endif
        [ActiveIssue("https://github.com/dotnet/runtime/issues/39187", TestPlatforms.Browser)]
        public async Task CompressedResponse_DecompressionEnabled_DecompressedContentReturned(DecompressionMethods method, bool enableAll)
        {
            DecompressionMethods methods = enableAll ? AllMethods : method;

            var expectedContent = new byte[12345];
            new Random(42).NextBytes(expectedContent);

            string encodingName = GetEncodingName(method);

            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.AutomaticDecompression = methods;

                    HttpResponseMessage response = await client.GetAsync(uri);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    // Decompression should cause these headers to be removed, since they only apply to the compressed content
                    Assert.False(response.Content.Headers.Contains("Content-Encoding"), "Content-Encoding unexpectedly found");
                    Assert.False(response.Content.Headers.Contains("Content-Length"), "Content-Length unexpectedly found");

                    Assert.Equal<byte>(expectedContent, await response.Content.ReadAsByteArrayAsync());
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    await connection.ReadRequestHeaderAsync();
                    await connection.Writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Encoding: {encodingName}\r\n\r\n");
                    using (Stream compressedStream = GetCompressionStream(method, connection.Stream))
                    {
                        await compressedStream.WriteAsync(expectedContent);
                    }
                });
            });
        }

        public static IEnumerable<object[]> DecompressedResponse_MethodNotSpecified_OriginalContentReturned_MemberData()
        {
            yield return new object[]
            {
                "gzip",
                new Func<Stream, Stream>(s => new GZipStream(s, CompressionLevel.Optimal, leaveOpen: true)),
                DecompressionMethods.None
            };
#if !NETFRAMEWORK
            yield return new object[]
            {
                "deflate",
                new Func<Stream, Stream>(s => new ZLibStream(s, CompressionLevel.Optimal, leaveOpen: true)),
                DecompressionMethods.Brotli
            };
            yield return new object[]
            {
                "br",
                new Func<Stream, Stream>(s => new BrotliStream(s, CompressionLevel.Optimal, leaveOpen: true)),
                DecompressionMethods.Deflate | DecompressionMethods.GZip
            };
#endif
        }

        [Theory]
        [MemberData(nameof(DecompressedResponse_MethodNotSpecified_OriginalContentReturned_MemberData))]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/39187", TestPlatforms.Browser)]
        public async Task CompressedResponse_DecompressionNotEnabled_OriginalContentReturned(
            string encodingName, Func<Stream, Stream> compress, DecompressionMethods methods)
        {
            var expectedContent = new byte[12345];
            new Random(42).NextBytes(expectedContent);

            var compressedContentStream = new MemoryStream();
            using (Stream s = compress(compressedContentStream))
            {
                await s.WriteAsync(expectedContent);
            }
            byte[] compressedContent = compressedContentStream.ToArray();

            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.AutomaticDecompression = methods;
                    Assert.Equal<byte>(compressedContent, await client.GetByteArrayAsync(uri));
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    await connection.ReadRequestHeaderAsync();
                    await connection.Writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Encoding: {encodingName}\r\n\r\n");
                    await connection.Stream.WriteAsync(compressedContent);
                });
            });
        }

        [Theory]
#if NETCOREAPP
        [InlineData(DecompressionMethods.Brotli, "br", "")]
        [InlineData(DecompressionMethods.Brotli, "br", "br")]
        [InlineData(DecompressionMethods.Brotli, "br", "gzip")]
        [InlineData(DecompressionMethods.Brotli, "br", "gzip, deflate")]
#endif
        [InlineData(DecompressionMethods.GZip, "gzip", "")]
        [InlineData(DecompressionMethods.Deflate, "deflate", "")]
        [InlineData(DecompressionMethods.GZip | DecompressionMethods.Deflate, "gzip, deflate", "")]
        [InlineData(DecompressionMethods.GZip, "gzip", "gzip")]
        [InlineData(DecompressionMethods.Deflate, "deflate", "deflate")]
        [InlineData(DecompressionMethods.GZip, "gzip", "deflate")]
        [InlineData(DecompressionMethods.GZip, "gzip", "br")]
        [InlineData(DecompressionMethods.Deflate, "deflate", "gzip")]
        [InlineData(DecompressionMethods.Deflate, "deflate", "br")]
        [InlineData(DecompressionMethods.GZip | DecompressionMethods.Deflate, "gzip, deflate", "gzip, deflate")]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/39187", TestPlatforms.Browser)]
        public async Task GetAsync_SetAutomaticDecompression_AcceptEncodingHeaderSentWithNoDuplicates(
            DecompressionMethods methods,
            string encodings,
            string manualAcceptEncodingHeaderValues)
        {
            // Brotli only supported on SocketsHttpHandler.
            if (IsWinHttpHandler && (encodings.Contains("br") || manualAcceptEncodingHeaderValues.Contains("br")))
            {
                return;
            }

            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                HttpClientHandler handler = CreateHttpClientHandler();
                handler.AutomaticDecompression = methods;

                using (HttpClient client = CreateHttpClient(handler))
                {
                    if (!string.IsNullOrEmpty(manualAcceptEncodingHeaderValues))
                    {
                        client.DefaultRequestHeaders.Add("Accept-Encoding", manualAcceptEncodingHeaderValues);
                    }

                    Task<HttpResponseMessage> clientTask = client.SendAsync(TestAsync, CreateRequest(HttpMethod.Get, url, UseVersion));
                    Task<List<string>> serverTask = server.AcceptConnectionSendResponseAndCloseAsync();
                    await TaskTimeoutExtensions.WhenAllOrAnyFailed(new Task[] { clientTask, serverTask });

                    List<string> requestLines = await serverTask;
                    string requestLinesString = string.Join("\r\n", requestLines);
                    _output.WriteLine(requestLinesString);

                    Assert.InRange(Regex.Matches(requestLinesString, "Accept-Encoding").Count, 1, 1);
                    Assert.InRange(Regex.Matches(requestLinesString, encodings).Count, 1, 1);
                    if (!string.IsNullOrEmpty(manualAcceptEncodingHeaderValues))
                    {
                        Assert.InRange(Regex.Matches(requestLinesString, manualAcceptEncodingHeaderValues).Count, 1, 1);
                    }

                    using (HttpResponseMessage response = await clientTask)
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    }
                }
            });
        }
    }

    public sealed class HttpClientHandler_Decompression_RemoteServer_Test : HttpClientHandlerTestBase
    {
        public HttpClientHandler_Decompression_RemoteServer_Test(ITestOutputHelper output) : base(output) { }

        public static IEnumerable<object[]> RemoteServersAndCompressionUris()
        {
            foreach (Configuration.Http.RemoteServer remoteServer in Configuration.Http.RemoteServers)
            {
                yield return new object[] { remoteServer, remoteServer.GZipUri };

                // Remote deflate endpoint isn't correctly following the deflate protocol.
                //yield return new object[] { remoteServer, remoteServer.DeflateUri };
            }
        }

        [OuterLoop("Uses external servers")]
        [Theory, MemberData(nameof(RemoteServersAndCompressionUris))]
        public async Task GetAsync_SetAutomaticDecompression_ContentDecompressed_GZip(Configuration.Http.RemoteServer remoteServer, Uri uri)
        {
            // Sync API supported only up to HTTP/1.1
            if (!TestAsync && remoteServer.HttpVersion.Major >= 2)
            {
                return;
            }

            HttpClientHandler handler = CreateHttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer, handler))
            {
                using (HttpResponseMessage response = await client.SendAsync(TestAsync, CreateRequest(HttpMethod.Get, uri, remoteServer.HttpVersion)))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    Assert.False(response.Content.Headers.Contains("Content-Encoding"), "Content-Encoding unexpectedly found");
                    Assert.False(response.Content.Headers.Contains("Content-Length"), "Content-Length unexpectedly found");
                    string responseContent = await response.Content.ReadAsStringAsync();

                    _output.WriteLine(responseContent);
                    TestHelper.VerifyResponseBody(
                        responseContent,
                        response.Content.Headers.ContentMD5,
                        false,
                        null);
                }
            }
        }

        // The remote server endpoint was written to use DeflateStream, which isn't actually a correct
        // implementation of the deflate protocol (the deflate protocol requires the zlib wrapper around
        // deflate).  Until we can get that updated (and deal with previous releases still testing it
        // via a DeflateStream-based implementation), we utilize httpbin.org to help validate behavior.
        [OuterLoop("Uses external servers")]
        [Theory]
        [InlineData("http://httpbin.org/deflate", "\"deflated\": true")]
        [InlineData("https://httpbin.org/deflate", "\"deflated\": true")]
        public async Task GetAsync_SetAutomaticDecompression_ContentDecompressed_Deflate(string uri, string expectedContent)
        {
            if (IsWinHttpHandler)
            {
                // WinHttpHandler targets netstandard2.0 and still erroneously uses DeflateStream rather than ZlibStream for deflate.
                return;
            }

            HttpClientHandler handler = CreateHttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpClient client = CreateHttpClient(handler))
            using (HttpResponseMessage response = await client.GetAsync(uri))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                Assert.False(response.Content.Headers.Contains("Content-Encoding"), "Content-Encoding unexpectedly found");
                Assert.False(response.Content.Headers.Contains("Content-Length"), "Content-Length unexpectedly found");

                Assert.Contains(expectedContent, await response.Content.ReadAsStringAsync());
            }
        }
    }
}

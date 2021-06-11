// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Net.Http.Headers
{
    internal static class TransferCodingHeaderParser
    {
        // TODO: Move
        internal static readonly GenericSingleValueHeaderParser<TransferCodingHeaderValue> SingleValueParser = new(TransferCodingHeaderValue.GetTransferCodingLength);
        internal static readonly GenericMultipleValueHeaderParser<TransferCodingHeaderValue> MultipleValueParser = new(TransferCodingHeaderValue.GetTransferCodingLength);
        internal static readonly GenericSingleValueHeaderParser<TransferCodingWithQualityHeaderValue> SingleValueWithQualityParser = new(TransferCodingWithQualityHeaderValue.GetTransferCodingWithQualityLength);
        internal static readonly GenericMultipleValueHeaderParser<TransferCodingWithQualityHeaderValue> MultipleValueWithQualityParser = new(TransferCodingWithQualityHeaderValue.GetTransferCodingWithQualityLength);
    }
}

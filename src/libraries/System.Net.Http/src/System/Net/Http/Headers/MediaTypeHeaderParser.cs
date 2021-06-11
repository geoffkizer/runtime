// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http.Headers
{
    internal static class MediaTypeHeaderParser
    {
        // TODO: Move these
        internal static readonly GenericSingleValueHeaderParser<MediaTypeHeaderValue> SingleValueParser = new(MediaTypeHeaderValue.GetMediaTypeLength);
        internal static readonly GenericSingleValueHeaderParser<MediaTypeWithQualityHeaderValue> SingleValueWithQualityParser = new(MediaTypeWithQualityHeaderValue.GetMediaTypeWithQualityLength);
        internal static readonly GenericMultipleValueHeaderParser<MediaTypeHeaderValue> MultipleValuesParser = new(MediaTypeHeaderValue.GetMediaTypeLength);
    }
}

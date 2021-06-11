// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;

namespace System.Net.Http.Headers
{
    internal delegate int GetParsedValueLengthDelegate<T>(string value, int startIndex, out T? parsedValue);

    internal static class GenericHeaderParser
    {
        internal static readonly GenericSingleValueHeaderParser<string> HostParser = new(ParseHost, StringComparer.OrdinalIgnoreCase);
        internal static readonly GenericMultipleValueHeaderParser<string> TokenListParser = new(ParseTokenList, StringComparer.OrdinalIgnoreCase);
        internal static readonly GenericSingleValueHeaderParser<NameValueWithParametersHeaderValue> SingleValueNameValueWithParametersParser = new(NameValueWithParametersHeaderValue.GetNameValueWithParametersLength);
        internal static readonly GenericMultipleValueHeaderParser<NameValueWithParametersHeaderValue> MultipleValueNameValueWithParametersParser = new(NameValueWithParametersHeaderValue.GetNameValueWithParametersLength);
        internal static readonly GenericSingleValueHeaderParser<NameValueHeaderValue> SingleValueNameValueParser = new(ParseNameValue);
        internal static readonly GenericMultipleValueHeaderParser<NameValueHeaderValue> MultipleValueNameValueParser = new(ParseNameValue);
        internal static readonly GenericSingleValueHeaderParser<string> SingleValueParserWithoutValidation = new(ParseWithoutValidation);
        internal static readonly GenericSingleValueHeaderParser<ProductHeaderValue> SingleValueProductParser = new(ParseProduct);
        internal static readonly GenericMultipleValueHeaderParser<ProductHeaderValue> MultipleValueProductParser = new(ParseProduct);
        internal static readonly GenericSingleValueHeaderParser<RangeConditionHeaderValue> RangeConditionParser = new(RangeConditionHeaderValue.GetRangeConditionLength);
        internal static readonly GenericSingleValueHeaderParser<AuthenticationHeaderValue> SingleValueAuthenticationParser = new(AuthenticationHeaderValue.GetAuthenticationLength);
        internal static readonly GenericMultipleValueHeaderParser<AuthenticationHeaderValue> MultipleValueAuthenticationParser = new(AuthenticationHeaderValue.GetAuthenticationLength);
        internal static readonly GenericSingleValueHeaderParser<RangeHeaderValue> RangeParser = new(RangeHeaderValue.GetRangeLength);
        internal static readonly GenericSingleValueHeaderParser<RetryConditionHeaderValue> RetryConditionParser = new(RetryConditionHeaderValue.GetRetryConditionLength);
        internal static readonly GenericSingleValueHeaderParser<ContentRangeHeaderValue> ContentRangeParser = new(ContentRangeHeaderValue.GetContentRangeLength);
        internal static readonly GenericSingleValueHeaderParser<ContentDispositionHeaderValue> ContentDispositionParser = new(ContentDispositionHeaderValue.GetDispositionTypeLength);
        internal static readonly GenericSingleValueHeaderParser<StringWithQualityHeaderValue> SingleValueStringWithQualityParser = new(StringWithQualityHeaderValue.GetStringWithQualityLength);
        internal static readonly GenericMultipleValueHeaderParser<StringWithQualityHeaderValue> MultipleValueStringWithQualityParser = new(StringWithQualityHeaderValue.GetStringWithQualityLength);
        internal static readonly GenericSingleValueHeaderParser<EntityTagHeaderValue> SingleValueEntityTagParser = new(ParseSingleEntityTag);
        internal static readonly GenericMultipleValueHeaderParser<EntityTagHeaderValue> MultipleValueEntityTagParser = new(ParseMultipleEntityTags);
        internal static readonly GenericSingleValueHeaderParser<ViaHeaderValue> SingleValueViaParser = new(ViaHeaderValue.GetViaLength);
        internal static readonly GenericMultipleValueHeaderParser<ViaHeaderValue> MultipleValueViaParser = new(ViaHeaderValue.GetViaLength);
        internal static readonly GenericSingleValueHeaderParser<WarningHeaderValue> SingleValueWarningParser = new(WarningHeaderValue.GetWarningLength);
        internal static readonly GenericMultipleValueHeaderParser<WarningHeaderValue> MultipleValueWarningParser = new(WarningHeaderValue.GetWarningLength);

        #region Parse methods

        private static int ParseNameValue(string value, int startIndex, out NameValueHeaderValue? parsedValue)
        {
            int resultLength = NameValueHeaderValue.GetNameValueLength(value, startIndex, out NameValueHeaderValue? temp);

            parsedValue = temp;
            return resultLength;
        }

        private static int ParseProduct(string value, int startIndex, out ProductHeaderValue? parsedValue)
        {
            int resultLength = ProductHeaderValue.GetProductLength(value, startIndex, out ProductHeaderValue? temp);

            parsedValue = temp;
            return resultLength;
        }

        private static int ParseSingleEntityTag(string value, int startIndex, out EntityTagHeaderValue? parsedValue)
        {
            parsedValue = null;

            int resultLength = EntityTagHeaderValue.GetEntityTagLength(value, startIndex, out EntityTagHeaderValue? temp);

            // If we don't allow '*' ("Any") as valid ETag value, return false (e.g. 'ETag' header)
            if (temp == EntityTagHeaderValue.Any)
            {
                return 0;
            }

            parsedValue = temp;
            return resultLength;
        }

        // Note that if multiple ETag values are allowed (e.g. 'If-Match', 'If-None-Match'), according to the RFC
        // the value must either be '*' or a list of ETag values. It's not allowed to have both '*' and a list of
        // ETag values. We're not that strict: We allow both '*' and ETag values in a list. If the server sends such
        // an invalid list, we want to be able to represent it using the corresponding header property.
        private static int ParseMultipleEntityTags(string value, int startIndex, out EntityTagHeaderValue? parsedValue)
        {
            int resultLength = EntityTagHeaderValue.GetEntityTagLength(value, startIndex, out EntityTagHeaderValue? temp);

            parsedValue = temp;
            return resultLength;
        }

        /// <summary>
        /// Allows for arbitrary header values without validation (aside from newline, which is always invalid in a header value).
        /// </summary>
        private static int ParseWithoutValidation(string value, int startIndex, out string? parsedValue)
        {
            if (HttpRuleParser.ContainsInvalidNewLine(value, startIndex))
            {
                parsedValue = null;
                return 0;
            }

            string result = value.Substring(startIndex);

            parsedValue = result;
            return result.Length;
        }

        private static int ParseHost(string value, int startIndex, out string? parsedValue)
        {
            int hostLength = HttpRuleParser.GetHostLength(value, startIndex, false, out string? host);

            parsedValue = host;
            return hostLength;
        }

        private static int ParseTokenList(string value, int startIndex, out string? parsedValue)
        {
            int resultLength = HttpRuleParser.GetTokenLength(value, startIndex);

            parsedValue = value.Substring(startIndex, resultLength);
            return resultLength;
        }
        #endregion
    }

    internal sealed class GenericSingleValueHeaderParser<T> : BaseSingleValueHeaderParser<T>
    {
        private readonly GetParsedValueLengthDelegate<T> _getParsedValueLength;
        private readonly IEqualityComparer? _comparer;

        public override IEqualityComparer? Comparer => _comparer;

        internal GenericSingleValueHeaderParser(GetParsedValueLengthDelegate<T> getParsedValueLength)
            : this(getParsedValueLength, null)
        {
        }

        internal GenericSingleValueHeaderParser(GetParsedValueLengthDelegate<T> getParsedValueLength, IEqualityComparer? comparer)
        {
            _getParsedValueLength = getParsedValueLength;
            _comparer = comparer;
        }

        protected override int GetParsedValueLength(string value, int startIndex, object? storeValue, out T? parsedValue) =>
            _getParsedValueLength(value, startIndex, out parsedValue);
    }

    internal sealed class GenericMultipleValueHeaderParser<T> : BaseMultipleValueHeaderParser<T>
    {
        private readonly GetParsedValueLengthDelegate<T> _getParsedValueLength;
        private readonly IEqualityComparer? _comparer;

        public override IEqualityComparer? Comparer => _comparer;

        internal GenericMultipleValueHeaderParser(GetParsedValueLengthDelegate<T> getParsedValueLength)
            : this(getParsedValueLength, null)
        {
        }

        internal GenericMultipleValueHeaderParser(GetParsedValueLengthDelegate<T> getParsedValueLength, IEqualityComparer? comparer)
        {
            _getParsedValueLength = getParsedValueLength;
            _comparer = comparer;
        }

        protected override int GetParsedValueLength(string value, int startIndex, object? storeValue, out T? parsedValue) =>
            _getParsedValueLength(value, startIndex, out parsedValue);
    }
}

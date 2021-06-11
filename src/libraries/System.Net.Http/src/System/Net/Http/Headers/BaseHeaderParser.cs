// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http.Headers
{
    internal abstract class BaseSingleValueHeaderParser<T> : SingleValueHeaderParser<T>
    {
        protected BaseSingleValueHeaderParser()
        {
        }

        /// <summary>
        /// Parses a full header or a segment of a multi-value header.
        /// </summary>
        /// <param name="value">The header value string to parse.</param>
        /// <param name="startIndex">The index to begin parsing at.</param>
        /// <param name="storeValue"></param>
        /// <param name="parsedValue">The resulting value parsed.</param>
        /// <returns>If a value could be parsed, the number of characters used to parse that value. Otherwise, 0.</returns>
        protected abstract int GetParsedValueLength(string value, int startIndex, object? storeValue,
            out T? parsedValue);

#pragma warning disable CS8765 // Doesn't match overriden member nullable attribute on out parameter
        public sealed override bool TryParseValue(string? value, object? storeValue, ref int index,
            out T? parsedValue)
#pragma warning restore CS8765
        {
            parsedValue = default;

            // Single valued headers do not accept an empty string.
            if (string.IsNullOrEmpty(value) || (index == value.Length))
            {
                return false;
            }

            bool separatorFound;
            int current = HeaderUtilities.GetNextNonEmptyOrWhitespaceIndex(value, index, false, out separatorFound);
            if (separatorFound)
            {
                return false;
            }

            if (current == value.Length)
            {
                return false;
            }

            int length = GetParsedValueLength(value, current, storeValue, out T? result);
            if (length == 0)
            {
                return false;
            }

            current = current + length;
            current = HeaderUtilities.GetNextNonEmptyOrWhitespaceIndex(value, current, false, out separatorFound);
            if (separatorFound)
            {
                return false;
            }

            if (current < value.Length)
            {
                // Unexpected trailing data
                return false;
            }

            index = current;
            parsedValue = result!;
            return true;
        }
    }

    internal abstract class BaseMultipleValueHeaderParser<T> : MultipleValueHeaderParser<T>
    {
        protected BaseMultipleValueHeaderParser()
        {
        }

        /// <summary>
        /// Parses a full header or a segment of a multi-value header.
        /// </summary>
        /// <param name="value">The header value string to parse.</param>
        /// <param name="startIndex">The index to begin parsing at.</param>
        /// <param name="storeValue"></param>
        /// <param name="parsedValue">The resulting value parsed.</param>
        /// <returns>If a value could be parsed, the number of characters used to parse that value. Otherwise, 0.</returns>
        protected abstract int GetParsedValueLength(string value, int startIndex, object? storeValue,
            out T? parsedValue);

#pragma warning disable CS8765 // Doesn't match overriden member nullable attribute on out parameter
        public sealed override bool TryParseValue(string? value, object? storeValue, ref int index,
            out T? parsedValue)
#pragma warning restore CS8765
        {
            parsedValue = default;

            // Since multiple values are supported, accept an empty string: The header may
            // be added multiple times to the request/response message. E.g.
            //  Accept: text/xml; q=1
            //  Accept:
            //  Accept: text/plain; q=0.2
            if (string.IsNullOrEmpty(value) || (index == value.Length))
            {
                return true;
            }

            bool separatorFound;
            int current = HeaderUtilities.GetNextNonEmptyOrWhitespaceIndex(value, index, true, out separatorFound);
            if (current == value.Length)
            {
                index = current;
                return true;
            }

            int length = GetParsedValueLength(value, current, storeValue, out T? result);
            if (length == 0)
            {
                return false;
            }

            current = current + length;
            current = HeaderUtilities.GetNextNonEmptyOrWhitespaceIndex(value, current, true, out separatorFound);

            // If we've not reached the end of the string, then we must have a separator; otherwise fail.
            if (!separatorFound && (current < value.Length))
            {
                return false;
            }

            index = current;
            parsedValue = result!;
            return true;
        }
    }
}

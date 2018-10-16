﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers.Reader;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using static System.Text.JsonLab.JsonThrowHelper;

namespace System.Text.JsonLab
{
    public ref partial struct Utf8JsonReader
    {
        // We are using a ulong to represent our nested state, so we can only go 64 levels deep.
        internal const int StackFreeMaxDepth = sizeof(ulong) * 8;

        private readonly ReadOnlySpan<byte> _buffer;

        public long Consumed { get; private set; }

        internal long TokenStartIndex { get; private set; }

        public int MaxDepth
        {
            get
            {
                return _maxDepth;
            }
            set
            {
                if (value <= 0)
                    ThrowArgumentException("Max depth must be positive.");
                _maxDepth = value;
                if (_maxDepth > StackFreeMaxDepth)
                    _stack = new Stack<InternalJsonTokenType>();
            }
        }

        private int _maxDepth;

        private BufferReader<byte> _reader;

        private Stack<InternalJsonTokenType> _stack;

        // Depth tracks the recursive depth of the nested objects / arrays within the JSON data.
        public int Depth { get; private set; }

        // This mask represents a tiny stack to track the state during nested transitions.
        // The first bit represents the state of the current level (1 == object, 0 == array).
        // Each subsequent bit is the parent / containing type (object or array). Since this
        // reader does a linear scan, we only need to keep a single path as we go through the data.
        private ulong _containerMask;

        // These properties are helpers for determining the current state of the reader
        internal bool InArray => !_inObject;
        private bool _inObject;

        /// <summary>
        /// Gets the token type of the last processed token in the JSON stream.
        /// </summary>
        public JsonTokenType TokenType { get; private set; }

        public JsonReaderOptions Options
        {
            get
            {
                return _readerOptions;
            }
            set
            {
                _readerOptions = value;
                if (_readerOptions == JsonReaderOptions.AllowComments && _stack == null)
                    _stack = new Stack<InternalJsonTokenType>();
            }
        }

        private JsonReaderOptions _readerOptions;

        public JsonReaderState State
            => new JsonReaderState
            {
                _containerMask = _containerMask,
                _depth = Depth,
                _inObject = _inObject,
                _stack = _stack,
                _tokenType = TokenType,
                _lineNumber = _lineNumber,
                _position = _position
            };

        /// <summary>
        /// Gets the value as a ReadOnlySpan<byte> of the last processed token. The contents of this
        /// is only relevant when <see cref="TokenType" /> is <see cref="JsonTokenType.Value" /> or
        /// <see cref="JsonTokenType.PropertyName" />. Otherwise, this value should be set to
        /// <see cref="ReadOnlySpan{T}.Empty"/>.
        /// </summary>
        public ReadOnlySpan<byte> Value { get; private set; }

        private readonly bool _isSingleSegment;
        private readonly bool _isFinalBlock;
        private bool _isSingleValue;

        internal bool ConsumedEverything => _isSingleSegment ? Consumed >= (uint)_buffer.Length : _reader.End;

        internal long _lineNumber;
        internal long _position;

        /// <summary>
        /// Constructs a new JsonReader instance. This is a stack-only type.
        /// </summary>
        /// <param name="data">The <see cref="Span{byte}"/> value to consume. </param>
        /// <param name="encoder">An encoder used for decoding bytes from <paramref name="data"/> into characters.</param>
        public Utf8JsonReader(ReadOnlySpan<byte> data)
        {
            _containerMask = 0;
            Depth = 0;
            _inObject = false;
            _stack = null;
            TokenType = JsonTokenType.None;
            _lineNumber = 1;
            _position = 0;

            _isFinalBlock = true;

            _reader = default;
            _isSingleSegment = true;
            _buffer = data;
            Consumed = 0;
            TokenStartIndex = Consumed;
            _maxDepth = StackFreeMaxDepth;
            Value = ReadOnlySpan<byte>.Empty;
            _isSingleValue = true;
            _readerOptions = JsonReaderOptions.Default;
        }

        public Utf8JsonReader(ReadOnlySpan<byte> data, bool isFinalBlock, JsonReaderState state = default)
        {
            if (!state.IsDefault)
            {
                _containerMask = state._containerMask;
                Depth = state._depth;
                _inObject = state._inObject;
                _stack = state._stack;
                TokenType = state._tokenType;
                _lineNumber = state._lineNumber;
                _position = state._position;
            }
            else
            {
                _containerMask = 0;
                Depth = 0;
                _inObject = false;
                _stack = null;
                TokenType = JsonTokenType.None;
                _lineNumber = 1;
                _position = 0;
            }

            _isFinalBlock = isFinalBlock;

            _reader = default;
            _isSingleSegment = true;
            _buffer = data;
            Consumed = 0;
            TokenStartIndex = Consumed;
            _maxDepth = StackFreeMaxDepth;
            Value = ReadOnlySpan<byte>.Empty;
            _isSingleValue = true;
            _readerOptions = JsonReaderOptions.Default;
        }

        /// <summary>
        /// Read the next token from the data buffer.
        /// </summary>
        /// <returns>True if the token was read successfully, else false.</returns>
        public bool Read()
        {
            return _isSingleSegment ? ReadSingleSegment() : ReadMultiSegment(ref _reader);
        }

        public void Skip()
        {
            if (TokenType == JsonTokenType.PropertyName)
            {
                Read();
            }

            if (TokenType == JsonTokenType.StartObject || TokenType == JsonTokenType.StartArray)
            {
                int depth = Depth;
                while (Read() && depth < Depth)
                {
                }
            }
        }

        private void StartObject()
        {
            Depth++;
            if (Depth > MaxDepth)
                ThrowJsonReaderException(ref this, ExceptionResource.ObjectDepthTooLarge);

            _position++;

            if (Depth <= StackFreeMaxDepth)
                _containerMask = (_containerMask << 1) | 1;
            else
                _stack.Push(InternalJsonTokenType.StartObject);

            TokenType = JsonTokenType.StartObject;
            _inObject = true;
        }

        private void EndObject()
        {
            if (!_inObject || Depth <= 0)
                ThrowJsonReaderException(ref this, ExceptionResource.ObjectEndWithinArray);

            if (Depth <= StackFreeMaxDepth)
            {
                _containerMask >>= 1;
                _inObject = (_containerMask & 1) != 0;
            }
            else
            {
                _inObject = _stack.Pop() != InternalJsonTokenType.StartArray;
            }

            Depth--;
            TokenType = JsonTokenType.EndObject;
        }

        private void StartArray()
        {
            Depth++;
            if (Depth > MaxDepth)
                ThrowJsonReaderException(ref this, ExceptionResource.ArrayDepthTooLarge);

            _position++;

            if (Depth <= StackFreeMaxDepth)
                _containerMask = _containerMask << 1;
            else
                _stack.Push(InternalJsonTokenType.StartArray);

            TokenType = JsonTokenType.StartArray;
            _inObject = false;
        }

        private void EndArray()
        {
            if (_inObject || Depth <= 0)
                ThrowJsonReaderException(ref this, ExceptionResource.ArrayEndWithinObject);

            if (Depth <= StackFreeMaxDepth)
            {
                _containerMask >>= 1;
                _inObject = (_containerMask & 1) != 0;
            }
            else
            {
                _inObject = _stack.Pop() != InternalJsonTokenType.StartArray;
            }

            Depth--;
            TokenType = JsonTokenType.EndArray;
        }

        private bool ReadFirstToken(byte first)
        {
            if (first == JsonConstants.OpenBrace)
            {
                Depth++;
                _containerMask = 1;
                TokenType = JsonTokenType.StartObject;
                Consumed++;
                _position++;
                _inObject = true;
                _isSingleValue = false;
            }
            else if (first == JsonConstants.OpenBracket)
            {
                Depth++;
                TokenType = JsonTokenType.StartArray;
                Consumed++;
                _position++;
                _isSingleValue = false;
            }
            else
            {
                if ((uint)(first - '0') <= '9' - '0' || first == '-')
                {
                    if (!TryGetNumber(_buffer.Slice((int)Consumed), out ReadOnlySpan<byte> number))
                        return false;
                    Value = number;
                    TokenType = JsonTokenType.Number;
                    Consumed += Value.Length;
                    _position += Value.Length;
                    goto Done;
                }
                else if (ConsumeValue(first))
                {
                    goto Done;
                }

                return false;

            Done:
                if (Consumed >= (uint)_buffer.Length)
                {
                    return true;
                }

                if (_buffer[(int)Consumed] <= JsonConstants.Space)
                {
                    SkipWhiteSpace();
                    if (Consumed >= (uint)_buffer.Length)
                    {
                        return true;
                    }
                }

                if (_readerOptions != JsonReaderOptions.Default)
                {
                    if (_readerOptions == JsonReaderOptions.AllowComments)
                    {
                        if (TokenType == JsonTokenType.Comment || _buffer[(int)Consumed] == JsonConstants.Solidus)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        // JsonReaderOptions.SkipComments
                        if (_buffer[(int)Consumed] == JsonConstants.Solidus)
                        {
                            return true;
                        }
                    }
                }
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndAfterSingleJson, _buffer[(int)Consumed]);
            }
            return true;
        }

        private bool ReadSingleSegment()
        {
            bool retVal = false;

            if (Consumed >= (uint)_buffer.Length)
            {
                if (!_isSingleValue && _isFinalBlock)
                {
                    if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                        ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                }
                goto Done;
            }

            byte first = _buffer[(int)Consumed];

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpace();
                if (Consumed >= (uint)_buffer.Length)
                {
                    if (!_isSingleValue && _isFinalBlock)
                    {
                        if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                            ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                    }
                    goto Done;
                }
                first = _buffer[(int)Consumed];
            }

            TokenStartIndex = Consumed;

            if (TokenType == JsonTokenType.None)
            {
                goto ReadFirstToken;
            }

            if (TokenType == JsonTokenType.StartObject)
            {
                if (first == JsonConstants.CloseBrace)
                {
                    Consumed++;
                    _position++;
                    EndObject();
                }
                else
                {
                    if (first != JsonConstants.Quote)
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfPropertyNotFound, first);

                    TokenStartIndex++;
                    long prevConsumed = Consumed;
                    long prevPosition = _position;
                    if (ConsumePropertyName())
                    {
                        return true;
                    }
                    Consumed = prevConsumed;
                    TokenType = JsonTokenType.StartObject;
                    _position = prevPosition;
                    return false;
                }
            }
            else if (TokenType == JsonTokenType.StartArray)
            {
                if (first == JsonConstants.CloseBracket)
                {
                    Consumed++;
                    _position++;
                    EndArray();
                }
                else
                {
                    return ConsumeValue(first);
                }
            }
            else if (TokenType == JsonTokenType.PropertyName)
            {
                return ConsumeValue(first);
            }
            else
            {
                long prevConsumed = Consumed;
                long prevPosition = _position;
                JsonTokenType prevTokenType = TokenType;
                InternalResult result = ConsumeNextToken(first);
                if (result == InternalResult.Success)
                {
                    return true;
                }
                if (result == InternalResult.FailureRollback)
                {
                    Consumed = prevConsumed;
                    TokenType = prevTokenType;
                    _position = prevPosition;
                }
                return false;
            }

            retVal = true;

        Done:
            return retVal;

        ReadFirstToken:
            retVal = ReadFirstToken(first);
            goto Done;
        }

        /// <summary>
        /// This method consumes the next token regardless of whether we are inside an object or an array.
        /// For an object, it reads the next property name token. For an array, it just reads the next value.
        /// </summary>
        private InternalResult ConsumeNextToken(byte marker)
        {
            Consumed++;
            _position++;

            if (_readerOptions != JsonReaderOptions.Default)
            {
                //TODO: Re-evaluate use of InternalResult enum for the common case
                if (_readerOptions == JsonReaderOptions.AllowComments)
                {
                    if (marker == JsonConstants.Solidus)
                    {
                        Consumed--;
                        _position--;
                        return ConsumeComment() ? InternalResult.Success : InternalResult.FailureRollback;
                    }
                    if (TokenType == JsonTokenType.Comment)
                    {
                        Consumed--;
                        _position--;
                        TokenType = (JsonTokenType)_stack.Pop();
                        return ReadSingleSegment() ? InternalResult.Success : InternalResult.FailureRollback;
                    }
                }
                else
                {
                    // JsonReaderOptions.SkipComments
                    if (marker == JsonConstants.Solidus)
                    {
                        Consumed--;
                        _position--;
                        if (SkipComment())
                        {
                            if (Consumed >= (uint)_buffer.Length)
                            {
                                if (!_isSingleValue && _isFinalBlock)
                                {
                                    if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                                        ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                                }
                                return InternalResult.FalseNoRollback;
                            }

                            byte first = _buffer[(int)Consumed];

                            if (first <= JsonConstants.Space)
                            {
                                SkipWhiteSpace();
                                if (Consumed >= (uint)_buffer.Length)
                                {
                                    if (!_isSingleValue && _isFinalBlock)
                                    {
                                        if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                                            ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                                    }
                                    return InternalResult.FalseNoRollback;
                                }
                                first = _buffer[(int)Consumed];
                            }

                            return ConsumeNextToken(first);
                        }
                        return InternalResult.FailureRollback;
                    }
                }
            }

            if (marker == JsonConstants.ListSeperator)
            {
                if (Consumed >= (uint)_buffer.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfPropertyOrValueNotFound);
                    }
                    else return InternalResult.FailureRollback;
                }
                byte first = _buffer[(int)Consumed];

                if (first <= JsonConstants.Space)
                {
                    SkipWhiteSpace();
                    // The next character must be a start of a property name or value.
                    if (Consumed >= (uint)_buffer.Length)
                    {
                        if (_isFinalBlock)
                        {
                            ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfPropertyOrValueNotFound);
                        }
                        else return InternalResult.FailureRollback;
                    }
                    first = _buffer[(int)Consumed];
                }

                TokenStartIndex = Consumed;
                if (_inObject)
                {
                    if (first != JsonConstants.Quote)
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfPropertyNotFound, first);
                    TokenStartIndex++;
                    return ConsumePropertyName() ? InternalResult.Success : InternalResult.FailureRollback;
                }
                else
                {
                    return ConsumeValue(first) ? InternalResult.Success : InternalResult.FailureRollback;
                }
            }
            else if (marker == JsonConstants.CloseBrace)
            {
                EndObject();
            }
            else if (marker == JsonConstants.CloseBracket)
            {
                EndArray();
            }
            else
            {
                Consumed--;
                _position--;
                ThrowJsonReaderException(ref this, ExceptionResource.FoundInvalidCharacter, marker);
            }
            return InternalResult.Success;
        }

        /// <summary>
        /// This method contains the logic for processing the next value token and determining
        /// what type of data it is.
        /// </summary>
        private bool ConsumeValue(byte marker)
        {
            if (marker == JsonConstants.Quote)
            {
                TokenStartIndex++;
                return ConsumeString();
            }
            else if (marker == JsonConstants.OpenBrace)
            {
                Consumed++;
                StartObject();
            }
            else if (marker == JsonConstants.OpenBracket)
            {
                Consumed++;
                StartArray();
            }
            else if ((uint)(marker - '0') <= '9' - '0' || marker == '-')
            {
                return ConsumeNumber();
            }
            else if (marker == 'f')
            {
                return ConsumeFalse();
            }
            else if (marker == 't')
            {
                return ConsumeTrue();
            }
            else if (marker == 'n')
            {
                return ConsumeNull();
            }
            else
            {
                if (_readerOptions != JsonReaderOptions.Default)
                {
                    if (_readerOptions == JsonReaderOptions.AllowComments)
                    {
                        if (marker == JsonConstants.Solidus)
                        {
                            return ConsumeComment();
                        }
                    }
                    else
                    {
                        // JsonReaderOptions.SkipComments
                        if (marker == JsonConstants.Solidus)
                        {
                            if (SkipComment())
                            {
                                if (Consumed >= (uint)_buffer.Length)
                                {
                                    if (!_isSingleValue && _isFinalBlock)
                                    {
                                        if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                                            ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                                    }
                                    return false;
                                }

                                byte first = _buffer[(int)Consumed];

                                if (first <= JsonConstants.Space)
                                {
                                    SkipWhiteSpace();
                                    if (Consumed >= (uint)_buffer.Length)
                                    {
                                        if (!_isSingleValue && _isFinalBlock)
                                        {
                                            if (TokenType != JsonTokenType.EndArray && TokenType != JsonTokenType.EndObject)
                                                ThrowJsonReaderException(ref this, ExceptionResource.InvalidEndOfJson);
                                        }
                                        return false;
                                    }
                                    first = _buffer[(int)Consumed];
                                }

                                return ConsumeValue(first);
                            }
                            return false;
                        }
                    }
                }

                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfValueNotFound, marker);
            }
            return true;
        }

        private bool SkipComment()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer.Slice((int)Consumed + 1);

            if (localCopy.Length > 0)
            {
                byte marker = localCopy[0];
                if (marker == JsonConstants.Solidus)
                    return SkipSingleLineComment(localCopy.Slice(1));
                else if (marker == '*')
                    return SkipMultiLineComment(localCopy.Slice(1));
                else
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfValueNotFound, marker);
            }

            if (_isFinalBlock)
            {
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfValueNotFound, JsonConstants.Solidus);
            }
            else return false;

            return true;
        }

        private bool SkipSingleLineComment(ReadOnlySpan<byte> localCopy)
        {
            //TODO: Match Json.NET's end of comment semantics
            int idx = localCopy.IndexOf(JsonConstants.LineFeed);
            if (idx == -1)
            {
                if (_isFinalBlock)
                {
                    idx = localCopy.Length;
                    // Assume everything on this line is a comment and there is no more data.
                    _position += 2 + localCopy.Length;
                    goto Done;
                }
                else return false;
            }

            Consumed++;
            _position = 0;
            _lineNumber++;
        Done:
            Consumed += 2 + idx;
            return true;
        }

        private bool SkipMultiLineComment(ReadOnlySpan<byte> localCopy)
        {
            int idx = 0;
            while (true)
            {
                int foundIdx = localCopy.Slice(idx).IndexOf(JsonConstants.Solidus);
                if (foundIdx == -1)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.EndOfCommentNotFound);
                    }
                    else return false;
                }
                if (foundIdx != 0 && localCopy[foundIdx + idx - 1] == '*')
                {
                    idx += foundIdx;
                    break;
                }
                idx += foundIdx + 1;
            }

            Debug.Assert(idx >= 1);
            Consumed += 3 + idx;

            (int newLines, int newLineIndex) = JsonReaderHelper.CountNewLines(localCopy.Slice(0, idx - 1));
            _lineNumber += newLines;
            if (newLineIndex != -1)
            {
                _position = idx - newLineIndex;
            }
            else
            {
                _position += 4 + idx - 1;
            }
            return true;
        }


        private bool ConsumeComment()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer.Slice((int)Consumed + 1);

            if (localCopy.Length > 0)
            {
                byte marker = localCopy[0];
                if (marker == JsonConstants.Solidus)
                    return ConsumeSingleLineComment(localCopy.Slice(1));
                else if (marker == '*')
                    return ConsumeMultiLineComment(localCopy.Slice(1));
                else
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfValueNotFound, marker);
            }

            if (_isFinalBlock)
            {
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedStartOfValueNotFound, JsonConstants.Solidus);
            }
            else return false;

            return true;
        }

        private bool ConsumeSingleLineComment(ReadOnlySpan<byte> localCopy)
        {
            int idx = localCopy.IndexOf(JsonConstants.LineFeed);
            if (idx == -1)
            {
                if (_isFinalBlock)
                {
                    // Assume everything on this line is a comment and there is no more data.
                    Value = localCopy;
                    _position += 2 + Value.Length;
                    goto Done;
                }
                else return false;
            }

            Value = localCopy.Slice(0, idx);
            Consumed++;
            _position = 0;
            _lineNumber++;
        Done:
            _stack.Push((InternalJsonTokenType)TokenType);
            TokenType = JsonTokenType.Comment;
            Consumed += 2 + Value.Length;
            return true;
        }

        private bool ConsumeMultiLineComment(ReadOnlySpan<byte> localCopy)
        {
            int idx = 0;
            while (true)
            {
                int foundIdx = localCopy.Slice(idx).IndexOf(JsonConstants.Solidus);
                if (foundIdx == -1)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.EndOfCommentNotFound);
                    }
                    else return false;
                }
                if (foundIdx != 0 && localCopy[foundIdx + idx - 1] == '*')
                {
                    idx += foundIdx;
                    break;
                }
                idx += foundIdx + 1;
            }

            Debug.Assert(idx >= 1);
            _stack.Push((InternalJsonTokenType)TokenType);
            Value = localCopy.Slice(0, idx - 1);
            TokenType = JsonTokenType.Comment;
            Consumed += 4 + Value.Length;

            (int newLines, int newLineIndex) = JsonReaderHelper.CountNewLines(Value);
            _lineNumber += newLines;
            if (newLineIndex != -1)
            {
                _position = Value.Length - newLineIndex + 1;
            }
            else
            {
                _position += 4 + Value.Length;
            }
            return true;
        }

        private bool ConsumeNumber()
        {
            if (!TryGetNumberLookForEnd(_buffer.Slice((int)Consumed), out ReadOnlySpan<byte> number))
                return false;
            Value = number;
            TokenType = JsonTokenType.Number;
            Consumed += Value.Length;
            _position += Value.Length;
            return true;
        }

        private bool ConsumeNull()
        {
            Value = JsonConstants.NullValue;

            ReadOnlySpan<byte> span = _buffer.Slice((int)Consumed);

            Debug.Assert(span.Length > 0 && span[0] == Value[0]);

            if (!span.StartsWith(Value))
            {
                if (_isFinalBlock)
                {
                    goto Throw;
                }
                else
                {
                    if (span.Length > 1 && span[1] != Value[1])
                        goto Throw;
                    if (span.Length > 2 && span[2] != Value[2])
                        goto Throw;
                    if (span.Length >= Value.Length)
                        goto Throw;
                    return false;
                }
            Throw:
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNull, bytes: span);
            }
            TokenType = JsonTokenType.Null;
            Consumed += 4;
            _position += 4;
            return true;
        }

        private bool ConsumeFalse()
        {
            Value = JsonConstants.FalseValue;

            ReadOnlySpan<byte> span = _buffer.Slice((int)Consumed);

            Debug.Assert(span.Length > 0 && span[0] == Value[0]);

            if (!span.StartsWith(Value))
            {
                if (_isFinalBlock)
                {
                    goto Throw;
                }
                else
                {
                    if (span.Length > 1 && span[1] != Value[1])
                        goto Throw;
                    if (span.Length > 2 && span[2] != Value[2])
                        goto Throw;
                    if (span.Length > 3 && span[3] != Value[3])
                        goto Throw;
                    if (span.Length >= Value.Length)
                        goto Throw;
                    return false;
                }
            Throw:
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedFalse, bytes: span);
            }
            TokenType = JsonTokenType.False;
            Consumed += 5;
            _position += 5;
            return true;
        }

        private bool ConsumeTrue()
        {
            Value = JsonConstants.TrueValue;

            ReadOnlySpan<byte> span = _buffer.Slice((int)Consumed);

            Debug.Assert(span.Length > 0 && span[0] == Value[0]);

            if (!span.StartsWith(Value))
            {
                if (_isFinalBlock)
                {
                    goto Throw;
                }
                else
                {
                    if (span.Length > 1 && span[1] != Value[1])
                        goto Throw;
                    if (span.Length > 2 && span[2] != Value[2])
                        goto Throw;
                    if (span.Length >= Value.Length)
                        goto Throw;
                    return false;
                }
            Throw:
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedTrue, bytes: span);
            }
            TokenType = JsonTokenType.True;
            Consumed += 4;
            _position += 4;
            return true;
        }

        private bool ConsumePropertyName()
        {
            if (!ConsumeString())
                return false;

            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;
            if (Consumed >= (uint)localCopy.Length)
            {
                if (_isFinalBlock)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedValueAfterPropertyNameNotFound);
                }
                else return false;
            }

            byte first = localCopy[(int)Consumed];

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpace();
                if (Consumed >= (uint)localCopy.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedValueAfterPropertyNameNotFound);
                    }
                    else return false;
                }
                first = localCopy[(int)Consumed];
            }

            // The next character must be a key / value seperator. Validate and skip.
            if (first != JsonConstants.KeyValueSeperator)
            {
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedSeparaterAfterPropertyNameNotFound, first);
            }

            Consumed++;
            _position++;
            TokenType = JsonTokenType.PropertyName;
            return true;
        }

        private bool ConsumeString()
        {
            Debug.Assert(_buffer.Length >= Consumed + 1);
            Debug.Assert(_buffer[(int)Consumed] == JsonConstants.Quote);

            return ConsumeStringVectorized();
        }

        private bool ConsumeStringVectorized()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;

            int idx = localCopy.Slice((int)Consumed + 1).IndexOf(JsonConstants.Quote);
            if (idx < 0)
            {
                if (_isFinalBlock)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.EndOfStringNotFound);
                }
                else return false;
            }

            if (localCopy[idx + (int)Consumed] != JsonConstants.ReverseSolidus)
            {
                localCopy = localCopy.Slice((int)Consumed + 1, idx);

                if (localCopy.IndexOfAnyControlOrEscape() != -1)
                {
                    _position++;
                    if (ValidateEscaping_AndHex(localCopy))
                        goto Done;
                    return false;
                }

                _position += idx + 1;

            Done:
                _position++;
                Value = localCopy;
                TokenType = JsonTokenType.String;
                Consumed += idx + 2;

                return true;
            }
            else
            {
                return ConsumeStringWithNestedQuotes();
            }
        }

        // https://tools.ietf.org/html/rfc8259#section-7
        private bool ValidateEscaping_AndHex(ReadOnlySpan<byte> data)
        {
            bool nextCharEscaped = false;
            for (int i = 0; i < data.Length; i++)
            {
                byte currentByte = data[i];
                if (currentByte == JsonConstants.ReverseSolidus)
                {
                    nextCharEscaped = !nextCharEscaped;
                }
                else if (nextCharEscaped)
                {
                    int index = JsonConstants.EscapableChars.IndexOf(currentByte);
                    if (index == -1)
                        ThrowJsonReaderException(ref this, ExceptionResource.InvalidCharacterWithinString, currentByte);

                    if (currentByte == 'n')
                    {
                        _position = -1; // Should be 0, but we increment _position below already
                        _lineNumber++;
                    }
                    else if (currentByte == 'u')
                    {
                        _position++;
                        int startIndex = i + 1;
                        for (int j = startIndex; j < data.Length; j++)
                        {
                            byte nextByte = data[j];
                            if ((uint)(nextByte - '0') > '9' - '0' && (uint)(nextByte - 'A') > 'F' - 'A' && (uint)(nextByte - 'a') > 'f' - 'a')
                            {
                                ThrowJsonReaderException(ref this, ExceptionResource.InvalidCharacterWithinString, nextByte);
                            }
                            if (j - startIndex >= 4)
                                break;
                            _position++;
                        }
                        i += 4;
                        if (i >= data.Length)
                        {
                            if (_isFinalBlock)
                                ThrowJsonReaderException(ref this, ExceptionResource.EndOfStringNotFound);
                            else
                                goto False;
                        }
                    }
                    nextCharEscaped = false;
                }
                else if (currentByte < JsonConstants.Space)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.InvalidCharacterWithinString, currentByte);
                }
                _position++;
            }

            return true;

        False:
            return false;
        }

        private bool ConsumeStringWithNestedQuotes()
        {
            //TODO: Optimize looking for nested quotes
            //TODO: Avoid redoing first IndexOf search
            long i = Consumed + 1;
            while (true)
            {
                int counter = 0;
                int foundIdx = _buffer.Slice((int)i).IndexOf(JsonConstants.Quote);
                if (foundIdx == -1)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.EndOfStringNotFound);
                    }
                    else return false;
                }
                if (foundIdx == 0)
                    break;
                for (long j = i + foundIdx - 1; j >= i; j--)
                {
                    if (_buffer[(int)j] != JsonConstants.ReverseSolidus)
                    {
                        if (counter % 2 == 0)
                        {
                            i += foundIdx;
                            goto FoundEndOfString;
                        }
                        break;
                    }
                    else
                        counter++;
                }
                i += foundIdx + 1;
            }

        FoundEndOfString:
            long startIndex = Consumed + 1;
            ReadOnlySpan<byte> localCopy = _buffer.Slice((int)startIndex, (int)(i - startIndex));

            if (localCopy.IndexOfAnyControlOrEscape() != -1)
            {
                _position++;
                if (ValidateEscaping_AndHex(localCopy))
                    goto Done;
                return false;
            }

            _position = i;
        Done:
            _position++;
            Value = localCopy;
            TokenType = JsonTokenType.String;
            Consumed = i + 1;

            return true;
        }

        private void SkipWhiteSpace()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;
            for (; Consumed < localCopy.Length; Consumed++)
            {
                byte val = localCopy[(int)Consumed];
                if (val != JsonConstants.Space &&
                    val != JsonConstants.CarriageReturn &&
                    val != JsonConstants.LineFeed &&
                    val != JsonConstants.Tab)
                {
                    break;
                }

                if (val == JsonConstants.LineFeed)
                {
                    _lineNumber++;
                    _position = 0;
                }
                else
                {
                    _position++;
                }
            }
        }

        // https://tools.ietf.org/html/rfc7159#section-6
        private bool TryGetNumber(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> number)
        {
            Debug.Assert(data.Length > 0);

            ReadOnlySpan<byte> delimiters = JsonConstants.Delimiters;

            number = default;

            int i = 0;
            byte nextByte = data[i];

            if (nextByte == '-')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }

                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);
            }

            Debug.Assert(nextByte >= '0' && nextByte <= '9');

            if (nextByte == '0')
            {
                i++;
                if (i < data.Length)
                {
                    nextByte = data[i];
                    if (delimiters.IndexOf(nextByte) != -1)
                        goto Done;

                    if (nextByte != '.' && nextByte != 'E' && nextByte != 'e')
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitComponentNotFound, nextByte);
                }
                else
                {
                    if (_isFinalBlock)
                        goto Done;
                    else return false;
                }
            }
            else
            {
                i++;
                for (; i < data.Length; i++)
                {
                    nextByte = data[i];
                    if ((uint)(nextByte - '0') > '9' - '0')
                        break;
                }
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                        goto Done;
                    else return false;
                }
                if (delimiters.IndexOf(nextByte) != -1)
                    goto Done;
                if (nextByte != '.' && nextByte != 'E' && nextByte != 'e')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitComponentNotFound, nextByte);
            }

            Debug.Assert(nextByte == '.' || nextByte == 'E' || nextByte == 'e');

            if (nextByte == '.')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }
                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);
                i++;
                for (; i < data.Length; i++)
                {
                    nextByte = data[i];
                    if ((uint)(nextByte - '0') > '9' - '0')
                        break;
                }
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                        goto Done;
                    else return false;
                }
                if (delimiters.IndexOf(nextByte) != -1)
                    goto Done;
                if (nextByte != 'E' && nextByte != 'e')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitEValueNotFound, nextByte);
            }

            Debug.Assert(nextByte == 'E' || nextByte == 'e');
            i++;

            if (i >= data.Length)
            {
                if (_isFinalBlock)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                }
                else return false;
            }

            nextByte = data[i];
            if (nextByte == '+' || nextByte == '-')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }
                nextByte = data[i];
            }

            if ((uint)(nextByte - '0') > '9' - '0')
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);

            i++;
            for (; i < data.Length; i++)
            {
                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    break;
            }

            if (i < data.Length)
            {
                if (delimiters.IndexOf(nextByte) == -1)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndOfDigitNotFound, nextByte);
                }
            }
            else if (!_isFinalBlock)
            {
                return false;
            }

        Done:
            number = data.Slice(0, i);
            return true;
        }

        // https://tools.ietf.org/html/rfc7159#section-6
        private bool TryGetNumberLookForEnd(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> number)
        {
            Debug.Assert(data.Length > 0);

            ReadOnlySpan<byte> delimiters = JsonConstants.Delimiters;

            number = default;

            int i = 0;
            byte nextByte = data[i];

            if (nextByte == '-')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }

                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);
            }

            Debug.Assert(nextByte >= '0' && nextByte <= '9');

            if (nextByte == '0')
            {
                i++;
                if (i < data.Length)
                {
                    nextByte = data[i];
                    if (delimiters.IndexOf(nextByte) != -1)
                        goto Done;

                    if (nextByte != '.' && nextByte != 'E' && nextByte != 'e')
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitComponentNotFound, nextByte);
                }
                else
                {
                    if (_isFinalBlock)
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndOfDigitNotFound, nextByte);
                    else return false;
                }
            }
            else
            {
                i++;
                for (; i < data.Length; i++)
                {
                    nextByte = data[i];
                    if ((uint)(nextByte - '0') > '9' - '0')
                        break;
                }
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndOfDigitNotFound, nextByte);
                    else return false;
                }
                if (delimiters.IndexOf(nextByte) != -1)
                    goto Done;
                if (nextByte != '.' && nextByte != 'E' && nextByte != 'e')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitComponentNotFound, nextByte);
            }

            Debug.Assert(nextByte == '.' || nextByte == 'E' || nextByte == 'e');

            if (nextByte == '.')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }
                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);
                i++;
                for (; i < data.Length; i++)
                {
                    nextByte = data[i];
                    if ((uint)(nextByte - '0') > '9' - '0')
                        break;
                }
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndOfDigitNotFound, nextByte);
                    else return false;
                }
                if (delimiters.IndexOf(nextByte) != -1)
                    goto Done;
                if (nextByte != 'E' && nextByte != 'e')
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedNextDigitEValueNotFound, nextByte);
            }

            Debug.Assert(nextByte == 'E' || nextByte == 'e');
            i++;

            if (i >= data.Length)
            {
                if (_isFinalBlock)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                }
                else return false;
            }

            nextByte = data[i];
            if (nextByte == '+' || nextByte == '-')
            {
                i++;
                if (i >= data.Length)
                {
                    if (_isFinalBlock)
                    {
                        ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFoundEndOfData, nextByte);
                    }
                    else return false;
                }
                nextByte = data[i];
            }

            if ((uint)(nextByte - '0') > '9' - '0')
                ThrowJsonReaderException(ref this, ExceptionResource.ExpectedDigitNotFound, nextByte);

            i++;
            for (; i < data.Length; i++)
            {
                nextByte = data[i];
                if ((uint)(nextByte - '0') > '9' - '0')
                    break;
            }

            if (i < data.Length)
            {
                if (delimiters.IndexOf(nextByte) == -1)
                {
                    ThrowJsonReaderException(ref this, ExceptionResource.ExpectedEndOfDigitNotFound, nextByte);
                }
            }
            else if (!_isFinalBlock)
            {
                return false;
            }

        Done:
            number = data.Slice(0, i);
            return true;
        }

        public string GetValueAsString()
        {
            return Encodings.Utf8.ToString(Value);
        }

        public int GetValueAsInt32()
        {
            if (Utf8Parser.TryParse(Value, out int value, out int bytesConsumed))
            {
                if (Value.Length == bytesConsumed)
                {
                    return value;
                }
            }
            //TODO: Proper error message
            ThrowInvalidCastException();
            return default;
        }

        public long GetValueAsInt64()
        {
            if (Utf8Parser.TryParse(Value, out long value, out int bytesConsumed))
            {
                if (Value.Length == bytesConsumed)
                {
                    return value;
                }
            }
            ThrowInvalidCastException();
            return default;
        }

        public float GetValueAsSingle()
        {
            //TODO: We know whether this is true or not ahead of time
            if (Value.IndexOfAny((byte)'e', (byte)'E') == -1)
            {
                if (Utf8Parser.TryParse(Value, out float value, out int bytesConsumed))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            else
            {
                if (Utf8Parser.TryParse(Value, out float value, out int bytesConsumed, 'e'))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            ThrowInvalidCastException();
            return default;
        }

        public double GetValueAsDouble()
        {
            if (Value.IndexOfAny((byte)'e', (byte)'E') == -1)
            {
                if (Utf8Parser.TryParse(Value, out double value, out int bytesConsumed))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            else
            {
                if (Utf8Parser.TryParse(Value, out double value, out int bytesConsumed, standardFormat: 'e'))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            ThrowInvalidCastException();
            return default;
        }

        public decimal GetValueAsDecimal()
        {
            if (Value.IndexOfAny((byte)'e', (byte)'E') == -1)
            {
                if (Utf8Parser.TryParse(Value, out decimal value, out int bytesConsumed))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            else
            {
                if (Utf8Parser.TryParse(Value, out decimal value, out int bytesConsumed, standardFormat: 'e'))
                {
                    if (Value.Length == bytesConsumed)
                    {
                        return value;
                    }
                }
            }
            ThrowInvalidCastException();
            return default;
        }

        public object GetValueAsNumber()
        {
            if (Utf8Parser.TryParse(Value, out int intVal, out int bytesConsumed))
            {
                if (Value.Length == bytesConsumed)
                {
                    return intVal;
                }
            }

            if (Utf8Parser.TryParse(Value, out long longVal, out bytesConsumed))
            {
                if (Value.Length == bytesConsumed)
                {
                    return longVal;
                }
            }

            if (Value.IndexOfAny((byte)'e', (byte)'E') == -1)
            {
                return NumberAsObject(Value);
            }
            else
            {
                return NumberAsObject(Value, standardFormat: 'e');
            }
        }

        private static object NumberAsObject(ReadOnlySpan<byte> value, char standardFormat = default)
        {
            if (Utf8Parser.TryParse(value, out decimal valueDecimal, out int bytesConsumed, standardFormat))
            {
                if (value.Length == bytesConsumed)
                {
                    return TryToChangeToInt32_64(valueDecimal);
                }
            }
            else if (Utf8Parser.TryParse(value, out double valueDouble, out bytesConsumed, standardFormat))
            {
                if (value.Length == bytesConsumed)
                {
                    return TryToChangeToInt32_64(valueDouble);
                }
            }
            else if (Utf8Parser.TryParse(value, out float valueFloat, out bytesConsumed, standardFormat))
            {
                if (value.Length == bytesConsumed)
                {
                    return TryToChangeToInt32_64(valueFloat);
                }
            }

            // Number too large for .NET
            ThrowInvalidCastException();
            return default;
        }

        private static object TryToChangeToInt32_64(float valueFloat)
        {
            float rounded = (float)Math.Floor(valueFloat);
            if (rounded != valueFloat)
            {
                return valueFloat;
            }
            if (rounded <= int.MaxValue && rounded >= int.MinValue)
                return Convert.ToInt32(rounded);
            else if (rounded <= long.MaxValue && rounded >= long.MinValue)
                return Convert.ToInt64(rounded);
            else
                return valueFloat;
        }

        private static object TryToChangeToInt32_64(double valueDouble)
        {
            double rounded = Math.Floor(valueDouble);
            if (rounded != valueDouble)
            {
                return valueDouble;
            }
            if (rounded <= int.MaxValue && rounded >= int.MinValue)
                return Convert.ToInt32(rounded);
            else if (rounded <= long.MaxValue && rounded >= long.MinValue)
                return Convert.ToInt64(rounded);
            else
                return valueDouble;
        }

        private static object TryToChangeToInt32_64(decimal valueDecimal)
        {
            decimal rounded = Math.Floor(valueDecimal);
            if (rounded != valueDecimal)
            {
                return valueDecimal;
            }
            if (rounded <= int.MaxValue && rounded >= int.MinValue)
                return Convert.ToInt32(rounded);
            else if (rounded <= long.MaxValue && rounded >= long.MinValue)
                return Convert.ToInt64(rounded);
            else
                return valueDecimal;
        }
    }
}
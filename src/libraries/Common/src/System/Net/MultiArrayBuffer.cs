// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System.Buffers;
using System.Diagnostics;

namespace System.Net
{
    // Warning: Mutable struct!
    // The purpose of this struct is to simplify buffer management in cases where the size of the buffer may grow large (e.g. >64K),
    // thus making it worthwhile to add the overhead involved in managing multiple individual array allocations.
    // Like ArrayBuffer, this manages a sliding buffer where bytes can be added at the end and removed at the beginning.
    // Unlike ArrayBuffer, the buffer itself is managed using 16K blocks which are added/removed to the block list as necessary.

    // [ActiveBuffer] contains the current buffer contents; these bytes will be preserved on any call to TryEnsureAvailableBytesUpToLimit.
    // [AvailableBuffer] contains the available bytes past the end of the current content,
    // and can be written to in order to add data to the end of the buffer.
    // Commit(byteCount) will extend the ActiveBuffer by [byteCount] bytes into the AvailableBuffer.
    // Discard(byteCount) will discard [byteCount] bytes as the beginning of the ActiveBuffer.
    // TryEnsureAvailableBytesUpToLimit will grow the buffer if necessary; *however*, this may invalidate
    // old values of [ActiveBuffer] and [AvailableBuffer], so they must be retrieved again.

    internal struct MultiArrayBuffer : IDisposable
    {
        private byte[]?[]? _blocks;
        private int _allocatedEnd;
        private int _activeStart;
        private int _availableStart;

        // Invariants:
        // 0 <= _activeStart <= _availableStart <= total buffer size (i.e. _blockCount * BlockSize)

        private const int BlockSize = 16 * 1024;

        public MultiArrayBuffer(int initialBufferSize)
        {
            // [initialBufferSize] is ignored for now;
            // I kept it because some callers are passing useful info here that we might want to act on in the future.

            _blocks = null;
            _allocatedEnd = 0;
            _activeStart = 0;
            _availableStart = 0;
        }

        public void Dispose()
        {
            _activeStart = 0;
            _availableStart = 0;

            if (_blocks is not null)
            {
                for (int i = 0; i < _blocks.Length; i++)
                {
                    if (_blocks[i] is not null)
                    {
                        ArrayPool<byte>.Shared.Return(_blocks[i]!);
                        _blocks[i] = null;
                    }
                }

                _blocks = null;
                _allocatedEnd = 0;
            }
        }

        public MultiMemory ActiveMemory => new MultiMemory(_blocks, _activeStart, _availableStart - _activeStart);

        public MultiMemory AvailableMemory => new MultiMemory(_blocks, _availableStart, _allocatedEnd - _availableStart);

        public void Discard(int byteCount)
        {
            Debug.Assert(byteCount >= 0);
            Debug.Assert(byteCount <= ActiveMemory.Length, $"MultiArrayBuffer.Discard: Expected byteCount={byteCount} <= {ActiveMemory.Length}");

            int oldStartBlock = _activeStart / BlockSize;
            _activeStart += byteCount;
            int newStartBlock = _activeStart / BlockSize;

            while (oldStartBlock < newStartBlock)
            {
                Debug.Assert(_blocks is not null);
                Debug.Assert(_blocks[oldStartBlock] is not null, $"Discard: oldStartBlock is null?? byteCount={byteCount}, _activeStart={_activeStart}, oldStartBlock={oldStartBlock}, newStartBlock={newStartBlock}");

                ArrayPool<byte>.Shared.Return(_blocks[oldStartBlock]!);
                _blocks[oldStartBlock] = null;

                oldStartBlock++;
            }

            if (_activeStart == _availableStart)
            {
                // Small optimization to restart at the beginning of the current block, since we have no active bytes.
                // Note, we don't try to release buffers in this case. Maybe we should. But if we did,
                // we'd need handle the fact that there could be more than one here, if we previously grew the buffer enough for that.

                _activeStart = newStartBlock * BlockSize;
                _availableStart = newStartBlock * BlockSize;
            }
        }

        public void Commit(int byteCount)
        {
            Debug.Assert(byteCount >= 0);
            Debug.Assert(byteCount <= AvailableMemory.Length, $"MultiArrayBuffer.Commit: Expected byteCount={byteCount} <= {AvailableMemory.Length}");

            _availableStart += byteCount;
        }

        public void EnsureAvailableSpaceUpToLimit(int byteCount, int limit)
        {
            if (ActiveMemory.Length >= limit)
            {
                // Already past limit. Do nothing.
                return;
            }

            // Enforce the limit.
            byteCount = Math.Min(byteCount, limit - ActiveMemory.Length);

            EnsureAvailableSpace(byteCount);
        }

        public void EnsureAvailableSpace(int byteCount)
        {
            Debug.Assert(byteCount >= 0);

            if (byteCount > AvailableMemory.Length)
            {
                GrowAvailableSpace(byteCount);
            }
        }

        public void GrowAvailableSpace(int byteCount)
        {
            Debug.Assert(byteCount > AvailableMemory.Length);

            int newBytesNeeded = byteCount - AvailableMemory.Length;
            int newBlocksNeeded = (newBytesNeeded + BlockSize - 1) / BlockSize;

            // Ensure we have enough space in the block array for the new blocks needed.
            if (_blocks is null)
            {
                Debug.Assert(_allocatedEnd == 0);
                Debug.Assert(_activeStart == 0);
                Debug.Assert(_availableStart == 0);

                int blockArraySize = 4;
                while (blockArraySize  < newBlocksNeeded)
                {
                    blockArraySize *= 2;
                }

                _blocks = new byte[]?[blockArraySize];
            }
            else
            {
                Debug.Assert(_allocatedEnd % BlockSize == 0);
                Debug.Assert(_allocatedEnd <= _blocks.Length * BlockSize);

                int allocatedBlocks = _allocatedEnd / BlockSize;
                int blockArraySize = _blocks.Length;
                if (allocatedBlocks + newBlocksNeeded > blockArraySize)
                {
                    // Not enough room in current block array.
                    int unusedInitialBlocks = _activeStart / BlockSize;

#if DEBUG
                    for (int i = 0; i < unusedInitialBlocks; i++)
                    {
                        Debug.Assert(_blocks[i] is null);
                    }

                    for (int i = unusedInitialBlocks; i < allocatedBlocks; i++)
                    {
                        Debug.Assert(_blocks[i] is not null);
                    }

                    for (int i = allocatedBlocks; i < blockArraySize; i++)
                    {
                        Debug.Assert(_blocks[i] is null);
                    }
#endif

                    int usedBlocks = (allocatedBlocks - unusedInitialBlocks);
                    int blocksNeeded = usedBlocks + newBlocksNeeded;
                    if (blocksNeeded > blockArraySize)
                    {
                        // Need to allocate a new array and copy.
                        while (blockArraySize < blocksNeeded)
                        {
                            blockArraySize *= 2;
                        }

                        byte[]?[] newBlockArray = new byte[]?[blockArraySize];
                        _blocks.AsSpan().Slice(unusedInitialBlocks, usedBlocks).CopyTo(newBlockArray);
                        _blocks = newBlockArray;
                    }
                    else
                    {
                        // We can shift the array down to make enough space
                        _blocks.AsSpan().Slice(unusedInitialBlocks, usedBlocks).CopyTo(_blocks);

                        // Null out the part of the array left over from the shift, so that we aren't holding references to those blocks.
                        _blocks.AsSpan().Slice(usedBlocks, unusedInitialBlocks).Fill(null);
                    }

                    int shift = unusedInitialBlocks + BlockSize;
                    _allocatedEnd -= shift;
                    _activeStart -= shift;
                    _availableStart -= shift;

                    Debug.Assert(_activeStart / BlockSize == 0, $"Start is not in first block after move or resize?? _activeStart={_activeStart}");
                }
            }

            // Allocate new blocks
            Debug.Assert(_allocatedEnd % BlockSize == 0);
            int blockCount = _allocatedEnd / BlockSize;
            for (int i = 0; i < newBlocksNeeded; i++)
            {
                Debug.Assert(_blocks[blockCount + i] is null);
                _blocks[blockCount + i] = ArrayPool<byte>.Shared.Rent(BlockSize);
            }

            _allocatedEnd += newBlocksNeeded + blockCount;

            // After all of that, we should have enough available memory now
            Debug.Assert(byteCount <= AvailableMemory.Length);
        }
    }

    // This is a Memory-like struct for handling multi-array segments from MultiArrayBuffer above.
    // It supports standard Span/Memory operations like indexing, Slice, Length, etc
    // It also supports CopyTo/CopyFrom Span<byte>

    internal readonly struct MultiMemory
    {
        private readonly byte[]?[]? _blocks;
        private readonly int _start;
        private readonly int _length;

        private const int BlockSize = 16 * 1024;

        internal MultiMemory(byte[]?[]? blocks, int start, int length)
        {
            if (length == 0)
            {
                _blocks = null;
                _start = 0;
                _length = 0;
            }
            else
            {
                Debug.Assert(blocks is not null);
                Debug.Assert(start >= 0);
                Debug.Assert(length >= 0);
                Debug.Assert(start + length <= blocks.Length * BlockSize);

                _blocks = blocks;
                _start = start;
                _length = length;
            }
        }

        private static int GetBlockIndex(int offset) => offset / BlockSize;
        private static int GetOffsetInBlock(int offset) => offset % BlockSize;

        public int Length => _length;

        public ref byte this[int index]
        {
            get
            {
                if (index < 0 || index >= _length)
                {
                    throw new IndexOutOfRangeException();
                }

                int offset = _start + index;
                return ref _blocks![GetBlockIndex(offset)]![GetOffsetInBlock(offset)];
            }
        }

        public int BlockCount => GetBlockIndex(_start + _length + (BlockSize - 1)) - GetBlockIndex(_start);

        public Memory<byte> GetBlock(int blockIndex)
        {
            if (blockIndex < 0 || blockIndex >= BlockCount)
            {
                throw new IndexOutOfRangeException();
            }

            Debug.Assert(_length > 0, "Length should never be 0 here because BlockCount would be 0");
            Debug.Assert(_blocks is not null);

            int startInBlock = (blockIndex == 0 ? GetOffsetInBlock(_start) : 0);
            int endInBlock = (blockIndex == BlockCount - 1 ? GetOffsetInBlock(_start + _length - 1) + 1 : BlockSize);

            Debug.Assert(0 <= startInBlock, $"Invalid startInBlock={startInBlock}. blockIndex={blockIndex}, _blocks.Length={_blocks.Length}, _start={_start}, _length={_length}");
            Debug.Assert(startInBlock < endInBlock, $"Invalid startInBlock={startInBlock}, endInBlock={endInBlock}. blockIndex={blockIndex}, _blocks.Length={_blocks.Length}, _start={_start}, _length={_length}");
            Debug.Assert(endInBlock <= BlockSize, $"Invalid endInBlock={endInBlock}. blockIndex={blockIndex}, _blocks.Length={_blocks.Length}, _start={_start}, _length={_length}");

            return new Memory<byte>(_blocks[GetBlockIndex(_start) + blockIndex], startInBlock, endInBlock - startInBlock);
        }

        public MultiMemory Slice(int start)
        {
            if (start < 0 || start > _length)
            {
                throw new IndexOutOfRangeException();
            }

            return new MultiMemory(_blocks, _start + start, _length - start);
        }

        public MultiMemory Slice(int start, int length)
        {
            if (start < 0 || length < 0 || start + length > _length)
            {
                throw new IndexOutOfRangeException();
            }

            return new MultiMemory(_blocks, _start + start, length);
        }

        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < _length)
            {
                throw new ArgumentException(nameof(destination));
            }

            for (int blockIndex = 0; blockIndex < BlockCount; blockIndex++)
            {
                Memory<byte> block = GetBlock(blockIndex);
                block.Span.CopyTo(destination);
                destination = destination.Slice(block.Length);
            }
        }

        public void CopyFrom(ReadOnlySpan<byte> source)
        {
            if (_length < source.Length)
            {
                throw new ArgumentException(nameof(source));
            }

            for (int blockIndex = 0; blockIndex < BlockCount; blockIndex++)
            {
                Memory<byte> block = GetBlock(blockIndex);

                if (source.Length <= block.Length)
                {
                    source.CopyTo(block.Span);
                    break;
                }

                source.Slice(0, block.Length).CopyTo(block.Span);
                source = source.Slice(block.Length);
            }
        }

        public static MultiMemory Empty => default;
    }
}

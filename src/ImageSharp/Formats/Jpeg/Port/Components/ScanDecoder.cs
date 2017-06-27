﻿// <copyright file="ScanDecoder.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Formats.Jpeg.Port.Components
{
    using System;
#if DEBUG
    using System.Diagnostics;
#endif
    using System.IO;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Provides the means to decode a spectral scan
    /// </summary>
    internal struct ScanDecoder
    {
        private int bitsData;

        private int bitsCount;

        private int specStart;

        private int specEnd;

        private int eobrun;

        private int compIndex;

        private int successiveState;

        private int successiveACState;

        private int successiveACNextValue;

        /// <summary>
        /// Decodes the spectral scan
        /// </summary>
        /// <param name="frame">The image frame</param>
        /// <param name="stream">The input stream</param>
        /// <param name="dcHuffmanTables">The DC Huffman tables</param>
        /// <param name="acHuffmanTables">The AC Huffman tables</param>
        /// <param name="components">The scan components</param>
        /// <param name="componentIndex">The component index within the array</param>
        /// <param name="componentsLength">The length of the components. Different to the array length</param>
        /// <param name="resetInterval">The reset interval</param>
        /// <param name="spectralStart">The spectral selection start</param>
        /// <param name="spectralEnd">The spectral selection end</param>
        /// <param name="successivePrev">The successive approximation bit high end</param>
        /// <param name="successive">The successive approximation bit low end</param>
        /// <returns>The <see cref="long"/> representing the processed length in bytes</returns>
        public long DecodeScan(
            Frame frame,
            Stream stream,
            HuffmanTables dcHuffmanTables,
            HuffmanTables acHuffmanTables,
            FrameComponent[] components,
            int componentIndex,
            int componentsLength,
            ushort resetInterval,
            int spectralStart,
            int spectralEnd,
            int successivePrev,
            int successive)
        {
            this.compIndex = componentIndex;
            this.specStart = spectralStart;
            this.specEnd = spectralEnd;
            this.successiveState = successive;
            bool progressive = frame.Progressive;
            int mcusPerLine = frame.McusPerLine;
            long startPosition = stream.Position;

            int mcu = 0;
            int mcuExpected;
            if (componentsLength == 1)
            {
                mcuExpected = components[this.compIndex].BlocksPerLine * components[this.compIndex].BlocksPerColumn;
            }
            else
            {
                mcuExpected = mcusPerLine * frame.McusPerColumn;
            }

            FileMarker fileMarker;
            while (mcu < mcuExpected)
            {
                // Reset interval stuff
                int mcuToRead = resetInterval != 0 ? Math.Min(mcuExpected - mcu, resetInterval) : mcuExpected;
                for (int i = 0; i < components.Length; i++)
                {
                    ref FrameComponent c = ref components[i];
                    c.Pred = 0;
                }

                this.eobrun = 0;

                if (!progressive)
                {
                    this.DecodeScanBaseline(dcHuffmanTables, acHuffmanTables, components, componentsLength, mcusPerLine, mcuToRead, ref mcu, stream);
                }
                else
                {
                    if (this.specStart == 0)
                    {
                        if (successivePrev == 0)
                        {
                            this.DecodeScanDCFirst(dcHuffmanTables, components, componentsLength, mcusPerLine, mcuToRead, ref mcu, stream);
                        }
                        else
                        {
                            this.DecodeScanDCSuccessive(components, componentsLength, mcusPerLine, mcuToRead, ref mcu, stream);
                        }
                    }
                    else
                    {
                        if (successivePrev == 0)
                        {
                            this.DecodeScanACFirst(acHuffmanTables, components, componentsLength, mcusPerLine, mcuToRead, ref mcu, stream);
                        }
                        else
                        {
                            this.DecodeScanACSuccessive(acHuffmanTables, components, componentsLength, mcusPerLine, mcuToRead, ref mcu, stream);
                        }
                    }
                }

                // Find marker
                this.bitsCount = 0;
                long position = stream.Position;
                fileMarker = JpegDecoderCore.FindNextFileMarkerNew(stream);

                // Some bad images seem to pad Scan blocks with e.g. zero bytes, skip past
                // those to attempt to find a valid marker (fixes issue4090.pdf) in original code.
                if (fileMarker.Invalid)
                {
#if DEBUG
                    Debug.WriteLine($"DecodeScan - Unexpected MCU data at {stream.Position}, next marker is: {fileMarker.Marker}");

#endif
                    // stream.Position = fileMarker.Position;
                }

                ushort marker = fileMarker.Marker;

                // if (marker <= 0xFF00)
                // {
                //    throw new ImageFormatException("Marker was not found");
                // }

                // RSTn We've alread read the bytes and altered the position so no need to skip
                if (marker >= JpegConstants.Markers.RST0 && marker <= JpegConstants.Markers.RST7)
                {
                    continue;
                }

                if (!fileMarker.Invalid)
                {
                    // We've found a valid marker.
                    // Rewind the stream to the position of the marker and beak
                    stream.Position = fileMarker.Position;
                    break;
                }

                // Rewind the stream
                stream.Position = position;
            }

            fileMarker = JpegDecoderCore.FindNextFileMarkerNew(stream);

            // Some images include more Scan blocks than expected, skip past those and
            // attempt to find the next valid marker (fixes issue8182.pdf) in original code.
            if (fileMarker.Invalid)
            {
#if DEBUG
                Debug.WriteLine($"DecodeScan - Unexpected MCU data, next marker is: {fileMarker.Marker}");
#endif
                stream.Position = fileMarker.Position;
            }

            return stream.Position - startPosition;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetBlockBufferOffset(FrameComponent component, int row, int col)
        {
            return 64 * (((component.BlocksPerLine + 1) * row) + col);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeScanBaseline(
            HuffmanTables dcHuffmanTables,
            HuffmanTables acHuffmanTables,
            FrameComponent[] components,
            int componentsLength,
            int mcusPerLine,
            int mcuToRead,
            ref int mcu,
            Stream stream)
        {
            if (componentsLength == 1)
            {
                ref FrameComponent component = ref components[this.compIndex];
                for (int n = 0; n < mcuToRead; n++)
                {
                    this.DecodeBlockBaseline(dcHuffmanTables, acHuffmanTables, ref component, mcu, stream);
                    mcu++;
                }
            }
            else
            {
                for (int n = 0; n < mcuToRead; n++)
                {
                    for (int i = 0; i < componentsLength; i++)
                    {
                        ref FrameComponent component = ref components[i];
                        int h = component.HorizontalFactor;
                        int v = component.VerticalFactor;
                        for (int j = 0; j < v; j++)
                        {
                            for (int k = 0; k < h; k++)
                            {
                                this.DecodeMcuBaseline(dcHuffmanTables, acHuffmanTables, ref component, mcusPerLine, mcu, j, k, stream);
                            }
                        }
                    }

                    mcu++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeScanDCFirst(
            HuffmanTables dcHuffmanTables,
            FrameComponent[] components,
            int componentsLength,
            int mcusPerLine,
            int mcuToRead,
            ref int mcu,
            Stream stream)
        {
            if (componentsLength == 1)
            {
                ref FrameComponent component = ref components[this.compIndex];
                for (int n = 0; n < mcuToRead; n++)
                {
                    this.DecodeBlockDCFirst(dcHuffmanTables, ref component, mcu, stream);
                    mcu++;
                }
            }
            else
            {
                for (int n = 0; n < mcuToRead; n++)
                {
                    for (int i = 0; i < componentsLength; i++)
                    {
                        ref FrameComponent component = ref components[i];
                        int h = component.HorizontalFactor;
                        int v = component.VerticalFactor;
                        for (int j = 0; j < v; j++)
                        {
                            for (int k = 0; k < h; k++)
                            {
                                this.DecodeMcuDCFirst(dcHuffmanTables, ref component, mcusPerLine, mcu, j, k, stream);
                            }
                        }
                    }

                    mcu++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeScanDCSuccessive(
            FrameComponent[] components,
            int componentsLength,
            int mcusPerLine,
            int mcuToRead,
            ref int mcu,
            Stream stream)
        {
            if (componentsLength == 1)
            {
                ref FrameComponent component = ref components[this.compIndex];
                for (int n = 0; n < mcuToRead; n++)
                {
                    this.DecodeBlockDCSuccessive(ref component, mcu, stream);
                    mcu++;
                }
            }
            else
            {
                for (int n = 0; n < mcuToRead; n++)
                {
                    for (int i = 0; i < componentsLength; i++)
                    {
                        ref FrameComponent component = ref components[i];
                        int h = component.HorizontalFactor;
                        int v = component.VerticalFactor;
                        for (int j = 0; j < v; j++)
                        {
                            for (int k = 0; k < h; k++)
                            {
                                this.DecodeMcuDCSuccessive(ref component, mcusPerLine, mcu, j, k, stream);
                            }
                        }
                    }

                    mcu++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeScanACFirst(
            HuffmanTables acHuffmanTables,
            FrameComponent[] components,
            int componentsLength,
            int mcusPerLine,
            int mcuToRead,
            ref int mcu,
            Stream stream)
        {
            if (componentsLength == 1)
            {
                ref FrameComponent component = ref components[this.compIndex];
                for (int n = 0; n < mcuToRead; n++)
                {
                    this.DecodeBlockACFirst(acHuffmanTables, ref component, mcu, stream);
                    mcu++;
                }
            }
            else
            {
                for (int n = 0; n < mcuToRead; n++)
                {
                    for (int i = 0; i < componentsLength; i++)
                    {
                        ref FrameComponent component = ref components[i];
                        int h = component.HorizontalFactor;
                        int v = component.VerticalFactor;
                        for (int j = 0; j < v; j++)
                        {
                            for (int k = 0; k < h; k++)
                            {
                                this.DecodeMcuACFirst(acHuffmanTables, ref component, mcusPerLine, mcu, j, k, stream);
                            }
                        }
                    }

                    mcu++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeScanACSuccessive(
            HuffmanTables acHuffmanTables,
            FrameComponent[] components,
            int componentsLength,
            int mcusPerLine,
            int mcuToRead,
            ref int mcu,
            Stream stream)
        {
            if (componentsLength == 1)
            {
                ref FrameComponent component = ref components[this.compIndex];
                for (int n = 0; n < mcuToRead; n++)
                {
                    this.DecodeBlockACSuccessive(acHuffmanTables, ref component, mcu, stream);
                    mcu++;
                }
            }
            else
            {
                for (int n = 0; n < mcuToRead; n++)
                {
                    for (int i = 0; i < componentsLength; i++)
                    {
                        ref FrameComponent component = ref components[i];
                        int h = component.HorizontalFactor;
                        int v = component.VerticalFactor;
                        for (int j = 0; j < v; j++)
                        {
                            for (int k = 0; k < h; k++)
                            {
                                this.DecodeMcuACSuccessive(acHuffmanTables, ref component, mcusPerLine, mcu, j, k, stream);
                            }
                        }
                    }

                    mcu++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBlockBaseline(HuffmanTables dcHuffmanTables, HuffmanTables acHuffmanTables, ref FrameComponent component, int mcu, Stream stream)
        {
            int blockRow = (mcu / component.BlocksPerLine) | 0;
            int blockCol = mcu % component.BlocksPerLine;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeBaseline(ref component, offset, dcHuffmanTables, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeMcuBaseline(HuffmanTables dcHuffmanTables, HuffmanTables acHuffmanTables, ref FrameComponent component, int mcusPerLine, int mcu, int row, int col, Stream stream)
        {
            int mcuRow = (mcu / mcusPerLine) | 0;
            int mcuCol = mcu % mcusPerLine;
            int blockRow = (mcuRow * component.VerticalFactor) + row;
            int blockCol = (mcuCol * component.HorizontalFactor) + col;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeBaseline(ref component, offset, dcHuffmanTables, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBlockDCFirst(HuffmanTables dcHuffmanTables, ref FrameComponent component, int mcu, Stream stream)
        {
            int blockRow = (mcu / component.BlocksPerLine) | 0;
            int blockCol = mcu % component.BlocksPerLine;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeDCFirst(ref component, offset, dcHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeMcuDCFirst(HuffmanTables dcHuffmanTables, ref FrameComponent component, int mcusPerLine, int mcu, int row, int col, Stream stream)
        {
            int mcuRow = (mcu / mcusPerLine) | 0;
            int mcuCol = mcu % mcusPerLine;
            int blockRow = (mcuRow * component.VerticalFactor) + row;
            int blockCol = (mcuCol * component.HorizontalFactor) + col;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeDCFirst(ref component, offset, dcHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBlockDCSuccessive(ref FrameComponent component, int mcu, Stream stream)
        {
            int blockRow = (mcu / component.BlocksPerLine) | 0;
            int blockCol = mcu % component.BlocksPerLine;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeDCSuccessive(ref component, offset, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeMcuDCSuccessive(ref FrameComponent component, int mcusPerLine, int mcu, int row, int col, Stream stream)
        {
            int mcuRow = (mcu / mcusPerLine) | 0;
            int mcuCol = mcu % mcusPerLine;
            int blockRow = (mcuRow * component.VerticalFactor) + row;
            int blockCol = (mcuCol * component.HorizontalFactor) + col;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeDCSuccessive(ref component, offset, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBlockACFirst(HuffmanTables acHuffmanTables, ref FrameComponent component, int mcu, Stream stream)
        {
            int blockRow = (mcu / component.BlocksPerLine) | 0;
            int blockCol = mcu % component.BlocksPerLine;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeACFirst(ref component, offset, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeMcuACFirst(HuffmanTables acHuffmanTables, ref FrameComponent component, int mcusPerLine, int mcu, int row, int col, Stream stream)
        {
            int mcuRow = (mcu / mcusPerLine) | 0;
            int mcuCol = mcu % mcusPerLine;
            int blockRow = (mcuRow * component.VerticalFactor) + row;
            int blockCol = (mcuCol * component.HorizontalFactor) + col;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeACFirst(ref component, offset, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBlockACSuccessive(HuffmanTables acHuffmanTables, ref FrameComponent component, int mcu, Stream stream)
        {
            int blockRow = (mcu / component.BlocksPerLine) | 0;
            int blockCol = mcu % component.BlocksPerLine;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeACSuccessive(ref component, offset, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeMcuACSuccessive(HuffmanTables acHuffmanTables, ref FrameComponent component, int mcusPerLine, int mcu, int row, int col, Stream stream)
        {
            int mcuRow = (mcu / mcusPerLine) | 0;
            int mcuCol = mcu % mcusPerLine;
            int blockRow = (mcuRow * component.VerticalFactor) + row;
            int blockCol = (mcuCol * component.HorizontalFactor) + col;
            int offset = GetBlockBufferOffset(component, blockRow, blockCol);
            this.DecodeACSuccessive(ref component, offset, acHuffmanTables, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadBit(Stream stream)
        {
            if (this.bitsCount > 0)
            {
                this.bitsCount--;
                return (this.bitsData >> this.bitsCount) & 1;
            }

            this.bitsData = stream.ReadByte();
            if (this.bitsData == 0xFF)
            {
                int nextByte = stream.ReadByte();
                if (nextByte != 0)
                {
                    throw new ImageFormatException($"Unexpected marker {(this.bitsData << 8) | nextByte}");
                }

                // Unstuff 0
            }

            this.bitsCount = 7;
            return this.bitsData >> 7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private short DecodeHuffman(HuffmanBranch[] tree, Stream stream)
        {
            HuffmanBranch[] node = tree;
            while (true)
            {
                int index = this.ReadBit(stream);
                HuffmanBranch branch = node[index];

                if (branch.Value > -1)
                {
                    return branch.Value;
                }

                node = branch.Children;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Receive(int length, Stream stream)
        {
            int n = 0;
            while (length > 0)
            {
                n = (n << 1) | this.ReadBit(stream);
                length--;
            }

            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReceiveAndExtend(int length, Stream stream)
        {
            if (length == 1)
            {
                return this.ReadBit(stream) == 1 ? 1 : -1;
            }

            int n = this.Receive(length, stream);
            if (n >= 1 << (length - 1))
            {
                return n;
            }

            return n + (-1 << length) + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeBaseline(ref FrameComponent component, int offset, HuffmanTables dcHuffmanTables, HuffmanTables acHuffmanTables, Stream stream)
        {
            int t = this.DecodeHuffman(dcHuffmanTables[component.DCHuffmanTableId], stream);
            int diff = t == 0 ? 0 : this.ReceiveAndExtend(t, stream);
            component.BlockData[offset] = (short)(component.Pred += diff);

            int k = 1;
            while (k < 64)
            {
                int rs = this.DecodeHuffman(acHuffmanTables[component.ACHuffmanTableId], stream);
                int s = rs & 15;
                int r = rs >> 4;

                if (s == 0)
                {
                    if (r < 15)
                    {
                        break;
                    }

                    k += 16;
                    continue;
                }

                k += r;
                byte z = QuantizationTables.DctZigZag[k];
                short re = (short)this.ReceiveAndExtend(s, stream);
                component.BlockData[offset + z] = re;
                k++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeDCFirst(ref FrameComponent component, int offset, HuffmanTables dcHuffmanTables, Stream stream)
        {
            int t = this.DecodeHuffman(dcHuffmanTables[component.DCHuffmanTableId], stream);
            int diff = t == 0 ? 0 : this.ReceiveAndExtend(t, stream) << this.successiveState;
            component.BlockData[offset] = (short)(component.Pred += diff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeDCSuccessive(ref FrameComponent component, int offset, Stream stream)
        {
            component.BlockData[offset] |= (short)(this.ReadBit(stream) << this.successiveState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeACFirst(ref FrameComponent component, int offset, HuffmanTables acHuffmanTables, Stream stream)
        {
            if (this.eobrun > 0)
            {
                this.eobrun--;
                return;
            }

            int k = this.specStart;
            int e = this.specEnd;
            while (k <= e)
            {
                short rs = this.DecodeHuffman(acHuffmanTables[component.ACHuffmanTableId], stream);
                int s = rs & 15;
                int r = rs >> 4;

                if (s == 0)
                {
                    if (r < 15)
                    {
                        this.eobrun = this.Receive(r, stream) + (1 << r) - 1;
                        break;
                    }

                    k += 16;
                    continue;
                }

                k += r;
                byte z = QuantizationTables.DctZigZag[k];
                component.BlockData[offset + z] = (short)(this.ReceiveAndExtend(s, stream) * (1 << this.successiveState));
                k++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeACSuccessive(ref FrameComponent component, int offset, HuffmanTables acHuffmanTables, Stream stream)
        {
            int k = this.specStart;
            int e = this.specEnd;
            int r = 0;
            while (k <= e)
            {
                byte z = QuantizationTables.DctZigZag[k];
                switch (this.successiveACState)
                {
                    case 0: // Initial state
                        short rs = this.DecodeHuffman(acHuffmanTables[component.ACHuffmanTableId], stream);
                        int s = rs & 15;
                        r = rs >> 4;
                        if (s == 0)
                        {
                            if (r < 15)
                            {
                                this.eobrun = this.Receive(r, stream) + (1 << r);
                                this.successiveACState = 4;
                            }
                            else
                            {
                                r = 16;
                                this.successiveACState = 1;
                            }
                        }
                        else
                        {
                            if (s != 1)
                            {
                                throw new ImageFormatException("Invalid ACn encoding");
                            }

                            this.successiveACNextValue = this.ReceiveAndExtend(s, stream);
                            this.successiveACState = r > 0 ? 2 : 3;
                        }

                        continue;
                    case 1: // Skipping r zero items
                    case 2:
                        if (component.BlockData[offset + z] != 0)
                        {
                            component.BlockData[offset + z] += (short)(this.ReadBit(stream) << this.successiveState);
                        }
                        else
                        {
                            r--;
                            if (r == 0)
                            {
                                this.successiveACState = this.successiveACState == 2 ? 3 : 0;
                            }
                        }

                        break;
                    case 3: // Set value for a zero item
                        if (component.BlockData[offset + z] != 0)
                        {
                            component.BlockData[offset + z] += (short)(this.ReadBit(stream) << this.successiveState);
                        }
                        else
                        {
                            component.BlockData[offset + z] = (short)(this.successiveACNextValue << this.successiveState);
                            this.successiveACState = 0;
                        }

                        break;
                    case 4: // Eob
                        if (component.BlockData[offset + z] != 0)
                        {
                            component.BlockData[offset + z] += (short)(this.ReadBit(stream) << this.successiveState);
                        }

                        break;
                }

                k++;
            }

            if (this.successiveACState == 4)
            {
                this.eobrun--;
                if (this.eobrun == 0)
                {
                    this.successiveACState = 0;
                }
            }
        }
    }
}
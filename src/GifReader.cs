using System;
using System.Collections.Generic;
using System.IO;

namespace SpeccyCapture
{
    // A GIF that can't be opened for trimming; the message is shown to the user as it is.
    sealed class GifReadException : Exception
    {
        public GifReadException(string message) : base(message) { }
    }

    // Reads a GIF saved by this app back into a Recording, so it can be trimmed again.
    // Frames are rebuilt at the native size (the 1x-4x scale is undone), with their timing,
    // colours and loop setting, so saving it again gives the same picture.
    static class GifReader
    {
        const string NotOurs = "This GIF can't be trimmed here. Only GIFs saved by Speccy Game Capture O-MATIC can be opened.";

        public static Recording Load(string path, out int scale, out bool loop)
        {
            byte[] d = File.ReadAllBytes(path);
            try
            {
                return Parse(d, out scale, out loop);
            }
            catch (IndexOutOfRangeException)
            {
                throw new GifReadException("This GIF is damaged or incomplete.");
            }
        }

        static Recording Parse(byte[] d, out int scale, out bool loop)
        {
            if (d.Length < 13 || d[0] != 'G' || d[1] != 'I' || d[2] != 'F')
                throw new GifReadException("This file isn't a GIF.");

            int fullW = U16(d, 6), fullH = U16(d, 8);
            int flags = d[10], background = d[11];
            int p = 13;
            if ((flags & 0x80) == 0) throw new GifReadException(NotOurs);
            int[] global = ReadTable(d, ref p, 2 << (flags & 7));

            scale = DetectScale(fullW, fullH);
            if (scale == 0) throw new GifReadException(NotOurs);
            int w = fullW / scale, h = fullH / scale;

            // The picture uses the first 16 colours of the global table, as the app writes them.
            var palette = new int[16];
            for (int i = 0; i < 16; i++) palette[i] = i < global.Length ? global[i] : 0;

            var canvas = new byte[fullW * fullH];
            var small = new byte[w * h];
            byte[] last = null;
            var frames = new List<FrameRecord>();
            int[] globalMap = MapColours(global, palette);
            int bgIndex = background < globalMap.Length && globalMap[background] >= 0 ? globalMap[background] : 0;

            loop = false;
            int delay = 0, transparent = -1, disposal = 0;

            while (p < d.Length)
            {
                int block = d[p++];
                if (block == 0x3B) break; // trailer

                if (block == 0x21) // extension
                {
                    int label = d[p++];
                    if (label == 0xF9 && d[p] >= 4) // graphic control
                    {
                        int f = d[p + 1];
                        disposal = (f >> 2) & 7;
                        delay = U16(d, p + 2);
                        transparent = (f & 1) != 0 ? d[p + 4] : -1;
                    }
                    else if (label == 0xFF && d[p] == 11)
                    {
                        string app = Ascii(d, p + 1, 11);
                        if (app == "NETSCAPE2.0" || app == "ANIMEXTS1.0") loop = true;
                    }
                    SkipBlocks(d, ref p);
                    continue;
                }

                if (block != 0x2C) break; // not a GIF block: stop here and keep what was read

                int fx = U16(d, p), fy = U16(d, p + 2), fw = U16(d, p + 4), fh = U16(d, p + 6);
                int ff = d[p + 8];
                p += 9;
                int[] map = globalMap;
                if ((ff & 0x80) != 0) map = MapColours(ReadTable(d, ref p, 2 << (ff & 7)), palette);
                bool interlaced = (ff & 0x40) != 0;

                int minCode = d[p++];
                byte[] pixels = Decode(d, ref p, minCode, fw * fh);

                byte[] before = disposal == 3 ? (byte[])canvas.Clone() : null;
                for (int r = 0; r < fh; r++)
                {
                    int cy = fy + (interlaced ? InterlacedRow(r, fh) : r);
                    if (cy >= fullH) continue;
                    int src = r * fw, dst = cy * fullW;
                    for (int x = 0; x < fw; x++)
                    {
                        int cx = fx + x;
                        if (cx >= fullW) break;
                        int v = pixels[src + x];
                        if (v == transparent) continue;
                        int c = v < map.Length ? map[v] : -1;
                        if (c < 0) throw new GifReadException(NotOurs);
                        canvas[dst + cx] = (byte)c;
                    }
                }

                // Undo the scaling: take the top-left pixel of each scale x scale block.
                for (int y = 0; y < h; y++)
                {
                    int row = y * scale * fullW;
                    for (int x = 0; x < w; x++) small[y * w + x] = canvas[row + x * scale];
                }

                int length = Math.Max(1, (delay + 1) / 2); // GIF delays are 1/100 s; one Spectrum frame is 2
                if (last != null && Speccy.Same(small, last, small.Length))
                {
                    FrameRecord prev = frames[frames.Count - 1];
                    frames[frames.Count - 1] = new FrameRecord(prev.Compressed, prev.Frames + length);
                }
                else
                {
                    frames.Add(new FrameRecord(Speccy.Compress(small, small.Length), length));
                    last = (byte[])small.Clone();
                }

                if (disposal == 2)
                {
                    for (int y = fy; y < Math.Min(fullH, fy + fh); y++)
                        for (int x = fx; x < Math.Min(fullW, fx + fw); x++) canvas[y * fullW + x] = (byte)bgIndex;
                }
                else if (before != null)
                {
                    canvas = before;
                }
                delay = 0; transparent = -1; disposal = 0;
            }

            if (frames.Count == 0) throw new GifReadException("This GIF has no pictures in it.");
            return new Recording(frames, w, h, palette);
        }

        // The scale (1-4) that turns this size back into a picture size the app records,
        // or 0 if there isn't one.
        static int DetectScale(int width, int height)
        {
            for (int scale = 1; scale <= 4; scale++)
            {
                if (width % scale != 0 || height % scale != 0) continue;
                int w = width / scale, h = height / scale;
                foreach (string emulator in Emulators.Names)
                    foreach (int border in Speccy.BorderSizes)
                    {
                        int ew, eh;
                        Emulators.PictureSize(emulator, border, out ew, out eh);
                        if (ew == w && eh == h) return scale;
                    }
            }
            return 0;
        }

        // For each colour in a GIF table, the matching one of the 16 palette colours (-1 if none).
        static int[] MapColours(int[] table, int[] palette)
        {
            var map = new int[table.Length];
            for (int i = 0; i < table.Length; i++)
            {
                // Indices 0-15 of the global table are the palette itself, so they stay as they are
                // (black appears twice, as normal and bright black).
                if (i < 16 && table[i] == palette[i]) { map[i] = i; continue; }
                map[i] = -1;
                for (int c = 0; c < 16; c++)
                    if (palette[c] == table[i]) { map[i] = c; break; }
            }
            return map;
        }

        static int[] ReadTable(byte[] d, ref int p, int count)
        {
            var table = new int[count];
            for (int i = 0; i < count; i++, p += 3) table[i] = (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
            return table;
        }

        static void SkipBlocks(byte[] d, ref int p)
        {
            while (d[p] != 0) p += d[p] + 1;
            p++;
        }

        // Row number in the picture for the r-th stored row of an interlaced image.
        static int InterlacedRow(int r, int height)
        {
            int[] starts = { 0, 4, 2, 1 }, steps = { 8, 8, 4, 2 };
            for (int pass = 0; pass < 4; pass++)
            {
                int rows = (height - starts[pass] + steps[pass] - 1) / steps[pass];
                if (rows < 0) rows = 0;
                if (r < rows) return starts[pass] + r * steps[pass];
                r -= rows;
            }
            return height;
        }

        // GIF LZW decoding. Reads the image's data sub-blocks and leaves p after them.
        static byte[] Decode(byte[] d, ref int p, int minCode, int count)
        {
            var output = new byte[count];
            if (minCode < 2 || minCode > 11) throw new GifReadException("This GIF is damaged or incomplete.");
            int clear = 1 << minCode, end = clear + 1;
            int codeSize = minCode + 1, next = clear + 2;
            var prefix = new int[4096];
            var suffix = new byte[4096];
            var stack = new byte[4097];
            int old = -1, outPos = 0;
            byte first = 0;
            int bitBuffer = 0, bitCount = 0;
            bool done = false;

            while (d[p] != 0)
            {
                int size = d[p++];
                int stop = p + size;
                for (; p < stop; p++)
                {
                    if (done) continue;
                    bitBuffer |= d[p] << bitCount;
                    bitCount += 8;
                    while (bitCount >= codeSize && !done)
                    {
                        int code = bitBuffer & ((1 << codeSize) - 1);
                        bitBuffer >>= codeSize;
                        bitCount -= codeSize;

                        if (code == clear) { codeSize = minCode + 1; next = clear + 2; old = -1; continue; }
                        if (code == end) { done = true; break; }
                        if (old == -1)
                        {
                            if (code >= clear) continue;
                            if (outPos < count) output[outPos++] = (byte)code;
                            old = code;
                            first = (byte)code;
                            continue;
                        }

                        int incoming = code, sp = 0;
                        if (code >= next)
                        {
                            if (code > next) { done = true; break; } // damaged data
                            stack[sp++] = first;
                            code = old;
                        }
                        while (code > end) { stack[sp++] = suffix[code]; code = prefix[code]; }
                        first = (byte)code;
                        stack[sp++] = first;
                        while (sp > 0)
                        {
                            sp--;
                            if (outPos < count) output[outPos++] = stack[sp];
                        }

                        if (next < 4096)
                        {
                            prefix[next] = old;
                            suffix[next] = first;
                            next++;
                            if (next == (1 << codeSize) && codeSize < 12) codeSize++;
                        }
                        old = incoming;
                    }
                }
            }
            p++; // block terminator
            return output;
        }

        static int U16(byte[] d, int p)
        {
            return d[p] | (d[p + 1] << 8);
        }

        static string Ascii(byte[] d, int p, int count)
        {
            var c = new char[count];
            for (int i = 0; i < count; i++) c[i] = (char)d[p + i];
            return new string(c);
        }
    }
}

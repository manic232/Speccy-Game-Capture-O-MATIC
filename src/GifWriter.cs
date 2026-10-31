using System;
using System.Collections.Generic;
using System.IO;

namespace SpeccyCapture
{
    // Writes an animated GIF using the recording's 16-colour Spectrum palette.
    // After the first frame, only the rectangle that changed is stored, and
    // unchanged pixels inside it are transparent, which keeps files small.
    static class GifWriter
    {
        const int TransparentIndex = 16;
        const int PaletteBits = 5;   // 32-entry table: 16 Spectrum colours + transparent + spare

        // Saves emulator-frame range [start, end) of the recording.
        // step: keep one frame every 'step' emulator frames (1 = 50 fps, 2 = 25 fps ...)
        public static void Save(string path, Recording rec, long start, long end, int step, int scale, bool loop,
                                Action<int, int> progress)
        {
            if (step < 1) step = 1;
            if (scale < 1) scale = 1;
            if (start < 0) start = 0;
            if (end > rec.TotalFrames) end = rec.TotalFrames;
            if (end <= start) end = Math.Min(rec.TotalFrames, start + 1);
            IList<FrameRecord> frames = rec.Frames;
            int width = rec.Width, height = rec.Height;

            // Build the output timeline: which stored frame is showing at each step.
            var timeline = new List<int[]>(); // { frame index, duration in emulator frames }
            for (long t = start; t < end; t += step)
            {
                int index = rec.IndexAt(t);
                int duration = (int)Math.Min(step, end - t);
                if (timeline.Count > 0 && timeline[timeline.Count - 1][0] == index)
                    timeline[timeline.Count - 1][1] += duration;
                else
                    timeline.Add(new[] { index, duration });
            }

            string temp = path + ".part";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                WriteHeader(file, width * scale, height * scale, loop, rec.Palette);

                var lzw = new LzwEncoder();
                byte[] previous = null;  // last frame written
                byte[] pending = null;   // frame waiting for its final duration
                int pendingFrames = 0;
                int decodedIndex = -1;
                byte[] decoded = null;

                for (int k = 0; k < timeline.Count; k++)
                {
                    if (timeline[k][0] != decodedIndex)
                    {
                        decodedIndex = timeline[k][0];
                        decoded = rec.Decode(decodedIndex);
                    }

                    if (pending != null && Speccy.Same(decoded, pending, decoded.Length))
                    {
                        pendingFrames += timeline[k][1];
                    }
                    else
                    {
                        if (pending != null)
                        {
                            WriteFrame(file, lzw, pending, previous, pendingFrames, scale, width, height);
                            previous = pending;
                        }
                        pending = decoded;
                        pendingFrames = timeline[k][1];
                    }

                    if (progress != null && (k % 25 == 0)) progress(k, timeline.Count);
                }
                if (pending != null) WriteFrame(file, lzw, pending, previous, pendingFrames, scale, width, height);

                file.WriteByte(0x3B); // trailer
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            if (progress != null) progress(timeline.Count, timeline.Count);
        }

        static void WriteHeader(Stream s, int width, int height, bool loop, int[] palette)
        {
            WriteAscii(s, "GIF89a");
            WriteShort(s, width);
            WriteShort(s, height);
            s.WriteByte((byte)(0x80 | (7 << 4) | (PaletteBits - 1))); // global colour table, 8-bit colour resolution
            s.WriteByte(0); // background colour index
            s.WriteByte(0); // pixel aspect ratio

            for (int i = 0; i < (1 << PaletteBits); i++)
            {
                int rgb = i < 16 ? palette[i] : 0;
                s.WriteByte((byte)(rgb >> 16));
                s.WriteByte((byte)(rgb >> 8));
                s.WriteByte((byte)rgb);
            }

            if (loop)
            {
                // NETSCAPE2.0 extension: loop forever
                s.WriteByte(0x21); s.WriteByte(0xFF); s.WriteByte(11);
                WriteAscii(s, "NETSCAPE2.0");
                s.WriteByte(3); s.WriteByte(1);
                WriteShort(s, 0);
                s.WriteByte(0);
            }
        }

        static void WriteFrame(Stream s, LzwEncoder lzw, byte[] image, byte[] previous, int frames, int scale, int width, int height)
        {
            int x0 = 0, y0 = 0, x1 = width - 1, y1 = height - 1;
            bool transparent = previous != null;

            if (previous != null)
            {
                // Bounding box of the pixels that changed.
                x0 = width; y0 = height; x1 = -1; y1 = -1;
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        if (image[row + x] != previous[row + x])
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
                    }
                }
                if (x1 < 0) { x0 = y0 = x1 = y1 = 0; } // nothing changed: a single transparent pixel
            }

            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            int sw = w * scale, sh = h * scale;
            var pixels = new byte[sw * sh];
            for (int y = 0; y < h; y++)
            {
                int src = (y0 + y) * width + x0;
                for (int x = 0; x < w; x++)
                {
                    byte v = image[src + x];
                    if (transparent && previous[src + x] == v) v = TransparentIndex;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int dst = (y * scale + dy) * sw + x * scale;
                        for (int dx = 0; dx < scale; dx++) pixels[dst + dx] = v;
                    }
                }
            }

            int delay = Math.Min(frames * 2, 65535); // GIF delays are in 1/100 s; one Spectrum frame = 2

            // Graphic control extension
            s.WriteByte(0x21); s.WriteByte(0xF9); s.WriteByte(4);
            s.WriteByte((byte)((1 << 2) | (transparent ? 1 : 0))); // disposal: leave in place
            WriteShort(s, delay);
            s.WriteByte((byte)(transparent ? TransparentIndex : 0));
            s.WriteByte(0);

            // Image descriptor
            s.WriteByte(0x2C);
            WriteShort(s, x0 * scale);
            WriteShort(s, y0 * scale);
            WriteShort(s, sw);
            WriteShort(s, sh);
            s.WriteByte(0);

            lzw.Encode(s, pixels, PaletteBits);
        }

        static void WriteShort(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        static void WriteAscii(Stream s, string text)
        {
            foreach (char c in text) s.WriteByte((byte)c);
        }
    }

    // GIF-flavoured LZW compression (variable code size, up to 12 bits).
    sealed class LzwEncoder
    {
        const int MaxBits = 12;
        const int MaxCode = 1 << MaxBits;

        readonly int[] table = new int[MaxCode << 5]; // key = prefix * 32 + pixel; value = code + 1
        readonly byte[] block = new byte[256];
        int blockLength;
        Stream output;

        int initBits, nBits, maxCode, clearCode, eofCode, freeEntry;
        bool clearFlag;
        int bitBuffer, bitCount;

        public void Encode(Stream s, byte[] pixels, int minCodeSize)
        {
            output = s;
            s.WriteByte((byte)minCodeSize);

            initBits = minCodeSize + 1;
            nBits = initBits;
            maxCode = (1 << nBits) - 1;
            clearCode = 1 << minCodeSize;
            eofCode = clearCode + 1;
            freeEntry = clearCode + 2;
            clearFlag = false;
            bitBuffer = 0;
            bitCount = 0;
            blockLength = 0;
            Array.Clear(table, 0, table.Length);

            Output(clearCode);
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int c = pixels[i];
                int key = (prefix << 5) | c;
                int code = table[key];
                if (code != 0)
                {
                    prefix = code - 1;
                    continue;
                }
                Output(prefix);
                prefix = c;
                if (freeEntry < MaxCode)
                {
                    table[key] = freeEntry + 1;
                    freeEntry++;
                }
                else
                {
                    Array.Clear(table, 0, table.Length);
                    freeEntry = clearCode + 2;
                    clearFlag = true;
                    Output(clearCode);
                }
            }
            Output(prefix);
            Output(eofCode);

            // Flush remaining bits and the last block.
            while (bitCount > 0)
            {
                AddByte((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount = Math.Max(0, bitCount - 8);
            }
            FlushBlock();
            s.WriteByte(0); // block terminator
        }

        void Output(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += nBits;
            while (bitCount >= 8)
            {
                AddByte((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
            }

            if (freeEntry > maxCode || clearFlag)
            {
                if (clearFlag)
                {
                    nBits = initBits;
                    maxCode = (1 << nBits) - 1;
                    clearFlag = false;
                }
                else
                {
                    nBits++;
                    maxCode = nBits == MaxBits ? MaxCode : (1 << nBits) - 1;
                }
            }
        }

        void AddByte(byte b)
        {
            block[blockLength++] = b;
            if (blockLength == 255) FlushBlock();
        }

        void FlushBlock()
        {
            if (blockLength == 0) return;
            output.WriteByte((byte)blockLength);
            output.Write(block, 0, blockLength);
            blockLength = 0;
        }
    }
}

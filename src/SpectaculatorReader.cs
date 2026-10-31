using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpeccyCapture
{
    // Reads finished display frames straight out of a running Spectaculator.
    //
    // Spectaculator renders each emulated frame into one of two buffers
    // (12288 bytes each: a 256x192 bitmap stored row by row, 32 bytes per row,
    // followed by one attribute byte for every bitmap byte). A small structure
    // holds { back buffer, finished buffer, buffer on screen, flag }, and the
    // first two swap once per frame. We find that structure by its shape, then
    // confirm it by watching it swap 50 times a second.
    sealed class SpectaculatorReader : IDisposable
    {
        public const int FrameBytes = 12288;

        readonly IntPtr handle;
        readonly long structAddress;
        readonly uint bufferA, bufferB;
        readonly byte[] header = new byte[16];

        public readonly Process Process;

        public long StructAddress { get { return structAddress; } }

        SpectaculatorReader(Process process, IntPtr handle, long structAddress, uint a, uint b)
        {
            Process = process;
            this.handle = handle;
            this.structAddress = structAddress;
            bufferA = a;
            bufferB = b;
        }

        public static SpectaculatorReader TryConnect(Process process, out string message)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, process.Id);
            if (h == IntPtr.Zero)
            {
                message = "Can't read Spectaculator's memory. If Spectaculator runs as administrator, run this app as administrator too.";
                return null;
            }

            List<long> candidates = FindCandidates(h);
            uint a, b;
            long found = PickLiveCandidate(h, candidates, out a, out b);
            if (found == 0)
            {
                Native.CloseHandle(h);
                message = "Spectaculator is running but its display isn't updating. Is emulation paused?";
                return null;
            }

            message = "Connected";
            return new SpectaculatorReader(process, h, found, a, b);
        }

        // Scan the emulator's heap for 16-byte groups shaped like the frame structure.
        static List<long> FindCandidates(IntPtr h)
        {
            var result = new HashSet<long>();
            int mbiSize = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            byte[] buf = null;
            long address = 0x10000;

            while (address < 0xFFFF0000L)
            {
                Native.MEMORY_BASIC_INFORMATION m;
                if (Native.VirtualQueryEx(h, new IntPtr(address), out m, new IntPtr(mbiSize)) == IntPtr.Zero) break;
                long regionBase = m.BaseAddress.ToInt64();
                long size = m.RegionSize.ToInt64();
                if (size <= 0) break;

                bool readable = m.State == Native.MEM_COMMIT && m.Type == Native.MEM_PRIVATE &&
                                (m.Protect & Native.PAGE_GUARD) == 0 && (m.Protect & Native.PAGE_READABLE) != 0;
                if (readable)
                {
                    long offset = 0;
                    while (offset < size)
                    {
                        int chunk = (int)Math.Min(size - offset, 16 << 20);
                        if (buf == null || buf.Length < chunk) buf = new byte[Math.Max(chunk, 1 << 20)];
                        IntPtr read;
                        if (Native.ReadProcessMemory(h, new IntPtr(regionBase + offset), buf, new IntPtr(chunk), out read))
                        {
                            int n = (int)read.ToInt64();
                            for (int i = 0; i + 16 <= n; i += 4)
                            {
                                if (LooksLikeFrameStruct(buf, i)) result.Add(regionBase + offset + i);
                            }
                        }
                        offset += chunk;
                        if (offset < size) offset -= 16; // overlap so nothing straddling a chunk edge is missed
                    }
                }
                address = regionBase + size;
            }
            return new List<long>(result);
        }

        static bool LooksLikeFrameStruct(byte[] buf, int i)
        {
            uint a = BitConverter.ToUInt32(buf, i);
            if (a < 0x10000 || (a & 3) != 0) return false;
            uint b = BitConverter.ToUInt32(buf, i + 4);
            if (b < 0x10000 || (b & 3) != 0 || a == b) return false;
            long d = (long)a - b;
            if (d < 0) d = -d;
            if (d < 0x3000 || d > 0x3400) return false;   // the two buffers sit ~12 KB apart
            uint c = BitConverter.ToUInt32(buf, i + 8);
            if (c != a && c != b) return false;
            uint flag = BitConverter.ToUInt32(buf, i + 12);
            return flag <= 1;
        }

        sealed class Candidate
        {
            public long Address;
            public uint A, B, LastFront;
            public int Flips;
            public bool Bad;
        }

        // Watch every candidate for a moment; the real one swaps its two buffers every frame.
        static long PickLiveCandidate(IntPtr h, List<long> addresses, out uint bufA, out uint bufB)
        {
            bufA = bufB = 0;
            var list = new List<Candidate>();
            var hdr = new byte[16];
            foreach (long addr in addresses)
            {
                if (!Read(h, addr, hdr, 16)) continue;
                var c = new Candidate();
                c.Address = addr;
                c.A = BitConverter.ToUInt32(hdr, 0);
                c.B = BitConverter.ToUInt32(hdr, 4);
                c.LastFront = c.B;
                list.Add(c);
            }
            if (list.Count == 0) return 0;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 700)
            {
                bool done = false;
                foreach (Candidate c in list)
                {
                    if (c.Bad) continue;
                    if (!Read(h, c.Address, hdr, 8)) { c.Bad = true; continue; }
                    uint s0 = BitConverter.ToUInt32(hdr, 0), s1 = BitConverter.ToUInt32(hdr, 4);
                    if (!((s0 == c.A && s1 == c.B) || (s0 == c.B && s1 == c.A))) { c.Bad = true; continue; }
                    if (s1 != c.LastFront) { c.Flips++; c.LastFront = s1; }
                    if (c.Flips >= 8) done = true;
                }
                if (done) break;
                Thread.Sleep(1);
            }

            Candidate best = null;
            foreach (Candidate c in list)
            {
                if (!c.Bad && c.Flips >= 3 && (best == null || c.Flips > best.Flips)) best = c;
            }
            if (best == null) return 0;
            bufA = best.A;
            bufB = best.B;
            return best.Address;
        }

        static bool Read(IntPtr h, long address, byte[] dest, int count)
        {
            IntPtr read;
            return Native.ReadProcessMemory(h, new IntPtr(address), dest, new IntPtr(count), out read) &&
                   read.ToInt64() == count;
        }

        // Returns the address of the most recently finished frame, or false if the link is broken.
        public bool Poll(out uint front)
        {
            front = 0;
            if (!Read(handle, structAddress, header, 8)) return false;
            uint s0 = BitConverter.ToUInt32(header, 0), s1 = BitConverter.ToUInt32(header, 4);
            if (!((s0 == bufferA && s1 == bufferB) || (s0 == bufferB && s1 == bufferA))) return false;
            front = s1;
            return true;
        }

        public bool ReadFrame(uint address, byte[] dest)
        {
            return Read(handle, address, dest, FrameBytes);
        }

        // Straight after each frame buffer sits { flag, count, pointer, -1 }. The pointer leads to
        // 'count' pairs of { border colour, CPU cycle within the frame }: every border colour
        // change during that frame. The list ends with a far-future entry.
        readonly byte[] borderHeader = new byte[16];
        byte[] borderBuffer = new byte[8 * 1024];

        public int ReadBorderEvents(uint frameAddress, int[] colours, int[] cycles)
        {
            if (!Read(handle, (long)frameAddress + FrameBytes, borderHeader, 16)) return 0;
            int count = BitConverter.ToInt32(borderHeader, 4);
            uint list = BitConverter.ToUInt32(borderHeader, 8);
            if (count < 1 || list < 0x10000) return 0;
            if (count > colours.Length) count = colours.Length;

            int bytes = count * 8;
            if (borderBuffer.Length < bytes) borderBuffer = new byte[bytes];
            if (!Read(handle, list, borderBuffer, bytes)) return 0;

            int last = int.MinValue;
            for (int i = 0; i < count; i++)
            {
                colours[i] = BitConverter.ToInt32(borderBuffer, i * 8) & 7;
                cycles[i] = BitConverter.ToInt32(borderBuffer, i * 8 + 4);
                if (cycles[i] < last) return i; // keep only the sorted part if the list looks odd
                last = cycles[i];
            }
            return count;
        }

        public void Dispose()
        {
            Native.CloseHandle(handle);
        }
    }

    // Turns Spectaculator's raw frames into finished pictures: draws the border from its
    // list of colour changes, applies FLASH, and works out frame timing.
    sealed class SpectaculatorSource : EmulatorSource
    {
        const int MaxBorderEvents = 70000;

        readonly SpectaculatorReader reader;
        readonly byte[] raw = new byte[SpectaculatorReader.FrameBytes];
        readonly int[] borderColours = new int[MaxBorderEvents];
        readonly int[] borderCycles = new int[MaxBorderEvents];
        readonly Stopwatch clock = Stopwatch.StartNew();
        uint lastFront;
        double lastTime;
        long frameCounter;

        SpectaculatorSource(Process process, SpectaculatorReader reader) : base(process)
        {
            this.reader = reader;
        }

        public static SpectaculatorSource TryConnect(Process process, out string message)
        {
            SpectaculatorReader r = SpectaculatorReader.TryConnect(process, out message);
            return r == null ? null : new SpectaculatorSource(process, r);
        }

        public override string Name { get { return Emulators.Spectaculator; } }

        public override int[] Palette { get { return Speccy.Palette; } }

        public override int NextFrame(byte[] image, int border)
        {
            uint front;
            if (!reader.Poll(out front)) return -1;
            if (front == lastFront) return 0;

            double now = clock.Elapsed.TotalMilliseconds;
            if (!reader.ReadFrame(front, raw)) return -1;
            int borderCount = border > 0 ? reader.ReadBorderEvents(front, borderColours, borderCycles) : 0;

            // How many emulator frames passed since the last one we saw. Usually 1;
            // a long gap means the emulator was paused, which we don't record as time.
            int elapsed = 1;
            if (lastFront != 0)
            {
                elapsed = (int)Math.Round((now - lastTime) / Speccy.FrameMs);
                if (elapsed < 1 || elapsed > 10) elapsed = 1;
            }
            lastFront = front;
            lastTime = now;

            frameCounter += elapsed;
            bool flashInverted = ((frameCounter >> 4) & 1) != 0; // FLASH swaps every 16 frames
            Speccy.Render(raw, image, border, flashInverted, borderColours, borderCycles, borderCount);
            return elapsed;
        }

        // Spectaculator sometimes moves to a different display buffer (after a reset or model change).
        public override EmulatorSource Rescan()
        {
            string ignored;
            SpectaculatorReader other = SpectaculatorReader.TryConnect(Process, out ignored);
            if (other == null) return null;
            if (other.StructAddress != reader.StructAddress) return new SpectaculatorSource(Process, other);
            other.Dispose();
            return null;
        }

        // The window title is "<game> - Spectaculator".
        public override string ReadGameName()
        {
            try
            {
                Process.Refresh();
                string title = Process.MainWindowTitle ?? "";
                int cut = title.IndexOf(" - Spectaculator", StringComparison.OrdinalIgnoreCase);
                string name = cut > 0 ? title.Substring(0, cut).Trim() : "";
                return name.Equals("Untitled", StringComparison.OrdinalIgnoreCase) ? "" : name;
            }
            catch
            {
                return "";
            }
        }

        public override void Dispose()
        {
            reader.Dispose();
        }
    }
}

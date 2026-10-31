using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpeccyCapture
{
    // Reads finished frames straight out of a running Fuse (Windows build; tested with 1.10.1).
    //
    // Fuse draws every frame into a 640 x 480 array of 16-bit colour numbers (0-15) inside
    // fuse.exe. The right and bottom halves are only used by Timex hi-res modes, so the
    // Spectrum picture is the top-left 320 x 240: the 256 x 192 screen with 32 pixels of
    // border left and right and 24 above and below. Fuse redraws it in one burst at the end
    // of each emulated frame and bumps its frame counters at the same moment, so we wait for
    // a counter to tick and read the picture just afterwards.
    //
    // Both are found by shape rather than fixed addresses: the picture is the only long run
    // of small 16-bit numbers in fuse.exe's data, and the counter is a number that goes up
    // by one every 20 ms.
    sealed class FuseSource : EmulatorSource
    {
        public const int MaxBorderX = 32, MaxBorderY = 24;

        const int StrideWords = 640;
        const int RowBytes = StrideWords * 2;
        const int PictureRows = Speccy.Height + 2 * MaxBorderY;   // 240
        const int PictureBytes = PictureRows * RowBytes;
        const int ImageBytes = 480 * RowBytes;
        const int RunWords = PictureRows * StrideWords;            // the picture's rows, end to end

        // Fuse's colours: 0xC0 for normal, 0xFF for bright.
        static readonly int[] FusePalette =
        {
            0x000000, 0x0000C0, 0xC00000, 0xC000C0, 0x00C000, 0x00C0C0, 0xC0C000, 0xC0C0C0,
            0x000000, 0x0000FF, 0xFF0000, 0xFF00FF, 0x00FF00, 0x00FFFF, 0xFFFF00, 0xFFFFFF
        };

        readonly IntPtr handle;
        readonly long imageAddress, counterAddress;
        readonly byte[] picture = new byte[PictureBytes];
        readonly byte[] counterBytes = new byte[4];
        readonly string gameName;
        uint lastCounter;
        bool haveFrame;

        FuseSource(Process process, IntPtr handle, long imageAddress, long counterAddress) : base(process)
        {
            this.handle = handle;
            this.imageAddress = imageAddress;
            this.counterAddress = counterAddress;
            // Fuse's title bar never shows the game, so use the file it was started with.
            string file = Emulators.FileFromCommandLine(process);
            gameName = file != null ? Path.GetFileNameWithoutExtension(file) : "";
        }

        public override string Name { get { return Emulators.Fuse; } }

        public override int[] Palette { get { return FusePalette; } }

        struct Region
        {
            public long Base, AllocationBase;
            public int Size;
        }

        public static FuseSource TryConnect(Process process, out string message)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, process.Id);
            if (h == IntPtr.Zero)
            {
                message = "Can't read Fuse's memory. If Fuse runs as administrator, run this app as administrator too.";
                return null;
            }

            bool keep = false;
            try
            {
                List<Region> regions = WritableModuleRegions(h);
                long image, moduleBase;
                if (!FindImage(h, regions, out image, out moduleBase))
                {
                    message = "Fuse is running, but its screen couldn't be found. This version of Fuse may not be supported.";
                    return null;
                }
                long counter = FindFrameCounter(h, regions, moduleBase, image);
                if (counter == 0)
                {
                    message = "Fuse is running but its display isn't updating. Is emulation paused?";
                    return null;
                }
                keep = true;
                message = "Connected";
                return new FuseSource(process, h, image, counter);
            }
            finally
            {
                if (!keep) Native.CloseHandle(h);
            }
        }

        // Written-to data pages that belong to loaded modules (fuse.exe and its DLLs).
        static List<Region> WritableModuleRegions(IntPtr h)
        {
            var list = new List<Region>();
            int mbiSize = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            long address = 0x10000;
            while (address < 0xFFFF0000L)
            {
                Native.MEMORY_BASIC_INFORMATION m;
                if (Native.VirtualQueryEx(h, new IntPtr(address), out m, new IntPtr(mbiSize)) == IntPtr.Zero) break;
                long regionBase = m.BaseAddress.ToInt64();
                long size = m.RegionSize.ToInt64();
                if (size <= 0) break;
                if (m.State == Native.MEM_COMMIT && m.Type == Native.MEM_IMAGE &&
                    (m.Protect & Native.PAGE_GUARD) == 0 && (m.Protect & Native.PAGE_WRITABLE) != 0 &&
                    size <= 64L << 20)
                {
                    list.Add(new Region { Base = regionBase, AllocationBase = m.AllocationBase.ToInt64(), Size = (int)size });
                }
                address = regionBase + size;
            }
            return list;
        }

        // The picture is the one stretch of at least 240 rows' worth of 16-bit values that are all 0-15.
        static bool FindImage(IntPtr h, List<Region> regions, out long image, out long moduleBase)
        {
            image = moduleBase = 0;
            bool foundWithContent = false;
            foreach (Region r in regions)
            {
                if (r.Size < RunWords * 2) continue;
                var data = new byte[r.Size];
                if (!Read(h, r.Base, data, r.Size)) continue;

                int start = -1, nonZero = 0;
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    bool small = data[i + 1] == 0 && data[i] <= 15;
                    if (small)
                    {
                        if (start < 0) { start = i; nonZero = 0; }
                        if (data[i] != 0 && (i - start) / 2 < RunWords) nonZero++;
                    }
                    if ((!small || i + 2 >= data.Length) && start >= 0)
                    {
                        int words = (i - start) / 2 + (small ? 1 : 0);
                        if (words >= RunWords && !foundWithContent)
                        {
                            // Prefer a run that actually has a picture in it over an empty one.
                            if (image == 0 || nonZero > 0)
                            {
                                image = r.Base + start;
                                moduleBase = r.AllocationBase;
                                foundWithContent = nonZero > 0;
                            }
                        }
                        start = -1;
                    }
                }
            }
            return image != 0;
        }

        // Samples the module's data for about half a second and picks a number that rises
        // by one per emulated frame.
        static long FindFrameCounter(IntPtr h, List<Region> regions, long moduleBase, long image)
        {
            var mine = new List<Region>();
            long total = 0;
            foreach (Region r in regions)
            {
                if (r.AllocationBase != moduleBase || r.Size > 8 << 20) continue;
                mine.Add(r);
                total += r.Size;
            }
            if (mine.Count == 0 || total > 32L << 20) return 0;

            const int Samples = 30;
            var snaps = new byte[mine.Count][][];
            for (int k = 0; k < mine.Count; k++) snaps[k] = new byte[Samples][];
            var clock = Stopwatch.StartNew();
            for (int s = 0; s < Samples; s++)
            {
                for (int k = 0; k < mine.Count; k++)
                {
                    var buf = new byte[mine[k].Size];
                    Read(h, mine[k].Base, buf, buf.Length);
                    snaps[k][s] = buf;
                }
                if (s < Samples - 1) Thread.Sleep(15);
            }
            double expected = clock.Elapsed.TotalMilliseconds / Speccy.FrameMs;

            long best = 0;
            uint bestValue = 0;
            int bestSteps = 0;
            for (int k = 0; k < mine.Count; k++)
            {
                for (int i = 0; i + 4 <= mine[k].Size; i += 4)
                {
                    long address = mine[k].Base + i;
                    if (address >= image && address < image + ImageBytes) continue;

                    uint first = BitConverter.ToUInt32(snaps[k][0], i), last = first;
                    int steps = 0;
                    bool ok = true;
                    for (int s = 1; s < Samples && ok; s++)
                    {
                        uint v = BitConverter.ToUInt32(snaps[k][s], i);
                        if (v < last || v - last > 6) ok = false;
                        else if (v != last) steps++;
                        last = v;
                    }
                    if (!ok) continue;
                    double rise = last - first;
                    if (rise < 5 || rise < expected * 0.5 || rise > expected * 2) continue;

                    // Several counters qualify and all tick together. Avoid tiny ones that may
                    // wrap round (FLASH timing counts 0-31), then take the steadiest.
                    bool better = best == 0 ||
                                  (last >= 64 && bestValue < 64) ||
                                  ((last >= 64) == (bestValue >= 64) && steps > bestSteps);
                    if (better)
                    {
                        best = address;
                        bestValue = last;
                        bestSteps = steps;
                    }
                }
            }
            return best;
        }

        static bool Read(IntPtr h, long address, byte[] dest, int count)
        {
            IntPtr read;
            return Native.ReadProcessMemory(h, new IntPtr(address), dest, new IntPtr(count), out read) &&
                   read.ToInt64() == count;
        }

        bool ReadCounter(out uint value)
        {
            value = 0;
            if (!Read(handle, counterAddress, counterBytes, 4)) return false;
            value = BitConverter.ToUInt32(counterBytes, 0);
            return true;
        }

        public override int NextFrame(byte[] image, int border)
        {
            uint counter;
            if (!ReadCounter(out counter)) return -1;
            if (haveFrame && counter == lastCounter) return 0;

            // Fuse draws the frame within half a millisecond of the tick; give it a little longer.
            if (haveFrame) Thread.Sleep(2);
            for (int attempt = 0; ; attempt++)
            {
                if (!Read(handle, imageAddress, picture, PictureBytes)) return -1;
                uint after;
                if (!ReadCounter(out after)) return -1;
                bool settled = after == counter;
                counter = after;
                if (settled || attempt == 2) break;
                Thread.Sleep(2); // another frame started while we read; read it again once it's done
            }

            int elapsed = haveFrame ? (int)(counter - lastCounter) : 1;
            if (elapsed < 1 || elapsed > 50) elapsed = 1;
            lastCounter = counter;
            haveFrame = true;

            int bx = Math.Min(border, MaxBorderX), by = Math.Min(border, MaxBorderY);
            int width = Speccy.Width + 2 * bx, height = Speccy.Height + 2 * by;
            int left = MaxBorderX - bx, top = MaxBorderY - by;
            int o = 0;
            for (int y = 0; y < height; y++)
            {
                int src = ((top + y) * StrideWords + left) * 2;
                for (int x = 0; x < width; x++, src += 2)
                {
                    int c = picture[src] & 15;
                    image[o++] = (byte)(c == 8 ? 0 : c); // bright black is black
                }
            }
            return elapsed;
        }

        // If the counter we picked stopped, look again in case another one is ticking.
        public override EmulatorSource Rescan()
        {
            string ignored;
            FuseSource other = TryConnect(Process, out ignored);
            if (other == null) return null;
            if (other.imageAddress != imageAddress || other.counterAddress != counterAddress) return other;
            other.Dispose();
            return null;
        }

        public override string ReadGameName()
        {
            return gameName;
        }

        public override void Dispose()
        {
            Native.CloseHandle(handle);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SpeccyCapture
{
    // One stored GIF frame: a palette-index image (compressed) and how many
    // 1/50 s emulator frames it stays on screen.
    sealed class FrameRecord
    {
        public readonly byte[] Compressed;
        public readonly int Frames;

        public FrameRecord(byte[] compressed, int frames)
        {
            Compressed = compressed;
            Frames = frames;
        }
    }

    // A finished recording: frames plus their size and timing.
    sealed class Recording
    {
        public readonly List<FrameRecord> Frames;
        public readonly int Width, Height;
        public readonly int[] Palette; // RGB for indices 0-15, from the emulator it was recorded in
        public readonly long TotalFrames;
        readonly long[] starts;

        public Recording(List<FrameRecord> frames, int width, int height, int[] palette)
        {
            Frames = frames;
            Width = width;
            Height = height;
            Palette = palette ?? Speccy.Palette;
            starts = new long[frames.Count];
            long t = 0;
            for (int i = 0; i < frames.Count; i++) { starts[i] = t; t += frames[i].Frames; }
            TotalFrames = t;
        }

        public int Pixels { get { return Width * Height; } }

        // Index of the stored frame showing at emulator-frame time t.
        public int IndexAt(long t)
        {
            int lo = 0, hi = Frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= t) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public byte[] Decode(int index)
        {
            return Speccy.Decompress(Frames[index].Compressed, Pixels);
        }
    }

    static class Speccy
    {
        public const int Width = 256;
        public const int Height = 192;
        public const double FrameMs = 20.0; // PAL Spectrum: 50 frames per second

        // 48K ULA timing: 224 CPU cycles per scanline, the top-left screen pixel is
        // drawn at cycle 14336 after the frame interrupt, and 1 cycle = 2 pixels.
        const int CyclesPerLine = 224;
        const int FirstPixelCycle = 14336;

        // Index 0-7 normal, 8-15 bright. Bright black is folded onto 0 when rendering.
        public static readonly int[] Palette =
        {
            0x000000, 0x0000D7, 0xD70000, 0xD700D7, 0x00D700, 0x00D7D7, 0xD7D700, 0xD7D7D7,
            0x000000, 0x0000FF, 0xFF0000, 0xFF00FF, 0x00FF00, 0x00FFFF, 0xFFFF00, 0xFFFFFF
        };

        // Border choices: pixels of border on every side.
        public static readonly int[] BorderSizes = { 0, 16, 32, 48 };

        // Draws one frame into dest (width = 256 + 2*border, height = 192 + 2*border).
        // raw is Spectaculator's bitmap + per-byte attributes; border colours come from
        // the frame's list of border changes.
        public static void Render(byte[] raw, byte[] dest, int border, bool flashInverted,
                                  int[] borderColours, int[] borderCycles, int borderCount)
        {
            int width = Width + 2 * border;

            for (int i = 0; i < 6144; i++)
            {
                int pixels = raw[i];
                int attr = raw[6144 + i];
                int bright = (attr & 0x40) != 0 ? 8 : 0;
                int ink = (attr & 7) | bright;
                int paper = ((attr >> 3) & 7) | bright;
                if (ink == 8) ink = 0;
                if (paper == 8) paper = 0;
                if (flashInverted && (attr & 0x80) != 0) { int t = ink; ink = paper; paper = t; }

                int y = i >> 5, x = (i & 31) * 8; // 32 bytes per row
                int o = (y + border) * width + border + x;
                for (int bit = 0; bit < 8; bit++)
                {
                    dest[o + bit] = (byte)((pixels & (0x80 >> bit)) != 0 ? ink : paper);
                }
            }

            if (border == 0) return;

            // Walk the border in beam order; the beam's cycle count only ever goes up,
            // so one pass through the colour-change list is enough.
            int e = 0;
            int colour = borderCount > 0 ? borderColours[0] : 0;
            for (int y = -border; y < Height + border; y++)
            {
                int row = (y + border) * width;
                bool inScreenRows = y >= 0 && y < Height;
                int lineCycle = FirstPixelCycle + CyclesPerLine * y;
                for (int x = -border; x < Width + border; x++)
                {
                    if (inScreenRows && x == 0) { x = Width - 1; continue; } // skip the screen area
                    int cycle = lineCycle + (x >> 1);
                    while (e + 1 < borderCount && borderCycles[e + 1] <= cycle) { e++; colour = borderColours[e]; }
                    dest[row + x + border] = (byte)colour;
                }
            }
        }

        public static bool Same(byte[] a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static byte[] Compress(byte[] data, int count)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true)) ds.Write(data, 0, count);
                return ms.ToArray();
            }
        }

        public static byte[] Decompress(byte[] data, int count)
        {
            var result = new byte[count];
            using (var ds = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                int total = 0;
                while (total < count)
                {
                    int n = ds.Read(result, total, count - total);
                    if (n <= 0) break;
                    total += n;
                }
            }
            return result;
        }
    }

    // Background thread: stays connected to a running emulator, keeps the latest
    // frame for the preview, and stores frames while recording.
    sealed class CaptureEngine
    {
        const int MaxPixels = (Speccy.Width + 96) * (Speccy.Height + 96);

        readonly object previewLock = new object();
        readonly object recordLock = new object();
        readonly byte[] latest = new byte[MaxPixels];
        int latestWidth = Speccy.Width, latestHeight = Speccy.Height;
        int[] latestPalette = Speccy.Palette;
        int latestVersion;

        Thread thread;
        volatile bool stopRequested;
        volatile bool connected;
        volatile bool stale;
        volatile string status = "Starting…";
        volatile string gameName = "";
        volatile string emulatorName = "";
        volatile int borderSize;

        bool recording;
        int recBorder, recWidth, recHeight;
        int[] recPalette;
        List<FrameRecord> recFrames;
        byte[] recCurrent;
        int recCurrentFrames;
        long recTotalFrames;
        long recCompressedBytes;

        public bool Connected { get { return connected; } }

        // True when connected but no new frames have arrived for a couple of seconds.
        public bool Stale { get { return stale; } }

        public string Status { get { return status; } }
        public string GameName { get { return gameName; } }

        // "Spectaculator" or "Fuse" while connected.
        public string EmulatorName { get { return emulatorName; } }

        // Border width in pixels on each side (0 = screen only). Takes effect on the next frame.
        public int BorderSize
        {
            get { return borderSize; }
            set { borderSize = Math.Max(0, Math.Min(48, value)); }
        }

        public bool IsRecording { get { lock (recordLock) return recording; } }

        public long RecordedFrames { get { lock (recordLock) return recTotalFrames; } }

        public long RecordedBytes { get { lock (recordLock) return recCompressedBytes; } }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "Emulator capture";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        public void Stop()
        {
            stopRequested = true;
            if (thread != null) thread.Join(2000);
        }

        // Copies the newest frame if it changed since 'version'. dest must hold MaxPixels.
        public bool GetLatest(byte[] dest, ref int version, out int width, out int height, out int[] palette)
        {
            lock (previewLock)
            {
                width = latestWidth;
                height = latestHeight;
                palette = latestPalette;
                if (version == latestVersion) return false;
                Buffer.BlockCopy(latest, 0, dest, 0, width * height);
                version = latestVersion;
                return true;
            }
        }

        public static int PreviewBufferSize { get { return MaxPixels; } }

        public void StartRecording()
        {
            lock (recordLock)
            {
                recBorder = borderSize;
                recWidth = recHeight = 0; // set by the first frame
                recPalette = null;
                recFrames = new List<FrameRecord>();
                recCurrent = null;
                recCurrentFrames = 0;
                recTotalFrames = 0;
                recCompressedBytes = 0;
                recording = true;
            }
        }

        public Recording StopRecording()
        {
            lock (recordLock)
            {
                recording = false;
                List<FrameRecord> frames = recFrames ?? new List<FrameRecord>();
                if (recCurrent != null)
                {
                    recCurrentFrames += 1; // the final frame is shown for one frame
                    recTotalFrames += 1;
                    frames.Add(new FrameRecord(Speccy.Compress(recCurrent, recWidth * recHeight), recCurrentFrames));
                    recCurrent = null;
                }
                recFrames = null;
                return new Recording(frames, recWidth, recHeight, recPalette);
            }
        }

        void Run()
        {
            Native.timeBeginPeriod(1);
            try
            {
                EmulatorSource source = null;
                while (!stopRequested)
                {
                    if (source == null)
                    {
                        string message;
                        try
                        {
                            source = ConnectToAny(out message);
                        }
                        catch (Exception ex)
                        {
                            message = "Error looking for an emulator: " + ex.Message;
                        }

                        if (source == null)
                        {
                            connected = false;
                            status = message;
                            for (int i = 0; i < 15 && !stopRequested; i++) Thread.Sleep(100);
                            continue;
                        }
                    }

                    EmulatorSource next = null;
                    try
                    {
                        emulatorName = source.Name;
                        gameName = source.ReadGameName();
                        connected = true;
                        stale = false;
                        status = "Connected to " + source.Name;
                        next = CaptureLoop(source);
                    }
                    catch (Exception ex)
                    {
                        status = "Capture error: " + ex.Message;
                    }
                    finally
                    {
                        source.Dispose();
                    }
                    if (next == null) connected = false;
                    source = next;
                }
                if (source != null) source.Dispose();
            }
            finally
            {
                Native.timeEndPeriod(1);
            }
        }

        // Tries the emulator in front first, then the most recently started.
        static EmulatorSource ConnectToAny(out string message)
        {
            List<Process> running = Emulators.FindRunning();
            if (running.Count == 0)
            {
                message = "Waiting for Spectaculator or Fuse to start…";
                return null;
            }
            message = null;
            foreach (Process p in running)
            {
                string m;
                EmulatorSource s = Emulators.TryConnect(p, out m);
                if (s != null) return s;
                if (message == null) message = m;
            }
            return null;
        }

        // Runs until the link breaks (returns null) or a better link turns up (returns it):
        // a fresh link to the same emulator, or another emulator window brought to the front.
        EmulatorSource CaptureLoop(EmulatorSource source)
        {
            var image = new byte[MaxPixels];
            var clock = Stopwatch.StartNew();
            long lastSlowCheck = 0;
            long lastFrameAt = 0;
            long lastRescan = 0;
            int failedPid = 0;
            long failedAt = 0;
            int failures = 0;

            while (!stopRequested)
            {
                int border;
                lock (recordLock) border = recording ? recBorder : borderSize;

                int elapsed = source.NextFrame(image, border);
                if (elapsed < 0)
                {
                    // A short glitch can happen when the emulator resets or changes model.
                    if (++failures > 100) return null;
                    Thread.Sleep(1);
                    continue;
                }
                failures = 0;

                if (elapsed == 0)
                {
                    // No new frame for a while: either emulation is paused, or the emulator
                    // has started drawing somewhere else. Look again now and then.
                    long ms = clock.ElapsedMilliseconds;
                    if (ms - lastFrameAt > 2000 && ms - lastRescan > 3000)
                    {
                        stale = true;
                        EmulatorSource other = source.Rescan();
                        lastRescan = clock.ElapsedMilliseconds;
                        if (other != null) return other;
                    }
                }
                else
                {
                    lastFrameAt = clock.ElapsedMilliseconds;
                    stale = false;
                    int width, height;
                    Emulators.PictureSize(source.Name, border, out width, out height);

                    lock (previewLock)
                    {
                        Buffer.BlockCopy(image, 0, latest, 0, width * height);
                        latestWidth = width;
                        latestHeight = height;
                        latestPalette = source.Palette;
                        latestVersion++;
                    }

                    lock (recordLock)
                    {
                        if (recording) AddRecordedFrame(image, width, height, source.Palette, elapsed);
                    }
                }

                long now = clock.ElapsedMilliseconds;
                if (now - lastSlowCheck > 500)
                {
                    lastSlowCheck = now;
                    if (source.Process.HasExited) return null;
                    gameName = source.ReadGameName();

                    // Follow a switch to another emulator window, but never in the middle of a recording.
                    if (!IsRecording)
                    {
                        Process front = Emulators.ForegroundEmulator();
                        if (front != null && front.Id != source.Process.Id && (front.Id != failedPid || now - failedAt > 5000))
                        {
                            string ignored;
                            EmulatorSource other = Emulators.TryConnect(front, out ignored);
                            if (other != null) return other;
                            failedPid = front.Id;
                            failedAt = clock.ElapsedMilliseconds;
                        }
                    }
                }

                Thread.Sleep(1);
            }
            return null;
        }

        // Called with recordLock held. Identical consecutive frames are merged.
        void AddRecordedFrame(byte[] image, int width, int height, int[] palette, int elapsed)
        {
            if (recWidth == 0)
            {
                recWidth = width;
                recHeight = height;
                recPalette = palette;
            }
            else if (width != recWidth || height != recHeight || palette != recPalette)
            {
                return; // a different emulator took over; keep the recording consistent
            }

            int pixels = recWidth * recHeight;
            if (recCurrent == null)
            {
                recCurrent = new byte[pixels];
                Buffer.BlockCopy(image, 0, recCurrent, 0, pixels);
                recCurrentFrames = 0;
                return;
            }

            recCurrentFrames += elapsed;
            recTotalFrames += elapsed;
            if (!Speccy.Same(image, recCurrent, pixels))
            {
                byte[] packed = Speccy.Compress(recCurrent, pixels);
                recCompressedBytes += packed.Length;
                recFrames.Add(new FrameRecord(packed, recCurrentFrames));
                Buffer.BlockCopy(image, 0, recCurrent, 0, pixels);
                recCurrentFrames = 0;
            }
        }
    }
}

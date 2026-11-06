// MainWindow.DisplayDialGif.cs — partial class: animated GIF on the Media Dock (round
// Display Dial screen), EXPERIMENTAL.
//
// What the hardware gives us: the dock has exactly ONE host-writable picture, the
// screensaver image (240x204 RGB565, StartPicUpdate targetDev=0 targetPic=1 — see
// EverestImageUploader). No RAM framebuffer / streaming command is known: every picture
// write is a FLASH write that takes ~2s on the wire and leaves the firmware rejecting
// further writes for several more seconds (see MainWindow.Everest.cs EvUploadNdkImages
// and MainWindow.HwBusy.cs). So "playing a GIF" here can only ever mean a slow slideshow
// of a few key frames, one flash write each.
//
// Two open questions only real hardware can answer, which is why this is a bounded
// experiment and not an endless loop:
//   1. Does the dock redraw the screensaver while it is already showing it, or only
//      when the screensaver engages?
//   2. Does a picture write count as host activity and kick the dock OUT of the
//      screensaver (MainWindow.MediaDock.cs: "every host write resets the dock's idle
//      counter")? If so, the picture would never be visible during playback.
// Every frame logs the dock page (byMMDockMenuIndex) before the upload so the log shows
// whether the screensaver stayed engaged.
//
// Flash wear: one frame = one erase/write cycle of the same flash region. A typical SPI
// NOR sector is rated for ~100k cycles; an unbounded 5s loop would reach that within
// about a week. Hence the hard per-run write budget (DialGifMaxUploadsPerRun) and the
// minimum interval floor, both deliberately not user-configurable.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using K2.App.Services;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private const int DialW = 240, DialH = 204;

    /// <summary>Hard ceiling of flash writes per playback run (see file header).</summary>
    private const int DialGifMaxUploadsPerRun = 12;

    /// <summary>At most this many frames are taken from the GIF, evenly spaced, so a short
    /// run still shows the whole motion instead of only its first few frames.</summary>
    private const int DialGifMaxKeyFrames = 6;

    /// <summary>Floor between two picture writes. Below this the firmware is still busy
    /// with the previous flash write and rejects the next one anyway.</summary>
    private const int DialGifMinIntervalMs = 5000;

    /// <summary>Retries for one frame while the firmware is still busy.</summary>
    private const int DialGifMaxAttemptsPerFrame = 8;
    private const int DialGifRetryDelayMs = 1500;

    private CancellationTokenSource? _dialGifCts;
    private Task? _dialGifTask;

    private bool DialGifRunning => _dialGifCts is not null;

    /// <summary>Asks the user how to use a picked animated GIF. Returns true for the
    /// animation test, false for a static picture, null for cancel.</summary>
    private bool? AskDialGifMode()
    {
        var res = MessageBox.Show(this,
            Loc.Get("dial_gif_mode_text", DialGifMaxKeyFrames, DialGifMaxUploadsPerRun, DialGifMinIntervalMs / 1000),
            Loc.Get("dial_gif_mode_title"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
        return res switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No  => false,
            _ => null,
        };
    }

    /// <summary>Decodes the GIF (or <see cref="CroppedGifRef"/> sidecar) into at most
    /// <see cref="DialGifMaxKeyFrames"/> pre-rendered 240x204 bitmaps.</summary>
    private static List<Bitmap>? LoadDialGifFrames(string gifOrCropRefPath, Action<string> log)
    {
        string realPath = gifOrCropRefPath;
        RectangleF? cropRect = null;
        if (CroppedGifRef.IsCropRef(gifOrCropRefPath))
        {
            var cref = CroppedGifRef.TryLoad(gifOrCropRefPath);
            if (cref is null) { log("[DIAL-GIF] crop sidecar unreadable"); return null; }
            realPath = cref.Source;
            if (!cref.NoCrop) cropRect = new RectangleF(cref.RectX, cref.RectY, cref.RectW, cref.RectH);
        }

        var result = new List<Bitmap>();
        try
        {
            using var img = Image.FromFile(realPath);
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int count = img.GetFrameCount(dim);
            int take = Math.Min(count, DialGifMaxKeyFrames);
            for (int k = 0; k < take; k++)
            {
                // Evenly spaced key frames across the whole animation.
                int idx = (int)((long)k * count / take);
                img.SelectActiveFrame(dim, idx);
                var bmp = new Bitmap(DialW, DialH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);   // transparent GIF pixels -> black, not undefined
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (cropRect is { } r)
                        g.DrawImage(img, new Rectangle(0, 0, DialW, DialH), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel);
                    else
                        g.DrawImage(img, new Rectangle(0, 0, DialW, DialH));
                }
                result.Add(bmp);
            }
            log($"[DIAL-GIF] decoded {result.Count} key frames out of {count}");
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            foreach (var b in result) b.Dispose();
            log("[DIAL-GIF] decode failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Starts a bounded playback run on the Media Dock screensaver picture.</summary>
    private void StartDialGif(string gifOrCropRefPath)
    {
        StopDialGif();
        if (_everest is not { IsOpen: true }) return;

        var frames = LoadDialGifFrames(gifOrCropRefPath, LogEverest);
        if (frames is null)
        {
            MessageBox.Show(this, Loc.Get("dial_gif_decode_failed"), Loc.Get("dial_gif_mode_title"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        _dialGifCts = cts;
        BtnDialStopGif.Visibility = Visibility.Visible;
        BtnDialLoadImage.IsEnabled = false;
        BtnDialReset.IsEnabled = false;

        _dialGifTask = Task.Run(() => DialGifLoop(frames, cts.Token))
            .ContinueWith(t =>
            {
                foreach (var b in frames) b.Dispose();
                Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_dialGifCts, cts)) _dialGifCts = null;
                    cts.Dispose();
                    BtnDialStopGif.Visibility = Visibility.Collapsed;
                    BtnDialLoadImage.IsEnabled = true;
                    BtnDialReset.IsEnabled = true;
                    _ndkUploadBusy = false;
                    if (t.Exception is not null)
                        LogEverest("[DIAL-GIF] loop threw: " + t.Exception.GetBaseException().Message);
                    // Same flash-write side effect as a single screensaver upload.
                    EvReArmColorStreamAfterFlashWrite();
                });
            }, TaskScheduler.Default);
    }

    /// <summary>Stops a running playback run (no-op if none). The last uploaded frame
    /// stays as the screensaver picture.</summary>
    private void StopDialGif()
    {
        try { _dialGifCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void DialGifLoop(List<Bitmap> frames, CancellationToken ct)
    {
        LogEverestSafe($"[DIAL-GIF] start: {frames.Count} frames, budget {DialGifMaxUploadsPerRun} writes, " +
                       $"interval >= {DialGifMinIntervalMs} ms");
        int uploads = 0, idx = 0;
        var sw = new Stopwatch();

        while (!ct.IsCancellationRequested && uploads < DialGifMaxUploadsPerRun)
        {
            // Dock page before the write: tells from the log whether the screensaver is
            // (still) engaged, see file header question 2.
            string page = _everest.TryGetExtendInfo(out var info) ? info.byMMDockMenuIndex.ToString() : "?";

            sw.Restart();
            bool ok = false;
            // Keeps the UI-thread dock/accessory polls off the firmware during the flash
            // write. Plain bool flag, read on the UI thread: no Dispatcher.Invoke here, so
            // shutdown can wait for this loop without deadlocking (see CleanupDialGif).
            _ndkUploadBusy = true;
            try
            {
                for (int attempt = 0; attempt < DialGifMaxAttemptsPerFrame && !ok; attempt++)
                {
                    if (attempt > 0 && ct.WaitHandle.WaitOne(DialGifRetryDelayMs)) break;
                    ok = _everest.UploadMMDockScreensaverBitmap(frames[idx]);
                }
            }
            finally { _ndkUploadBusy = false; }

            if (!ok)
            {
                if (!ct.IsCancellationRequested)
                    LogEverestSafe($"[DIAL-GIF] frame {idx} rejected after {DialGifMaxAttemptsPerFrame} attempts — stopping");
                break;
            }

            uploads++;
            LogEverestSafe($"[DIAL-GIF] frame {idx} written ({uploads}/{DialGifMaxUploadsPerRun}) " +
                           $"in {sw.ElapsedMilliseconds} ms, dock page before write = {page}");

            idx = (idx + 1) % frames.Count;
            int wait = DialGifMinIntervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0 && ct.WaitHandle.WaitOne(wait)) break;
        }

        LogEverestSafe($"[DIAL-GIF] stop after {uploads} writes" + (ct.IsCancellationRequested ? " (cancelled)" : ""));
    }

    /// <summary>Called on window close (CleanupDisplayDial): cancels playback and waits
    /// briefly so no picture write is still in flight when the driver is disposed.</summary>
    private void CleanupDialGif()
    {
        StopDialGif();
        try { _dialGifTask?.Wait(TimeSpan.FromSeconds(10)); } catch { /* ignore */ }
    }

    private void BtnDialStopGif_Click(object sender, RoutedEventArgs e) => StopDialGif();
}

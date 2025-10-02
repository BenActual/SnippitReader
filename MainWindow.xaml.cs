using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.IO;                                  // MemoryStream
using static System.IO.WindowsRuntimeStreamExtensions;
using Windows.Storage.Streams;                    // IRandomAccessStream
using Windows.Globalization;


// Alias namespaces so we can disambiguate between WPF and WinRT imaging
using WpfImaging = System.Windows.Media.Imaging;
using WinImaging = Windows.Graphics.Imaging;
using Windows.Media.Ocr;



namespace SnippitReader
{
    public partial class MainWindow : Window
    {
        // ---- Hotkey interop ----
        // These functions let us register/unregister global hotkeys via the Windows API (user32.dll)
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Constants for hotkey registration
        private const int HOTKEY_ID = 0x5157;   // Unique ID for our hotkey
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint VK_X = 0x58;         // Virtual key code for 'X'

        // Holds the last captured or pasted image
        private BitmapSource? _lastImage;
       

        // Updates the status text in the UI with a timestamp and a message
        private void Status(string msg) => StatusText.Text = $"[{DateTime.Now:T}] {msg}";

        // ---- Button Handlers ----

        // Called when the "Snip" button is clicked
        private async void SnipBtn_Click(object sender, RoutedEventArgs e) => await StartSnipAsync();

        // Starts the snipping process by calling the Windows screen clipping tool


        // NEW: WinRT SoftwareBitmap converted from _lastImage (ready for OCR)
        private WinImaging.SoftwareBitmap? _lastSoftwareBitmap;
        private async Task StartSnipAsync()
        {
            try
            {
                Status("Opening snipping UI… (or press Win+Shift+S)");

                // Launch the modern Windows snipping overlay
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });

                // Wait for the user to take a screenshot and for it to appear on the clipboard
                var img = await WaitForClipboardImageAsync(TimeSpan.FromSeconds(12));
                if (img == null)
                {
                    Status("Timed out waiting for screenshot on clipboard.");
                    return;
                }

                // Show and store the original WPF image
                _lastImage = img;
                Preview.Source = img;

                // Dispose any old SoftwareBitmap
                if (_lastSoftwareBitmap != null)
                {
                    _lastSoftwareBitmap.Dispose();
                    _lastSoftwareBitmap = null;
                }

                // Convert WPF BitmapSource -> WinRT SoftwareBitmap (for OCR)
                _lastSoftwareBitmap = await ImagingConversion.ToSoftwareBitmapAsync(_lastImage);


                // Run OCR on the SoftwareBitmap
                var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
                                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));

                var ocrResult = await ocrEngine.RecognizeAsync(_lastSoftwareBitmap);
                var recognizedText = ocrResult.Text;

                // Show OCR result in UI
                OcrOutput.Text = recognizedText;

                Status(string.IsNullOrWhiteSpace(recognizedText) ? "No text detected." : "OCR complete.");
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }


        // ===== Helpers =====
        private static BitmapSource SoftwareBitmapToBitmapSource(WinImaging.SoftwareBitmap sbmp)
        {
            using var ms = new MemoryStream();

            // Encode SoftwareBitmap to PNG stream
            var enc = WinImaging.BitmapEncoder.CreateAsync(
                WinImaging.BitmapEncoder.PngEncoderId,
                ms.AsRandomAccessStream()).AsTask().Result;

            enc.SetSoftwareBitmap(sbmp);
            enc.FlushAsync().AsTask().Wait();

            ms.Position = 0;

            // Decode PNG back into a WPF BitmapImage
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // Waits for an image to show up in the clipboard, up to the given timeout
        private async Task<BitmapSource?> WaitForClipboardImageAsync(TimeSpan timeout)
        {
            var stop = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < stop)
            {
                var img = GetClipboardImageNoWait();
                if (img != null) return img;

                // Retry every 150ms
                await Task.Delay(150);
            }
            return null;
        }

        // Tries to read an image from the clipboard without blocking
        private BitmapSource? GetClipboardImageNoWait()
        {
            try
            {
                // Easiest case: standard image format
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    img?.Freeze(); // Make it cross-thread safe
                    return img;
                }

                // Fallback: check for bitmap/DIB data
                if (Clipboard.ContainsData(DataFormats.Dib) || Clipboard.ContainsData(DataFormats.Bitmap))
                {
                    var img = Clipboard.GetImage();
                    img?.Freeze();
                    return img;
                }
            }
            catch
            {
                // Clipboard can be busy/locked by other apps → just ignore and retry later
            }
            return null;
        }

        public static class ImagingConversion
        {
            /// <summary>
            /// Converts a WPF BitmapSource to a WinRT SoftwareBitmap suitable for Windows OCR.
            /// By default converts to BGRA8 + Premultiplied alpha (what OcrEngine expects).
            /// </summary>
            public static async Task<WinImaging.SoftwareBitmap> ToSoftwareBitmapAsync(
                WpfImaging.BitmapSource source,
                bool convertToBgra8Premultiplied = true)
            {
                if (source is null) throw new ArgumentNullException(nameof(source));

                // 1) Encode WPF BitmapSource -> PNG bytes in memory
                using var ms = new MemoryStream();
                var encoder = new WpfImaging.PngBitmapEncoder();
                encoder.Frames.Add(WpfImaging.BitmapFrame.Create(source));
                encoder.Save(ms);
                ms.Position = 0;

                // 2) Wrap as WinRT stream
                using IRandomAccessStream raStream = ms.AsRandomAccessStream();

                // 3) Decode to SoftwareBitmap
                var decoder = await WinImaging.BitmapDecoder.CreateAsync(raStream);
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                // 4) Ensure OCR-friendly pixel format if requested
                if (convertToBgra8Premultiplied)
                {
                    var converted = WinImaging.SoftwareBitmap.Convert(
                        softwareBitmap,
                        WinImaging.BitmapPixelFormat.Bgra8,
                        WinImaging.BitmapAlphaMode.Premultiplied);

                    softwareBitmap.Dispose(); // free original if different
                    return converted;
                }

                return softwareBitmap;
            }
        }

    }
}

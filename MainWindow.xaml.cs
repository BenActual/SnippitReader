using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SnippitReader
{
    public partial class MainWindow : Window
    {
        // ---- Hotkey interop ----
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 0x5157;   // any unique ID
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint VK_X = 0x58;         // 'X'

        private BitmapSource? _lastImage;

        public MainWindow()
        {
            InitializeComponent();
            Status("Ready. Press Win+Ctrl+X or click Start Snip.");
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            src.AddHook(WndProc);

            // Register Win + Ctrl + X
            var ok = RegisterHotKey(src.Handle, HOTKEY_ID, MOD_WIN | MOD_CONTROL, VK_X);
            if (!ok)
                Status("Failed to register hotkey (Win+Ctrl+X). It may be in use.");
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                var src = (HwndSource?)PresentationSource.FromVisual(this);
                if (src != null) UnregisterHotKey(src.Handle, HOTKEY_ID);
            }
            catch { /* ignore */ }
            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                _ = StartSnipAsync();   // trigger the same flow as the button
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void Status(string msg) => StatusText.Text = $"[{DateTime.Now:T}] {msg}";

        // Button handlers
        private async void SnipBtn_Click(object sender, RoutedEventArgs e) => await StartSnipAsync();

        private async Task StartSnipAsync()
        {
            try
            {
                Status("Opening snipping UI… (or press Win+Shift+S)");
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });

                var img = await WaitForClipboardImageAsync(TimeSpan.FromSeconds(12));
                if (img == null)
                {
                    Status("Timed out waiting for screenshot on clipboard.");
                    return;
                }

                _lastImage = img;
                Preview.Source = img;
                Status("Screenshot captured.");
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private async void PasteBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var img = await Task.Run(GetClipboardImageNoWait);
                if (img == null)
                {
                    Status("Clipboard has no image.");
                    return;
                }
                _lastImage = img;
                Preview.Source = img;
                Status("Pasted image from clipboard.");
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastImage == null) { Status("Nothing to copy yet."); return; }
                Clipboard.SetImage(_lastImage);
                Status("Image copied back to clipboard.");
            }
            catch (Exception ex)
            {
                Status("Clipboard error: " + ex.Message);
            }
        }

        // ===== Helpers =====

        private async Task<BitmapSource?> WaitForClipboardImageAsync(TimeSpan timeout)
        {
            var stop = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < stop)
            {
                var img = GetClipboardImageNoWait();
                if (img != null) return img;
                await Task.Delay(150);
            }
            return null;
        }

        private BitmapSource? GetClipboardImageNoWait()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    img?.Freeze(); // cross-thread safe
                    return img;
                }

                if (Clipboard.ContainsData(DataFormats.Dib) || Clipboard.ContainsData(DataFormats.Bitmap))
                {
                    var img = Clipboard.GetImage();
                    img?.Freeze();
                    return img;
                }
            }
            catch
            {
                // Clipboard can be busy; ignore and retry
            }
            return null;
        }
    }
}

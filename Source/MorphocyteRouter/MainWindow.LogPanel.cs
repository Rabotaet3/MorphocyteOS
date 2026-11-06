using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private int _pageTransitionVersion;
    private Func<bool> _pageAnimationsEnabled = () => SystemParameters.ClientAreaAnimation;
    private enum WorkspacePage { Subscription, Routes, Journal }
    private WorkspacePage _activePage=WorkspacePage.Routes;

    private void RoutesTab_Click(object sender, RoutedEventArgs e) => ChangeMainPage(false);
    private void JournalTab_Click(object sender, RoutedEventArgs e) => ChangeMainPage(true);
    private void SubscriptionTab_Click(object sender,RoutedEventArgs e) => ChangeWorkspacePage(WorkspacePage.Subscription);

    private void ChangeMainPage(bool journal) => ChangeWorkspacePage(journal?WorkspacePage.Journal:WorkspacePage.Routes);

    private async void ChangeWorkspacePage(WorkspacePage page)
    {
        if (_busy || _closing) return;
        // Capture before the save operation temporarily changes button states.
        AnimateWorkspacePage(page);
        await RunExclusiveAsync(async () =>
        {
            _settings.ShowEventLog = page==WorkspacePage.Journal;
            await SaveSettingsAsync();
        });
        if (page==WorkspacePage.Journal && JournalPage.IsVisible) await RefreshConnectionSnapshotAsync();
    }

    private void SelectMainPage(bool journal) => SelectWorkspacePage(journal?WorkspacePage.Journal:WorkspacePage.Routes);

    private void SelectWorkspacePage(WorkspacePage page)
    {
        ResetPageTransition();
        _activePage=page;
        RoutesPage.Visibility = page==WorkspacePage.Routes ? Visibility.Visible : Visibility.Collapsed;
        JournalPage.Visibility = page==WorkspacePage.Journal ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionPage.Visibility=page==WorkspacePage.Subscription?Visibility.Visible:Visibility.Collapsed;
        RoutesTabButton.SetResourceReference(Control.BorderBrushProperty, page==WorkspacePage.Routes ? "ThemeAccent" : "ThemeButtonBorder");
        JournalTabButton.SetResourceReference(Control.BorderBrushProperty, page==WorkspacePage.Journal ? "ThemeAccent" : "ThemeButtonBorder");
        SubscriptionTabButton.SetResourceReference(Control.BorderBrushProperty,page==WorkspacePage.Subscription?"ThemeAccent":"ThemeButtonBorder");
        if(page==WorkspacePage.Subscription) RefreshSubscriptionServers();
        if (page==WorkspacePage.Journal && IsCoreLogVisible)
        {
            RefreshCoreLogText();
        }
    }

    private void AnimateMainPage(bool journal) => AnimateWorkspacePage(journal?WorkspacePage.Journal:WorkspacePage.Routes);

    private void AnimateWorkspacePage(WorkspacePage page)
    {
        if (_activePage==page) return;
        BitmapSource? frame = null;
        var displaySize = MainPageHost.RenderSize;
        var displayOffset = new Vector();
        if (IsLoaded && IsVisible && !_closing && _pageAnimationsEnabled())
        {
            // Capture the current composition, including an interrupted fade.
            // The real pages switch immediately: keyboard commands, the log and
            // diagnostics still see exactly one active page during animation.
            MainPageHost.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(FrameContent);
            var bounds = MainPageHost.TransformToAncestor(FrameContent).TransformBounds(new Rect(MainPageHost.RenderSize));
            var width = bounds.Width * dpi.DpiScaleX;
            var height = bounds.Height * dpi.DpiScaleY;
            if (width > 0 && height > 0)
            {
                try
                {
                    // Rendering the offset child directly shifts/clips its pixels
                    // and leaves transparent gaps. Crop the actual composition in
                    // the frame, including its own parent offset, the background
                    // behind the page and the interface-scale transform.
                    var frameOffset = VisualTreeHelper.GetOffset(FrameContent);
                    var sampleBounds = bounds; sampleBounds.Offset(frameOffset);
                    var scaleX = bounds.Width / MainPageHost.ActualWidth;
                    var scaleY = bounds.Height / MainPageHost.ActualHeight;
                    // Render once at the window's actual pixel grid, then copy
                    // the page rectangle. A VisualBrush re-rasterizes vectors
                    // differently at 150% scale and causes a one-pixel jump.
                    var rootWidth = FrameContent.ActualWidth * dpi.DpiScaleX;
                    var rootHeight = FrameContent.ActualHeight * dpi.DpiScaleY;
                    var quality = Math.Min(1, Math.Sqrt(8_000_000 / (rootWidth * rootHeight)));
                    var densityX = dpi.DpiScaleX * quality; var densityY = dpi.DpiScaleY * quality;
                    var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Floor(rootWidth * quality)),
                        Math.Max(1, (int)Math.Floor(rootHeight * quality)), 96 * densityX, 96 * densityY, PixelFormats.Pbgra32);
                    bitmap.Render(FrameContent);
                    var x = Math.Max(0, (int)Math.Floor(sampleBounds.Left * densityX));
                    var y = Math.Max(0, (int)Math.Floor(sampleBounds.Top * densityY));
                    var right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(sampleBounds.Right * densityX));
                    var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(sampleBounds.Bottom * densityY));
                    sampleBounds = new Rect(x / densityX, y / densityY, (right - x) / densityX, (bottom - y) / densityY);
                    displaySize = new Size(sampleBounds.Width / scaleX, sampleBounds.Height / scaleY);
                    displayOffset = new Vector((sampleBounds.Left - frameOffset.X - bounds.Left) / scaleX,
                        (sampleBounds.Top - frameOffset.Y - bounds.Top) / scaleY);
                    var pixelsX = right - x; var pixelsY = bottom - y; var stride = checked(pixelsX * 4);
                    var pixels = new byte[checked(stride * pixelsY)];
                    bitmap.CopyPixels(new Int32Rect(x, y, pixelsX, pixelsY), pixels, stride, 0);
                    frame = BitmapSource.Create(pixelsX, pixelsY, 96 * pixelsX / displaySize.Width,
                        96 * pixelsY / displaySize.Height, PixelFormats.Pbgra32, null, pixels, stride);
                    frame.Freeze();
                }
                catch (InvalidOperationException) { /* Navigation still works without the decorative frame. */ }
                catch (ArgumentException) { }
            }
        }
        SelectWorkspacePage(page);
        if (frame is null) return;
        var incoming=page switch { WorkspacePage.Subscription=>(FrameworkElement)SubscriptionPage,WorkspacePage.Journal=>JournalPage,_=>RoutesPage };
        // Two separate phases prevent the text of unrelated pages from being
        // composited on top of each other. The incoming page stays transparent
        // until the outgoing snapshot has completely disappeared.
        incoming.Opacity=0;
        PageTransitionImage.Source = frame;
        PageTransitionImage.Width = displaySize.Width; PageTransitionImage.Height = displaySize.Height;
        Canvas.SetLeft(PageTransitionImage, displayOffset.X); Canvas.SetTop(PageTransitionImage, displayOffset.Y);
        // The animation clock starts on the next render tick. Cover the incoming
        // page immediately too, so it cannot flash through before that first tick.
        PageTransitionLayer.Opacity = 1;
        PageTransitionLayer.Visibility = Visibility.Visible;
        var version = _pageTransitionVersion;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(85))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += (_, _) => {
            if(version!=_pageTransitionVersion) return;
            PageTransitionLayer.Visibility=Visibility.Collapsed;
            PageTransitionImage.Source=null;
            var reveal=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(115)) {
                EasingFunction=new SineEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.HoldEnd
            };
            reveal.Completed+=(_,_)=> { if(version==_pageTransitionVersion) ResetPageTransition(); };
            incoming.BeginAnimation(OpacityProperty,reveal,HandoffBehavior.SnapshotAndReplace);
        };
        PageTransitionLayer.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetPageTransition()
    {
        _pageTransitionVersion++;
        PageTransitionLayer.BeginAnimation(OpacityProperty, null);
        PageTransitionLayer.Opacity = 0;
        PageTransitionLayer.Visibility = Visibility.Collapsed;
        PageTransitionImage.Source = null;
        foreach(var page in new FrameworkElement[]{SubscriptionPage,RoutesPage,JournalPage})
        { page.BeginAnimation(OpacityProperty,null); page.Opacity=1; }
    }

    private void MainPageHost_SizeChanged(object sender, SizeChangedEventArgs e) => ResetPageTransition();
}

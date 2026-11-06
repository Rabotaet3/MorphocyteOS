using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MorphocyteRouter;

// A render translation bridges logical scroll steps, including reversals,
// without animating sizes or rebuilding the server rows.
internal sealed class ServerListMotion : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _rows;
    private readonly Action _repaint;
    private readonly TranslateTransform _translation=new();
    private int _version;
    internal bool IsAnimating { get; private set; }
    internal Func<bool> AnimationsEnabled { get; set; }=()=>SystemParameters.ClientAreaAnimation;

    internal ServerListMotion(ScrollViewer scroll,StackPanel rows,Action repaint)
    {
        _scroll=scroll; _rows=rows; _repaint=repaint;
        rows.RenderTransform=_translation;
        scroll.PreviewMouseWheel+=Wheel;
        scroll.PreviewMouseDown+=PointerDown;
        scroll.PreviewKeyDown+=KeyDown;
        scroll.Unloaded+=Unloaded;
    }

    private void Wheel(object sender,MouseWheelEventArgs e)
    {
        if(_scroll.ScrollableHeight<=0 || e.Delta==0) return;
        // The OS preference counts text lines, not multi-line server cards.
        // Keep one wheel message bounded even when Windows accelerates it.
        ScrollByRows(-Math.Sign(e.Delta)*Math.Clamp(2*Math.Abs(e.Delta)/120d,1,2));
        e.Handled=true;
    }

    internal void ScrollByRows(double delta)
    {
        if(_rows.Children.OfType<FrameworkElement>().FirstOrDefault() is not { } anchor || !_scroll.IsLoaded) return;
        var target=Math.Clamp(_scroll.VerticalOffset+delta,0,_scroll.ScrollableHeight);
        if(Math.Abs(target-_scroll.VerticalOffset)<.01) return;
        var oldTop=anchor.TranslatePoint(new Point(),_scroll).Y;
        Cancel();
        _scroll.ScrollToVerticalOffset(target); _scroll.UpdateLayout();
        var shift=oldTop-anchor.TranslatePoint(new Point(),_scroll).Y;
        if(!AnimationsEnabled() || Math.Abs(shift)<.5) { _repaint(); return; }
        IsAnimating=true;
        _translation.Y=shift;
        _repaint();
        var version=++_version;
        var animation=new DoubleAnimation(shift,0,TimeSpan.FromMilliseconds(210)) {
            EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.HoldEnd
        };
        animation.Completed+=(_,_)=> { if(version==_version) Cancel(); };
        _translation.BeginAnimation(TranslateTransform.YProperty,animation,HandoffBehavior.SnapshotAndReplace);
    }

    internal void Cancel()
    {
        _version++;
        IsAnimating=false;
        _translation.BeginAnimation(TranslateTransform.YProperty,null);
        _translation.Y=0;
        _repaint();
    }
    private void PointerDown(object sender,MouseButtonEventArgs e)=>Cancel();
    private void KeyDown(object sender,KeyEventArgs e)=>Cancel();
    private void Unloaded(object sender,RoutedEventArgs e)=>Cancel();
    public void Dispose()
    {
        Cancel();
        _scroll.PreviewMouseWheel-=Wheel; _scroll.PreviewMouseDown-=PointerDown;
        _scroll.PreviewKeyDown-=KeyDown; _scroll.Unloaded-=Unloaded;
    }
}

using System.Windows;
using System.Windows.Controls;
using MorphocyteRouter;

// Test-only host for exercising the shared list in a real modal dispatcher.
// The application offers server selection on its subscription page.
internal static class TestServerPicker
{
    internal static string? Show(Window owner,IReadOnlyList<string> servers,string current,
        Func<string,CancellationToken,Task<int>>? probe=null,
        IReadOnlyDictionary<string,VpnServerPresentation>? presentations=null)
    {
        var dialog=DialogChrome.CreateWindow(owner,"СЕРВЕРЫ ПОДПИСКИ",680,580,500,400,ResizeMode.CanResize);
        string? result=null;
        using var view=new VpnServerListView(servers,current,probe,name=> {
            result=name; dialog.Close(); return Task.FromResult(true);
        },presentations) { Margin=new Thickness(20,14,20,14) };
        dialog.Closed+=(_,_)=>view.Dispose();
        var close=DialogChrome.MakeButton("ЗАКРЫТЬ",false); close.Click+=(_,_)=>dialog.Close();
        var footer=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; footer.Children.Add(close);
        dialog.Content=DialogChrome.BuildFrame(dialog,"СЕРВЕРЫ ПОДПИСКИ",view,footer);
        dialog.Loaded+=(_,_)=>view.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        DialogChrome.ShowModal(owner,dialog);
        return result;
    }
}

using System.Windows;

namespace MorphocyteRouter;

public static class WorkspaceControlAppearance
{
    public static readonly DependencyProperty KeepEnabledAppearanceProperty = DependencyProperty.RegisterAttached(
        "KeepEnabledAppearance", typeof(bool), typeof(WorkspaceControlAppearance), new PropertyMetadata(false));

    public static bool GetKeepEnabledAppearance(DependencyObject control) =>
        (bool)control.GetValue(KeepEnabledAppearanceProperty);

    public static void SetKeepEnabledAppearance(DependencyObject control, bool value) =>
        control.SetValue(KeepEnabledAppearanceProperty, value);
}

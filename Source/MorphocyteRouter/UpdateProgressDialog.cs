using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal static class UpdateProgressDialog
{
    internal static async Task<PreparedUpdate?> ShowAsync(Window owner, UpdateCheckResult release, string cacheRoot, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        PreparedUpdate? prepared = null;
        Exception? failure = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = DialogChrome.CreateWindow(owner, "Загрузка обновления", 540, 290, 500, 260);
        var text = new TextBlock { Text = "Подготавливаю загрузку…", TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 16) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 10 };
        bar.SetResourceReference(Control.ForegroundProperty, "ThemeAccent");
        bar.SetResourceReference(Control.BackgroundProperty, "ThemeSurface");
        var body = new StackPanel { Margin = new Thickness(20, 24, 20, 24) };
        body.Children.Add(text); body.Children.Add(bar);
        var hint = new TextBlock { Text = "После проверки архива VPN остановится, файлы заменятся и приложение перезапустится. Загрузку можно отменить.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 18, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted"); body.Children.Add(hint);
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Click += (_, _) => { lifetime.Cancel(); cancel.IsEnabled = false; text.Text = "Отменяю загрузку…"; };
        dialog.Content = DialogChrome.BuildFrame(dialog, "ОБНОВЛЕНИЕ " + release.LatestVersion, body, cancel);
        var started = false;
        dialog.Closed += (_, _) => { lifetime.Cancel(); if (!started) finished.TrySetResult(); };
        dialog.Loaded += async (_, _) =>
        {
            started = true;
            try
            {
                var progress = new Progress<UpdateProgress>(update =>
                {
                    if (!dialog.IsVisible) return;
                    text.Text = update.Message; bar.Value = update.Fraction;
                });
                prepared = await UpdateDownloader.PrepareAsync(release, cacheRoot, progress, lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) dialog.Close(); finished.TrySetResult(); }
        };
        DialogChrome.ShowModal(owner, dialog);
        await finished.Task;
        if (failure is not null) throw failure;
        return prepared;
    }
}

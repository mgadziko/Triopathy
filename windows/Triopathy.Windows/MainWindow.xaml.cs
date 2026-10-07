using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Triopathy.Windows;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        viewModel.ScrollRequested += () => Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd());
        Loaded += async (_, _) => await viewModel.RefreshAsync();
        PreviewKeyDown += (_, e) => {
            if (Keyboard.Modifiers != ModifierKeys.Control && Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
            if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { LoadContextClick(this, new()); e.Handled = true; }
            if (e.Key is Key.OemPlus or Key.Add) { viewModel.FontSize++; e.Handled = true; }
            if (e.Key is Key.OemMinus or Key.Subtract) { viewModel.FontSize--; e.Handled = true; }
            if (e.Key is Key.D0 or Key.NumPad0) { viewModel.FontSize = 16; e.Handled = true; } };
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        ViewModel.Stop();
        if (ViewModel.PersistSettings) try { var saved = Triopathy.Core.AppSettings.Load(ViewModel.Services.Settings.StorageDirectory); saved.FontSize = ViewModel.FontSize; saved.Save(); } catch (Exception ex) { MessageBox.Show(this, "Settings could not be saved: " + ex.Message, "Triopathy"); }
        ViewModel.Dispose(); base.OnClosing(e);
    }
    private void LoadContextClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit) return;
        var dialog = new OpenFileDialog { Title = "Load context seed", Filter = "Context documents (*.txt;*.json)|*.txt;*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) try { ViewModel.LoadContext(dialog.FileName); } catch (Exception ex) { ViewModel.Status = "Could not load context: " + ex.Message; }
    }
    private void Export(bool json)
    {
        if (!ViewModel.CanExport) return;
        var dialog = new SaveFileDialog { Title = "Save transcript", Filter = json ? "JSON transcript (*.json)|*.json" : "Text transcript (*.txt)|*.txt", FileName = "triopathy-transcript-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), DefaultExt = json ? ".json" : ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) == true) try { ViewModel.Export(dialog.FileName, json); } catch (Exception ex) { ViewModel.Status = "Could not save transcript: " + ex.Message; }
    }
    private void SaveJsonClick(object sender, RoutedEventArgs e) => Export(true);
    private void SaveTextClick(object sender, RoutedEventArgs e) => Export(false);
    private async void ConnectionsClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit) return;
        try { if (ViewModel.PersistSettings) ViewModel.Services.ReloadConnections(); }
        catch (Exception ex) { ViewModel.Status = "Could not load connections: " + ex.Message; return; }
        new ConnectionsWindow(ViewModel.Services, ViewModel.Demo) { Owner = this }.ShowDialog(); await ViewModel.RefreshAsync();
    }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void LargerClick(object sender, RoutedEventArgs e) => ViewModel.FontSize++;
    private void SmallerClick(object sender, RoutedEventArgs e) => ViewModel.FontSize--;
    private void ResetFontClick(object sender, RoutedEventArgs e) => ViewModel.FontSize = 16;
    public void ScrollToOpening() => TranscriptScroll.ScrollToTop();
    private void WebSourceNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { var uri = Triopathy.Core.WebResearch.PublicUri(e.Uri.AbsoluteUri); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ViewModel.Status = "Could not open source: " + ex.Message; }
        e.Handled = true;
    }
}

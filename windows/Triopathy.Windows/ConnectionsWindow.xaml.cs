using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Triopathy.Core;

namespace Triopathy.Windows;

public partial class ConnectionsWindow : Window
{
    private readonly Services services;
    private readonly AppSettings edit;
    private readonly bool demo;
    private CancellationTokenSource? signIn;
    private readonly CancellationTokenSource lifetime = new();
    public ConnectionsWindow(Services services, bool demo = false)
    {
        InitializeComponent(); this.services = services; this.demo = demo;
        edit = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(services.Settings, Json.Options), Json.Options)!;
        DataContext = edit; ProtocolColumn.ItemsSource = new[] { "openai", "ollama" };
        RefreshLabels();
        if (demo) { ConnectButton.IsEnabled = ModelsButton.IsEnabled = DisconnectButton.IsEnabled = ApplyButton.IsEnabled = RemoveKeyButton.IsEnabled = false; Feedback.Text = "Sample mode. Connections cannot be saved or authorized."; }
        Loaded += async (_, _) => { if (!demo && services.Plan.IsConnected) await LoadModelsAsync(); };
        Closed += (_, _) => { signIn?.Cancel(); lifetime.Cancel(); };
    }
    private void RefreshLabels()
    {
        try
        {
            AccountLabel.Text = services.Plan.AccountLabel;
            KeyLabel.Text = services.OpenAI.HasApiKey ? "An API key is saved. Leave the field blank to keep it." : "No API key saved. API use is billed separately from a ChatGPT plan.";
            DisconnectButton.IsEnabled = ModelsButton.IsEnabled = services.Plan.IsConnected && !demo;
            RemoveKeyButton.IsEnabled = services.OpenAI.HasApiKey && !demo;
        }
        catch { Feedback.Text = "Saved credentials could not be read. Remove the connection and sign in again."; }
    }
    private void BrowseProfilesClick(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "Choose Hermes profiles folder" }; if (dialog.ShowDialog(this) == true) { edit.ProfilesDirectory = dialog.FolderName; DataContext = null; DataContext = edit; } }
    private void BrowseLocalClick(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Title = "Choose Organon configuration", Filter = "YAML configuration (*.yaml;*.yml)|*.yaml;*.yml" }; if (dialog.ShowDialog(this) == true) { edit.LocalConfigFile = dialog.FileName; DataContext = null; DataContext = edit; } }
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (demo || signIn != null) return;
        try
        {
            ParticipantGrid.CommitEdit(DataGridEditingUnit.Cell, true); ParticipantGrid.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (var p in edit.Participants.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Endpoint))) HermesService.MakeBackend(p.Endpoint.Trim(), p.Model, p.Protocol);
            if (string.IsNullOrWhiteSpace(edit.ApiModel)) throw new InvalidDataException("Choose an API model.");
            edit.Save();
            if (!string.IsNullOrWhiteSpace(ApiKey.Password)) services.Secrets.Write("api-key", ApiKey.Password.Trim());
            if (services.Plan.IsConnected && PlanModels.SelectedValue is string selected) services.Plan.SelectModel(selected);
            services.Settings.Participants = edit.Participants; services.Settings.ProfilesDirectory = edit.ProfilesDirectory; services.Settings.LocalConfigFile = edit.LocalConfigFile;
            services.Settings.CodexEnabled = edit.CodexEnabled; services.Settings.ApiModel = edit.ApiModel.Trim();
            ApiKey.Clear(); Feedback.Text = "Connections saved."; RefreshLabels();
        }
        catch (Exception ex) { Feedback.Text = "Could not save: " + ex.Message; }
    }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (demo || signIn != null) return;
        signIn = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ConnectButton.IsEnabled = ApplyButton.IsEnabled = ModelsButton.IsEnabled = DisconnectButton.IsEnabled = false; CancelSignIn.Visibility = Visibility.Visible;
        Feedback.Text = "Complete sign-in and plan authorization in your browser. This attempt expires in five minutes.";
        try
        {
            var models = await services.Plan.ConnectAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), signIn.Token);
            PlanModels.ItemsSource = models; PlanModels.SelectedValue = services.Plan.SelectedModel;
            Feedback.Text = models.Count == 0 ? "Connected, but no models were offered. Try Refresh models." : "ChatGPT connected. Choose a model and save connections.";
        }
        catch (OperationCanceledException) { Feedback.Text = "Sign-in cancelled or timed out."; }
        catch (Exception ex) { Feedback.Text = "Could not connect: " + ex.Message; }
        finally { signIn.Dispose(); signIn = null; ConnectButton.IsEnabled = ApplyButton.IsEnabled = true; CancelSignIn.Visibility = Visibility.Collapsed; RefreshLabels(); }
    }
    private async Task LoadModelsAsync()
    {
        ModelsButton.IsEnabled = false;
        try { var models = await services.Plan.AvailableModelsAsync(lifetime.Token); PlanModels.ItemsSource = models; PlanModels.SelectedValue = services.Plan.SelectedModel; if (PlanModels.SelectedItem == null && models.Count > 0) PlanModels.SelectedIndex = 0; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Feedback.Text = "Could not load models: " + ex.Message; }
        finally { RefreshLabels(); }
    }
    private async void ModelsClick(object sender, RoutedEventArgs e) => await LoadModelsAsync();
    private void DisconnectClick(object sender, RoutedEventArgs e)
    { if (demo) return; try { services.Plan.Disconnect(); PlanModels.ItemsSource = null; RefreshLabels(); Feedback.Text = "ChatGPT connection removed from this PC."; } catch (Exception ex) { Feedback.Text = ex.Message; } }
    private void RemoveKeyClick(object sender, RoutedEventArgs e)
    { if (demo) return; try { services.Secrets.Delete("api-key"); ApiKey.Clear(); RefreshLabels(); Feedback.Text = "Saved API key removed."; } catch (Exception ex) { Feedback.Text = ex.Message; } }
    private void CancelSignInClick(object sender, RoutedEventArgs e) => signIn?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    public void ScrollToCredentials() => ConnectionsScroll.ScrollToEnd();
}

using System;
using System.Windows.Controls;
using ClaudeVsExtension.Agent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using System.Text.Json.Serialization;

namespace ClaudeVsExtension;

public partial class AgentToolWindowControl : UserControl
{
    private readonly AgentClient _agentClient = new();

    public AgentToolWindowControl()
    {
        InitializeComponent();

        Loaded += AgentToolWindowControl_Loaded;

        Unloaded += AgentToolWindowControl_Unloaded;
    }

    private async void AgentToolWindowControl_Loaded(
object sender,
System.Windows.RoutedEventArgs e)
    {
        var userDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeVsStudio",
            "WebView2");

        var environment = await CoreWebView2Environment
            .CreateAsync(null, userDataFolder);

        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        var extensionAssemblyPath = System.IO.Path.GetDirectoryName(typeof(AgentToolWindowControl).Assembly.Location)!;

        var htmlPath = System.IO.Path.Combine(
            extensionAssemblyPath,
            "Ui",
            "index.html");

        Browser.Source = new Uri(htmlPath);
    }

    private async void AgentToolWindowControl_Unloaded(
    object sender,
    System.Windows.RoutedEventArgs e)
    {
        await _agentClient.StopAsync();
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var messageJson = e.WebMessageAsJson;

            var request = JsonSerializer.Deserialize<WebChatMessage>(messageJson);

            if (request == null || string.IsNullOrWhiteSpace(request.Text))
                return;

            await _agentClient.StartAsync();

            var agentResponse = await _agentClient.AskAsync(request.Text);

            var responseJson = JsonSerializer.Serialize(new
            {
                type = "assistant",
                text = agentResponse
            });

            Browser.CoreWebView2.PostWebMessageAsJson(responseJson);
        }
        catch (Exception ex)
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                type = "assistant",
                text = ex.Message
            });

            Browser.CoreWebView2.PostWebMessageAsJson(responseJson);
        }
    }

    private class WebChatMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}
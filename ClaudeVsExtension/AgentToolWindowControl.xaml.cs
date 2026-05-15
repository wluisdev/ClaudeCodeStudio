using System;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace ClaudeVsExtension
{
    public partial class AgentToolWindowControl : UserControl
    {
        public AgentToolWindowControl()
        {
            InitializeComponent();

            Loaded += AgentToolWindowControl_Loaded;
        }

        private async void AgentToolWindowControl_Loaded(
    object sender,
    System.Windows.RoutedEventArgs e)
        {
            var userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeVsStudio",
                "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(
                null,
                userDataFolder);

            await Browser.EnsureCoreWebView2Async(environment);

            Browser.NavigateToString("""
    <!DOCTYPE html>
    <html>
    <body style='background:#1e1e1e;color:white;font-family:Segoe UI;padding:20px'>
        <h1>Claude VS Extension</h1>
        <p>WebView2 funcionando 🚀</p>
    </body>
    </html>
    """);
        }
    }
}
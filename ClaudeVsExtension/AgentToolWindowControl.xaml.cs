using System;
using System.Windows.Controls;

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

            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(null, userDataFolder);

            await Browser.EnsureCoreWebView2Async(environment);

            var extensionAssemblyPath = System.IO.Path.GetDirectoryName(
    typeof(AgentToolWindowControl).Assembly.Location);

            var htmlPath = System.IO.Path.Combine(
                extensionAssemblyPath,
                "Ui",
                "index.html");

            Browser.Source = new Uri(htmlPath);
        }
    }
}
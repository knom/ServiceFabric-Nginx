using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using RazorEngine;
using RazorEngine.Templating;

namespace Knom.SFNginxService
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class NginxService : StatelessService
    {
        public NginxService(StatelessServiceContext context)
            : base(context)
        { }

        public Process Process { get; set; }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            yield return new ServiceInstanceListener(_ => new NginxCommunicationListener("httpNginx", this));
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Setup local nginx directories
                string nginxBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"nginx\");
                string nginxDistPath = Path.Combine(nginxBasePath, @"dist\");
                string nginxExePath = Path.Combine(nginxDistPath, @"nginx.exe");

                ZipFile file = new ZipFile(Path.Combine(nginxBasePath, "nginx.zip"));

                string folderName = file[0].Name;

                file.Extract(nginxBasePath);

                Directory.Move(
                    Path.Combine(nginxBasePath, folderName),
                    nginxDistPath);

                // Create directories
                Directory.CreateDirectory(Path.Combine(nginxDistPath, "logs"));
                Directory.Delete(Path.Combine(nginxDistPath, "html"), true);
                Directory.CreateDirectory(Path.Combine(nginxDistPath, "html"));
                Directory.CreateDirectory(Path.Combine(nginxDistPath, "temp"));

                // Read the node sample HTML template
                string nodeTemplate = File.ReadAllText(Path.Combine(nginxBasePath, "node.html.template"));

                // Merge with data and write to disk
                string nodeOut = Engine.Razor.RunCompile(nodeTemplate, "node",
                    null, new { Node = FabricRuntime.GetNodeContext().NodeName });

                File.WriteAllText(Path.Combine(nginxDistPath, "html", "node.html"), nodeOut);


                // Use NGINX.CONF template and write back
                string configTemplatePath = Path.Combine(nginxBasePath, "nginx.conf.template");
                string configOutPath = Path.Combine(nginxDistPath, "conf\\", "nginx.conf");

                var endpoints = FabricRuntime.GetActivationContext().GetEndpoint("httpNginx");

                // Build template for config
                var template = new ConfigurationTemplate()
                {
                    Endpoints = new[] { endpoints },
                    RootFolder = nginxDistPath.Replace('\\', '/')
                };

                string configTemplate = File.ReadAllText(configTemplatePath);

                string configOut = Engine.Razor.RunCompile(configTemplate,
                    "config", null, template);

                File.WriteAllText(configOutPath, configOut);


                // Start NGINX
                Process = StartNginx(nginxExePath, nginxDistPath);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Process.HasExited)
                    {
                        Process = StartNginx(nginxExePath, nginxDistPath);
                    }
                    Process.WaitForExit(1000);

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }

                Process?.Kill();
            }
            catch (TaskCanceledException)
            {
                Process?.Kill();
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
        }

        private static Process StartNginx(string nginxExePath, string nginxPath)
        {
            var p = new ProcessStartInfo()
            {
                FileName = nginxExePath,
                WorkingDirectory = nginxPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var pro = new Process
            {
                StartInfo = p,
            };

            pro.Start();

            return pro;
        }
    }
}

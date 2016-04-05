using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using MoreLinq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RazorEngine;
using RazorEngine.Compilation.ImpromptuInterface.InvokeExt;
using RazorEngine.Templating;

namespace Knom.SFNginxService
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class NginxService : StatelessService, IKillExeProcess
    {
        public NginxService(StatelessServiceContext context)
            : base(context)
        { }

        private Process _process = null;

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            yield return new ServiceInstanceListener(_ => new NginxCommunicationListener("httpNginx", this, Context));
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Concat local nginx directories
                string nginxBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"nginx\");
                string nginxDistPath = Path.Combine(nginxBasePath, @"dist\");
                string nginxExePath = Path.Combine(nginxDistPath, @"nginx.exe");

                // Extract nignx.zip
                ExtractNginxZipPackage(nginxBasePath, nginxDistPath);

                // Create nginx DIRs
                CreateNginxDirectories(nginxDistPath);

                // Create file with the name of the NODE
                CreateNodeInfoHtmlFile(nginxBasePath, nginxDistPath);

                // Create config file
                await CreateNginxConfigFile(nginxBasePath, nginxDistPath);

                // Start NGINX
                _process = StartNginx(nginxExePath, nginxDistPath);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_process.HasExited)
                    {
                        _process = StartNginx(nginxExePath, nginxDistPath);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }

                this.KillProcess();
            }
            catch (TaskCanceledException)
            {
                this.KillProcess();
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
        }

        private async Task CreateNginxConfigFile(string nginxBasePath, string nginxDistPath)
        {
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

            var configSB = new StringBuilder(configOut);

            StringBuilder upStreamBuilder = new StringBuilder();

            var matches = Regex.Matches(configOut, @"(fabric:\/([A-Za-z0-9_\-\.]*)\/([A-Za-z0-9_\-\.]*))\/?;")
                .OfType<Match>().DistinctBy(p => p.Value).ToList();
            if (matches.Any())
            {
                // iterate through all fabric:// services
                foreach (var match in matches)
                {
                    // Resolve the endpoints for that service
                    var resolvedEndpoints = await GetServiceEndpoints(match.Groups[1].Value);

                    // Create a name for the upstream group
                    string name = $"{match.Groups[2].Value}_{match.Groups[3].Value}";

                    // Add the "upstream" config
                    upStreamBuilder.AppendLine($"upstream {name} {{");

                    foreach (var ep in resolvedEndpoints)
                    {
                        var hostMatch = Regex.Match(ep, "http://(.*:([0-9]*)?)");
                        upStreamBuilder.AppendLine($"\tserver {hostMatch.Groups[1]};");
                    }

                    upStreamBuilder.AppendLine("}");

                    // replace the fabric:// occurrence in the config --> the upstream config
                    configSB.Replace(match.Value, match.Result($"http://{name}/;"));
                }

                // add the upstream configs into the config file
                configSB.Replace("http {", "http {\r\n" + upStreamBuilder);

                configOut = configSB.ToString();
            }

            File.WriteAllText(configOutPath, configOut);
        }

        private async Task<IEnumerable<string>> GetServiceEndpoints(string serviceUrl)
        {
            var serviceUri = new Uri(serviceUrl);
            FabricClient client = new FabricClient();

            var serviceDescription = await client.ServiceManager.GetServiceDescriptionAsync(serviceUri);
            if (serviceDescription.PartitionSchemeDescription.Scheme == PartitionScheme.Singleton)
            {
                var partition = await client.ServiceManager.ResolveServicePartitionAsync(serviceUri);
                return partition.Endpoints.Select(p => (string)JObject.Parse(p.Address)["Endpoints"][""]);
            }
            else
            {
                throw new InvalidOperationException("Services with another partition type than SINGLETON are currently not supported!");
            }
        }

        private static void CreateNodeInfoHtmlFile(string nginxBasePath, string nginxDistPath)
        {
            // Read the node sample HTML template
            string nodeTemplate = File.ReadAllText(Path.Combine(nginxBasePath, "node.html.template"));

            // Merge with data and write to disk
            string nodeOut = Engine.Razor.RunCompile(nodeTemplate, "node",
                null, new { Node = FabricRuntime.GetNodeContext().NodeName });

            File.WriteAllText(Path.Combine(nginxDistPath, "html", "node.html"), nodeOut);
        }

        private static void CreateNginxDirectories(string nginxDistPath)
        {
            // Create directories
            Directory.CreateDirectory(Path.Combine(nginxDistPath, "logs"));
            Directory.Delete(Path.Combine(nginxDistPath, "html"), true);
            Directory.CreateDirectory(Path.Combine(nginxDistPath, "html"));
            Directory.CreateDirectory(Path.Combine(nginxDistPath, "temp"));
        }

        private static void ExtractNginxZipPackage(string nginxBasePath, string nginxDistPath)
        {
            ZipFile file = new ZipFile(Path.Combine(nginxBasePath, "nginx.zip"));

            string folderName = file[0].Name;

            file.Extract(nginxBasePath);

            Directory.Move(
                Path.Combine(nginxBasePath, folderName),
                nginxDistPath);
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

        public void KillProcess()
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }
        }
    }
}

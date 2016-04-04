using System.Fabric;
using System.Fabric.Description;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace Knom.SFNginxService
{
    public class NginxCommunicationListener: ICommunicationListener
    {
        private readonly NginxService _nginxService;
        private readonly EndpointResourceDescription _endpoint;

        public NginxCommunicationListener(string endpointName, NginxService nginxService)
        {
            _nginxService = nginxService;
            _endpoint = FabricRuntime.GetActivationContext().GetEndpoint(endpointName);
        }

        public Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult($"{FabricRuntime.GetNodeContext().IPAddressOrFQDN}:{_endpoint.Port}");
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _nginxService.Process?.Kill();
            return Task.FromResult(0);
        }

        public void Abort()
        {
            _nginxService.Process?.Kill();
        }
    }
}
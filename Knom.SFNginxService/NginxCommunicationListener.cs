using System.Fabric;
using System.Fabric.Description;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace Knom.SFNginxService
{
    public class NginxCommunicationListener: ICommunicationListener
    {
        private readonly IKillExeProcess _process;
        private readonly StatelessServiceContext _context;
        private readonly EndpointResourceDescription _endpoint;

        public NginxCommunicationListener(string endpointName, IKillExeProcess process, StatelessServiceContext context)
        {
            _process = process;
            _context = context;

            _endpoint = _context.CodePackageActivationContext.GetEndpoint(endpointName);
        }

        public Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult($"{_context.NodeContext.IPAddressOrFQDN}:{_endpoint.Port}");
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _process.KillProcess();
            return Task.FromResult(0);
        }

        public void Abort()
        {
            _process.KillProcess();
        }
    }
}
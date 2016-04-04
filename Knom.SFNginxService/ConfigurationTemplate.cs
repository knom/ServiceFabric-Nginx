using System.Collections;
using System.Collections.Generic;
using System.Fabric.Description;

namespace Knom.SFNginxService
{
    public class ConfigurationTemplate
    {
        public IEnumerable<EndpointResourceDescription> Endpoints { get; set; }
        public string RootFolder { get; set; }
    }
}
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Proto.Remote.GrpcNet;
using Xunit;

// ReSharper disable MethodHasAsyncOverload

namespace Proto.Remote.Tests
{
    public class GrpcNetClientWithHostedGrpcNetServerTests : RemoteTests,
        IClassFixture<GrpcNetClientWithHostedGrpcNetServerTests.Fixture>
    {
        public GrpcNetClientWithHostedGrpcNetServerTests(Fixture fixture) : base(fixture)
        {
        }

        public class Fixture : RemoteFixture
        {
            private readonly IHost _serverHost;

            public Fixture()
            {
                var clientConfig = ConfigureClientRemoteConfig(GrpcNetRemoteConfig.BindToLocalhost(5000));
                Remote = GetGrpcNetRemote(clientConfig);
                var serverConfig = ConfigureServerRemoteConfig(GrpcNetRemoteConfig.BindToLocalhost(5001));
                (_serverHost, ServerRemote) = GetHostedGrpcNetRemote(serverConfig);
            }

            public override async Task DisposeAsync()
            {
                await Remote.ShutdownAsync();
                await _serverHost.StopAsync();
                _serverHost.Dispose();
            }
        }
    }
}
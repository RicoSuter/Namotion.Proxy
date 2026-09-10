using System.Threading.Tasks;
using Namotion.Interceptor.WebSocket.Server;
using PublicApiGenerator;
using VerifyXunit;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests
{
    public class VerifyChecksTests
    {
        [Fact]
        public Task Run() => VerifyChecks.Run();

        /// <summary>
        /// Snapshot of the assembly's public API. When this fails after an intentional API change,
        /// review the diff and accept by replacing the .verified.txt file with the test's .received.txt.
        /// </summary>
        [Fact]
        public Task PublicApi() => Verifier.Verify(typeof(WebSocketSubjectServer).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
        }));
    }
}

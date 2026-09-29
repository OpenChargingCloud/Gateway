/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Gateway <https://github.com/OpenChargingCloud/Gateway>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// What kind of node a gateway is, as somebody who ran one before it was a
    /// node sees it: the same names everywhere, its own roles, and the kinds of
    /// certificate it keeps.
    /// </summary>
    /// <remarks>
    /// Against a gateway that is started, because the names are read where
    /// they end up - in a log file, in a header, in the accounts - and the
    /// groups are made at the start. Every one of these held before the
    /// gateway was built on WWCPNode, and every one of them is something the
    /// node would say differently if the gateway did not tell it.
    /// </remarks>
    public class GatewayKindTests
    {

        #region Data

        private String   directory  = "";
        private Gateway? gateway;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-kind-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task TearDown()
        {

            if (gateway is not null)
                await gateway.DisposeAsync();

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A temporary directory that outlives one test run is not worth
                // failing the run over.
            }

        }

        #endregion


        #region (helper) StartedGateway()

        /// <summary>
        /// A gateway listening on a free port of the loopback, writing its log
        /// into the test's directory - made again, on fresh ports, where
        /// another test run on this machine took its port first.
        /// </summary>
        private async Task StartedGateway()
        {

            gateway = await TestPorts.StartedOnFreshPorts(() => new Gateway(
                          HTTPPort:        IPPort.Parse(TestPorts.Free()),
                          AccountsPath:    Path.Combine(directory, "accounts"),
                          ConfigFile:      new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                          LogPath:         Path.Combine(directory, "logs"),
                          LogToConsole:    false,
                          BridgeDebugLog:  false
                      ));

        }

        #endregion


        #region AGatewayIsNamedAsItWas()

        /// <summary>
        /// "gateway-" before the date of a log file, "Gateway" after
        /// "OpenChargingCloud" in the server's name, "Gateway" as the
        /// organization of the accounts - and the first line of the log saying
        /// what is starting.
        /// </summary>
        /// <remarks>
        /// The organization above all: it was written into the accounts file at
        /// the first start of every gateway there is, and a start that looked
        /// for another one would not find the one its accounts are in.
        ///
        /// The server's name as the Configuration page shows it, which is where
        /// anybody sees it: the responses of this server carry no Server header.
        /// </remarks>
        [Test]
        public async Task AGatewayIsNamedAsItWas()
        {

            await StartedGateway();

            var serverName  = gateway!.ConfigurationJSON()["http"]?["serverName"]?.ToString() ?? "";

            var logFiles    = Directory.GetFiles(Path.Combine(directory, "logs")).Select(Path.GetFileName).ToArray();

            // Shared for writing, because the gateway still has the file open:
            // it is written for as long as the gateway runs.
            using var file      = new FileStream(Path.Combine(directory, "logs", logFiles.FirstOrDefault() ?? "none"),
                                                 FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader    = new StreamReader(file);

            var firstLine   = reader.ReadLine() ?? "";

            Assert.Multiple(() => {

                Assert.That(logFiles,                                    Is.EqualTo(new[] { $"gateway-{DateTime.UtcNow:yyyy-MM-dd}.log" }));
                Assert.That(firstLine,                                   Does.Contain("[gateway]").And.Contain($"Gateway v{gateway!.Version} starting up."));
                Assert.That(serverName,                                  Is.EqualTo($"OpenChargingCloud Gateway v{gateway!.Version}"));
                Assert.That(gateway!.ExtAPI.TryGetOrganization(Organization_Id.Parse("Gateway"), out _),
                                                                         Is.True, "the accounts are in the organization 'Gateway'");

            });

        }

        #endregion

        #region AGatewayMakesTheGroupsOfItsOwnRoles()

        /// <summary>
        /// Viewer, operator and system administrator - and neither of the two
        /// the node would make for a vehicle.
        /// </summary>
        [Test]
        public async Task AGatewayMakesTheGroupsOfItsOwnRoles()
        {

            await StartedGateway();

            Assert.That(gateway!.ExtAPI.UserGroups.Select(group => group.Id.ToString()),
                        Is.EquivalentTo(new[] { "viewer", "operator", "systemadmin" }));

        }

        #endregion

        #region TheConfigurationPageStillLeadsWithTheGateway()

        /// <summary>
        /// The cards the Configuration page shows, in the order it shows them:
        /// the gateway's own first, then what the node below says of itself,
        /// and the assemblies last.
        /// </summary>
        [Test]
        public async Task TheConfigurationPageStillLeadsWithTheGateway()
        {

            await StartedGateway();

            Assert.That(gateway!.ConfigurationJSON().Properties().Select(card => card.Name),
                        Is.EqualTo(new[] { "gateway", "http", "web", "log", "time", "assemblies" }));

        }

        #endregion

        #region AGatewayKeepsTheCertificatesOfTLS()

        /// <summary>
        /// A certificates directory beside the configuration file, with one
        /// directory for each of the four kinds of TLS and none for the seven of
        /// ISO 15118 - and the log saying what is in it.
        /// </summary>
        /// <remarks>
        /// A gateway kept none, until the time servers and name servers it asks
        /// had to be believed through roots of its own and held to certificates
        /// it recognises, as a vehicle's are. The seven of ISO 15118 stay a
        /// vehicle's: seven empty directories beside the configuration file
        /// would promise something nothing here reads.
        /// </remarks>
        [Test]
        public async Task AGatewayKeepsTheCertificatesOfTLS()
        {

            await StartedGateway();

            var store    = Path.Combine(directory, "certificates");
            var logFile  = Directory.GetFiles(Path.Combine(directory, "logs")).Single();

            // Shared for writing, because the gateway still has the file open.
            using var file      = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader    = new StreamReader(file);

            var logText  = reader.ReadToEnd();

            String KindsDirectory(CertificateKind Kind)
                => Path.Combine(store, Kind.Directory().Replace('/', Path.DirectorySeparatorChar));

            Assert.Multiple(() => {

                Assert.That(gateway!.Certificates.Kinds,      Is.EqualTo(CertificateKindExtensions.TLS));
                Assert.That(gateway!.Certificates.Directory,  Is.EqualTo(Path.GetFullPath(store)), "beside the configuration file");

                foreach (var kind in CertificateKindExtensions.TLS)
                    Assert.That(Directory.Exists(KindsDirectory(kind)),  Is.True,   kind.AsText());

                foreach (var kind in CertificateKindExtensions.ISO15118)
                    Assert.That(Directory.Exists(KindsDirectory(kind)),  Is.False,  kind.AsText());

                Assert.That(logText,  Does.Contain($"Certificates: 0 in '{Path.GetFullPath(store)}' (empty)."));
                Assert.That(logText,  Does.Not.Contain("keeps none"));

            });

        }

        #endregion

    }

}

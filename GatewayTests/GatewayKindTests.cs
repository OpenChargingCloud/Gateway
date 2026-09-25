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

using System.Net;
using System.Net.Sockets;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// What kind of node a gateway is, as somebody who ran one before it was a
    /// node sees it: the same names everywhere, and its own roles.
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
        /// into the test's directory.
        /// </summary>
        private async Task<Int32> StartedGateway()
        {

            var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port  = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            gateway = new Gateway(
                          HTTPPort:        IPPort.Parse(port),
                          AccountsPath:    Path.Combine(directory, "accounts"),
                          ConfigFile:      new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                          LogPath:         Path.Combine(directory, "logs"),
                          LogToConsole:    false,
                          BridgeDebugLog:  false
                      );

            await gateway.Start();

            return port;

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

        #region AGatewayKeepsNoCertificateStore()

        /// <summary>
        /// No certificates directory beside the configuration file, and the log
        /// saying that the gateway keeps none - rather than that it keeps an
        /// empty store.
        /// </summary>
        /// <remarks>
        /// A gateway presents no certificate and believes none of its own. The
        /// node below keeps a store for every kind that does, and made one - an
        /// empty directory, and a line saying it was empty - at every start of
        /// a gateway too, until the gateway could tell it which kinds of
        /// certificate it keeps: none.
        /// </remarks>
        [Test]
        public async Task AGatewayKeepsNoCertificateStore()
        {

            await StartedGateway();

            var logFile  = Directory.GetFiles(Path.Combine(directory, "logs")).Single();

            // Shared for writing, because the gateway still has the file open.
            using var file      = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader    = new StreamReader(file);

            var logText  = reader.ReadToEnd();

            Assert.Multiple(() => {
                Assert.That(Directory.Exists(Path.Combine(directory, "certificates")),  Is.False,  "no store beside the configuration file");
                Assert.That(logText,                                                     Does.Contain("Certificates: this gateway keeps none."));
                Assert.That(logText,                                                     Does.Not.Contain("(empty)"),  "nothing about an empty store");
            });

        }

        #endregion

    }

}

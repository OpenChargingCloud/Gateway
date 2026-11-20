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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.WWCP.Node.Connections;

using cloud.charging.open.Gateway.Configuration;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A gateway dials its CSMS as a charging station and a local controller
    /// dial theirs: over the connections written down in its store, with the
    /// credentials they name, at its start and from the moment one is written
    /// down while it runs - and hangs up when it stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How a connection is dialled, believed and dialled again is
    /// WWCPNode's, and tested in WWCP_OCPP_Node and by the station and
    /// the controller. What is tested here is that a gateway is such a node:
    /// that its store, its OCPP node and its start and stop are wired to it.
    /// </para>
    /// <para>
    /// A plain WebSocket server stands in for the CSMS. It speaks no OCPP and
    /// does not need to: what is measured is whether the gateway got in, and
    /// with what - as the station's DiallingTests measure it. It asks for
    /// credentials unless told not to, and knows the one login it is given.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GatewayDiallingTests
    {

        #region Data

        private const           String    NoTimeServers  = """{ "nts": { "groups": { "legal": { "enabled": false } } } }""";

        private const           String    ThePassword    = "a-password-long-enough-for-ocpp";

        /// <summary>
        /// How long a gateway may take to reach a CSMS that answers, or to be
        /// heard of by it.
        /// </summary>
        private static readonly TimeSpan  Patience       = TimeSpan.FromSeconds(20);

        private String            directory  = "";
        private Gateway?          gateway;
        private WebSocketServer?  csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-dialling-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task LetGo()
        {

            if (gateway is not null)
                await gateway.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

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


        #region (helper) GatewayFrom(Configuration = null)

        /// <summary>
        /// A gateway whose time client is switched off, on a free port of the
        /// loopback, with the given configuration file - made, and not started.
        /// </summary>
        private Gateway GatewayFrom(String Configuration = NoTimeServers)
        {

            var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(file, Configuration);

            gateway  = new Gateway(
                           HTTPPort:        IPPort.Parse(TestPorts.Free()),
                           AccountsPath:    Path.Combine(directory, "accounts"),
                           ConfigFile:      new WWCPConfigFile(file),
                           LogToConsole:    false,
                           BridgeDebugLog:  false
                       );

            return gateway;

        }

        #endregion

        #region (helper) ACSMSThatKnows(Login, Password)

        /// <summary>
        /// A stand-in for a CSMS that lets in nobody but the given login with
        /// the given password.
        /// </summary>
        private WebSocketServer ACSMSThatKnows(String  Login,
                                               String  Password)
        {

            csms = new WebSocketServer(
                       HTTPPort:   IPPort.Parse(TestPorts.Free()),
                       AutoStart:  true
                   );

            csms.AddOrUpdateHTTPBasicAuth(Login, Password);

            return csms;

        }

        #endregion

        #region (helper) WriteDownTheCSMS(Gateway, CSMS, Login, Password)

        /// <summary>
        /// The CSMS as a connection of the gateway that dials by itself, with
        /// a password - written down as the Connections page writes it.
        /// </summary>
        private static String WriteDownTheCSMS(Gateway          Gateway,
                                               WebSocketServer  CSMS,
                                               String           Login,
                                               String           Password)
        {

            Assert.That(Gateway.Connections.TryAddPasswordAuthentication(Login, Password, I18NString.Create("The CSMS's password"),
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(Gateway.Connections.TryAddConnection(I18NString.Create("CSMS"), URL.Parse($"ws://127.0.0.1:{CSMS.IPPort}/{Login}"), "CSMS",
                                                             true, login, null, out var connection, out error),
                        Is.True, error);

            return connection!;

        }

        #endregion

        #region (helper) Until(Condition)

        /// <summary>
        /// Wait until the condition holds or Patience has passed, and say which.
        /// </summary>
        private static async Task<Boolean> Until(Func<Boolean> Condition)
        {

            var giveUp = DateTimeOffset.UtcNow + Patience;

            while (DateTimeOffset.UtcNow < giveUp)
            {

                if (Condition())
                    return true;

                await Task.Delay(50);

            }

            return Condition();

        }

        #endregion


        #region AGatewayIsAnOCPPNodeCalledWhatItsFileSays()

        /// <summary>
        /// "gw001" where the file says nothing, as a local controller is
        /// "lc001", and what the "ocpp" section says where it says something;
        /// and its connections speak of a gateway.
        /// </summary>
        [Test]
        public async Task AGatewayIsAnOCPPNodeCalledWhatItsFileSays()
        {

            var unnamed = GatewayFrom();

            Assert.Multiple(() => {
                Assert.That(unnamed.Node.Id.ToString(),  Is.EqualTo(OCPPConfiguration.DefaultNodeId));
                Assert.That(unnamed.Node.Model,          Is.EqualTo(OCPPConfiguration.DefaultModel));
                Assert.That(unnamed.NodeNoun,            Is.EqualTo("gateway"));
                Assert.That(unnamed.Connections.NodeNoun, Is.EqualTo("gateway"));
            });

            await unnamed.DisposeAsync();

            var named = GatewayFrom("""{ "nts": { "groups": { "legal": { "enabled": false } } }, "ocpp": { "nodeId": "gw042", "serialNumber": "4711" } }""");

            Assert.Multiple(() => {
                Assert.That(named.Node.Id.ToString(),           Is.EqualTo("gw042"));
                Assert.That(named.Node.SerialNumber,            Is.EqualTo("4711"));
                Assert.That(named.ConfigurationJSON()["ocpp"]?["id"]?.ToString(),  Is.EqualTo("gw042"),
                            "the Configuration page shows another name than the one the gateway dials under");
            });

        }

        #endregion

        #region WhatTheSharedConnectionsSay_SpeaksOfAGateway()

        /// <summary>
        /// A text the gateway shares with every node that dials - a refusal of
        /// what was typed for a connection, written where there is no node to
        /// ask - says "this gateway", and not "this node".
        /// </summary>
        [Test]
        public void WhatTheSharedConnectionsSay_SpeaksOfAGateway()
        {

            GatewayFrom();

            Assert.That(ConnectionEntry.Validate(I18NString.Create("CSMS over FTP"), "ftp://csms.example.org", null, null, out _, out _, out _, out var error),
                        Is.False);

            Assert.Multiple(() => {
                Assert.That(error, Is.EqualTo("'ftp' is not something this gateway dials. OCPP goes over ws, wss or ssh."));
                Assert.That(error, Does.Not.Contain("this node"));
            });

        }

        #endregion

        #region AnOCPPSectionThatIsWrongStopsTheGateway()

        /// <summary>
        /// A field the section does not know is said, rather than passed over:
        /// whoever wrote it believed they had named the gateway.
        /// </summary>
        [Test]
        public void AnOCPPSectionThatIsWrongStopsTheGateway()
        {

            var refused = Assert.Throws<InvalidOperationException>(
                              () => GatewayFrom("""{ "nts": { "groups": { "legal": { "enabled": false } } }, "ocpp": { "nodeID": "gw042" } }""")
                          );

            Assert.That(refused!.Message, Does.Contain("'ocpp.nodeID' is not known"));

            gateway = null;

        }

        #endregion

        #region ItDialsItsCSMSWithThePasswordItsConnectionNames()

        /// <summary>
        /// Written down before the start, dialled at the start: the gateway
        /// says it is connected, and the CSMS has it.
        /// </summary>
        [Test]
        public async Task ItDialsItsCSMSWithThePasswordItsConnectionNames()
        {

            var backEnd     = ACSMSThatKnows("gw001", ThePassword);

            await TestPorts.StartedOnFreshPorts(() => GatewayFrom());

            var connection  = WriteDownTheCSMS(gateway!, backEnd, "gw001", ThePassword);

            // Written down after the start in this test, and so dialled by
            // what follows the store - a gateway started with the connection
            // in its store dials it in OnStarted, which the next test sees.
            Assert.That(await Until(() => gateway!.DialledConnections.GetValueOrDefault(connection)?.Contains("Connected") == true),
                        Is.True,
                        gateway!.DialledConnections.GetValueOrDefault(connection) ?? "never dialled");

            Assert.That(await Until(() => backEnd.WebSocketConnections.Any()), Is.True,
                        "the gateway says it is connected, and the CSMS has nobody");

        }

        #endregion

        #region AConnectionWrittenDownBeforeTheStartIsDialledAtTheStart()

        /// <summary>
        /// What a gateway that is started again finds in its store is dialled
        /// at the start, by itself.
        /// </summary>
        [Test]
        public async Task AConnectionWrittenDownBeforeTheStartIsDialledAtTheStart()
        {

            var backEnd     = ACSMSThatKnows("gw001", ThePassword);

            // Written down by a gateway that was never started, and read by the
            // next one from the same directory.
            var first       = GatewayFrom();
            var connection  = WriteDownTheCSMS(first, backEnd, "gw001", ThePassword);
            await first.DisposeAsync();
            gateway = null;

            await TestPorts.StartedOnFreshPorts(() => GatewayFrom());

            Assert.That(gateway!.Connections.Connections.Select(entry => entry.Id), Does.Contain(connection),
                        "the store was not read again");

            Assert.That(await Until(() => gateway!.DialledConnections.GetValueOrDefault(connection)?.Contains("Connected") == true),
                        Is.True,
                        gateway!.DialledConnections.GetValueOrDefault(connection) ?? "never dialled");

        }

        #endregion

        #region AWrongPasswordIsNotLetIn()

        /// <summary>
        /// A CSMS that knows another password: the gateway does not get in, and
        /// says that it did not.
        /// </summary>
        [Test]
        public async Task AWrongPasswordIsNotLetIn()
        {

            var backEnd     = ACSMSThatKnows("gw001", "another-password-long-enough-for-ocpp");

            await TestPorts.StartedOnFreshPorts(() => GatewayFrom());

            var connection  = WriteDownTheCSMS(gateway!, backEnd, "gw001", ThePassword);

            // Refused, and not tried again: a password that is wrong does not
            // become right by being said again.
            Assert.That(await Until(() => gateway!.ConnectionStates.GetValueOrDefault(connection)?.Status == ConnectionStatus.Refused),
                        Is.True,
                        gateway!.DialledConnections.GetValueOrDefault(connection) ?? "never dialled");

            Assert.Multiple(() => {
                Assert.That(gateway!.DialledConnections[connection], Does.Not.Contain("Connected"));
                Assert.That(backEnd.WebSocketConnections,            Is.Empty, "a gateway with the wrong password was let in");
            });

        }

        #endregion

        #region StoppedItHangsUp()

        /// <summary>
        /// A gateway that stops leaves no connection behind at its CSMS.
        /// </summary>
        [Test]
        public async Task StoppedItHangsUp()
        {

            var backEnd     = ACSMSThatKnows("gw001", ThePassword);

            await TestPorts.StartedOnFreshPorts(() => GatewayFrom());

            WriteDownTheCSMS(gateway!, backEnd, "gw001", ThePassword);

            Assert.That(await Until(() => backEnd.WebSocketConnections.Any()), Is.True, "the CSMS never had the gateway");

            await gateway!.DisposeAsync();
            gateway = null;

            Assert.That(await Until(() => !backEnd.WebSocketConnections.Any()), Is.True,
                        "the gateway stopped, and its connection stayed open at the CSMS");

        }

        #endregion

    }

}

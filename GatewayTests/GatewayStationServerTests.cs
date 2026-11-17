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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node.Serving;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A gateway is dialled as a local controller is: on its station server,
    /// by what is below it - here another gateway, which is what a local
    /// controller or a charging station below it would be to it - signing in
    /// as a station that may; and by a CSMS it cannot dial, as a connection
    /// the other end makes, on the same port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The station server, its section, its logins and how it lets somebody in
    /// are OCPPServingNode's, and tested in WWCP_OCPP_Node and by the local
    /// controller. What is tested here is that a gateway is such a node: that
    /// the section of its file is read, at the gateway's own port where it
    /// names none, that the server listens while the gateway does and that
    /// what signs in there is let in by what the gateway wrote down.
    /// </para>
    /// <para>
    /// Two gateways, the one below dialling the one above: a gateway that dials
    /// is exactly what a node below a gateway does, and it says whether it got
    /// in.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GatewayStationServerTests
    {

        #region Data

        private const           String    ThePassword  = "a-password-long-enough-for-ocpp";

        private static readonly TimeSpan  Patience     = TimeSpan.FromSeconds(20);

        private readonly List<String>   directories  = [];
        private readonly List<Gateway>  gateways     = [];

        #endregion

        #region TearDown

        [TearDown]
        public async Task LetGo()
        {

            foreach (var gateway in gateways)
                await gateway.DisposeAsync();

            gateways.Clear();

            foreach (var directory in directories)
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
                catch (Exception)
                {
                    // A temporary directory that outlives one test run is not
                    // worth failing the run over.
                }
            }

            directories.Clear();

        }

        #endregion


        #region (helper) AGateway(Sections)

        /// <summary>
        /// A gateway in a directory of its own, whose time client is switched
        /// off, on a free port of the loopback, with the given sections in its
        /// configuration file beside that - made, and not started.
        /// </summary>
        private Gateway AGateway(params JProperty[] Sections)

            => AGatewayIn(_ => { }, Sections);

        /// <summary>
        /// A gateway as AGateway makes it, with its directory prepared first.
        /// </summary>
        private Gateway AGatewayIn(Action<String>      Prepare,
                                   params JProperty[]  Sections)
        {

            var directory = Path.Combine(Path.GetTempPath(), "gateway-station-server-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
            directories.Add(directory);

            Prepare(directory);

            var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(file, new JObject(
                                        [ new JProperty("nts", new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                          .. Sections ]
                                    ).ToString());

            var gateway = new Gateway(
                              HTTPPort:        IPPort.Parse(TestPorts.Free()),
                              AccountsPath:    Path.Combine(directory, "accounts"),
                              ConfigFile:      new WWCPConfigFile(file),
                              LogToConsole:    false,
                              BridgeDebugLog:  false
                          );

            gateways.Add(gateway);

            return gateway;

        }

        #endregion

        #region (helper) AGatewayAbove()

        /// <summary>
        /// A gateway whose station server is switched on, at a free port - the
        /// one the others dial - started.
        /// </summary>
        private async Task<Gateway> AGatewayAbove()
        {

            Gateway? above = null;

            await TestPorts.StartedOnFreshPorts(() => above = AGateway(
                                                        new JProperty("ocpp",       new JObject(new JProperty("nodeId",  "gw-above"))),
                                                        new JProperty("ocppServer", new JObject(new JProperty("enabled", true),
                                                                                                new JProperty("port",    TestPorts.Free())))
                                                    ));

            return above!;

        }

        #endregion

        #region (helper) AGatewayBelowDialling(Above, Name, Password)

        /// <summary>
        /// A gateway called by the given name that dials the given one above it
        /// with the given password, started.
        /// </summary>
        private async Task<(Gateway Below, String Connection)> AGatewayBelowDialling(Gateway  Above,
                                                                                     String   Name,
                                                                                     String   Password)
        {

            Gateway? below = null;

            await TestPorts.StartedOnFreshPorts(() => below = AGateway(
                                                        new JProperty("ocpp", new JObject(new JProperty("nodeId", Name)))
                                                    ));

            Assert.That(below!.Connections.TryAddAuthentication("What it signs in to the gateway above with", "basic", Name, Password,
                                                                out var login, out var error),
                        Is.True, error);

            Assert.That(below.Connections.TryAddConnection("The gateway above", Door(Above, Name), "CSMS", true, login, null,
                                                           out var connection, out error),
                        Is.True, error);

            return (below, connection!);

        }

        #endregion

        #region (helper) AGatewayTakingHTTP3(Port, WithAServerKey)

        /// <summary>
        /// A gateway whose station server is switched on, on the loopback at
        /// the given port, on security profile 2 and with "http3": true - with
        /// a server key the stations pin where told, and without one, so
        /// unencrypted, where not - started.
        /// </summary>
        private async Task<Gateway> AGatewayTakingHTTP3(UInt16   Port,
                                                        Boolean  WithAServerKey)
        {

            Gateway? gateway = null;

            await TestPorts.StartedOnFreshPorts(() => gateway = AGatewayIn(
                                                          directory => {

                                                              if (!WithAServerKey)
                                                                  return;

                                                              using var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName));

                                                              Assert.That(store.TryCreateServerKey(null, false, out _, out _, out var error), Is.True, error);

                                                          },
                                                          new JProperty("ocpp",       new JObject(new JProperty("nodeId", "gw-http3"))),
                                                          new JProperty("ocppServer", new JObject(new JProperty("enabled",           true),
                                                                                                  new JProperty("address",           "127.0.0.1"),
                                                                                                  new JProperty("port",              Port),
                                                                                                  new JProperty("securityProfiles",  new JArray(1, 2)),
                                                                                                  new JProperty("http3",             true)))
                                                      ));

            return gateway!;

        }

        /// <summary>
        /// A port free for UDP as for TCP: HTTP/3 comes to the UDP port of the
        /// number of the TCP one.
        /// </summary>
        private static UInt16 APortFreeForBoth()
        {

            for (var attempt = 0; attempt < 200; attempt++)
            {

                var port = Random.Shared.Next(20000, 32768);

                try
                {

                    using var udp4 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,     port));
                    using var udp6 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, port));

                    var tcp4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    tcp4.Start();
                    tcp4.Stop();

                    return (UInt16) port;

                }
                catch (System.Net.Sockets.SocketException)
                { }

            }

            throw new InvalidOperationException("No port free for TCP and UDP was found.");

        }

        #endregion

        #region (helper) Door(Gateway, Name) / Until(Condition)

        private static String Door(Gateway  Gateway,
                                   String   Name)

            => $"ws://127.0.0.1:{Gateway.OCPPServerSettings.TCPPort}/{Name}";

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


        #region TheStationServerIsOffAtItsOwnPortWhereTheFileSaysNothing()

        /// <summary>
        /// Off, as a local controller's is, and at the gateway's port beside its
        /// web interface where the section names none - a port nobody chose is
        /// not opened, and one somebody switches on is not the local
        /// controller's.
        /// </summary>
        [Test]
        public void TheStationServerIsOffAtItsOwnPortWhereTheFileSaysNothing()
        {

            var gateway = AGateway();

            Assert.Multiple(() => {
                Assert.That(gateway.OCPPServerEnabled,             Is.False);
                Assert.That(gateway.OCPPServerSettings.TCPPort,    Is.EqualTo(Gateway.DefaultStationServerPort));
                Assert.That(Gateway.DefaultStationServerPort,      Is.EqualTo(IPPort.Parse(2354)));
                Assert.That(gateway.StationLogins.Logins,          Is.Empty);
                Assert.That(gateway.ConfigurationJSON()["stationServer"]?["enabled"]?.Value<Boolean>(), Is.False);
            });

        }

        #endregion

        #region AStationServerSectionThatIsWrongStopsTheGateway()

        /// <summary>
        /// The section is read as a local controller reads it, and what it
        /// refuses stops the gateway.
        /// </summary>
        [Test]
        public void AStationServerSectionThatIsWrongStopsTheGateway()
        {

            var refused = Assert.Throws<InvalidOperationException>(
                              () => AGateway(new JProperty("ocppServer", new JObject(new JProperty("securityProfiles", new JArray(4)))))
                          );

            Assert.That(refused!.Message, Does.Contain("ocppServer.securityProfiles"));

        }

        #endregion

        #region APortOfItsOwnForOCPP16StopsTheGateway()

        /// <summary>
        /// A gateway has one station server for every version of OCPP, as a
        /// local controller has: a port of its own for OCPP 1.6 is a charging
        /// station's, and stops the gateway where its file names one.
        /// </summary>
        [Test]
        public void APortOfItsOwnForOCPP16StopsTheGateway()
        {

            var refused = Assert.Throws<InvalidOperationException>(
                              () => AGateway(new JProperty("ocppServer", new JObject(new JProperty("port16", 9444))))
                          );

            Assert.That(refused!.Message, Does.Contain("'ocppServer.port16' is a charging station's").And.Contain("subprotocols"));

        }

        #endregion

        #region HTTP3IsTakenWhereTheFileSaysSo_AndSwitchedWhileTheServerRuns()

        /// <summary>
        /// "http3": true, and the station port takes WebSockets over HTTP/3 on
        /// the UDP port of its number; switched off and on again while it
        /// runs, written to the file each time - and a port for OCPP 1.6 is
        /// refused there as in the file, with nothing changed.
        /// </summary>
        [Test]
        public async Task HTTP3IsTakenWhereTheFileSaysSo_AndSwitchedWhileTheServerRuns()
        {

            var port    = APortFreeForBoth();
            var gateway = await AGatewayTakingHTTP3(port, WithAServerKey: true);
            var server  = gateway.StationServer!;

            Assert.That(gateway.OCPPServerTLS, Is.True, "the station port is not encrypted");

            await Until(() => server.HTTP3Port is not null || server.WhyNoHTTP3 is not null);

            Assert.That(server.HTTP3Port?.ToUInt16(), Is.EqualTo(port), "The gateway takes no WebSockets over HTTP/3: " + server.WhyNoHTTP3);

            var state = gateway.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(state.Value<Boolean>("http3"),                      Is.True);
                Assert.That(state["state"]!.Value<Int32?>("http3Port"),         Is.EqualTo(port));
                Assert.That(state["state"]!.Value<String?>("http3Problem"),     Is.Null);
            });

            // Off while it runs: the UDP port is closed, and the file says so.
            Assert.That(gateway.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("http3", false)), out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(server.HTTP3Port,                                                                       Is.Null);
                Assert.That(gateway.OCPPServerConfigurationJSON().Value<Boolean>("http3"),                         Is.False);
                Assert.That(JObject.Parse(File.ReadAllText(gateway.ConfigFile.Path))["ocppServer"]!.Value<Boolean?>("http3"), Is.False);
            });

            // And on again.
            Assert.That(gateway.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("http3", true)), out error), Is.True, error);

            Assert.That(await Until(() => server.HTTP3Port?.ToUInt16() == port), Is.True, "HTTP/3 was not taken again: " + server.WhyNoHTTP3);

            // A port for OCPP 1.6 is a charging station's.
            Assert.That(gateway.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("port16", 9444)), out error), Is.False);

            Assert.Multiple(() => {
                Assert.That(error,                                                                                 Does.Contain("'ocppServer.port16' is a charging station's"));
                Assert.That(JObject.Parse(File.ReadAllText(gateway.ConfigFile.Path))["ocppServer"]!["port16"],     Is.Null);
            });

        }

        #endregion

        #region HTTP3WithoutTLS_IsSaidToBeNotTaken()

        /// <summary>
        /// Asked for on a station port without a server certificate - which is
        /// unencrypted - HTTP/3 is not taken, and the Configuration page is
        /// told why.
        /// </summary>
        [Test]
        public async Task HTTP3WithoutTLS_IsSaidToBeNotTaken()
        {

            var gateway = await AGatewayTakingHTTP3(APortFreeForBoth(), WithAServerKey: false);
            var state   = gateway.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(gateway.OCPPServerTLS,                                Is.False);
                Assert.That(gateway.StationServer!.AcceptHTTP3,                    Is.False);
                Assert.That(state.Value<Boolean>("http3"),                         Is.True);
                Assert.That(state["state"]!.Value<Int32?>("http3Port"),            Is.Null);
                Assert.That(state["state"]!.Value<String?>("http3Problem"),        Is.EqualTo(Gateway.NoHTTP3WithoutTLS));
            });

        }

        #endregion

        #region WhatIsBelowSignsInAsAStationThatMay()

        /// <summary>
        /// The gateway below dials the one above as a charging station would,
        /// under its name with its password: let in where the gateway above
        /// has it as a station that may sign in, and turned away where it has
        /// another password.
        /// </summary>
        [Test]
        public async Task WhatIsBelowSignsInAsAStationThatMay()
        {

            var above = await AGatewayAbove();

            Assert.That(above.OCPPServerRunning, Is.True, "the station server of the gateway above does not listen");

            Assert.That(above.StationLogins.TrySetPassword("gw-below", ThePassword, null, null, out _, out var error), Is.True, error);

            var (below, connection) = await AGatewayBelowDialling(above, "gw-below", ThePassword);

            Assert.That(await Until(() => below.DialledConnections.GetValueOrDefault(connection)?.Contains("Connected") == true),
                        Is.True,
                        below.DialledConnections.GetValueOrDefault(connection) ?? "never dialled");

            Assert.That(await Until(() => above.StationServer?.WebSocketConnections.Any() == true), Is.True,
                        "the gateway below says it is connected, and the one above has nobody");

            var (stranger, guessed) = await AGatewayBelowDialling(above, "gw-stranger", ThePassword);

            Assert.That(await Until(() => stranger.ConnectionStates.GetValueOrDefault(guessed)?.Status == ConnectionStatus.Refused),
                        Is.True,
                        stranger.DialledConnections.GetValueOrDefault(guessed) ?? "never dialled");

        }

        #endregion

        #region ACSMSThatDialsIsLetInAsAConnectionTheOtherEndMakes()

        /// <summary>
        /// A CSMS the gateway cannot dial dials it, on the port of its stations,
        /// under the name its connection's address ends with and with the
        /// password that connection names - and is a connection of the gateway
        /// from then on, not a station of it.
        /// </summary>
        [Test]
        public async Task ACSMSThatDialsIsLetInAsAConnectionTheOtherEndMakes()
        {

            var above = await AGatewayAbove();

            Assert.That(above.Connections.TryAddAuthentication("What the CSMS signs in with", "basic", "csms01", ThePassword,
                                                               out var login, out var error),
                        Is.True, error);

            Assert.That(above.Connections.TryAddConnection("The CSMS, dialling in", Door(above, "csms01"), "CSMS", true, login, null,
                                                           out var inbound, out error,
                                                           Inbound: true),
                        Is.True, error);

            // What dials in here is a gateway that dials as a CSMS would: under
            // the name of the connection, with its password.
            var (csms, connection) = await AGatewayBelowDialling(above, "csms01", ThePassword);

            Assert.That(await Until(() => csms.DialledConnections.GetValueOrDefault(connection)?.Contains("Connected") == true),
                        Is.True,
                        csms.DialledConnections.GetValueOrDefault(connection) ?? "never dialled");

            Assert.That(await Until(() => above.ConnectionStates.GetValueOrDefault(inbound)?.Status == ConnectionStatus.Connected),
                        Is.True,
                        above.DialledConnections.GetValueOrDefault(inbound) ?? "never let in");

            Assert.That(above.StationLogins.Logins, Is.Empty, "the CSMS became a station");

        }

        #endregion

        #region AStationTakesNoNameOfACSMSThatDials()

        /// <summary>
        /// A CSMS that dials and a station come in over the same port, under a
        /// name each: one that is a station's is not free for a CSMS.
        /// </summary>
        [Test]
        public void AStationTakesNoNameOfACSMSThatDials()
        {

            var gateway = AGateway();

            Assert.That(gateway.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out var error), Is.True, error);

            Assert.That(gateway.Connections.TryAddAuthentication("What it signs in with", "basic", "cs001", ThePassword,
                                                                 out var login, out error),
                        Is.True, error);

            Assert.That(gateway.Connections.TryAddConnection("A CSMS under the name of a station", Door(gateway, "cs001"), "CSMS", true, login, null,
                                                             out _, out var refused,
                                                             Inbound: true),
                        Is.False);

            Assert.That(refused, Does.Contain("is a charging station of this gateway"));

        }

        #endregion

    }

}

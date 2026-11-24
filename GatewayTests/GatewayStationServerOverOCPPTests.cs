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

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logins;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// The port the charging stations below a gateway connect to, as the CSMS sees it:
    /// OCPPServerController of the gateway's own Device Model, read and written over the line the
    /// gateway connects up to its CSMS, as the page "Charging stations" reads and writes it; the
    /// stations that may sign in there and their login groups, StationLoginController and
    /// StationLoginGroupController - and what a station on that port cannot do: ask the gateway
    /// what only the CSMS asks, or send another station a request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controllers, the Device Model they live in and the gates are WWCP_OCPP's and
    /// WWCP_OCPP_Node's, and tested there and by the local controller. What is tested here is that a
    /// gateway is wired to them: that its OCPP node answers from its own Device Model, that the
    /// components are attached to it, that the gates are at its door and that the components are the
    /// resource "stations".
    /// </para>
    /// <para>
    /// The gateway 'gw003' listens for stations on 127.0.0.1 with security profile 1 alone and has no
    /// server certificate, and connects to a stand-in CSMS that speaks just enough OCPP to ask and to be
    /// answered. What a CSMS may set is everything the page may set (Achim, 2026-11-17).
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GatewayStationServerOverOCPPTests
    {

        #region Data

        private const           String    ThePassword  = "a-password-long-enough-for-ocpp";

        private static readonly TimeSpan  Guard        = TimeSpan.FromSeconds(20);

        private String   directory  = "";
        private CSMS     csms       = default!;
        private Gateway  gateway    = default!;
        private IPPort   port;
        private Int32    lastId;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAGatewayAndACSMS()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-station-server-ocpp-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);

            csms      = new CSMS(TestPorts.Free(), "gw003", ThePassword);

            gateway   = await TestPorts.StartedOnFreshPorts(() => {

                            port = TestPorts.Free();

                            var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

                            File.WriteAllText(file, new JObject(
                                                        new JProperty("nts",         new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                        new JProperty("ocpp",        new JObject(new JProperty("nodeId", "gw003"))),
                                                        new JProperty("webSocketServer",  new JObject(
                                                            new JProperty("enabled",           true),
                                                            new JProperty("address",           "127.0.0.1"),
                                                            new JProperty("port",              port.ToUInt16()),
                                                            new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS")),
                                                            new JProperty("subprotocols",      new JArray("ocpp2.1"))
                                                        ))
                                                    ).ToString());

                            return new Gateway(
                                       HTTPPort:        TestPorts.Free(),
                                       AccountsPath:    Path.Combine(directory, "accounts"),
                                       ConfigFile:      new WWCPConfigFile(file),
                                       LogToConsole:    false,
                                       BridgeDebugLog:  false
                                   );

                        });

            Assert.That(gateway.Connections.TryAddPasswordAuthentication(User_Id.Parse("gw003"), ThePassword, I18NString.Create("The CSMS's password"),
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(gateway.Connections.TryAddConnection(I18NString.Create("CSMS"), URL.Parse($"ws://127.0.0.1:{csms.Server.IPPort}/gw003"), "CSMS",
                                                             true, login, null, out _, out error),
                        Is.True, error);

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && csms.Connections != 1)
                await Task.Delay(50);

            Assert.Multiple(() => {
                Assert.That(csms.Connections,           Is.EqualTo(1), "the gateway did not reach its CSMS");
                Assert.That(gateway.OCPPServerRunning,  Is.True,       "the station port does not run");
            });

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


        #region (class) CSMS

        /// <summary>
        /// A stand-in for a CSMS: lets in the one login it is given, answers what the gateway asks with
        /// an empty answer - a BootNotification with an accepted one - and asks the gateway what a test
        /// tells it to.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentDictionary<String, TaskCompletionSource<JArray>> waiting = new();
            private          Int32 lastId;

            public WebSocketServer Server { get; }

            /// <summary>
            /// What the gateway asked, in the order it asked it.
            /// </summary>
            public ConcurrentQueue<JArray> Asked { get; } = new();

            public CSMS(IPPort Port, String Login, String Password)
            {

                Server = new WebSocketServer(
                             HTTPPort:               Port,
                             SecWebSocketProtocols:  [ "ocpp2.1" ],
                             DisableWebSocketPings:  true,
                             AutoStart:              true
                         );

                Server.AddOrUpdateHTTPBasicAuth(Login, Password);

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    switch (message[0].Value<Int32>())
                    {

                        // A request of the gateway's.
                        case 2:
                            Asked.Enqueue(message);
                            await Server.SendTextMessage(connection,
                                                         new JArray(3, message[1], message[2].Value<String>() == "BootNotification"
                                                                                       ? new JObject(new JProperty("status",       "Accepted"),
                                                                                                     new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                                                     new JProperty("interval",     300))
                                                                                       : new JObject()).ToString(Formatting.None));
                            break;

                        // An answer to one of this CSMS's own.
                        case 3:
                        case 4:
                            if (waiting.TryRemove(message[1].Value<String>() ?? "", out var answer))
                                answer.TrySetResult(message);
                            break;

                    }

                };

            }

            public Int32 Connections
                => Server.WebSocketConnections.Count();

            /// <summary>
            /// Send the gateway a request addressed to itself, and wait for its answer.
            /// </summary>
            public async Task<JArray> Ask(String Action, JObject Payload)
            {

                var id     = $"csms-{Interlocked.Increment(ref lastId)}";
                var answer = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting[id] = answer;

                await Server.SendTextMessage(Server.WebSocketConnections.Single(), new JArray(2, id, Action, Payload).ToString(Formatting.None));

                if (await Task.WhenAny(answer.Task, Task.Delay(Guard)) != answer.Task)
                    Assert.Fail($"The gateway did not answer {Action} {id}.");

                return await answer.Task;

            }

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region (private) Get(Variables) / Set(Values)

        /// <summary>
        /// GetVariables of the given variables of OCPPServerController, from the gateway itself:
        /// variable name to (status, value).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Value)>> Get(params String[] Variables)
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           Variables.Select(variable => new JObject(
                                               new JProperty("component", new JObject(new JProperty("name", "OCPPServerController"))),
                                               new JProperty("variable",  new JObject(new JProperty("name", variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["getVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeValue"]?. Value<String>()));

        }

        /// <summary>
        /// SetVariables of the given variables of OCPPServerController to the given values, in one
        /// request: variable name to (status, additional info).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Info)>> Set(params (String Variable, String Value)[] Values)
        {

            var answer = await csms.Ask(
                                   "SetVariables",
                                   SetVariablesOf(Values)
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["setVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeStatusInfo"]?["additionalInfo"]?.Value<String>()));

        }

        private static JObject SetVariablesOf(params (String Variable, String Value)[] Values)

            => new (new JProperty("setVariableData", new JArray(
                        Values.Select(value => new JObject(
                            new JProperty("attributeValue",  value.Value),
                            new JProperty("component",       new JObject(new JProperty("name", "OCPPServerController"))),
                            new JProperty("variable",        new JObject(new JProperty("name", value.Variable)))
                        ))
                    )));

        /// <summary>
        /// GetVariables of the given variables of the given components and instances:
        /// "Component[Instance].Variable" to (status, value).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Value)>> GetOf(params (String Component, String Instance, String Variable)[] Entries)
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           Entries.Select(entry => new JObject(
                                               new JProperty("component", new JObject(new JProperty("name", entry.Component), new JProperty("instance", entry.Instance))),
                                               new JProperty("variable",  new JObject(new JProperty("name", entry.Variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["getVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => $"{result["component"]?["name"]}[{result["component"]?["instance"]}].{result["variable"]?["name"]}",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeValue"]?. Value<String>()));

        }

        /// <summary>
        /// SetVariables of the given variables of the given components and instances, in one
        /// request: "Component[Instance].Variable" to (status, additional info).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Info)>> SetOf(params (String Component, String Instance, String Variable, String Value)[] Entries)
        {

            var answer = await csms.Ask(
                                   "SetVariables",
                                   new JObject(
                                       new JProperty("setVariableData", new JArray(
                                           Entries.Select(entry => new JObject(
                                               new JProperty("attributeValue",  entry.Value),
                                               new JProperty("component",       new JObject(new JProperty("name", entry.Component), new JProperty("instance", entry.Instance))),
                                               new JProperty("variable",        new JObject(new JProperty("name", entry.Variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["setVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => $"{result["component"]?["name"]}[{result["component"]?["instance"]}].{result["variable"]?["name"]}",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeStatusInfo"]?["additionalInfo"]?.Value<String>()));

        }

        #endregion

        #region (private) InTheFile / Listens(Port) / Until(Condition)

        /// <summary>
        /// The section "webSocketServer" as the configuration file has it now.
        /// </summary>
        private JObject InTheFile
            => JObject.Parse(File.ReadAllText(gateway.ConfigFile.Path))["webSocketServer"] as JObject ?? [];

        /// <summary>
        /// Whether something listens on the given TCP port of 127.0.0.1.
        /// </summary>
        private static async Task<Boolean> Listens(IPPort Port)
        {

            using var client = new TcpClient();

            try
            {
                await client.ConnectAsync("127.0.0.1", Port.ToInt32()).WaitAsync(TimeSpan.FromSeconds(5));
                return true;
            }
            catch
            {
                return false;
            }

        }

        private static async Task<Boolean> Until(Func<Boolean> Condition)
        {

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !Condition())
                await Task.Delay(50);

            return Condition();

        }

        #endregion

        #region (private) A station on the station port

        /// <summary>
        /// Whether a station of the given id gets in on the station port with the given password.
        /// </summary>
        private async Task<Boolean> SignsIn(String Id, String Password)
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{Password}")));

            using var timeout = new CancellationTokenSource(Guard);

            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{Id}"), timeout.Token);
                return client.State == WebSocketState.Open;
            }
            catch (WebSocketException)
            {
                return false;
            }

        }

        /// <summary>
        /// A station on the station port over ws://, signed in with its password.
        /// </summary>
        private async Task<ClientWebSocket> AStation(User_Id Id)
        {

            Assert.That(gateway.WebSocketLogins.TrySetPasswordAuthentication(Id, ThePassword, null, null, out var error), Is.True, error);

            var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{ThePassword}")));

            using var timeout = new CancellationTokenSource(Guard);

            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{Id}"), timeout.Token);

            return client;

        }

        /// <summary>
        /// A request in the frame of the overlay network, to the given destination and sent as the
        /// given node.
        /// </summary>
        private (String Id, String Text) Frame(String   To,
                                               String   From,
                                               String   Action,
                                               JObject  Payload)
        {

            var id = $"station-{Interlocked.Increment(ref lastId)}";

            return (id, new JArray(2, To, From, id, Action, Payload).ToString(Formatting.None));

        }

        /// <summary>
        /// Send the given frame and read the answer to it - skipping whatever else the gateway sends.
        /// </summary>
        private static async Task<JArray?> Ask(ClientWebSocket Client, (String Id, String Text) Frame)
        {

            using var timeout = new CancellationTokenSource(Guard);

            await Client.SendAsync(Encoding.UTF8.GetBytes(Frame.Text), WebSocketMessageType.Text, true, timeout.Token);

            var buffer = new Byte[64 * 1024];

            try
            {
                while (true)
                {

                    using var message = new MemoryStream();

                    WebSocketReceiveResult result;

                    do
                    {
                        result = await Client.ReceiveAsync(buffer, timeout.Token);
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    var answer = JArray.Parse(Encoding.UTF8.GetString(message.ToArray()));

                    if (answer[0].Value<Int32>() is 3 or 4 && answer[1].Value<String>() == Frame.Id)
                        return answer;

                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }

        }

        /// <summary>
        /// The text messages the station got within the given time - each whole,
        /// however many frames it came in. A close is no message: it ends what
        /// the station can get, and is not counted as an empty one.
        /// </summary>
        private static async Task<String[]> Received(ClientWebSocket Client, TimeSpan Within)
        {

            var got      = new List<String>();
            var buffer   = new Byte[64 * 1024];
            var message  = new MemoryStream();

            using var timeout = new CancellationTokenSource(Within);

            try
            {
                while (true)
                {

                    var result = await Client.ReceiveAsync(buffer, timeout.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    message.Write(buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                        continue;

                    if (result.MessageType == WebSocketMessageType.Text)
                        got.Add(Encoding.UTF8.GetString(message.GetBuffer(), 0, (Int32) message.Length));

                    message.SetLength(0);

                }
            }
            catch (OperationCanceledException)
            { }
            catch (WebSocketException)
            { }

            return [.. got];

        }

        #endregion


        #region TheCSMSReadsTheStationPortAsItRuns()

        /// <summary>
        /// The CSMS reads the station port as the file and the page have it, and what it is doing.
        /// </summary>
        [Test]
        public async Task TheCSMSReadsTheStationPortAsItRuns()
        {

            var read = await Get("Enabled", "Address", "Port", "SecurityProfiles", "HTTP3", "Running", "TLS", "WaitingForARestart");

            Assert.Multiple(() => {
                Assert.That(read["Enabled"],            Is.EqualTo(("Accepted", "true")));
                Assert.That(read["Address"],            Is.EqualTo(("Accepted", "127.0.0.1")));
                Assert.That(read["Port"],               Is.EqualTo(("Accepted", port.ToString())));
                Assert.That(read["SecurityProfiles"],   Is.EqualTo(("Accepted", "1")));
                Assert.That(read["HTTP3"],              Is.EqualTo(("Accepted", "false")));
                Assert.That(read["Running"],            Is.EqualTo(("Accepted", "true")));
                Assert.That(read["TLS"],                Is.EqualTo(("Accepted", "false")));
                Assert.That(read["WaitingForARestart"], Is.EqualTo(("Accepted", "")));
            });

        }

        #endregion

        #region ItRegistersWithItsCSMS_AsAStationDoes()

        /// <summary>
        /// Asked to, the gateway sends its CSMS its BootNotification over its line up - who it is,
        /// and that it powered up - and stands accepted, as a charging station does: the same
        /// registration, read the same way.
        /// </summary>
        [Test]
        public async Task ItRegistersWithItsCSMS_AsAStationDoes()
        {

            // Sent by no node by itself: asked for, as after a reboot.
            _ = gateway.SendBootNotifications();

            Assert.That(await Until(() => gateway.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"),
                        Is.True,
                        gateway.RegistrationJSON().ToString());

            var boot = csms.Asked.FirstOrDefault(request => request[2]?.Value<String>() == "BootNotification");

            Assert.Multiple(() => {
                Assert.That(boot,                                                         Is.Not.Null, "no BootNotification reached the CSMS");
                Assert.That(boot?[3]?["reason"]?.Value<String>(),                         Is.EqualTo("PowerUp"));
                Assert.That(boot?[3]?["chargingStation"]?["vendorName"]?.Value<String>(), Is.Not.Empty);
                Assert.That(gateway.RegistrationJSON()["2.1"]?["interval"]?.Value<Int32>(), Is.EqualTo(300));
                Assert.That(gateway.RegistrationJSON().ContainsKey("1.6"),                Is.False, "a gateway speaks no OCPP 1.6 upwards");
            });

        }

        #endregion

        #region WhatTakesEffectAtOnce_IsAcceptedAndWrittenDown()

        /// <summary>
        /// Security profiles and the logging take effect at once: accepted, written to the file, and
        /// read back as set - by the CSMS and by the page.
        /// </summary>
        [Test]
        public async Task WhatTakesEffectAtOnce_IsAcceptedAndWrittenDown()
        {

            var set   = await Set(("SecurityProfiles", "1,2"), ("LogMessages", "true"));
            var read  = await Get("SecurityProfiles", "LogMessages", "WaitingForARestart");
            var page  = gateway.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {

                Assert.That(set["SecurityProfiles"].Status,  Is.EqualTo("Accepted"), set["SecurityProfiles"].Info);
                Assert.That(set["LogMessages"].Status,       Is.EqualTo("Accepted"), set["LogMessages"].Info);

                Assert.That(read["SecurityProfiles"].Value,  Is.EqualTo("1,2"));
                Assert.That(read["LogMessages"].Value,       Is.EqualTo("true"));
                Assert.That(read["WaitingForARestart"].Value, Is.EqualTo(""));

                Assert.That(InTheFile["clientAuthentication"]?.Values<String>(),  Is.EqualTo(new[] { "httpAuthWithoutTLS", "httpAuthOverTLS" }));
                Assert.That(InTheFile["logging"]?["messages"]?.Value<Boolean>(),  Is.True);

                Assert.That(page["clientAuthentication"]?.Values<String>(),       Is.EqualTo(new[] { "httpAuthWithoutTLS", "httpAuthOverTLS" }), "the page reads something else");

            });

        }

        #endregion

        #region WhatWaitsForARestart_IsRebootRequired()

        /// <summary>
        /// A new port is written down and waits for the next start: "RebootRequired",
        /// WaitingForARestart names it, and the port still listens where it did.
        /// </summary>
        [Test]
        public async Task WhatWaitsForARestart_IsRebootRequired()
        {

            var newPort  = TestPorts.Free();

            var set      = await Set(("Port", newPort.ToString()));
            var read     = await Get("Port", "WaitingForARestart");
            var listens  = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(set["Port"].Status,                  Is.EqualTo("RebootRequired"), set["Port"].Info);
                Assert.That(read["Port"].Value,                  Is.EqualTo(newPort.ToString()));
                Assert.That(read["WaitingForARestart"].Value,    Is.EqualTo("Port"));
                Assert.That(InTheFile["port"]?.Value<Int32>(),   Is.EqualTo(newPort.ToInt32()));
                Assert.That(listens,                             Is.True, "the port stopped listening before a restart");
            });

        }

        #endregion

        #region TheCSMSSwitchesThePortOffAndOnAgain()

        /// <summary>
        /// The CSMS may switch the station port off: it is answered, then the port stops listening
        /// and the file says so, and the line up to the CSMS stays. Switched on again, it listens
        /// again.
        /// </summary>
        [Test]
        public async Task TheCSMSSwitchesThePortOffAndOnAgain()
        {

            var off = await Set(("Enabled", "false"));

            Assert.That(off["Enabled"].Status, Is.EqualTo("Accepted"), off["Enabled"].Info);

            var stopped    = await Until(() => !gateway.OCPPServerRunning);
            var listensOff = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(stopped,                                 Is.True,  "the port still runs");
                Assert.That(listensOff,                              Is.False, "the port still listens");
                Assert.That(InTheFile["enabled"]?.Value<Boolean>(),  Is.False);
                Assert.That(csms.Connections,                        Is.EqualTo(1), "the line up to the CSMS went with the port");
            });

            var on = await Set(("Enabled", "true"));

            Assert.That(on["Enabled"].Status, Is.EqualTo("Accepted"), on["Enabled"].Info);

            var started    = await Until(() => gateway.OCPPServerRunning);
            var listensOn  = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(started,    Is.True, "the port does not run again");
                Assert.That(listensOn,  Is.True, "the port does not listen again");
            });

        }

        #endregion


        #region AStation_CannotSetTheStationPort()

        /// <summary>
        /// A station on the station port asks the gateway to change that port - as itself, which it
        /// is, and as "the CSMS", which the frame of the overlay network lets it write - to what the
        /// CSMS would be let set at once. Refused at the door with a SecurityError, and the port is as
        /// it was.
        /// </summary>
        /// <remarks>
        /// The answer to the frame sent as "the CSMS" would go to the CSMS: what is looked at is
        /// whether the port was changed. The station's own frame comes after it on the same
        /// connection, so once that is answered, the other has been dealt with too.
        /// </remarks>
        [Test]
        public async Task AStation_CannotSetTheStationPort()
        {

            using var cs001  = await AStation(User_Id.Parse("cs001"));

            var asCSMS       = Frame(gateway.Node.Id.ToString(), "CSMS",  "SetVariables", SetVariablesOf(("SecurityProfiles", "1,2")));
            var asItself     = Frame(gateway.Node.Id.ToString(), "cs001", "SetVariables", SetVariablesOf(("SecurityProfiles", "1,2")));

            await cs001.SendAsync(Encoding.UTF8.GetBytes(asCSMS.Text), WebSocketMessageType.Text, true, CancellationToken.None);

            var answer       = await Ask(cs001, asItself);

            Assert.Multiple(() => {
                Assert.That(answer,                                          Is.Not.Null,                 "no answer at all");
                Assert.That(answer?[0].Value<Int32>(),                       Is.EqualTo(4),               $"not refused: {answer?.ToString(Formatting.None)}");
                Assert.That(answer?[2].Value<String>(),                      Is.EqualTo("SecurityError"));
                Assert.That(InTheFile["clientAuthentication"]?.Values<String>(),  Is.EqualTo(new[] { "httpAuthWithoutTLS" }),  "a station changed the station port");
                Assert.That(gateway.OCPPSecurityProfiles,                         Is.EqualTo(new[] { 1 }),                     "a station changed the station port");
            });

        }

        #endregion

        #region AStation_CannotSendAnotherStationARequest()

        /// <summary>
        /// A station addresses another station below the gateway - as "the CSMS" and as itself. The
        /// other station gets neither; the station is told about the one in its own name.
        /// </summary>
        [Test]
        public async Task AStation_CannotSendAnotherStationARequest()
        {

            using var cs002  = await AStation(User_Id.Parse("cs002"));
            using var cs001  = await AStation(User_Id.Parse("cs001"));

            var reset        = new JObject(new JProperty("type", "Immediate"));
            var asCSMS       = Frame("cs002", "CSMS",  "Reset", reset);
            var asItself     = Frame("cs002", "cs001", "Reset", reset);

            await cs001.SendAsync(Encoding.UTF8.GetBytes(asCSMS.Text), WebSocketMessageType.Text, true, CancellationToken.None);

            var refused      = await Ask(cs001, asItself);
            var atCS002      = await Received(cs002, TimeSpan.FromMilliseconds(800));

            Assert.Multiple(() => {
                Assert.That(refused?[0].Value<Int32>(),  Is.EqualTo(4), $"the station was not told: {refused?.ToString(Formatting.None)}");
                Assert.That(atCS002,                     Is.Empty,      $"a station sent another one a Reset through the gateway: {String.Join(" ", atCS002)}");
            });

        }

        #endregion

        #region TheCSMSOnlyActions_AreHeldBackAtTheGatewaysDoor()

        /// <summary>
        /// The gate at the gateway's door is the one of every node the stations connect to: the
        /// requests a CSMS sends.
        /// </summary>
        [Test]
        public void TheCSMSOnlyActions_AreHeldBackAtTheGatewaysDoor()
        {

            Assert.Multiple(() => {
                Assert.That(Gateway.CSMSOnlyActions,     Does.Contain("SetVariables"));
                Assert.That(Gateway.CSMSOnlyActions,     Does.Contain("Reset"));
                Assert.That(Gateway.CSMSOnlyActions,     Does.Not.Contain("BootNotification"));

                // And those of OCPP 1.6, for its stations passed through.
                Assert.That(Gateway.CentralSystemOnlyActions, Does.Contain("ChangeConfiguration"));
                Assert.That(Gateway.CentralSystemOnlyActions, Does.Contain("RemoteStartTransaction"));
                Assert.That(Gateway.CentralSystemOnlyActions, Does.Contain("Reset"));
                Assert.That(Gateway.CentralSystemOnlyActions, Does.Not.Contain("BootNotification"));
                Assert.That(Gateway.CentralSystemOnlyActions, Does.Not.Contain("StartTransaction"));
                Assert.That(gateway.Node.OCPP.IN.RequestGate, Is.Not.Null, "the gateway's OCPP node has no gate");
            });

        }

        #endregion


        #region TheCSMSListsAStation_ThatSignsInThen()

        /// <summary>
        /// A station not listed yet is listed by the CSMS with its password: accepted, read back in the
        /// group "default", and it signs in - where it did not before.
        /// </summary>
        [Test]
        public async Task TheCSMSListsAStation_ThatSignsInThen()
        {

            const String password = "cs002-password-long-enough";

            var before  = await SignsIn("cs002", password);
            var set     = await SetOf(("StationLoginController", "cs002", "Password", password),
                                      ("StationLoginController", "cs002", "Enabled",  "true"));
            var read    = await GetOf(("StationLoginController", "cs002", "HasPassword"),
                                      ("StationLoginController", "cs002", "Group"));
            var after   = await SignsIn("cs002", password);

            Assert.Multiple(() => {
                Assert.That(before,                                                 Is.False, "a station that is not listed got in");
                Assert.That(set["StationLoginController[cs002].Password"].Status,  Is.EqualTo("Accepted"), set["StationLoginController[cs002].Password"].Info);
                Assert.That(set["StationLoginController[cs002].Enabled"].Status,   Is.EqualTo("Accepted"), set["StationLoginController[cs002].Enabled"].Info);
                Assert.That(read["StationLoginController[cs002].HasPassword"],     Is.EqualTo(("Accepted", "true")));
                Assert.That(read["StationLoginController[cs002].Group"],           Is.EqualTo(("Accepted", "default")));
                Assert.That(after,                                                  Is.True,  "the station listed over OCPP does not get in");
            });

        }

        #endregion

        #region TheCSMSReadsAStationAndItsGroup_ButNoSecret()

        /// <summary>
        /// A station listed on the page is in the gateway's Device Model at once: the CSMS reads it and
        /// its group - that it has a password, never the password.
        /// </summary>
        [Test]
        public async Task TheCSMSReadsAStationAndItsGroup_ButNoSecret()
        {

            Assert.That(gateway.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("cs001"), ThePassword, null, null, out var error), Is.True, error);

            var read = await GetOf(("StationLoginController",      "cs001",   "Group"),
                                   ("StationLoginController",      "cs001",   "HasPassword"),
                                   ("StationLoginController",      "cs001",   "Password"),
                                   ("StationLoginGroupController", "default", "Members"));

            Assert.Multiple(() => {
                Assert.That(read["StationLoginController[cs001].Group"],          Is.EqualTo(("Accepted", "default")));
                Assert.That(read["StationLoginController[cs001].HasPassword"],    Is.EqualTo(("Accepted", "true")));
                Assert.That(read["StationLoginController[cs001].Password"],       Is.EqualTo(("Rejected", (String?) null)), "a password was read out");
                Assert.That(read["StationLoginGroupController[default].Members"], Is.EqualTo(("Accepted", "1")));
            });

        }

        #endregion

        #region AStation_CannotListAnother()

        /// <summary>
        /// A station on the station port cannot list another station: SetVariables for the gateway is
        /// refused at its door, and nobody new gets in.
        /// </summary>
        [Test]
        public async Task AStation_CannotListAnother()
        {

            using var cs001 = await AStation(User_Id.Parse("cs001"));

            const String password = "cs009-password-long-enough";

            var asItself  = Frame(gateway.Node.Id.ToString(), "cs001", "SetVariables",
                                  new JObject(new JProperty("setVariableData", new JArray(
                                      new JObject(new JProperty("attributeValue",  password),
                                                  new JProperty("component",       new JObject(new JProperty("name", "StationLoginController"), new JProperty("instance", "cs009"))),
                                                  new JProperty("variable",        new JObject(new JProperty("name", "Password"))))
                                  ))));

            var answer    = await Ask(cs001, asItself);
            var signsIn   = await SignsIn("cs009", password);

            Assert.Multiple(() => {
                Assert.That(answer?[0].Value<Int32>(),   Is.EqualTo(4),  $"not refused: {answer?.ToString(Formatting.None)}");
                Assert.That(answer?[2].Value<String>(),  Is.EqualTo("SecurityError"));
                Assert.That(signsIn,                     Is.False,       "a station listed another one");
            });

        }

        #endregion


        #region OCPPServerController_IsTheResourceStations()

        /// <summary>
        /// Who may read and set the station port, and the stations that may sign in there and their
        /// groups, over OCPP is who may on their page: the resource "stations" - decided entry by
        /// entry, as every component of the gateway's own Device Model.
        /// </summary>
        [Test]
        public void OCPPServerController_IsTheResourceStations()
        {

            var policy = gateway.Node.OCPP.SignaturePolicy;

            Assert.Multiple(() => {
                Assert.That(policy.ResourceOfComponent?.Invoke("OCPPServerController"),         Is.EqualTo(GatewayAccess.Stations));
                Assert.That(policy.ResourceOfComponent?.Invoke("StationLoginController"),       Is.EqualTo(GatewayAccess.Stations));
                Assert.That(policy.ResourceOfComponent?.Invoke("StationLoginGroupController"),  Is.EqualTo(GatewayAccess.Stations));
                Assert.That(policy.ActionsDecidedPerEntry,                               Is.SupersetOf(new[] { "SetVariables", "GetVariables", "GetBaseReport", "GetReport" }));
                Assert.That(policy.IncomingDirection,                                    Is.EqualTo("CSMS->CS"));
                Assert.That(gateway.Node.GetComponentConfigs("OCPPServerController"),    Is.Not.Empty, "the component is not in the gateway's Device Model");
            });

        }

        #endregion

    }

}

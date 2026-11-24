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
using System.Net;
using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Logins;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A CSMS the gateway cannot connect to connects to the gateway: on the port of the charging
    /// stations, under the name a connection the other end makes is written down with, by what that
    /// connection names. From then on it is the line up of the gateway as much as one the gateway
    /// connected to - what the stations send goes up over it, what it asks of a station goes down,
    /// and what only the CSMS asks of the gateway it may ask - and the stations beside it on the same
    /// port are stations still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Who is let in, and as what, is OCPPServingNode's and WebSocketSignIns', and tested there and
    /// by the local controller, whose tests these are ported from. What is tested here is that a
    /// gateway is such a node - and that it lets a CSMS connect at all: a local controller does only
    /// while it reports to a CSMS, a gateway always, having no such switch.
    /// </para>
    /// <para>
    /// Plain WebSocket clients at both ends rather than a CSMS and charging stations of the OCPP
    /// library: what is tested is who is let in as what, and what is passed on, frame by frame. The
    /// CSMS in the overlay network, where it says so in the header of its connection, and in the
    /// plain frame of OCPP.
    /// </para>
    /// </remarks>
    [TestFixture(false)]
    [TestFixture(true)]
    public class GatewayCSMSConnectingInTests(Boolean Overlay)
    {

        #region (class) Line

        /// <summary>
        /// One end of a WebSocket to the station port: a CSMS that connected, or a charging station.
        /// It keeps what it gets, and answers what it is asked.
        /// </summary>
        private sealed class Line : IAsyncDisposable
        {

            private readonly ClientWebSocket          socket  = new();
            private readonly CancellationTokenSource  ended   = new();
            private readonly ConcurrentQueue<JArray>  got     = new();

            /// <summary>
            /// What it is in the frames it writes: "CSMS", or the station.
            /// </summary>
            public String  Name    { get; }

            public Line(String Name)
            {
                this.Name = Name;
            }

            /// <summary>
            /// Everything that came.
            /// </summary>
            public JArray[] Got
                => [.. got];

            public Boolean IsOpen
                => socket.State == WebSocketState.Open;

            /// <summary>
            /// Connect, and say what the door answered: 101, or what it refused with.
            /// </summary>
            public async Task<HttpStatusCode> Connect(String   URL,
                                                      String   Login,
                                                      String   Password,
                                                      Boolean  InTheOverlayNetwork = false)
            {

                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.AddSubProtocol("ocpp2.1");
                socket.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{Password}")));

                if (InTheOverlayNetwork)
                    socket.Options.SetRequestHeader("X-OCPP-NetworkingMode", "OverlayNetwork");

                using var timeout = new CancellationTokenSource(Guard);

                try
                {
                    await socket.ConnectAsync(new Uri(URL), timeout.Token);
                }
                catch (WebSocketException)
                {
                    return socket.HttpStatusCode;
                }

                _ = Task.Run(Read);

                return socket.HttpStatusCode;

            }

            private async Task Read()
            {

                var buffer = new Byte[64 * 1024];

                try
                {
                    while (!ended.IsCancellationRequested)
                    {

                        using var message = new MemoryStream();

                        WebSocketReceiveResult result;

                        do
                        {
                            result = await socket.ReceiveAsync(buffer, ended.Token);
                            message.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                            return;

                        var frame = Frame(Encoding.UTF8.GetString(message.ToArray()));

                        got.Enqueue(frame);

                        // A request: answered in the frame it came in, to whom it came from.
                        if (frame[0].Value<Int32>() == 2)
                            await Send(IsOverlay(frame)
                                           ? new JArray(3, SourceOf(frame), Name, IdOf(frame), AnswerTo(ActionOf(frame)))
                                           : new JArray(3, IdOf(frame), AnswerTo(ActionOf(frame))));

                    }
                }
                catch (Exception)
                {
                    // Closed under it: the end of the test, or the door letting go of it.
                }

            }

            private JObject AnswerTo(String Action)

                => Action switch {
                       "BootNotification"  => new JObject(
                                                  new JProperty("status",       "Accepted"),
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                  new JProperty("interval",     300)
                                              ),
                       "Heartbeat"         => new JObject(
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                              ),
                       _                   => new JObject(new JProperty("answeredBy", Name))
                   };

            public async Task Send(JArray Frame)
            {

                using var timeout = new CancellationTokenSource(Guard);

                await socket.SendAsync(Encoding.UTF8.GetBytes(Frame.ToString(Formatting.None)), WebSocketMessageType.Text, true, timeout.Token);

            }

            /// <summary>
            /// The answer to the request of the given id, once there is one or the guard has passed.
            /// </summary>
            public async Task<JArray?> AnswerTo(String RequestId, TimeSpan? Patience = null)
            {

                var giveUp = DateTimeOffset.UtcNow + (Patience ?? Guard);

                do
                {

                    if (got.FirstOrDefault(frame => frame[0].Value<Int32>() is 3 or 4 && IdOf(frame) == RequestId) is JArray answer)
                        return answer;

                    await Task.Delay(20);

                }
                while (DateTimeOffset.UtcNow < giveUp);

                return null;

            }

            /// <summary>
            /// The first request of the given action, once there is one or the guard has passed.
            /// </summary>
            public async Task<JArray?> Asked(String Action, TimeSpan? Patience = null)
            {

                var giveUp = DateTimeOffset.UtcNow + (Patience ?? Guard);

                do
                {

                    if (got.FirstOrDefault(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == Action) is JArray request)
                        return request;

                    await Task.Delay(20);

                }
                while (DateTimeOffset.UtcNow < giveUp);

                return null;

            }

            public async ValueTask DisposeAsync()
            {

                ended.Cancel();

                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The test is over", timeout.Token);
                    }
                }
                catch
                { }

                socket.Dispose();
                ended. Dispose();

            }

        }

        #endregion

        #region (static) Frames: Frame / IsOverlay / IdOf / ActionOf / PayloadOf / PathOf / SourceOf

        /// <summary>
        /// A frame as it was sent: the dates in it as the strings they were, which Json.NET would
        /// otherwise read as dates and write back in another format.
        /// </summary>
        private static JArray Frame(String Text)
        {

            using var reader = new JsonTextReader(new StringReader(Text)) { DateParseHandling = DateParseHandling.None };

            return JArray.Load(reader);

        }

        /// <summary>
        /// Whether the frame is one of the overlay network: a request of six elements, a result of
        /// five, an error of seven.
        /// </summary>
        private static Boolean IsOverlay(JArray Frame)

            => Frame[0].Value<Int32>() switch {
                   2 => Frame.Count == 6,
                   3 => Frame.Count == 5,
                   4 => Frame.Count == 7,
                   _ => false
               };

        private static String IdOf(JArray Frame)
            => (IsOverlay(Frame) ? Frame[3] : Frame[1]).Value<String>() ?? "";

        private static String ActionOf(JArray Frame)
            => (IsOverlay(Frame) ? Frame[4] : Frame[2]).Value<String>() ?? "";

        private static JObject? PayloadOf(JArray Frame)
            => Frame[0].Value<Int32>() == 2
                   ? (IsOverlay(Frame) ? Frame[5] : Frame[3]) as JObject
                   : (IsOverlay(Frame) ? Frame[4] : Frame[2]) as JObject;

        /// <summary>
        /// The network path of an overlay frame - one hop or several - as a list, where the message
        /// came from first.
        /// </summary>
        private static String[] PathOf(JArray Frame)

            => Frame[2] is JArray hops
                   ? [.. hops.Select(hop => hop.Value<String>() ?? "")]
                   : [ Frame[2].Value<String>() ?? "" ];

        private static String SourceOf(JArray Frame)
            => PathOf(Frame)[0];

        #endregion

        #region Data

        private const           String    GatewayId    = "gw002";
        private const           String    CSMSName     = "csms-01";
        private const           String    ThePassword  = "a-password-long-enough-for-ocpp";
        private const           String    ItsPassword  = "what-the-csms-signs-in-with-here";

        private static readonly TimeSpan  Guard        = TimeSpan.FromSeconds(20);

        private readonly ConcurrentQueue<String>  said       = [];

        private String    directory  = "";
        private Gateway?  gateway;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {

            said.Clear();

            directory = Path.Combine(Path.GetTempPath(), "gateway-csms-connecting-in-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task LetGo()
        {

            if (gateway is not null)
                await gateway.DisposeAsync();

            gateway = null;

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


        #region (private) TheGateway(BeforeItStarts = null)

        /// <summary>
        /// The gateway 'gw002', started: its time client switched off, its station server on a free
        /// port of the loopback, taking passwords without TLS, and no connection of its own - what is
        /// to be done to it before it starts, done first.
        /// </summary>
        private async Task<Gateway> TheGateway(Action<Gateway>? BeforeItStarts = null)
        {

            gateway = await TestPorts.StartedOnFreshPorts(() => {

                          var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

                          File.WriteAllText(file, new JObject(
                                                      new JProperty("nts",         new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                      new JProperty("ocpp",        new JObject(new JProperty("nodeId", GatewayId))),
                                                      new JProperty("webSocketServer",  new JObject(
                                                          new JProperty("enabled",           true),
                                                          new JProperty("address",           "127.0.0.1"),
                                                          new JProperty("port",              TestPorts.Free().ToUInt16()),
                                                          new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS")),
                                                          new JProperty("subprotocols",      new JArray("ocpp2.1"))
                                                      ))
                                                  ).ToString());

                          // Another try on fresh ports starts from a store of its own.
                          foreach (var store in new[] { "accounts", ConnectionStore.DefaultDirectoryName })
                              if (Directory.Exists(Path.Combine(directory, store)))
                                  Directory.Delete(Path.Combine(directory, store), recursive: true);

                          var made = new Gateway(
                                         HTTPPort:        TestPorts.Free(),
                                         AccountsPath:    Path.Combine(directory, "accounts"),
                                         ConfigFile:      new WWCPConfigFile(file),
                                         LogToConsole:    false,
                                         BridgeDebugLog:  false
                                     );

                          made.Log.OnLogged += entry => said.Enqueue(entry.Message);

                          BeforeItStarts?.Invoke(made);

                          return made;

                      });

            return gateway;

        }

        #endregion

        #region (private) helpers

        private String Log
            => String.Join(Environment.NewLine, said);

        private String Mode
            => Overlay ? "overlay" : "standard";

        private static String Door(Gateway Gateway, String Name)
            => $"ws://127.0.0.1:{Gateway.OCPPServerSettings.TCPPort}/{Name}";

        private String Door(String Name)
            => Door(gateway!, Name);

        /// <summary>
        /// Write down the CSMS that connects: its WebSocket login with what it signs in with, and the
        /// connection the other end makes.
        /// </summary>
        private static String WriteDownTheCSMS(Gateway Gateway, User_Id? Login = null)
        {

            var name = Login ?? User_Id.Parse(CSMSName);

            Assert.That(Gateway.WebSocketLogins.TrySetPasswordAuthentication(name, ItsPassword, null, I18NString.Create("What the CSMS signs in with"), out var error),
                        Is.True, error);

            Assert.That(Gateway.Connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), URL.Parse(Door(Gateway, name.ToString())), "CSMS", true, null, null,
                                                             out var id, out error,
                                                             Inbound: true),
                        Is.True, error);

            return id!;

        }

        private String WriteDownTheCSMS(User_Id? Login = null)
            => WriteDownTheCSMS(gateway!, Login);

        /// <summary>
        /// Switch the connection of the CSMS on or off: "Connect by itself", which is what lets it in.
        /// </summary>
        private void LetIn(String Id, Boolean LetIn)
        {

            var connection = gateway!.Connections.Connections.Single(one => one.Id == Id);

            Assert.That(gateway.Connections.TryUpdateConnection(Id, connection.Description, connection.URL, "CSMS", LetIn,
                                                                null, null,
                                                                out var error),
                        Is.True, error);

        }

        private ConnectionStatus? StatusOf(String Id)
            => gateway!.ConnectionStates.TryGetValue(Id, out var state) ? state.Status : null;

        private String SaidOf(String Id)
            => gateway!.OutboundConnections.TryGetValue(Id, out var itSaid) ? itSaid : "nothing yet";

        private async Task Until(Func<Boolean> Condition, String What)
        {

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > giveUp)
                    Assert.Fail($"{What} did not happen ({Mode}).{Environment.NewLine}{Log}");

                await Task.Delay(25);

            }

        }

        /// <summary>
        /// The CSMS, connected and the line up of the gateway.
        /// </summary>
        private async Task<(Line CSMS, String Id)> TheCSMSIsIn()
        {

            var id    = WriteDownTheCSMS();
            var csms  = new Line("CSMS");

            Assert.That(await csms.Connect(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, "The CSMS being connected");

            return (csms, id);

        }

        /// <summary>
        /// A charging station on the station port, signed in with its password.
        /// </summary>
        private async Task<Line> AStation(User_Id Id)
        {

            Assert.That(gateway!.WebSocketLogins.TrySetPasswordAuthentication(Id, ThePassword, null, null, out var error), Is.True, error);

            var station = new Line(Id.ToString());

            Assert.That(await station.Connect(Door(Id.ToString()), Id.ToString(), ThePassword), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            return station;

        }

        /// <summary>
        /// Whether the gateway let in a charging station of the given name, as the log has it.
        /// </summary>
        private Boolean SignedInAsAStation(String Name)
            => said.Any(line => line.StartsWith($"The charging station '{Name}' signed in", StringComparison.Ordinal));

        private static JObject SomeVariable

            => new (
                   new JProperty("getVariableData", new JArray(
                       new JObject(
                           new JProperty("component", new JObject(new JProperty("name", "OCPPServerController"))),
                           new JProperty("variable",  new JObject(new JProperty("name", "Enabled")))
                       )
                   ))
               );

        #endregion


        #region AGateway_LetsACSMSConnect_WithoutReportingToOneOfItsOwn()

        /// <summary>
        /// A gateway has no switch for reporting to a CSMS at all, as a local controller has: one that
        /// connects to no CSMS of its own and whose file says nothing of one lets the CSMS written down
        /// as connecting in, from its start on - says at its start where it is waited for, and lets it
        /// in under its name.
        /// </summary>
        [Test]
        public async Task AGateway_LetsACSMSConnect_WithoutReportingToOneOfItsOwn()
        {

            String? id = null;

            await TheGateway(BeforeItStarts: made => id = WriteDownTheCSMS(made));

            Assert.Multiple(() => {
                Assert.That(gateway!.Connections.Connections.Where(connection => !connection.Inbound), Is.Empty, "the gateway has a connection of its own");
                Assert.That(JObject.Parse(File.ReadAllText(gateway.ConfigFile.Path)).ContainsKey("csms"), Is.False, "the file says something of a CSMS");
            });

            await Until(() => StatusOf(id!) == ConnectionStatus.Waiting, "Waiting for the CSMS");

            Assert.That(Log, Does.Contain("1 connection of this gateway is made by the other end: a CSMS connects to " + gateway!.OCPPServerURL));

            await using var csms = new Line("CSMS");

            Assert.That(await csms.Connect(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id!) == ConnectionStatus.Connected, "The CSMS being connected");

        }

        #endregion

        #region WhereTheCSMSIsWaitedFor_IsSaidAsTheAddressItConnectsTo()

        /// <summary>
        /// What the gateway says at its start of the CSMS it waits for is the address the CSMS
        /// connects to: the station port, a slash, and its name - which whoever sets up the CSMS
        /// copies from the log.
        /// </summary>
        [Test]
        public async Task WhereTheCSMSIsWaitedFor_IsSaidAsTheAddressItConnectsTo()
        {

            await TheGateway(BeforeItStarts: made => WriteDownTheCSMS(made));

            Assert.That(Log, Does.Contain($"a CSMS connects to {gateway!.OCPPServerURL}/<its name>, the port of the charging stations."));

        }

        #endregion

        #region TheCSMSThatConnects_IsLetInByWhatItsConnectionNames()

        /// <summary>
        /// Written down, it is waited for. It is let in under its name with its password and with
        /// nothing else - and is the line up of the gateway then, not a charging station of it.
        /// </summary>
        [Test]
        public async Task TheCSMSThatConnects_IsLetInByWhatItsConnectionNames()
        {

            await TheGateway();

            var id = WriteDownTheCSMS();

            await Until(() => StatusOf(id) == ConnectionStatus.Waiting, "Waiting for the CSMS");

            Assert.That(SaidOf(id), Does.Contain($"Waiting for '{CSMSName}' to connect"));

            await using var guessing  = new Line("CSMS");
            await using var stranger  = new Line("CSMS");
            await using var asAnother = new Line("CSMS");
            await using var csms      = new Line("CSMS");

            Assert.Multiple(async () => {
                Assert.That(await guessing. Connect(Door(CSMSName),  CSMSName,  ThePassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "a password that is not its own");
                Assert.That(await stranger. Connect(Door("csms-02"), "csms-02", ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "a name that is not written down");
                Assert.That(await asAnother.Connect(Door(CSMSName),  "csms-02", ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "another login than its name");
            });

            Assert.That(StatusOf(id), Is.EqualTo(ConnectionStatus.Waiting), "something that was turned away counted as the CSMS");

            Assert.That(await csms.Connect(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, "The CSMS being connected");

            Assert.Multiple(() => {
                Assert.That(SignedInAsAStation(CSMSName),  Is.False, "the CSMS was let in as a charging station");
                Assert.That(Log,                           Does.Contain($"The back end '{CSMSName}' connected to this gateway"));
                Assert.That(Log,                           Does.Not.Contain(ItsPassword), "what it signed in with is in the log");
            });

        }

        #endregion

        #region WhatOnlyTheCSMSAsks_ItAsksOverTheLineItConnected()

        /// <summary>
        /// What only the CSMS asks of the gateway - GetVariables, here - the CSMS that connected may
        /// ask, and a charging station on the same port may not.
        /// </summary>
        [Test]
        public async Task WhatOnlyTheCSMSAsks_ItAsksOverTheLineItConnected()
        {

            await TheGateway();

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS  = csms;
            await using var cs001    = await AStation(User_Id.Parse("cs001"));

            await csms.Send(Overlay
                                ? new JArray(2, GatewayId, "CSMS", "csms-1", "GetVariables", SomeVariable)
                                : new JArray(2, "csms-1", "GetVariables", SomeVariable));

            // In the frame of the overlay network, which lets a station write whatever it likes.
            await cs001.Send(new JArray(2, GatewayId, "cs001", "station-1", "GetVariables", SomeVariable));

            var toTheCSMS     = await csms. AnswerTo("csms-1");
            var toTheStation  = await cs001.AnswerTo("station-1");

            Assert.Multiple(() => {

                Assert.That(toTheCSMS?[0].Value<Int32>(),     Is.EqualTo(3), $"the CSMS that connected was not answered: {toTheCSMS?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(PayloadOf(toTheCSMS!)?["getVariableResult"], Is.Not.Null, toTheCSMS?.ToString(Formatting.None));

                if (Overlay)
                    Assert.That(SourceOf(toTheCSMS!),         Is.EqualTo(GatewayId));

                Assert.That(toTheStation?[0].Value<Int32>(),  Is.EqualTo(4), $"a charging station was answered what only the CSMS asks: {toTheStation?.ToString(Formatting.None)}");
                Assert.That(toTheStation?.ToString(Formatting.None), Does.Contain("SecurityError"));

            });

        }

        #endregion

        #region WhatAStationSends_GoesUpToIt_AndWhatItAsksOfAStationGoesDown()

        /// <summary>
        /// The stations' requests go up to the CSMS that connected, and its answers come back to them;
        /// what it asks of a station behind the gateway reaches that station, and the answer the CSMS.
        /// </summary>
        [Test]
        public async Task WhatAStationSends_GoesUpToIt_AndWhatItAsksOfAStationGoesDown()
        {

            await TheGateway();

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS  = csms;
            await using var cs001    = await AStation(User_Id.Parse("cs001"));
            await using var cs002    = await AStation(User_Id.Parse("cs002"));

            #region Up

            await cs001.Send(new JArray(2, "boot-1", "BootNotification", new JObject(
                                            new JProperty("chargingStation", new JObject(
                                                new JProperty("model",       "Bay"),
                                                new JProperty("vendorName",  "GraphDefined")
                                            )),
                                            new JProperty("reason", "PowerUp")
                                        )));

            var answer  = await cs001.AnswerTo("boot-1");
            var up      = csms.Got.FirstOrDefault(frame => frame[0].Value<Int32>() == 2 &&
                                                           ActionOf(frame) == "BootNotification" &&
                                                           PayloadOf(frame)?["chargingStation"]?["model"]?.Value<String>() == "Bay");

            Assert.Multiple(() => {

                Assert.That(up,                                       Is.Not.Null, $"the station's BootNotification did not reach the CSMS that connected.{Environment.NewLine}{Log}");
                Assert.That(answer?[0].Value<Int32>(),                Is.EqualTo(3), $"the station got no answer: {answer?.ToString(Formatting.None)}");
                Assert.That(answer?[2]?["status"]?.Value<String>(),   Is.EqualTo("Accepted"));

                if (up is not null)
                {

                    Assert.That(IsOverlay(up),                        Is.EqualTo(Overlay), up.ToString(Formatting.None));

                    if (Overlay)
                    {
                        Assert.That(SourceOf(up),                     Is.EqualTo("cs001"),   up.ToString(Formatting.None));
                        Assert.That(PathOf(up).Last(),                Is.EqualTo(GatewayId), up.ToString(Formatting.None));
                    }

                }

            });

            #endregion

            #region Down

            // To a station behind the gateway: in the frame of the overlay network, which the
            // gateway reads in either mode.
            await csms.Send(new JArray(2, "cs002", "CSMS", "csms-7", "GetLocalListVersion", new JObject()));

            var down      = await cs002.Asked("GetLocalListVersion");
            var itsAnswer = await csms. AnswerTo("csms-7");

            Assert.Multiple(() => {
                Assert.That(down,                                          Is.Not.Null, $"the request of the CSMS did not reach the station.{Environment.NewLine}{Log}");
                Assert.That(cs001.Got.Any(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == "GetLocalListVersion"),
                                                                           Is.False, "it reached another station as well");
                Assert.That(itsAnswer?[0].Value<Int32>(),                  Is.EqualTo(3), $"the CSMS got no answer: {itsAnswer?.ToString(Formatting.None)}");
                Assert.That(PayloadOf(itsAnswer!)?["answeredBy"]?.Value<String>(), Is.EqualTo("cs002"));
            });

            #endregion

            #region And a station still sends another station nothing

            await cs001.Send(new JArray(2, "cs002", "cs001", "station-9", "Reset", new JObject(new JProperty("type", "Immediate"))));

            var refused = await cs001.AnswerTo("station-9");

            Assert.Multiple(() => {
                Assert.That(refused?[0].Value<Int32>(),   Is.EqualTo(4), $"a station's request for another station was not refused: {refused?.ToString(Formatting.None)}");
                Assert.That(cs002.Got.Any(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == "Reset"), Is.False, "a station reset another station");
            });

            #endregion

        }

        #endregion

        #region ItRegistersWithTheCSMSThatConnects_AsAStationDoes()

        /// <summary>
        /// A CSMS that connects is a back end like one the gateway connects to: once it is in, and the
        /// gateway is asked to, it sends it its BootNotification - who it is, and that it powered up -
        /// and stands accepted, as a charging station does: the same registration, read the same way.
        /// </summary>
        [Test]
        public async Task ItRegistersWithTheCSMSThatConnects_AsAStationDoes()
        {

            await TheGateway();

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS = csms;

            // Sent by no node by itself: asked for, as after a reboot.
            _ = gateway!.SendBootNotifications();

            var boot = await csms.Asked("BootNotification");

            Assert.That(boot, Is.Not.Null, $"no BootNotification reached the CSMS that connected.{Environment.NewLine}{Log}");

            await Until(() => gateway.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted", "The gateway being accepted");

            Assert.Multiple(() => {
                Assert.That(PayloadOf(boot!)?["reason"]?.Value<String>(),                         Is.EqualTo("PowerUp"));
                Assert.That(PayloadOf(boot!)?["chargingStation"]?["vendorName"]?.Value<String>(), Is.Not.Empty);
                Assert.That(gateway.RegistrationJSON()["2.1"]?["interval"]?.Value<Int32>(),       Is.EqualTo(300));
                Assert.That(gateway.RegistrationJSON().ContainsKey("1.6"),                        Is.False, "a gateway speaks no OCPP 1.6 upwards");
            });

        }

        #endregion

        #region ItsName_IsNoStations_AndAStationIsNoCSMS()

        /// <summary>
        /// One port, one way to sign in, and names that are told apart at its door: what the CSMS signs
        /// in with is its WebSocket login and no credentials of the gateway's, a station's password
        /// opens nothing under the name of the CSMS, and the stations sign in beside it as before.
        /// </summary>
        [Test]
        public async Task ItsName_IsNoStations_AndAStationIsNoCSMS()
        {

            await TheGateway();

            Assert.That(gateway!.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("cs001"), ThePassword, null, null, out var error), Is.True, error);

            Assert.That(gateway.Connections.TryAddPasswordAuthentication(User_Id.Parse("csms-02"), ItsPassword, I18NString.Create("What the gateway signs in with"),
                                                                         out var login, out error),
                        Is.True, error);

            Assert.Multiple(() => {

                Assert.That(gateway.Connections.TryAddConnection(I18NString.Create("A CSMS with credentials of the gateway"), URL.Parse(Door("csms-02")), "CSMS", true, login, null,
                                                                 out _, out var refused,
                                                                 Inbound: true),
                            Is.False);

                Assert.That(refused, Does.Contain("signs in with its WebSocket login"));

            });

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS       = csms;
            await using var asTheCSMS     = new Line("cs001");
            await using var cs001         = new Line("cs001");

            Assert.Multiple(async () => {
                Assert.That(await asTheCSMS.Connect(Door(CSMSName), "cs001", ThePassword), Is.Not.EqualTo(HttpStatusCode.SwitchingProtocols), "a station's password opened the name of the CSMS");
                Assert.That(await cs001.    Connect(Door("cs001"),  "cs001", ThePassword), Is.EqualTo(HttpStatusCode.SwitchingProtocols),     "a station is not let in beside the CSMS");
            });

            await Until(() => SignedInAsAStation("cs001"), "The station being connected");

            Assert.Multiple(() => {
                Assert.That(csms.IsOpen,                   Is.True,  "the CSMS was let go of");
                Assert.That(SignedInAsAStation(CSMSName),  Is.False, "the CSMS was let in as a charging station");
            });

        }

        #endregion

        #region SwitchedOff_ItIsLetGoOf_AndLetInAgainOnceSwitchedOn()

        /// <summary>
        /// "Connect by itself" is what lets it in: switched off, the CSMS that is in is let go of and
        /// turned away at the door - and let in again once it is switched on.
        /// </summary>
        [Test]
        public async Task SwitchedOff_ItIsLetGoOf_AndLetInAgainOnceSwitchedOn()
        {

            await TheGateway();

            var (csms, id) = await TheCSMSIsIn();

            await using var theCSMS = csms;

            LetIn(id, false);

            await Until(() => StatusOf(id) == ConnectionStatus.Waiting && !csms.IsOpen, "The CSMS being let go of");

            await using var turnedAway = new Line("CSMS");

            Assert.Multiple(async () => {
                Assert.That(SaidOf(id),                                                               Does.Contain("is not let in"));
                Assert.That(await turnedAway.Connect(Door(CSMSName), CSMSName, ItsPassword, Overlay),  Is.EqualTo(HttpStatusCode.Unauthorized));
            });

            LetIn(id, true);

            await using var again = new Line("CSMS");

            Assert.That(await again.Connect(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, "The CSMS being connected again");

        }

        #endregion

        #region ItsAnswers_FindItUnderTheIdentificationItAsksWith()

        /// <summary>
        /// The CSMS writes its own identification into what it asks, not the name it came under: the
        /// answers of the gateway and of a station behind it find it all the same, over the line it
        /// connected. Let go of, that identification leads nowhere any more.
        /// </summary>
        [Test]
        public async Task ItsAnswers_FindItUnderTheIdentificationItAsksWith()
        {

            const String ItsIdentification = "csms-main";

            await TheGateway();

            var (csms, id) = await TheCSMSIsIn();

            await using var theCSMS  = csms;
            await using var cs002    = await AStation(User_Id.Parse("cs002"));

            // In the frame of the overlay network, which the gateway reads in either mode.
            await csms.Send(new JArray(2, GatewayId, ItsIdentification, "csms-1", "GetVariables",        SomeVariable));
            await csms.Send(new JArray(2, "cs002",   ItsIdentification, "csms-7", "GetLocalListVersion", new JObject()));

            var fromTheGateway  = await csms.AnswerTo("csms-1");
            var fromTheStation  = await csms.AnswerTo("csms-7");

            Assert.Multiple(() => {

                Assert.That(fromTheGateway?[0].Value<Int32>(),  Is.EqualTo(3), $"the gateway's answer did not reach the CSMS: {fromTheGateway?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(fromTheStation?[0].Value<Int32>(),  Is.EqualTo(3), $"the station's answer did not reach the CSMS: {fromTheStation?.ToString(Formatting.None)}{Environment.NewLine}{Log}");

                if (fromTheGateway is not null)
                    Assert.That(PayloadOf(fromTheGateway)?["getVariableResult"], Is.Not.Null, fromTheGateway.ToString(Formatting.None));

                if (fromTheStation is not null)
                    Assert.That(PayloadOf(fromTheStation)?["answeredBy"]?.Value<String>(), Is.EqualTo("cs002"));

            });

            LetIn(id, false);

            await Until(() => StatusOf(id) == ConnectionStatus.Waiting && !csms.IsOpen, "The CSMS being let go of");

            Assert.That(gateway!.Node.Routing.LookupNetworkingNode(NetworkingNode_Id.Parse(ItsIdentification), out _),
                        Is.False, "the identification of the CSMS that was let go of still leads somewhere");

        }

        #endregion

    }

}

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
    /// Several CSMS behind one gateway, and each charging station forwarded to its own: the forwarding
    /// target its login names - the first connected of the target's peers -, or the back end in charge
    /// where it names none. Down, a CSMS reaches the stations whose target it is in, and no other.
    /// </summary>
    /// <remarks>
    /// Plain WebSocket clients at both ends, as in GatewayCSMSConnectingInTests, whose helpers these
    /// are: what is tested is what is passed on to whom, frame by frame. In OCPP 2.1, and in OCPP 1.6
    /// passed through: charging stations of OCPP 1.6 and central systems of OCPP 1.6 that connect.
    /// </remarks>
    [TestFixture(false, "2.1")]
    [TestFixture(true,  "2.1")]
    [TestFixture(false, "1.6")]
    [TestFixture(true,  "1.6")]
    public class GatewayForwardingTargetsTests(Boolean Overlay, String Version)
    {

        #region (class) Line

        /// <summary>
        /// One end of a WebSocket to the station port: a CSMS that connected, or a charging station.
        /// It keeps what it gets, and answers what it is asked.
        /// </summary>
        private sealed class Line : IAsyncDisposable
        {

            /// <summary>
            /// The last transaction id a CSMS of the test gave: the first one it gives is 4711, the
            /// next one, by any CSMS, 4712 - as two CSMS do not give the same.
            /// </summary>
            public static Int32 TransactionIds = 4710;

            private readonly ClientWebSocket          socket  = new();
            private readonly CancellationTokenSource  ended   = new();
            private readonly ConcurrentQueue<JArray>  got     = new();

            /// <summary>
            /// What it is in the frames it writes: "CSMS", or the station.
            /// </summary>
            public String  Name        { get; }

            /// <summary>
            /// Whether it turns away every station that boots at it.
            /// </summary>
            public Boolean   TurnsAway  { get; }

            /// <summary>
            /// How long it takes to answer a request.
            /// </summary>
            public TimeSpan  Slowness   { get; }

            public Line(String     Name,
                        Boolean    TurnsAway  = false,
                        TimeSpan?  Slowness   = null)
            {
                this.Name       = Name;
                this.TurnsAway  = TurnsAway;
                this.Slowness   = Slowness ?? TimeSpan.Zero;
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
                                                      String   Subprotocol,
                                                      Boolean  InTheOverlayNetwork = false)
            {

                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.AddSubProtocol(Subprotocol);
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
                        {

                            if (Slowness > TimeSpan.Zero)
                                await Task.Delay(Slowness, ended.Token);

                            await Send(IsOverlay(frame)
                                           ? new JArray(3, SourceOf(frame), Name, IdOf(frame), AnswerTo(ActionOf(frame)))
                                           : new JArray(3, IdOf(frame), AnswerTo(ActionOf(frame))));

                        }

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
                                                  new JProperty("status",       TurnsAway ? "Rejected" : "Accepted"),
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                  new JProperty("interval",     300)
                                              ),
                       "Heartbeat"         => new JObject(
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                              ),
                       "StartTransaction"  => new JObject(
                                                  new JProperty("transactionId", Interlocked.Increment(ref TransactionIds)),
                                                  new JProperty("idTagInfo",     new JObject(new JProperty("status", "Accepted")))
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
            Line.TransactionIds = 4710;

            directory = Path.Combine(Path.GetTempPath(), "gateway-forwarding-targets-" + Guid.NewGuid().ToString("N")[..12]);
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
                                                          new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp1.6"))
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
            => (Overlay ? "overlay" : "standard") + ", OCPP " + Version;

        private Boolean Is16
            => Version == "1.6";

        /// <summary>
        /// The subprotocol both ends ask for.
        /// </summary>
        private String Subprotocol
            => Is16 ? "ocpp1.6" : "ocpp2.1";

        /// <summary>
        /// The name a back end of this version goes by in what it sends.
        /// </summary>
        private String BackEndName
            => Is16 ? "CentralSystem" : "CSMS";

        private static String Door(Gateway Gateway, String Name)
            => $"ws://127.0.0.1:{Gateway.OCPPServerSettings.TCPPort}/{Name}";

        private String Door(String Name)
            => Door(gateway!, Name);

        /// <summary>
        /// Write down the CSMS that connects: its WebSocket login with what it signs in with, and the
        /// connection the other end makes.
        /// </summary>
        private static String WriteDownTheCSMS(Gateway Gateway, User_Id? Login = null, String? OCPPVersion = null)
        {

            var name = Login ?? User_Id.Parse(CSMSName);

            Assert.That(Gateway.WebSocketLogins.TrySetPasswordAuthentication(name, ItsPassword, null, I18NString.Create("What the CSMS signs in with"), out var error),
                        Is.True, error);

            Assert.That(Gateway.Connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), URL.Parse(Door(Gateway, name.ToString())), "CSMS", true, null, null,
                                                             out var id, out error,
                                                             OCPPVersion:  OCPPVersion,
                                                             Inbound:      true),
                        Is.True, error);

            return id!;

        }

        private String WriteDownTheCSMS(User_Id? Login = null)
            => WriteDownTheCSMS(gateway!, Login, Is16 ? "OCPP1.6" : null);

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

            Assert.That(await csms.Connect(Door(CSMSName), CSMSName, ItsPassword, Subprotocol, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

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

            Assert.That(await station.Connect(Door(Id.ToString()), Id.ToString(), ThePassword, Subprotocol), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

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


        #region (private) more helpers

        /// <summary>
        /// The CSMS of the given name connected, the line up of the gateway it writes down.
        /// </summary>
        private async Task<(Line CSMS, String Id)> TheCSMSIsIn(String Name)
        {

            var id    = WriteDownTheCSMS(User_Id.Parse(Name));
            var csms  = new Line("CSMS");

            Assert.That(await csms.Connect(Door(Name), Name, ItsPassword, Subprotocol, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, $"'{Name}' being connected");

            return (csms, id);

        }

        private String PeerOf(String ConnectionId)
            => gateway!.Connections.PeerOf(ConnectionId)?.Id ?? throw new InvalidOperationException($"The connection '{ConnectionId}' has no peer.");

        private void Target(String Id, params String[] ConnectionIds)
            => Assert.That(gateway!.WebSocketLogins.TryAddOrUpdateTarget(Id, ConnectionIds.Select(PeerOf), null, out var error), Is.True, error);

        private void Forward(String Station, String? TargetId)
            => Assert.That(gateway!.WebSocketLogins.TrySetTarget(User_Id.Parse(Station), TargetId, out var error), Is.True, error);

        /// <summary>
        /// A BootNotification of the given model, in the version of OCPP the stations speak.
        /// </summary>
        private JArray Boot(String Id, String Model)

            => Is16
                   ? new (2, Id, "BootNotification", new JObject(
                             new JProperty("chargePointModel",   Model),
                             new JProperty("chargePointVendor",  "GraphDefined")
                         ))
                   : new (2, Id, "BootNotification", new JObject(
                             new JProperty("chargingStation", new JObject(
                                 new JProperty("model",       Model),
                                 new JProperty("vendorName",  "GraphDefined")
                             )),
                             new JProperty("reason", "PowerUp")
                         ));

        private Boolean GotBoot(Line Line, String Model)

            => Line.Got.Any(frame => frame[0].Value<Int32>() == 2 &&
                                     ActionOf(frame) == "BootNotification" &&
                                     (Is16 ? PayloadOf(frame)?["chargePointModel"]
                                           : PayloadOf(frame)?["chargingStation"]?["model"])?.Value<String>() == Model);

        private static Boolean GotAsked(Line Line, String Action)
            => Line.Got.Any(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == Action);

        #endregion


        #region AStationWithATarget_GoesToItsPeer_AndOneWithoutToTheBackEndInCharge()

        /// <summary>
        /// Two CSMS, the first one the back end in charge: a station whose login names a target with
        /// the second one sends up to the second one alone, and a station without a target to the
        /// first one, as before there were targets - each answered by its own.
        /// </summary>
        [Test]
        public async Task AStationWithATarget_GoesToItsPeer_AndOneWithoutToTheBackEndInCharge()
        {

            await TheGateway();

            var (csms1, id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2, id2) = await TheCSMSIsIn("csms-02");

            await using var first   = csms1;
            await using var second  = csms2;

            await using var cs001   = await AStation(User_Id.Parse("cs001"));
            await using var cs002   = await AStation(User_Id.Parse("cs002"));

            Target ("second", id2);
            Forward("cs001",  "second");

            await cs001.Send(Boot("boot-1", "Targeted"));
            await cs002.Send(Boot("boot-2", "Untargeted"));

            var toCS001 = await cs001.AnswerTo("boot-1");
            var toCS002 = await cs002.AnswerTo("boot-2");

            Assert.Multiple(() => {

                Assert.That(GotBoot(second, "Targeted"),     Is.True,  $"the station with a target did not reach its peer.{Environment.NewLine}{Log}");
                Assert.That(GotBoot(first,  "Targeted"),     Is.False, "the station with a target reached the back end in charge as well");
                Assert.That(GotBoot(first,  "Untargeted"),   Is.True,  $"the station without a target did not reach the back end in charge.{Environment.NewLine}{Log}");
                Assert.That(GotBoot(second, "Untargeted"),   Is.False, "the station without a target reached the second CSMS");

                Assert.That(toCS001?[0].Value<Int32>(),      Is.EqualTo(3), $"cs001 got no answer: {toCS001?.ToString(Formatting.None)}");
                Assert.That(toCS002?[0].Value<Int32>(),      Is.EqualTo(3), $"cs002 got no answer: {toCS002?.ToString(Formatting.None)}");

            });

        }

        #endregion

        #region ATarget_TriesItsPeersInOrder_AndRefusesWhereNoneIsConnected()

        /// <summary>
        /// A target tries its peers in their order: the second where the first is not connected - and
        /// where none is, the station is told so, and nothing goes to the back end in charge instead.
        /// </summary>
        [Test]
        public async Task ATarget_TriesItsPeersInOrder_AndRefusesWhereNoneIsConnected()
        {

            await TheGateway();

            var (csms1, _)   = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2, id2) = await TheCSMSIsIn("csms-02");

            await using var first   = csms1;
            await using var second  = csms2;

            // Written down, never connected.
            var away = WriteDownTheCSMS(User_Id.Parse("csms-03"));

            await using var cs001   = await AStation(User_Id.Parse("cs001"));
            await using var cs002   = await AStation(User_Id.Parse("cs002"));

            Target ("away-first",  away, id2);
            Target ("away-alone",  away);
            Forward("cs001", "away-first");
            Forward("cs002", "away-alone");

            await cs001.Send(Boot("boot-1", "Falls through"));
            await cs002.Send(Boot("boot-2", "Nowhere"));

            var toCS001 = await cs001.AnswerTo("boot-1");
            var toCS002 = await cs002.AnswerTo("boot-2");

            Assert.Multiple(() => {

                Assert.That(GotBoot(second, "Falls through"),  Is.True,  $"the second peer of the target was not tried.{Environment.NewLine}{Log}");
                Assert.That(GotBoot(first,  "Falls through"),  Is.False, "the back end in charge was given what its target says");
                Assert.That(toCS001?[0].Value<Int32>(),        Is.EqualTo(3), toCS001?.ToString(Formatting.None));

                Assert.That(GotBoot(first,  "Nowhere"),        Is.False, "a station whose target is not connected reached the back end in charge");
                Assert.That(GotBoot(second, "Nowhere"),        Is.False, "a station whose target is not connected reached another back end");
                Assert.That(toCS002?[0].Value<Int32>(),        Is.EqualTo(4), $"a station whose target is not connected was not told so: {toCS002?.ToString(Formatting.None)}");
                Assert.That(Log,                               Does.Contain("none of the back ends of its target 'away-alone'"));

            });

        }

        #endregion

        #region ABackEnd_ReachesTheStationsOfItsTargetAlone()

        /// <summary>
        /// Down, a back end reaches the stations whose target it is in, and the back end in charge the
        /// stations without one - each answer going back to the CSMS that asked. Any other is refused:
        /// the second CSMS does not reach a station of the first, nor the first one of the second.
        /// </summary>
        [Test]
        public async Task ABackEnd_ReachesTheStationsOfItsTargetAlone()
        {

            await TheGateway();

            var (csms1, id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2, id2) = await TheCSMSIsIn("csms-02");

            await using var first   = csms1;
            await using var second  = csms2;

            await using var cs001   = await AStation(User_Id.Parse("cs001"));
            await using var cs002   = await AStation(User_Id.Parse("cs002"));

            Target ("second", id2);
            Forward("cs001",  "second");

            // Each its own station: answered, and the answer back to the one that asked.
            await second.Send(new JArray(2, "cs001", BackEndName, "c2-own",     "GetLocalListVersion", new JObject()));
            await first. Send(new JArray(2, "cs002", BackEndName, "c1-own",     "GetLocalListVersion", new JObject()));

            var c2Own = await second.AnswerTo("c2-own");
            var c1Own = await first. AnswerTo("c1-own");

            // Each the other's: refused.
            await second.Send(new JArray(2, "cs002", BackEndName, "c2-foreign", "Reset", new JObject(new JProperty("type", Is16 ? "Hard" : "Immediate"))));
            await first. Send(new JArray(2, "cs001", BackEndName, "c1-foreign", "Reset", new JObject(new JProperty("type", Is16 ? "Hard" : "Immediate"))));

            var c2Foreign = await second.AnswerTo("c2-foreign");
            var c1Foreign = await first. AnswerTo("c1-foreign");

            Assert.Multiple(() => {

                Assert.That(c2Own?[0].Value<Int32>(),                         Is.EqualTo(3), $"the second CSMS was not answered by its station: {c2Own?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(c2Own is null ? null : PayloadOf(c2Own)?["answeredBy"]?.Value<String>(), Is.EqualTo("cs001"));
                Assert.That(c1Own?[0].Value<Int32>(),                         Is.EqualTo(3), $"the first CSMS was not answered by its station: {c1Own?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(c1Own is null ? null : PayloadOf(c1Own)?["answeredBy"]?.Value<String>(), Is.EqualTo("cs002"));

                Assert.That(first. Got.Any(frame => frame[0].Value<Int32>() is 3 or 4 && IdOf(frame) == "c2-own"), Is.False, "the answer to the second CSMS went to the first one");

                Assert.That(c2Foreign?[0].Value<Int32>(),                     Is.EqualTo(4), $"the second CSMS reached a station of the first: {c2Foreign?.ToString(Formatting.None)}");
                Assert.That(c1Foreign?[0].Value<Int32>(),                     Is.EqualTo(4), $"the first CSMS reached a station of the second: {c1Foreign?.ToString(Formatting.None)}");
                Assert.That(GotAsked(cs001, "Reset") || GotAsked(cs002, "Reset"), Is.False, "a station was reset by a CSMS that is not its own");

            });

        }

        #endregion

        #region AStation_CannotAskTheGateway_WhatOnlyItsBackEndAsks()

        /// <summary>
        /// The gate at the door holds for the stations of either version: a station that addresses the
        /// gateway itself with what only its back end asks - SetVariables in OCPP 2.1, ChangeConfiguration
        /// in OCPP 1.6 - is told it may not.
        /// </summary>
        [Test]
        public async Task AStation_CannotAskTheGateway_WhatOnlyItsBackEndAsks()
        {

            await TheGateway();

            var (csms1, _) = await TheCSMSIsIn("csms-01");

            await using var first  = csms1;
            await using var cs001  = await AStation(User_Id.Parse("cs001"));

            await cs001.Send(Is16
                                 ? new JArray(2, GatewayId, "cs001", "ask-1", "ChangeConfiguration", new JObject(new JProperty("key", "HeartbeatInterval"), new JProperty("value", "1")))
                                 : new JArray(2, GatewayId, "cs001", "ask-1", "SetVariables",        new JObject(new JProperty("setVariableData", new JArray()))));

            var refused = await cs001.AnswerTo("ask-1");

            Assert.Multiple(() => {
                Assert.That(refused?[0].Value<Int32>(),                Is.EqualTo(4), $"the station was not refused: {refused?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(refused?.ToString(Formatting.None) ?? "",  Does.Contain("CSMS to ask"));
            });

        }

        #endregion

        #region TheStationsOfEachPeer_AreCounted_ByTheirTargets_AndWhereTheyGoNow()

        /// <summary>
        /// What the connections page says of each peer: how many stations name it first in their
        /// target, how many after another, how many without one go to it as the back end in charge -
        /// a login switched off not counted -, and how many go to it now; switched off, nothing.
        /// </summary>
        [Test]
        public async Task TheStationsOfEachPeer_AreCounted_ByTheirTargets_AndWhereTheyGoNow()
        {

            await TheGateway();

            var (csms1, id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2, id2) = await TheCSMSIsIn("csms-02");

            await using var first   = csms1;
            await using var second  = csms2;

            // Written down, never connected.
            var away = WriteDownTheCSMS(User_Id.Parse("csms-03"));

            foreach (var station in new[] { "cs001", "cs002", "cs003", "cs004", "cs005", "cs006" })
                Assert.That(gateway!.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse(station), ThePassword, null, null, out var notSet), Is.True, notSet);

            Target ("away-first",  away, id2);
            Target ("second",      id2);
            Target ("away-alone",  away);
            Forward("cs001", "away-first");
            Forward("cs002", "second");
            Forward("cs003", "away-alone");

            // cs004 and cs005 name no target; cs006 neither, and is switched off.
            Assert.That(gateway!.WebSocketLogins.TrySetEnabled(User_Id.Parse("cs006"), false, out var notSwitched), Is.True, notSwitched);

            var counted = gateway.StationsByPeer();

            Assert.That(counted, Is.Not.Null, "a gateway that forwards counts nothing");

            Assert.Multiple(() => {
                Assert.That(counted![PeerOf(id1)],   Is.EqualTo(new PeerStations(First: 0, InReserve: 0, WithoutTarget: 2, Now: 2)), "the back end in charge");
                Assert.That(counted [PeerOf(id2)],   Is.EqualTo(new PeerStations(First: 1, InReserve: 1, WithoutTarget: 0, Now: 2)), "the second CSMS");
                Assert.That(counted [PeerOf(away)],  Is.EqualTo(new PeerStations(First: 2, InReserve: 0, WithoutTarget: 0, Now: 0)), "the CSMS that is away");
            });

            Assert.That(gateway.TryUpdateForwardingConfiguration(new JObject(new JProperty("enabled", false)), out var error), Is.True, error);
            Assert.That(gateway.StationsByPeer(), Is.Null, "switched off, it counts all the same");

        }

        #endregion

        #region TheOCPPLog_HasWhatWasForwarded_BothWays()

        /// <summary>
        /// The OCPP log of the gateway has what it forwarded, as it went over its connections: a
        /// request of a station with a target, in from the station and out to its peer, the answer in
        /// from the peer and out to the station; a request of a CSMS down to its station and back; and
        /// a request refused, in from the CSMS that sent it and the error out to it - every answer told
        /// by the action of the request it answers, though the gateway asked under an id of its own.
        /// </summary>
        [Test]
        public async Task TheOCPPLog_HasWhatWasForwarded_BothWays()
        {

            await TheGateway();

            var (csms1, _)   = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2, id2) = await TheCSMSIsIn("csms-02");

            await using var first   = csms1;
            await using var second  = csms2;

            await using var cs001   = await AStation(User_Id.Parse("cs001"));
            await using var cs002   = await AStation(User_Id.Parse("cs002"));

            Target ("second", id2);
            Forward("cs001",  "second");

            // Up, to the target, and back.
            await cs001.Send(Boot("boot-1", "Targeted"));
            Assert.That((await cs001.AnswerTo("boot-1"))?[0].Value<Int32>(), Is.EqualTo(3), Log);

            // Down, from the target to its station, and back.
            await second.Send(new JArray(2, "cs001", BackEndName, "c2-own", "GetLocalListVersion", new JObject()));
            Assert.That((await second.AnswerTo("c2-own"))?[0].Value<Int32>(), Is.EqualTo(3), Log);

            // Down, to a station that is not this CSMS's: refused.
            await first.Send(new JArray(2, "cs001", BackEndName, "c1-foreign", "Reset", new JObject(new JProperty("type", Is16 ? "Hard" : "Immediate"))));
            Assert.That((await first.AnswerTo("c1-foreign"))?[0].Value<Int32>(), Is.EqualTo(4), Log);

            await Until(() => gateway!.OCPPMessages.Count >= 9, "the OCPP log having every message");

            var messages = gateway!.OCPPMessages.Recent(100).ToArray();

            String Seen()
                => String.Join(Environment.NewLine, messages.Select(entry => $"{entry.Direction} {entry.Kind} {entry.Peer} {entry.Action} {entry.Message.ToString(Formatting.None)}"));

            Int32 Count(cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageDirection  Direction,
                        cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind       Kind,
                        String                                                                 Action,
                        String?                                                                Peer = null)

                => messages.Count(entry => entry.Direction == Direction &&
                                           entry.Kind      == Kind      &&
                                           entry.Action    == Action    &&
                                           (Peer is null || entry.Peer == Peer));

            TestContext.Out.WriteLine(Seen());

            const cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageDirection In   = cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageDirection.In;
            const cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageDirection Out  = cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageDirection.Out;
            const cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind      Call = cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind.Request;
            const cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind      Ok   = cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind.Response;
            const cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind      No   = cloud.charging.open.protocols.OCPP.Node.Logging.OCPPMessageKind.RequestError;

            Assert.Multiple(() => {

                Assert.That(Count(In,  Call, "BootNotification",    "cs001"), Is.EqualTo(1), $"the station's request, in.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, Call, "BootNotification"),             Is.EqualTo(1), $"the station's request, out to its peer.{Environment.NewLine}{Seen()}");
                Assert.That(Count(In,  Ok,   "BootNotification"),             Is.EqualTo(1), $"the peer's answer, in.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, Ok,   "BootNotification",    "cs001"), Is.EqualTo(1), $"the answer, out to the station.{Environment.NewLine}{Seen()}");

                Assert.That(Count(In,  Call, "GetLocalListVersion"),          Is.EqualTo(1), $"the CSMS's request, in.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, Call, "GetLocalListVersion", "cs001"), Is.EqualTo(1), $"the CSMS's request, out to its station.{Environment.NewLine}{Seen()}");
                Assert.That(Count(In,  Ok,   "GetLocalListVersion", "cs001"), Is.EqualTo(1), $"the station's answer, in.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, Ok,   "GetLocalListVersion"),          Is.EqualTo(1), $"the answer, out to the CSMS.{Environment.NewLine}{Seen()}");

                Assert.That(Count(In,  Call, "Reset"),                        Is.EqualTo(1), $"the foreign request, in.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, No,   "Reset"),                        Is.EqualTo(1), $"its refusal, out.{Environment.NewLine}{Seen()}");
                Assert.That(Count(Out, Call, "Reset"),                        Is.EqualTo(0), $"the foreign request was passed on.{Environment.NewLine}{Seen()}");

            });

        }

        #endregion

        #region SwitchedOff_NothingIsForwarded_AndSwitchedOnAgainItIs()

        /// <summary>
        /// A gateway forwards where its file says nothing, as its kind does. Switched off, what a
        /// station sends up is refused, and the station told so; switched on again, without a restart,
        /// it goes up again - and the file says what was switched.
        /// </summary>
        [Test]
        public async Task SwitchedOff_NothingIsForwarded_AndSwitchedOnAgainItIs()
        {

            await TheGateway();

            Assert.Multiple(() => {
                Assert.That(gateway!.ForwardsByDefault,  Is.True, "a gateway does not forward by its kind");
                Assert.That(gateway.Forwards,            Is.True);
            });

            var (csms1, _) = await TheCSMSIsIn("csms-01");

            await using var first  = csms1;
            await using var cs001  = await AStation(User_Id.Parse("cs001"));

            Assert.That(gateway!.TryUpdateForwardingConfiguration(new JObject(new JProperty("enabled", false)), out var error), Is.True, error);

            await cs001.Send(Boot("boot-off", "Off"));

            var refused = await cs001.AnswerTo("boot-off");

            Assert.Multiple(() => {
                Assert.That(gateway.Forwards,                   Is.False);
                Assert.That(refused?[0].Value<Int32>(),         Is.EqualTo(4), $"switched off, the station was not told: {refused?.ToString(Formatting.None)}");
                Assert.That(GotBoot(first, "Off"),              Is.False, "switched off, it was forwarded all the same");
                Assert.That(JObject.Parse(File.ReadAllText(gateway.ConfigFile.Path))["forwarding"]?["enabled"]?.Value<Boolean>(), Is.False, "the file does not say it");
            });

            Assert.That(gateway.TryUpdateForwardingConfiguration(new JObject(), out error), Is.True, error);

            await cs001.Send(Boot("boot-on", "On"));

            var answered = await cs001.AnswerTo("boot-on");

            Assert.Multiple(() => {
                Assert.That(gateway.Forwards,                   Is.True, "back to what its kind does, it does not forward");
                Assert.That(GotBoot(first, "On"),               Is.True, $"switched on again, it was not forwarded.{Environment.NewLine}{Log}");
                Assert.That(answered?[0].Value<Int32>(),        Is.EqualTo(3), answered?.ToString(Formatting.None));
            });

        }

        #endregion

        #region (private) failover helpers

        /// <summary>
        /// The CSMS of the given name, connected again over a line of its own: the one before it went.
        /// </summary>
        private async Task<Line> BackAgain(String Name, String Id, Boolean TurnsAway = false, TimeSpan? Slowness = null)
        {

            var csms = new Line("CSMS", TurnsAway, Slowness);

            Assert.That(await csms.Connect(Door(Name), Name, ItsPassword, Subprotocol, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(Id) == ConnectionStatus.Connected, $"'{Name}' being connected again");

            return csms;

        }

        /// <summary>
        /// The CSMS of the given connection gone: its line closed, and the gateway knowing it.
        /// </summary>
        private async Task Gone(Line CSMS, String Id)
        {

            await CSMS.DisposeAsync();

            await Until(() => StatusOf(Id) != ConnectionStatus.Connected, "The CSMS being gone");

        }

        private JArray Status(String Id, Int32 Connector, String State)

            => Is16
                   ? new (2, Id, "StatusNotification", new JObject(
                             new JProperty("connectorId",  Connector),
                             new JProperty("errorCode",    "NoError"),
                             new JProperty("status",       State)
                         ))
                   : new (2, Id, "StatusNotification", new JObject(
                             new JProperty("timestamp",        DateTimeOffset.UtcNow.ToString("o")),
                             new JProperty("connectorStatus",  State),
                             new JProperty("evseId",           1),
                             new JProperty("connectorId",      Connector)
                         ));

        private static JArray Heartbeat(String Id)
            => new (2, Id, "Heartbeat", new JObject());

        /// <summary>
        /// A charging session begins: a TransactionEvent "Started" in OCPP 2.1, a StartTransaction in
        /// OCPP 1.6 - which its CSMS answers with the transaction 4711.
        /// </summary>
        private JArray Started(String Id, String Session)

            => Is16
                   ? new (2, Id, "StartTransaction", new JObject(
                             new JProperty("connectorId",  1),
                             new JProperty("idTag",        "TAG-1"),
                             new JProperty("meterStart",   0),
                             new JProperty("timestamp",    DateTimeOffset.UtcNow.ToString("o"))
                         ))
                   : TransactionEvent(Id, Session, "Started", 0);

        /// <summary>
        /// A part of a charging session: a TransactionEvent "Updated" in OCPP 2.1, the MeterValues of
        /// the transaction 4711 in OCPP 1.6.
        /// </summary>
        private JArray Updated(String Id, String Session, Int32 SeqNo)

            => Is16
                   ? new (2, Id, "MeterValues", new JObject(
                             new JProperty("connectorId",    1),
                             new JProperty("transactionId",  4711),
                             new JProperty("meterValue",     new JArray(new JObject(
                                 new JProperty("timestamp",     DateTimeOffset.UtcNow.ToString("o")),
                                 new JProperty("sampledValue",  new JArray(new JObject(new JProperty("value", $"{SeqNo}0"))))
                             )))
                         ))
                   : TransactionEvent(Id, Session, "Updated", SeqNo);

        /// <summary>
        /// A charging session ends: a TransactionEvent "Ended" in OCPP 2.1, a StopTransaction of the
        /// transaction 4711 in OCPP 1.6.
        /// </summary>
        private JArray Ended(String Id, String Session, Int32 SeqNo)

            => Is16
                   ? new (2, Id, "StopTransaction", new JObject(
                             new JProperty("transactionId",  4711),
                             new JProperty("meterStop",      SeqNo * 10),
                             new JProperty("timestamp",      DateTimeOffset.UtcNow.ToString("o"))
                         ))
                   : TransactionEvent(Id, Session, "Ended", SeqNo);

        private static JArray TransactionEvent(String Id, String Session, String EventType, Int32 SeqNo)

            => new (2, Id, "TransactionEvent", new JObject(
                       new JProperty("eventType",        EventType),
                       new JProperty("timestamp",        DateTimeOffset.UtcNow.ToString("o")),
                       new JProperty("triggerReason",    EventType == "Started" ? "Authorized" : EventType == "Ended" ? "StopAuthorized" : "MeterValuePeriodic"),
                       new JProperty("seqNo",            SeqNo),
                       new JProperty("transactionInfo",  new JObject(new JProperty("transactionId", Session)))
                   ));

        /// <summary>
        /// The parts of charging sessions a CSMS got, in the order it got them: what each is, and of
        /// which session - its transaction id, or, in OCPP 1.6, 4711.
        /// </summary>
        private static String[] SessionPartsGot(Line CSMS)

            => [.. CSMS.Got.Where (frame => frame[0].Value<Int32>() == 2 &&
                                            ActionOf(frame) is "TransactionEvent" or "StartTransaction" or "MeterValues" or "StopTransaction").
                            Select(frame => ActionOf(frame) == "TransactionEvent"
                                                ? $"{PayloadOf(frame)?["eventType"]} {PayloadOf(frame)?["transactionInfo"]?["transactionId"]}"
                                                : $"{ActionOf(frame)} {PayloadOf(frame)?["transactionId"]?.ToString() ?? "-"}")];

        /// <summary>
        /// The requests a CSMS got in the name of the given station - every one, in a frame of the
        /// standard networking mode, which names nobody.
        /// </summary>
        private static String[] AskedFor(Line CSMS, String Station)

            => [.. CSMS.Got.Where (frame => frame[0].Value<Int32>() == 2 && (!IsOverlay(frame) || SourceOf(frame) == Station)).
                            Select(ActionOf)];

        /// <summary>
        /// Everything a station got that is not the answer to one of the given requests of its own.
        /// </summary>
        private static JArray[] Unasked(Line Station, params String[] RequestIds)

            => [.. Station.Got.Where(frame => !(frame[0].Value<Int32>() is 3 or 4 && RequestIds.Contains(IdOf(frame))))];

        private static Int32 KindOf(JArray? Frame)
            => Frame?[0].Value<Int32>() ?? 0;

        #endregion


        #region WhenItsBackEndGoes_TheNextOneIsToldWhoTheStationIs_AndTheFirstOneWhenItReturns()

        /// <summary>
        /// A station's back end goes: the next one of its target is told who the station is - its
        /// BootNotification and the state of its connector, in its name - before what the station sends
        /// next, which goes to it. The station gets no answer it did not ask for. When the first one
        /// returns, the station goes back to it, and it is told the same - and what changed meanwhile.
        /// </summary>
        [Test]
        public async Task WhenItsBackEndGoes_TheNextOneIsToldWhoTheStationIs_AndTheFirstOneWhenItReturns()
        {

            await TheGateway();

            var (first,  id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2,  id2) = await TheCSMSIsIn("csms-02");

            await using var second = csms2;
            await using var cs001  = await AStation(User_Id.Parse("cs001"));

            Target ("both", id1, id2);
            Forward("cs001", "both");

            await cs001.Send(Boot  ("boot-1", "Failing over"));
            Assert.That(KindOf(await cs001.AnswerTo("boot-1")),   Is.EqualTo(3), Log);

            await cs001.Send(Status("status-1", 1, "Available"));
            Assert.That(KindOf(await cs001.AnswerTo("status-1")), Is.EqualTo(3), Log);

            Assert.That(AskedFor(first,  "cs001"), Is.EqualTo(new[] { "BootNotification", "StatusNotification" }), "before it went, the first one did not get what the station sent");
            Assert.That(AskedFor(second, "cs001"), Is.Empty, "before the first one went, the second one got something");

            await Gone(first, id1);

            await cs001.Send(Status("status-2", 2, "Occupied"));
            var toStatus2 = await cs001.AnswerTo("status-2");

            await Until(() => AskedFor(second, "cs001").Length >= 3, "The second CSMS being told who the station is");

            Assert.Multiple(() => {
                Assert.That(KindOf(toStatus2),             Is.EqualTo(3), $"the station was not answered: {toStatus2?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(AskedFor(second, "cs001"),     Is.EqualTo(new[] { "BootNotification", "StatusNotification", "StatusNotification" }),
                                                           $"the second one was not told who the station is, and then what it sent ({Mode}).{Environment.NewLine}{Log}");
                Assert.That(GotBoot(second, "Failing over"), Is.True, "the BootNotification it was told is not the station's");
                Assert.That(Unasked(cs001, "boot-1", "status-1", "status-2"), Is.Empty, "the station got an answer it did not ask for");
                Assert.That(Log,                           Does.Contain("is at '").And.Contain("no longer at '"));
            });

            await using var again = await BackAgain("csms-01", id1);

            await Until(() => AskedFor(again, "cs001").Length >= 3, "The first CSMS being told who the station is, back again");

            await cs001.Send(Heartbeat("beat-1"));
            Assert.That(KindOf(await cs001.AnswerTo("beat-1")), Is.EqualTo(3), Log);

            Assert.Multiple(() => {
                Assert.That(AskedFor(again, "cs001"),       Is.EqualTo(new[] { "BootNotification", "StatusNotification", "StatusNotification", "Heartbeat" }),
                                                            $"back again, the first one was not told who the station is, or did not get what it sent next ({Mode}).{Environment.NewLine}{Log}");
                Assert.That(AskedFor(second, "cs001"),      Does.Not.Contain("Heartbeat"), "the second one got what the station sent after the first one returned");
            });

        }

        #endregion

        #region ASession_StaysWithTheBackEndItBeganAt_WhatCannotReachItIsHeldBack_AndSentInOrder()

        /// <summary>
        /// A charging session begins at the first CSMS, which goes: what follows of the session does
        /// not go to the second one - the station is answered by the gateway and it is held back, on
        /// disk -, a session that begins now does. When the first one returns, it gets what was held
        /// back before the end of the session, which the station sends while the first one is being
        /// told who it is again - slowly -, and nothing is held back any more.
        /// </summary>
        [Test]
        public async Task ASession_StaysWithTheBackEndItBeganAt_WhatCannotReachItIsHeldBack_AndSentInOrder()
        {

            await TheGateway();

            var (first,  id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);
            var (csms2,  id2) = await TheCSMSIsIn("csms-02");

            await using var second = csms2;
            await using var cs001  = await AStation(User_Id.Parse("cs001"));

            Target ("both", id1, id2);
            Forward("cs001", "both");

            await cs001.Send(Boot   ("boot-1",  "Charging"));
            await cs001.AnswerTo("boot-1");

            await cs001.Send(Started("start-1", "tx-1"));
            Assert.That(KindOf(await cs001.AnswerTo("start-1")), Is.EqualTo(3), Log);

            var startedAtTheFirst = SessionPartsGot(first);

            await Gone(first, id1);

            await cs001.Send(Updated("update-1", "tx-1", 1));
            var toUpdate = await cs001.AnswerTo("update-1");

            var heldBack = Path.Combine(directory, "ocpp-buffer");

            Assert.Multiple(() => {
                Assert.That(startedAtTheFirst,                  Is.EqualTo(new[] { Is16 ? "StartTransaction -" : "Started tx-1" }), Log);
                Assert.That(KindOf(toUpdate),                   Is.EqualTo(3), $"the station was not answered: {toUpdate?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(toUpdate is null ? null : PayloadOf(toUpdate), Is.EqualTo(new JObject()), "the gateway's answer says more than that it was taken");
                Assert.That(SessionPartsGot(second),            Is.Empty, "a part of a session that began at the first one went to the second one");
                Assert.That(gateway!.HeldBack.Count,            Is.EqualTo(1), Log);
                Assert.That(Directory.Exists(heldBack) && Directory.EnumerateFiles(heldBack, "*.jsonl").Any(), Is.True, "nothing is held back on disk");
            });

            // A session that begins now begins where the station is now.
            await cs001.Send(Started("start-2", "tx-2"));
            Assert.That(KindOf(await cs001.AnswerTo("start-2")), Is.EqualTo(3), Log);

            Assert.That(SessionPartsGot(second), Is.EqualTo(new[] { Is16 ? "StartTransaction -" : "Started tx-2" }), Log);

            await using var again = await BackAgain("csms-01", id1, Slowness: TimeSpan.FromMilliseconds(300));

            await cs001.Send(Ended("end-1", "tx-1", 2));
            var toEnd = await cs001.AnswerTo("end-1");

            await Until(() => gateway!.HeldBack.Count == 0 && SessionPartsGot(again).Length >= 2, "What was held back, and the end of the session, being sent");

            Assert.Multiple(() => {
                Assert.That(KindOf(toEnd),                      Is.EqualTo(3), $"the station was not answered: {toEnd?.ToString(Formatting.None)}");
                Assert.That(SessionPartsGot(again),             Is.EqualTo(Is16 ? new[] { "MeterValues 4711", "StopTransaction 4711" }
                                                                                : new[] { "Updated tx-1",     "Ended tx-1" }),
                                                                $"what was held back was not sent to the first one when it returned, in order ({Mode}).{Environment.NewLine}{Log}");
                Assert.That(Directory.EnumerateFiles(heldBack, "*.jsonl").Any(), Is.False, "what was sent is still held back on disk");
            });

        }

        #endregion

        #region WithoutItsBackEnd_TheGatewayAnswersHeartbeatAndStatus_NotAnAuthorize()

        /// <summary>
        /// A station without a target, and the back end in charge gone, none other there: the gateway
        /// answers a Heartbeat with its own time and a StatusNotification, and holds back a part of a
        /// charging session - but answers no Authorize, which is the back end's to decide. Back again,
        /// the back end gets what it missed: the state of the connector and the part of the session,
        /// and no BootNotification, as the station was at it all the time.
        /// </summary>
        [Test]
        public async Task WithoutItsBackEnd_TheGatewayAnswersHeartbeatAndStatus_NotAnAuthorize()
        {

            await TheGateway();

            var (first, id1) = await TheCSMSIsIn("csms-01");

            await using var cs001 = await AStation(User_Id.Parse("cs001"));

            await cs001.Send(Boot   ("boot-1",  "Alone"));
            await cs001.AnswerTo("boot-1");

            await cs001.Send(Started("start-1", "tx-9"));
            await cs001.AnswerTo("start-1");

            await Gone(first, id1);

            await cs001.Send(Heartbeat("beat-1"));
            await cs001.Send(Status   ("status-1", 1, "Occupied"));
            await cs001.Send(Updated  ("update-1", "tx-9", 1));
            await cs001.Send(new JArray(2, "auth-1", "Authorize", Is16
                                                                    ? new JObject(new JProperty("idTag", "TAG-1"))
                                                                    : new JObject(new JProperty("idToken", new JObject(new JProperty("idToken", "TAG-1"), new JProperty("type", "ISO14443"))))));

            var toBeat    = await cs001.AnswerTo("beat-1");
            var toStatus  = await cs001.AnswerTo("status-1");
            var toUpdate  = await cs001.AnswerTo("update-1");
            var toAuth    = await cs001.AnswerTo("auth-1");

            Assert.Multiple(() => {
                Assert.That(KindOf(toBeat),                   Is.EqualTo(3), $"the Heartbeat was not answered: {toBeat?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(toBeat is null ? null : PayloadOf(toBeat)?["currentTime"], Is.Not.Null, "the Heartbeat was answered without the time");
                Assert.That(KindOf(toStatus),                 Is.EqualTo(3), $"the StatusNotification was not answered: {toStatus?.ToString(Formatting.None)}");
                Assert.That(KindOf(toUpdate),                 Is.EqualTo(3), $"the part of the session was not answered: {toUpdate?.ToString(Formatting.None)}");
                Assert.That(KindOf(toAuth),                   Is.EqualTo(4), $"the Authorize was answered by the gateway, or not at all ({Mode}): {toAuth?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
            });

            await using var again = await BackAgain("csms-01", id1);

            await Until(() => gateway!.HeldBack.Count == 0 && AskedFor(again, "cs001").Contains("StatusNotification"), "What the back end missed being sent");

            Assert.Multiple(() => {
                Assert.That(AskedFor(again, "cs001"),         Does.Not.Contain("BootNotification"), "the station was booted anew at the back end it was at all the time");
                Assert.That(SessionPartsGot(again),           Is.EqualTo(new[] { Is16 ? "MeterValues 4711" : "Updated tx-9" }), Log);
                Assert.That(AskedFor(again, "cs001"),         Does.Not.Contain("Heartbeat"), "a Heartbeat the gateway answered was sent on");
            });

        }

        #endregion

        #region ABackEndThatTurnsTheStationAway_IsPassedOver()

        /// <summary>
        /// The first back end goes, the second one turns the station away when it is told who it is:
        /// the station goes on to the third one, and what it sends goes there.
        /// </summary>
        [Test]
        public async Task ABackEndThatTurnsTheStationAway_IsPassedOver()
        {

            await TheGateway();

            var (first, id1) = await TheCSMSIsIn("csms-01");
            await Task.Delay(20);

            var id2 = WriteDownTheCSMS(User_Id.Parse("csms-02"));
            await using var second = await BackAgain("csms-02", id2, TurnsAway: true);
            await Task.Delay(20);

            var (csms3, id3) = await TheCSMSIsIn("csms-03");

            await using var third = csms3;
            await using var cs001 = await AStation(User_Id.Parse("cs001"));

            Target ("three", id1, id2, id3);
            Forward("cs001", "three");

            await cs001.Send(Boot("boot-1", "Passed over"));
            await cs001.AnswerTo("boot-1");

            await Gone(first, id1);

            await cs001.Send(Heartbeat("beat-1"));
            var toBeat = await cs001.AnswerTo("beat-1");

            // Past a look of the gateway at its back ends: the one that turned it away is not asked again.
            await Task.Delay(TimeSpan.FromSeconds(6));

            await cs001.Send(Heartbeat("beat-2"));
            await cs001.AnswerTo("beat-2");

            Assert.Multiple(() => {
                Assert.That(KindOf(toBeat),               Is.EqualTo(3), $"the station was not answered: {toBeat?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(AskedFor(second, "cs001"),    Is.EqualTo(new[] { "BootNotification" }), "the second one was not told who the station is, or was asked again after it turned it away");
                Assert.That(AskedFor(third,  "cs001"),    Is.EqualTo(new[] { "BootNotification", "Heartbeat", "Heartbeat" }), $"the station did not go on to the third one ({Mode}).{Environment.NewLine}{Log}");
                Assert.That(Log,                          Does.Contain("turned the charging station 'cs001' away"));
            });

        }

        #endregion

    }

}

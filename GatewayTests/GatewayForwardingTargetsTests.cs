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
    /// are: what is tested is what is passed on to whom, frame by frame.
    /// </remarks>
    [TestFixture(false)]
    [TestFixture(true)]
    public class GatewayForwardingTargetsTests(Boolean Overlay)
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


        #region (private) more helpers

        /// <summary>
        /// The CSMS of the given name connected, the line up of the gateway it writes down.
        /// </summary>
        private async Task<(Line CSMS, String Id)> TheCSMSIsIn(String Name)
        {

            var id    = WriteDownTheCSMS(User_Id.Parse(Name));
            var csms  = new Line("CSMS");

            Assert.That(await csms.Connect(Door(Name), Name, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, $"'{Name}' being connected");

            return (csms, id);

        }

        private String PeerOf(String ConnectionId)
            => gateway!.Connections.PeerOf(ConnectionId)?.Id ?? throw new InvalidOperationException($"The connection '{ConnectionId}' has no peer.");

        private void Target(String Id, params String[] ConnectionIds)
            => Assert.That(gateway!.WebSocketLogins.TryAddOrUpdateTarget(Id, ConnectionIds.Select(PeerOf), null, out var error), Is.True, error);

        private void Forward(String Station, String? TargetId)
            => Assert.That(gateway!.WebSocketLogins.TrySetTarget(User_Id.Parse(Station), TargetId, out var error), Is.True, error);

        private static JArray Boot(String Id, String Model)

            => new (2, Id, "BootNotification", new JObject(
                       new JProperty("chargingStation", new JObject(
                           new JProperty("model",       Model),
                           new JProperty("vendorName",  "GraphDefined")
                       )),
                       new JProperty("reason", "PowerUp")
                   ));

        private static Boolean GotBoot(Line Line, String Model)

            => Line.Got.Any(frame => frame[0].Value<Int32>() == 2 &&
                                     ActionOf(frame) == "BootNotification" &&
                                     PayloadOf(frame)?["chargingStation"]?["model"]?.Value<String>() == Model);

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
            await second.Send(new JArray(2, "cs001", "CSMS", "c2-own",     "GetLocalListVersion", new JObject()));
            await first. Send(new JArray(2, "cs002", "CSMS", "c1-own",     "GetLocalListVersion", new JObject()));

            var c2Own = await second.AnswerTo("c2-own");
            var c1Own = await first. AnswerTo("c1-own");

            // Each the other's: refused.
            await second.Send(new JArray(2, "cs002", "CSMS", "c2-foreign", "Reset", new JObject(new JProperty("type", "Immediate"))));
            await first. Send(new JArray(2, "cs001", "CSMS", "c1-foreign", "Reset", new JObject(new JProperty("type", "Immediate"))));

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

    }

}

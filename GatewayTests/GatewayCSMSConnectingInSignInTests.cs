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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A CSMS that connects to the gateway signs in with what is more than a password: a client
    /// certificate - known by the key pin of its WebSocket login, or by a chain the station port
    /// accepts, issued to the name it comes under - or a one-time password of its login, bound to
    /// the TLS session of its line, over HTTP/3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How a sign-in is checked is WebSocketSignIns', and tested there and by the local controller,
    /// whose tests these are ported from. What is tested here is that the station port of a gateway
    /// lets a CSMS in by those very checks.
    /// </para>
    /// <para>
    /// The CSMS is a second gateway here, whose connection to the first is one it makes: what it
    /// sends - a certificate with its chain, a bound one-time password over HTTP/3 - is what
    /// WWCPNode sends, which a CSMS that connects uses unchanged. The gateway under test lets it in
    /// on the port of its charging stations, by its WebSocket login, as it lets in a charging
    /// station.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GatewayCSMSConnectingInSignInTests
    {

        #region Data

        private static readonly User_Id   CSMSName         = User_Id.Parse("csms-01");
        private const           String    TheSharedSecret  = "a-shared-secret-long-enough-for-ocpp";

        private static readonly TimeSpan  ThroughWithin    = TimeSpan.FromSeconds(15);

        private TestCA    ca                = default!;
        private String    gatewayDirectory  = default!;
        private String    csmsDirectory     = default!;
        private Gateway   gateway           = default!;
        private Gateway?  csms;
        private String?   csmsConnection;
        private IPPort    port;

        private readonly ConcurrentQueue<String>  said  = [];
        private readonly List<TestCA>             more  = [];

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// The gateway under test: its station port encrypted with a certificate of the site's
        /// authority, taking security profiles 2 and 3, over HTTP/3 as well - and OCPP-over-SSH on
        /// its SSH server.
        /// </summary>
        [SetUp]
        public async Task StartTheGatewayTheCSMSConnectsTo()
        {

            said.Clear();

            ca       = TestCA.Create($"Site CA {Guid.NewGuid()}", WithIntermediate: true);

            gateway  = await TestPorts.StartedOnFreshPorts(() => {

                           Remove(gatewayDirectory);

                           port              = APortFreeForBoth();
                           gatewayDirectory  = TemporaryDirectory("gateway");

                           // What the station port presents, issued by the authority for a key made in
                           // the node's store: a TLS server identity of the listener "ocpp".
                           AStationPortIdentityIn(gatewayDirectory, [ "127.0.0.1" ]);

                           var made = AGatewayIn(gatewayDirectory,
                                                 new JProperty("ocpp",            new JObject(new JProperty("nodeId", "gw001"))),
                                                 new JProperty("webSocketServer", new JObject(
                                                     new JProperty("enabled",               true),
                                                     new JProperty("address",               "127.0.0.1"),
                                                     new JProperty("port",                  port.ToUInt16()),
                                                     new JProperty("clientAuthentication",  new JArray("httpAuthOverTLS", "tlsClientCertificate")),
                                                     new JProperty("reachableAs",           new JArray("127.0.0.1")),
                                                     new JProperty("subprotocols",          new JArray("ocpp2.1")),
                                                     new JProperty("http3",                 true),
                                                     new JProperty("ssh",                   new JObject(new JProperty("enabled", true)))
                                                 )),
                                                 new JProperty("ssh",             new JObject(
                                                     new JProperty("enabled",               true),
                                                     new JProperty("port",                  TestPorts.Free().ToUInt16())
                                                 )));

                           made.Log.OnLogged += entry => said.Enqueue(entry.Message);

                           return made;

                       });

            csmsDirectory = TemporaryDirectory("csms");

        }

        [TearDown]
        public async Task StopThem()
        {

            if (csms is not null)
                await csms.DisposeAsync();

            csms            = null;
            csmsConnection  = null;

            await gateway.DisposeAsync();

            ca.Dispose();

            foreach (var other in more)
                other.Dispose();

            more.Clear();

            Remove(csmsDirectory);
            Remove(gatewayDirectory);

        }

        #endregion


        #region (private static) TemporaryDirectory(Purpose) / Remove(Directory) / AGatewayIn(Directory, Sections)

        /// <summary>
        /// A directory of its own for one gateway of one test.
        /// </summary>
        private static String TemporaryDirectory(String Purpose)
        {

            var directory = Path.Combine(Path.GetTempPath(), $"gateway-csms-signs-in-{Purpose}-{Guid.NewGuid().ToString("N")[..12]}");

            Directory.CreateDirectory(directory);

            return directory;

        }

        /// <summary>
        /// Take a test's directory away again - never throwing: a directory that survives a run is
        /// untidy and nothing more.
        /// </summary>
        private static void Remove(String? Directory)
        {
            try
            {
                if (Directory is not null && System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception)
            { }
        }

        /// <summary>
        /// A gateway in the given directory, whose time client is switched off, on a free port of the
        /// loopback, with the given sections in its configuration file beside that - made, and not
        /// started.
        /// </summary>
        private static Gateway AGatewayIn(String              Directory,
                                          params JProperty[]  Sections)
        {

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(file, new JObject(
                                        [ new JProperty("nts", new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                          .. Sections ]
                                    ).ToString());

            return new Gateway(
                       HTTPPort:        TestPorts.Free(),
                       AccountsPath:    Path.Combine(Directory, "accounts"),
                       ConfigFile:      new WWCPConfigFile(file),
                       LogToConsole:    false,
                       BridgeDebugLog:  false
                   );

        }

        #endregion

        #region (private) AStationPortIdentityIn(Directory, ReachableAs)

        /// <summary>
        /// A certificate the site's authority issued for a key made in the certificate store of a
        /// gateway that is not built yet, for the names given - a TLS server identity of the station
        /// port, which decides whether it encrypts when it starts.
        /// </summary>
        private void AStationPortIdentityIn(String               Directory,
                                            IEnumerable<String>  ReachableAs)
        {

            var store = new CertificateStore(Path.Combine(Directory, CertificatesConfiguration.DefaultDirectory),
                                             new EventLog(),
                                             NodeName:      "gateway",
                                             Listeners:     [ Gateway.StationListener ],
                                             RequestKinds:  [ CertificateKind.TLSServerIdentity ]);

            store.Reload();

            var names = ReachableAs.ToArray();

            Assert.That(store.TryCreateRequest(CertificateKind.TLSServerIdentity,
                                               names[0],
                                               null,
                                               new CertificateRequestAsks(null, null, null, null, names),
                                               [ Gateway.StationListener ],
                                               null,
                                               out var request,
                                               out var error,
                                               out _),
                        Is.True, error);

            using var certificate = ca.Sign(request!.CSR, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            Assert.That(store.TryAnswerRequest(ca.ChainPEM(certificate), out _, out _, out _, out error, out _),
                        Is.True, error);

        }

        #endregion

        #region (private static) APortFreeForBoth()

        /// <summary>
        /// A port free for UDP as for TCP: a WebSocket over HTTP/3 comes to the UDP port of the number
        /// of the TCP one.
        /// </summary>
        private static IPPort APortFreeForBoth()
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

                    return IPPort.Parse((UInt16) port);

                }
                catch (System.Net.Sockets.SocketException)
                { }

            }

            throw new InvalidOperationException("No port free for TCP and UDP was found.");

        }

        #endregion

        #region (private) TheGatewayTakesHTTP3()

        private void TheGatewayTakesHTTP3()
        {

            var server = gateway.StationServer;

            SpinWait.SpinUntil(() => server?.HTTP3Port is not null || server?.WhyNoHTTP3 is not null, TimeSpan.FromSeconds(10));

            Assert.That(server?.HTTP3Port?.ToString(), Is.EqualTo(port.ToString()),
                        "The gateway takes no WebSockets over HTTP/3: " + server?.WhyNoHTTP3);

        }

        #endregion

        #region (private) AnIdentity(Subject, Authority = null, WithItsSubCA = true) / KeyPinOf(Identity)

        /// <summary>
        /// A TLS identity the given authority issued - the site's, where none is given -, with its
        /// private key and the sub-CA it was issued by, as the PKCS#12 an import takes: or with its
        /// key alone, where it is not brought with its sub-CA.
        /// </summary>
        private Byte[] AnIdentity(String   Subject,
                                  TestCA?  Authority     = null,
                                  Boolean  WithItsSubCA  = true)
        {

            var authority     = Authority ?? ca;

            using var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request       = new CertificateRequest($"CN={Subject}", key, HashAlgorithmName.SHA256);

            using var issued  = authority.Sign(request.CreateSigningRequestPem(),
                                               DateTimeOffset.UtcNow.AddDays(-1),
                                               DateTimeOffset.UtcNow.AddYears(1),
                                               ClientAuthentication: true);

            using var withKey = issued.CopyWithPrivateKey(key);

            var collection    = new X509Certificate2Collection { withKey };

            if (WithItsSubCA && authority.Intermediate is not null)
                collection.Add(authority.Intermediate);

            return collection.Export(X509ContentType.Pkcs12)!;

        }

        /// <summary>
        /// The key pin of the key of the first certificate of a PKCS#12.
        /// </summary>
        private static String KeyPinOf(Byte[] Identity)
        {

            var certificates = X509CertificateLoader.LoadPkcs12Collection(Identity, null);

            try
            {
                return KeyPin.FromCertificate(certificates.First(one => one.HasPrivateKey)).ToHex();
            }
            finally
            {
                foreach (var one in certificates)
                    one.Dispose();
            }

        }

        #endregion

        #region (private) TheCSMSIsWrittenDown(KeyPins = null, ByCA = false, BoundOneTimePass = false, Operator = null)

        /// <summary>
        /// Write down, on the gateway under test, the CSMS that connects to it: its WebSocket login,
        /// with the key pin of its client certificate or a one-time password bound to its TLS session
        /// - or, by its CA, the chain of the given authority, the site's where none is given, which
        /// the station port accepts -, and the connection it makes.
        /// </summary>
        private String TheCSMSIsWrittenDown(String?  KeyPins           = null,
                                            Boolean  ByCA              = false,
                                            Boolean  BoundOneTimePass  = false,
                                            TestCA?  Operator          = null)
        {

            if (BoundOneTimePass)
            {

                Assert.That(gateway.WebSocketLogins.TrySetTOTPAuthentication(CSMSName, TheSharedSecret, null, null, null, null, null, I18NString.Create("What the CSMS signs in with"),
                                                                             out var loginError),
                            Is.True, loginError);

                Assert.That(gateway.WebSocketLogins.TrySetTOTPBinding(CSMSName, true, out loginError), Is.True, loginError);

            }

            if (KeyPins is not null)
                Assert.That(gateway.WebSocketLogins.TryAddKeyPin(CSMSName, KeyPins, "its client certificate", null, I18NString.Create("What the CSMS signs in with"), "tests",
                                                                 out _, out var pinError),
                            Is.True, pinError);

            if (ByCA)
                Assert.That(gateway.ClientTrust.TryAdd((Operator ?? ca).Certificate.ExportCertificatePem(), "The operator's authority",
                                                       out _, out _, out var trustError),
                            Is.True, trustError);

            Assert.That(gateway.Connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), URL.Parse($"wss://127.0.0.1:{port}/{CSMSName}"), "CSMS", true, null, null,
                                                             out var id, out var error,
                                                             Inbound: true),
                        Is.True, error);

            return id!;

        }

        #endregion

        #region (private) ACSMSThatConnects(Path, Identity = null, OneTimePass = false, Bound = false, HTTPVersion = null)

        /// <summary>
        /// The CSMS: a second gateway whose connection to the gateway under test it makes itself, on
        /// the given path, believing it by the site's authority - with a client certificate, the
        /// identity its connection names, or with a one-time password over the given version of HTTP.
        /// </summary>
        private async Task<Gateway> ACSMSThatConnects(String   Path,
                                                      Byte[]?  Identity     = null,
                                                      Boolean  OneTimePass  = false,
                                                      Boolean  Bound        = false,
                                                      String?  HTTPVersion  = null)
        {

            var attempt = 0;

            Gateway Made()
            {

                if (attempt++ > 0)
                {
                    Remove(csmsDirectory);
                    csmsDirectory = TemporaryDirectory("csms");
                }

                return AGatewayIn(csmsDirectory,
                                  new JProperty("ocpp", new JObject(new JProperty("nodeId", "csms"))));

            }

            csms = await TestPorts.StartedOnFreshPorts(Made);

            Assert.That(csms.Certificates.Import(Encoding.ASCII.GetBytes(ca.Certificate.ExportCertificatePem()),
                                                 CertificateKind.TLSRoot, null, "The site's authority", null,
                                                 out _, out var rootError),
                        Is.True, rootError);

            String? handle = null;

            if (Identity is not null)
            {
                Assert.That(csms.Certificates.Import(Identity, CertificateKind.TLSIdentity, null, "Towards the gateway", null,
                                                     out var entry, out var identityError),
                            Is.True, identityError);
                handle = entry!.Id;
            }

            String? login = null;

            if (OneTimePass)
                Assert.That(csms.Connections.TryAddTOTPAuthentication(CSMSName, TheSharedSecret, I18NString.Create("What it signs in to the gateway with"),
                                                                      out login, out var tokenError,
                                                                      TLSChannelBinding: Bound),
                            Is.True, tokenError);

            Assert.That(csms.Connections.TryAddConnection(I18NString.Create("The gateway, as its CSMS sees it"), URL.Parse($"wss://127.0.0.1:{port}/{Path}"), "CSMS", true, login, handle,
                                                          out csmsConnection, out var connectionError,
                                                          HTTPVersion: HTTPVersion),
                        Is.True, connectionError);

            return csms;

        }

        #endregion

        #region (private) TheStationsAreOfTheSiteToo()

        /// <summary>
        /// The charging stations of the gateway hold certificates of the site's authority as well: a
        /// certificate of the CSMS gets through the handshake as a station's would, and what keeps it
        /// out of the name of the CSMS is the WebSocket login of the CSMS alone.
        /// </summary>
        private void TheStationsAreOfTheSiteToo()

            => Assert.That(gateway.ClientTrust.TryAdd(ca.ChainPEM(ca.Certificate), "The site's authority", out _, out _, out var trustError),
                           Is.True, trustError);

        #endregion

        #region (private) StatusOf(Id) / CSMSConnected / CSMSSays / Log / UntilConnected(Id) / NeverConnected(Id)

        private ConnectionStatus? StatusOf(String Id)
            => gateway.ConnectionStates.TryGetValue(Id, out var state) ? state.Status : null;

        /// <summary>
        /// Whether the CSMS says it is connected to the gateway.
        /// </summary>
        private Boolean CSMSConnected
            => csms is not null &&
               csmsConnection is not null &&
               csms.ConnectionStates.TryGetValue(csmsConnection, out var state) &&
               state.Status == ConnectionStatus.Connected;

        /// <summary>
        /// What the connection of the CSMS says, for a test that fails.
        /// </summary>
        private String CSMSSays
            => csms is not null && csmsConnection is not null && csms.OutboundConnections.TryGetValue(csmsConnection, out var itSays)
                   ? itSays
                   : "nothing";

        private String Log
            => String.Join(Environment.NewLine, said);

        private async Task<Boolean> UntilConnected(String Id)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp && !(StatusOf(Id) == ConnectionStatus.Connected && CSMSConnected))
                await Task.Delay(100);

            return StatusOf(Id) == ConnectionStatus.Connected && CSMSConnected;

        }

        /// <summary>
        /// Whether the CSMS keeps trying for a few seconds and is never let in: a refusal is not one
        /// moment, the line may come back.
        /// </summary>
        private async Task<Boolean> NeverConnected(String? Id)
        {

            var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);

            while (DateTimeOffset.UtcNow < giveUp)
            {

                if (CSMSConnected || (Id is not null && StatusOf(Id) == ConnectionStatus.Connected))
                    return false;

                await Task.Delay(100);

            }

            return true;

        }

        #endregion


        #region ByTheKeyPinOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()

        /// <summary>
        /// Its WebSocket login pins the key of its client certificate: the key is the whole check, and
        /// the name in the certificate is not the one it comes under. Let in, as security profile 3,
        /// with no password at all.
        /// </summary>
        [Test]
        public async Task ByTheKeyPinOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()
        {

            var identity  = AnIdentity("not-the-name-it-comes-under");
            var id        = TheCSMSIsWrittenDown(KeyPins: KeyPinOf(identity));

            await ACSMSThatConnects(CSMSName.ToString(), Identity: identity);

            Assert.That(await UntilConnected(id), Is.True, $"{CSMSSays}{Environment.NewLine}{Log}");

            Assert.Multiple(() => {
                Assert.That(Log, Does.Contain($"The back end '{CSMSName}' connected to this gateway"));
                Assert.That(Log, Does.Contain("signed in with its key pin"));
                Assert.That(Log, Does.Contain("security profile 3"));
                Assert.That(Log, Does.Not.Contain($"The charging station '{CSMSName}' signed in"), "the CSMS was let in as a charging station");
            });

        }

        #endregion

        #region AnotherKey_IsNotLetIn()

        /// <summary>
        /// A certificate of the site's authority, which the stations are of as well, with another key
        /// than the one its login pins: through the handshake as a station's, and not let in under the
        /// name of the CSMS.
        /// </summary>
        [Test]
        public async Task AnotherKey_IsNotLetIn()
        {

            TheStationsAreOfTheSiteToo();

            var id = TheCSMSIsWrittenDown(KeyPins: KeyPinOf(AnIdentity(CSMSName.ToString())));

            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(CSMSName.ToString()));

            Assert.That(await NeverConnected(id), Is.True, "a key its login does not pin was let in");

        }

        #endregion

        #region ByItsCA_IssuedToItsName_ItIsLetIn()

        /// <summary>
        /// The station port accepts the chain of its authority: issued by that authority, through a
        /// sub-CA it sends beside it, to the name the CSMS comes under. Let in, without a login, as a
        /// charging station is.
        /// </summary>
        [Test]
        public async Task ByItsCA_IssuedToItsName_ItIsLetIn()
        {

            var id = TheCSMSIsWrittenDown(ByCA: true);

            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(CSMSName.ToString()));

            Assert.That(await UntilConnected(id), Is.True, $"{CSMSSays}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' connected to this gateway").And.Contain("signed in with a certificate (security profile 3)"));

        }

        #endregion

        #region ByItsCA_IssuedToAnotherName_OrByAnotherCA_ItIsNotLetIn(IssuedTo, ByAnotherCA)

        /// <summary>
        /// Issued by the right authority to another name, or by another authority to its name: not let
        /// in.
        /// </summary>
        [TestCase("csms-02", false)]
        [TestCase("csms-01", true)]
        public async Task ByItsCA_IssuedToAnotherName_OrByAnotherCA_ItIsNotLetIn(String IssuedTo, Boolean ByAnotherCA)
        {

            var id = TheCSMSIsWrittenDown(ByCA: true);

            TestCA? another = null;

            if (ByAnotherCA)
            {
                another = TestCA.Create($"Another CA {Guid.NewGuid()}", WithIntermediate: true);
                more.Add(another);
            }

            // Issued to another name by the site's authority: through the handshake, and kept out of
            // the name of the CSMS by the name it is issued to - at the door of the station server,
            // which takes no certificate for a path that is not its common name.
            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(IssuedTo, another));

            Assert.That(await NeverConnected(id), Is.True, ByAnotherCA ? "another authority's certificate was let in" : "a certificate issued to another name was let in");

        }

        #endregion

        #region ItsCertificate_OpensNoStationsPath()

        /// <summary>
        /// The certificate the CSMS signs in with gets through the handshake by the key pin of its
        /// login. On the path of a charging station it is no station's: the key pin lets in its login,
        /// on its own path, and nobody else.
        /// </summary>
        [Test]
        public async Task ItsCertificate_OpensNoStationsPath()
        {

            var identity = AnIdentity("cs009");

            TheCSMSIsWrittenDown(KeyPins: KeyPinOf(identity));

            await ACSMSThatConnects("cs009", Identity: identity);

            Assert.Multiple(async () => {
                Assert.That(await NeverConnected(null),  Is.True, "the certificate of the CSMS let a charging station in");
                Assert.That(Log,                         Does.Contain($"is the WebSocket login '{CSMSName}''s, and it asked to connect as 'cs009'"));
            });

        }

        #endregion

        #region AOneTimePasswordBoundToItsLine_LetsItInOverHTTP3()

        /// <summary>
        /// Its WebSocket login binds the one-time password to the TLS session: over HTTP/3, whose TLS
        /// exports what it is bound with, it is let in.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordBoundToItsLine_LetsItInOverHTTP3()
        {

            TheGatewayTakesHTTP3();

            var id = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            await ACSMSThatConnects(CSMSName.ToString(), OneTimePass: true, Bound: true, HTTPVersion: "3");

            Assert.That(await UntilConnected(id), Is.True, $"{CSMSSays}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' connected to this gateway").And.Contain("bound to its").And.Contain("(security profile 2"));

        }

        #endregion

        #region AOneTimePasswordThatIsNotBound_IsNotLetIn_WhereTheBindingIsAsked()

        /// <summary>
        /// The same CSMS, its one-time password not bound: not let in, and the log says what it has to
        /// do.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordThatIsNotBound_IsNotLetIn_WhereTheBindingIsAsked()
        {

            TheGatewayTakesHTTP3();

            var id = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            await ACSMSThatConnects(CSMSName.ToString(), OneTimePass: true, Bound: false, HTTPVersion: "3");

            var never = await NeverConnected(id);

            Assert.Multiple(() => {
                Assert.That(never,  Is.True, "a one-time password that is not bound was let in");
                Assert.That(Log,    Does.Contain("has to open its WebSocket over HTTP/3 and bind its one-time password"));
            });

        }

        #endregion

        #region ABoundOneTimePassword_WhereNoHTTP3IsTaken_IsSaidToBeUnableToSignIn()

        /// <summary>
        /// Its one-time password bound to the TLS session, and the station port taking no WebSockets
        /// over HTTP/3: what the connection says is why it can not sign in, not that it is waited for.
        /// </summary>
        [Test]
        public void ABoundOneTimePassword_WhereNoHTTP3IsTaken_IsSaidToBeUnableToSignIn()
        {

            gateway.StationServer!.AcceptHTTP3 = false;

            var id    = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            SpinWait.SpinUntil(() => gateway.ConnectionStates.ContainsKey(id), ThroughWithin);

            var says  = gateway.ConnectionStates.TryGetValue(id, out var state) ? state.Said : "nothing";

            Assert.That(says, Does.Contain($"'{CSMSName}' can not sign in").And.Contain("takes no WebSockets over HTTP/3"));

        }

        #endregion

        #region ThroughASubCA_WhichItSendsAlong_ItIsLetIn()

        /// <summary>
        /// Its certificate comes from a sub-CA of its operator's authority, and the station port
        /// accepts the root of that authority alone: the CSMS sends the sub-CA beside its certificate,
        /// and the gateway builds the chain with it.
        /// </summary>
        /// <remarks>
        /// Another authority than the site's, which issued the certificate of the station port: a
        /// sub-CA the gateway's own TLS context put into the Windows store of intermediates would let
        /// a chain through that nobody sent. What a client sends along is put there on Windows as
        /// well, by .NET, and taken out again with the authority - see <see cref="TestCA"/>; there
        /// the test does not tell what was sent from what is kept, as it does on Linux.
        /// </remarks>
        [Test]
        public async Task ThroughASubCA_WhichItSendsAlong_ItIsLetIn()
        {

            var operatorCA = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            more.Add(operatorCA);

            var id = TheCSMSIsWrittenDown(ByCA: true, Operator: operatorCA);

            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(CSMSName.ToString(), operatorCA));

            Assert.That(await UntilConnected(id), Is.True, $"{CSMSSays}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' connected to this gateway").And.Contain("signed in with a certificate"));

        }

        #endregion

        #region ThroughASubCA_WhichTheGatewayKeeps_ItsCertificateAloneIsLetIn()

        /// <summary>
        /// The other way: the CSMS shows its certificate alone, and the station port keeps the sub-CA
        /// between it and the root with the chain it accepts. Not let in while it does not, and let in
        /// once it does - read at every handshake, without a restart.
        /// </summary>
        [Test]
        public async Task ThroughASubCA_WhichTheGatewayKeeps_ItsCertificateAloneIsLetIn()
        {

            var operatorCA = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            more.Add(operatorCA);

            var id = TheCSMSIsWrittenDown();

            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(CSMSName.ToString(), operatorCA, WithItsSubCA: false));

            Assert.That(await NeverConnected(id), Is.True, "its certificate alone was let in, though nobody told the gateway the sub-CA above it");

            Assert.That(gateway.ClientTrust.TryAdd(operatorCA.Intermediate!.ExportCertificatePem() + "\n" + operatorCA.Certificate.ExportCertificatePem(),
                                                   "The operator's authority, with its issuing CA", out _, out _, out var keptError),
                        Is.True, keptError);

            Assert.That(await UntilConnected(id), Is.True, $"{CSMSSays}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' connected to this gateway").And.Contain("signed in with a certificate"));

        }

        #endregion

        #region AnIssuingCAOfAnotherAuthority_OpensNothing(SentAlong)

        /// <summary>
        /// A sub-CA the gateway keeps in its store is believed nothing by the station port: a
        /// certificate of it, of an authority the station port does not accept, is not let in,
        /// whether it is shown alone or with the sub-CA.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public async Task AnIssuingCAOfAnotherAuthority_OpensNothing(Boolean SentAlong)
        {

            var operatorCA  = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            var another     = TestCA.Create($"Another CA {Guid.NewGuid()}",  WithIntermediate: true);
            more.Add(operatorCA);
            more.Add(another);

            var id = TheCSMSIsWrittenDown(ByCA: true, Operator: operatorCA);

            Assert.That(gateway.Certificates.Import(Encoding.ASCII.GetBytes(another.Intermediate!.ExportCertificatePem()),
                                                    CertificateKind.TLSIntermediate, null, "Somebody else's issuing CA", null,
                                                    out _, out var keptError),
                        Is.True, keptError);

            await ACSMSThatConnects(CSMSName.ToString(), Identity: AnIdentity(CSMSName.ToString(), another, WithItsSubCA: SentAlong));

            Assert.That(await NeverConnected(id), Is.True, "a certificate of an issuing CA of another authority was let in");

        }

        #endregion

        #region OverSSH_ItsKey_LetsItIn_AndNoOtherKeyDoes()

        /// <summary>
        /// Over SSH, the CSMS signs in to the gateway's SSH server with the SSH key of its WebSocket
        /// login, under the login its ssh:// connection names, and opens the subsystem of OCPP: let
        /// in as that connection, and not as a charging station. A key nobody gave it, under its name,
        /// is turned away, and it is waited for still.
        /// </summary>
        /// <remarks>
        /// Not one of the local controller's tests: it has the same door over SSH, tested by its
        /// stations, and no test of a CSMS coming through it. A plain SSH message client stands in for
        /// the CSMS, as it stands in for a station there.
        /// </remarks>
        [Test]
        public async Task OverSSH_ItsKey_LetsItIn_AndNoOtherKeyDoes()
        {

            var itsKey       = SshHostKey.GenerateEd25519();
            var strangersKey = SshHostKey.GenerateEd25519();

            Assert.That(gateway.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on, and not served");

            Assert.That(gateway.TryAddStationSSHKey(CSMSName, SshPublicKey.FromHostKey(itsKey).ToAuthorizedKeyLine(), "its SSH key", null,
                                                    I18NString.Create("What the CSMS signs in with"), "tests",
                                                    out _, out var error, out _),
                        Is.True, error);

            Assert.That(gateway.Connections.TryAddConnection(I18NString.Create("The CSMS, connecting over SSH"), URL.Parse($"ssh://{CSMSName}@127.0.0.1:{gateway.SSHPort}"), "CSMS", true, null, null,
                                                             out var id, out error,
                                                             Inbound: true),
                        Is.True, error);

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp && StatusOf(id!) != ConnectionStatus.Waiting)
                await Task.Delay(50);

            Assert.That(gateway.OutboundConnections.GetValueOrDefault(id!), Does.Contain($"Waiting for '{CSMSName}' to connect over SSH"));

            var (stranger, _) = await OverSSH(CSMSName, strangersKey);

            Assert.Multiple(() => {
                Assert.That(stranger,      Is.Null, "a key nobody gave it was let in under the name of the CSMS");
                Assert.That(StatusOf(id!), Is.EqualTo(ConnectionStatus.Waiting), "something that was turned away counted as the CSMS");
            });

            var (connection, why) = await OverSSH(CSMSName, itsKey);

            Assert.That(connection, Is.Not.Null, why);

            giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp && StatusOf(id!) != ConnectionStatus.Connected)
                await Task.Delay(50);

            Assert.Multiple(() => {
                Assert.That(StatusOf(id!),                Is.EqualTo(ConnectionStatus.Connected), $"{gateway.OutboundConnections.GetValueOrDefault(id!)}{Environment.NewLine}{Log}");
                Assert.That(connection!.IsAlive,          Is.True, "the CSMS was let go of");
                Assert.That(gateway.OutboundConnections.GetValueOrDefault(id!), Does.Contain($"'{CSMSName}' connected over SSH"));
                Assert.That(Log,                          Does.Not.Contain($"The charging station '{CSMSName}' signed in"), "the CSMS was let in as a charging station");
            });

            await connection!.Close(MessageCloseCode.NormalClosure, "The test is over");

        }

        /// <summary>
        /// Connect to the subsystem of OCPP of the gateway's SSH server as the given login with the
        /// given key: the connection, or why it was not let in.
        /// </summary>
        private async Task<(SshMessageConnection? Connection, String? Why)> OverSSH(User_Id      Login,
                                                                                  ISshHostKey  Key)
        {

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host          = "127.0.0.1",
                                                  Port          = gateway.SSHPort!.Value,
                                                  Ssh           = new SSHClientOptions {
                                                                      Username       = Login,
                                                                      VerifyHostKey  = blob => gateway.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                                      Credentials    = [ Key ]
                                                                  },
                                                  Subsystem     = OCPPSshServer.OCPPSubsystem,
                                                  Subprotocols  = [ "ocpp2.1" ]
                                              });

            try
            {
                return (await client.ConnectAsync().WaitAsync(ThroughWithin), null);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }

        }

        #endregion

    }

}

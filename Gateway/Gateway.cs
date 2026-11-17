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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;


using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.protocols.OCPP.Node;

using cloud.charging.open.Gateway.CommandLine;
using cloud.charging.open.Gateway.Configuration;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// One (OCPP) gateway: a WWCP node with its JSON API at "/api" and its
    /// web interface at "/".
    /// </summary>
    /// <remarks>
    /// The web interface is a bundle of HTML, CSS and JavaScript built by
    /// webpack from Frontend/ and embedded into this assembly, so that the
    /// gateway is one file to deploy and needs nothing installed beside it. The
    /// browser and the gateway talk over the JSON API and one Server-Sent
    /// Events stream; nothing is rendered on the server.
    ///
    /// A gateway is an OCPP node as a charging station and a local controller
    /// are: it dials its CSMS - one or several - over the connections written
    /// down on its Connections page, with the credentials and client
    /// identities they name, and follows, believes, lets go of and dials them
    /// again as they do, because that is WWCPNode's and the same for
    /// all three (see Gateway.Dialling.cs). Below that is what every program
    /// of this family has - the sign-in, the name servers, the time servers,
    /// the certificates and the log, and the JSON API that serves them - which
    /// is the WWCP node.
    ///
    /// And it is dialled, as a local controller is: by the charging stations
    /// and local controllers below it, on its station server - its section
    /// "ocppServer", the stations that may sign in and their login groups, the
    /// chains a station's certificate may lead to and the keys and
    /// certificates it presents, the same as a local controller's because they
    /// are OCPPServingNode's (see Gateway.Serving.cs) - and by a CSMS it
    /// cannot dial, on the same port, as a connection the other end makes.
    ///
    /// Not here yet: what a gateway is for, handing messages on from one side
    /// to the other.
    /// </remarks>
    public partial class Gateway : OCPPServingNode
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of Gateway.csproj).
        /// </summary>
        public const            String    HTTPRoot         = "cloud.charging.open.Gateway.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Beside the ports of its siblings - the vehicle's 2347, the
        /// station's 2348, the local controller's 2350 and the CSMS's 2351 - so
        /// that a gateway started on the same bench as the things it sits
        /// between does not fight any of them over a port. The node below has a
        /// port of its own for a node of no particular kind, and this one is
        /// handed to it rather than left to it.
        /// </remarks>
        public static new readonly  IPPort    DefaultHTTPPort  = IPPort.Parse(2353);

        /// <summary>
        /// What kind of node a gateway is.
        /// </summary>
        /// <remarks>
        /// Every name as it was before the gateway was a node, and that is the
        /// point of spelling each of them out: "Gateway" after
        /// "OpenChargingCloud" in the Server header, "gateway-2026-09-25.log"
        /// for a day's log file, and "Gateway" as the organization its accounts
        /// are in - which a first start wrote into the accounts file and every
        /// start after it reads back, so it is the one that must never change.
        /// </remarks>
        public static readonly      NodeKind  GatewayKind      = new (
                                                                     Name:           "gateway",
                                                                     Tag:            "gateway",
                                                                     Product:        "Gateway",
                                                                     Organization:   "Gateway",
                                                                     LogFilePrefix:  "gateway"
                                                                 );

        /// <summary>
        /// The kinds of certificate a gateway keeps: the four of TLS, the
        /// identity its web interface shows over HTTPS, and none of the seven
        /// of ISO 15118, which are a vehicle's.
        /// </summary>
        /// <remarks>
        /// The roots a time server or a name server of this gateway may chain
        /// to where the machine knows no root of theirs, and the certificates
        /// they present, kept to hold them to by their fingerprints - and, for
        /// when the gateway speaks TLS itself, what it presents and what the
        /// clients connecting to it will have to chain to. A kind that is not
        /// here has no directory in the store, and an import of one is refused.
        ///
        /// The TLS server identity is what the web interface, the JSON API and
        /// the sign-in show where the "web" section says "tls": { "certificate":
        /// "store" }, or --https - kept from the start, so that
        /// --import-certificate tlsServerIdentity=&lt;file&gt; puts it in at the
        /// same start that asks for it.
        /// </remarks>
        public static readonly      IReadOnlyList<CertificateKind>  CertificateKinds  = [ .. CertificateKindExtensions.TLS, CertificateKind.TLSServerIdentity ];

        #endregion

        #region Properties

        /// <summary>
        /// The JSON API the browser talks to.
        /// </summary>
        public GatewayHTTPAPI     API   { get; }

        /// <summary>
        /// Who this gateway says it is when it speaks OCPP: the "ocpp" section
        /// of its configuration file, or what it was given.
        /// </summary>
        public OCPPConfiguration  OCPP  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One gateway, with its web interface.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this gateway sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this gateway's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts: one file, whose sections the node below reads.</param>
        /// <param name="DNSClient">How to resolve names, or null to make a client.</param>
        /// <param name="NTSClient">Where to read the time, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the certificate store lives in between starts; what the configuration file says, or "certificates" beside it, by default.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="TimeProvider">The clock, or null for the system one.</param>
        /// <param name="SSH">What the program says about serving the command line over SSH; nothing by default - see SSHSettings.</param>
        /// <param name="Web">What the program says of HTTPS and of the reverse proxies in front - --https, --https-cert, --redirect-port and --trusted-proxy - over the "web" section of the configuration file; nothing by default, and then the file alone says it.</param>
        /// <param name="OCPP">Who this gateway says it is when it speaks OCPP, where the "ocpp" section of the configuration file says nothing; see <see cref="OCPPConfiguration"/>.</param>
        public Gateway(IIPAddress?            HTTPHostname       = null,
                       IPPort?                HTTPPort           = null,
                       HTTPServer?            HTTPServer         = null,
                       HTTPPath?              BasePath           = null,
                       HTTPPath?              HTTPRootPath       = null,
                       HTTPExtAPI?            ExtAPI             = null,
                       String?                AccountsPath       = null,
                       WWCPConfigFile?        ConfigFile         = null,
                       DNSClient?             DNSClient          = null,
                       NTSClient?             NTSClient          = null,
                       IStaticContentSource?  Frontend           = null,
                       String?                CertificatesPath   = null,
                       EventLog?              Log                = null,
                       Boolean                LogToConsole       = true,
                       LogLevel               ConsoleLogLevel    = LogLevel.Info,
                       String?                LogPath            = null,
                       Boolean                BridgeDebugLog     = true,
                       TimeProvider?          TimeProvider       = null,
                       SSHSettings?           SSH                = null,
                       WebConfiguration?      Web                = null,
                       OCPPConfiguration?     OCPP               = null)

            : base(Kind:               GatewayKind,
                   Version:            typeof(Gateway).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:           HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:       HTTPHostname,
                   HTTPServer:         HTTPServer,
                   BasePath:           BasePath,
                   HTTPRootPath:       HTTPRootPath,
                   ExtAPI:             ExtAPI,
                   AccountsPath:       AccountsPath,
                   Resources:          GatewayAccess.Resources,
                   RoleDefinitions:    GatewayAccess.Roles,
                   ConfigFile:         ConfigFile,
                   DNSClient:          DNSClient,
                   NTSClient:          NTSClient,
                   Frontend:           Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(Gateway).Assembly),
                   CertificatesPath:   CertificatesPath,
                   CertificateKinds:   CertificateKinds,

                   // Beside "dns" and "nts": a TLS root may vouch for the
                   // servers of the OCPP connections and for nothing else -
                   // see WWCPNode.ServerPins.cs - as on a charging
                   // station.
                   CertificateUsages:  [ OCPPCertificateUsage ],

                   // Who the gateway is to a CSMS it dials may be made here: a
                   // key, a signing request, and the certificate that comes
                   // back for it. The key never leaves. And who its own web
                   // interface is, as a charging station and a local
                   // controller do it: a key made here need not be carried to
                   // the gateway.
                   CertificateRequestKinds: [ CertificateKind.TLSIdentity, CertificateKind.TLSServerIdentity ],

                   Log:                Log,
                   LogToConsole:       LogToConsole,
                   ConsoleLogLevel:    ConsoleLogLevel,
                   LogPath:            LogPath,
                   BridgeDebugLog:     BridgeDebugLog,
                   TimeProvider:       TimeProvider,
                   SSH:                SSH,

                   // HTTPS of the gateway's own, or the proxies in front
                   // believed: with --any the sign-in would otherwise cross the
                   // network in the clear - see the node's "web" section.
                   Web:                Web)

        {

            #region Who this gateway says it is

            // Its own section of the document the node below has already read.
            // A section that is wrong stops the gateway here: somebody wrote down
            // who it is and got it wrong, and quietly running as somebody else
            // would be worse than stopping.
            if (!OCPPConfiguration.TryRead(ConfigurationDocument, out var ocpp, out var problem))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {problem} Repair or remove '{this.ConfigFile.Path}' and start again.");

            this.OCPP = ocpp.IsEmpty
                            ? OCPP ?? ocpp
                            : ocpp;

            #endregion

            #region What it dials, and the OCPP node it dials with

            // Before anything asks for them: the HTTP API below reads the
            // connections. Nothing is dialled yet: Start() does.
            BuildConnectionStores();

            gw01 = BuildOCPPNode(this.OCPP);

            // "this." and not for tidiness: the parameters of this constructor
            // shadow the properties of the same name, and the "Log" parameter is
            // null whenever the caller did not bring an event log of its own.
            this.Log.Info($"OCPP 2.1 gateway '{gw01.Id}' is set up as {gw01.VendorName} {gw01.Model}; " +
                          $"{Connections.Connections.Count(connection => connection.AutoConnect)} of its {Connections.Connections.Count} connections are dialled by themselves.",
                          "ocpp");

            #endregion

            #region The server the stations below it connect to

            // After the node, because it is attached to it, and after the
            // configuration file, because what it listens on is written there.
            // Nothing listens yet: Start() does.
            BuildStationServer();

            #endregion

            #region The JSON API

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this gateway are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This gateway signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // The JSON API at "/api", beside the web interface the node below
            // has already put at "/". The more specific of the two, so that an
            // unknown /api path never reaches the single-page-application
            // stub.
            this.API = new GatewayHTTPAPI(
                           HTTPServer:  this.HTTPServer,
                           Gateway:     this,
                           ExtAPI:      this.ExtAPI,
                           Log:         this.Log,
                           APIPath:     this.HTTPRootPath,
                           Version:     Version
                       );

            #endregion

            #region What somebody signed in over SSH gets

            // A gateway's command line, with its commands beside the node's -
            // whichever program runs this gateway, and not only GatewayCLI.
            // Before the node starts, as the node asks; a program with a
            // command line of its own sets another after this.
            CommandLines = (terminal, caller) => new GatewayCLI(this, terminal, caller);

            #endregion

        }

        #endregion


        #region (protected override) OnStarted()

        /// <summary>
        /// What a gateway says and does once it is up: where its API is, and
        /// then its CSMS.
        /// </summary>
        /// <remarks>
        /// After the web interface, so that a gateway whose CSMS is unreachable
        /// is still a gateway whoever is watching the Logs page sees dialling
        /// out - and the page where the address that was wrong is put right. A
        /// CSMS that does not answer is logged and shown, not fatal.
        /// </remarks>
        protected override async Task OnStarted()
        {

            Log.Info($"The JSON API is at {APIURL}v1/status", "web", "http");

            await DialConfiguredConnections();

        }

        #endregion

        #region (protected override) OnListening()

        /// <summary>
        /// The port the charging stations connect to, beside the web
        /// interface's - taken once the node below has its own port and before
        /// it calls itself started, so that a gateway whose stations cannot
        /// connect does not say it is up.
        /// </summary>
        protected override Task OnListening()

            => StartOCPPServer();

        #endregion

        #region (protected override) OnStopping()

        /// <summary>
        /// Hang up what this gateway dialled, and then close the port of the
        /// stations below it - a station still connected has somewhere for its
        /// last messages to go while the CSMS is let go of.
        /// </summary>
        protected override async Task OnStopping()
        {

            await HangUp();

            await StopOCPPServer();

        }

        #endregion


        #region ConfigurationJSON()

        /// <summary>
        /// What this gateway is, as the Configuration page of the web interface
        /// reads it: what the node below says of itself - the assemblies it was
        /// built from last, one line per repository - and on top the gateway.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            // First, because it is the card the page leads with.
            json.AddFirst(new JProperty("gateway",    new JObject(
                              new JProperty("version",        Version),
                              new JProperty("createdAt",      CreatedAt.ToString("o")),
                              new JProperty("machine",        Environment.MachineName),
                              new JProperty("runtime",        Environment.Version.ToString()),
                              new JProperty("os",             Environment.OSVersion.ToString())
                          )));

            // Who it is in OCPP, as a local controller says it of itself - in
            // the order the page draws it, before the assemblies.
            var ocpp = new JProperty("ocpp",     new JObject(
                         new JProperty("version",          "2.1"),
                         new JProperty("role",             "Gateway"),
                         new JProperty("id",               gw01.Id.ToString()),
                         new JProperty("vendor",           gw01.VendorName),
                         new JProperty("model",            gw01.Model),
                         new JProperty("serialNumber",     gw01.SerialNumber),
                         new JProperty("softwareVersion",  gw01.SoftwareVersion),
                         new JProperty("file",             ConfigFile.Path)
                     ));

            // And the server the stations below it connect to, as a local
            // controller says it.
            var stationServer = new JProperty("stationServer", StationServerJSON());

            if (json.Property("assemblies") is JProperty assemblies)
            {
                assemblies.AddBeforeSelf(ocpp);
                assemblies.AddBeforeSelf(stationServer);
            }
            else
            {
                json.Add(ocpp);
                json.Add(stationServer);
            }

            return json;

        }

        #endregion

    }

}

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

using cloud.charging.open.Gateway.Web;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

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
    /// What a gateway is for - taking WebSocket frames from one side and
    /// handing them on to the other - is not here yet. What is here is what
    /// every program of this family has before it does anything of its own -
    /// the sign-in, the name servers, the time servers and the log - and that
    /// is the node below, which the gateway shares with the vehicle. What is
    /// the gateway's own is its names, its port, its roles and its JSON API.
    /// </remarks>
    public class Gateway : WWCPNode
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

        #endregion

        #region Properties

        /// <summary>
        /// The JSON API the browser talks to.
        /// </summary>
        public GatewayHTTPAPI  API  { get; }

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
        /// <param name="CertificatesPath">The directory the certificate store of the node below lives in between starts.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="TimeProvider">The clock, or null for the system one.</param>
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
                       TimeProvider?          TimeProvider       = null)

            : base(Kind:              GatewayKind,
                   Version:           typeof(Gateway).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   Roles:             GatewayRoles.All.Select(role => role.Name),
                   HTTPPort:          HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:      HTTPHostname,
                   HTTPServer:        HTTPServer,
                   BasePath:          BasePath,
                   HTTPRootPath:      HTTPRootPath,
                   ExtAPI:            ExtAPI,
                   AccountsPath:      AccountsPath,
                   ConfigFile:        ConfigFile,
                   DNSClient:         DNSClient,
                   NTSClient:         NTSClient,
                   Frontend:          Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(Gateway).Assembly),
                   CertificatesPath:  CertificatesPath,
                   Log:               Log,
                   LogToConsole:      LogToConsole,
                   ConsoleLogLevel:   ConsoleLogLevel,
                   LogPath:           LogPath,
                   BridgeDebugLog:    BridgeDebugLog,
                   TimeProvider:      TimeProvider)

        {

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

        }

        #endregion


        #region (protected override) OnStarted()

        /// <summary>
        /// What a gateway says once it is up: where its API is.
        /// </summary>
        protected override Task OnStarted()
        {

            Log.Info($"The JSON API is at {APIURL}v1/status", "web", "http");

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        /// <summary>
        /// End the event streams before the server stops.
        /// </summary>
        /// <remarks>
        /// Every browser with the Logs page open holds a request that is
        /// waiting for the next log entry rather than for its socket, and the
        /// HTTP server waits for every request it started. Closing the sockets
        /// does not wake those, so they are ended here first - whoever owns the
        /// server, because the streams are this gateway's.
        /// </remarks>
        protected override Task OnStopping()
        {

            API.CloseEventStreams();

            return Task.CompletedTask;

        }

        #endregion


        #region ConfigurationJSON()

        /// <summary>
        /// What this gateway is, as the Configuration page of the web interface
        /// reads it: what the node below says of itself, and on top the
        /// gateway and the assemblies it was built from.
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

            json.Add(new JProperty("assemblies", new JArray(
                         BuiltFrom.Assemblies.Select(AssemblyJSON)
                     )));

            return json;

        }

        #endregion

        #region (private static) AssemblyJSON(Assembly)

        /// <summary>
        /// One library of this gateway, as the Configuration page reads it.
        /// </summary>
        /// <remarks>
        /// Nothing is named here any more. What used to be four hand-written
        /// lines is whatever BuiltFrom finds loaded, so a library that joins
        /// this gateway appears by itself and one that leaves stops being
        /// claimed - which a hand-written list never manages for long.
        ///
        /// The label is the repository where there is one, because that is what
        /// somebody looking at a bug report can check out; the assembly's own
        /// name stays beside it for the libraries that carry no stamp yet.
        /// </remarks>
        private static JObject AssemblyJSON(LoadedAssembly Assembly)
        {

            var json = new JObject(
                           new JProperty("name",      Assembly.Repository ?? Assembly.Name),
                           new JProperty("assembly",  Assembly.Name),
                           new JProperty("version",   Assembly.Version)
                       );

            // Only when it is known: an empty commit in a bug report reads like
            // an answer, and it is not one.
            if (Assembly.Commit is not null)
                json.Add(new JProperty("commit", Assembly.Commit));

            return json;

        }

        #endregion

    }

}

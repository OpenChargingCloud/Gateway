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

using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using NullMailer = org.GraphDefined.Vanaheimr.Hermod.SMTP.NullMailer;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;


using cloud.charging.open.Gateway.Configuration;
using cloud.charging.open.Gateway.Logging;
using cloud.charging.open.Gateway.Web;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// One (OCPP) gateway: the HTTP server in front of it, the JSON API at
    /// "/api" and the web interface at "/".
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
    /// every program of this family has before it does anything of its own:
    /// the sign-in, the name servers, the time servers and the log.
    /// </remarks>
    public partial class Gateway : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of Gateway.csproj).
        /// </summary>
        public const String  HTTPRoot            = "cloud.charging.open.Gateway.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Beside the ports of its siblings - the vehicle's 2347, the
        /// station's 2348, the local controller's 2350 and the CSMS's 2351 - so
        /// that a gateway started on the same bench as the things it sits
        /// between does not fight any of them over a port.
        /// </remarks>
        public static readonly IPPort DefaultHTTPPort = IPPort.Parse(2353);

        /// <summary>
        /// Where the accounts live, unless another directory is given.
        /// </summary>
        public const String  DefaultAccountsPath          = "accounts";

        /// <summary>
        /// Where the log files go, unless another directory is given.
        /// </summary>
        public const String  DefaultLogPath               = "logs";

        /// <summary>
        /// The accounts themselves, inside that directory.
        /// </summary>
        public const String  DefaultAccountsDatabaseFile  = "users.db";

        /// <summary>
        /// Where the HTTPExt API answers: accounts, groups and API keys.
        /// </summary>
        /// <remarks>
        /// Beside "/api" rather than under it, because it is not this
        /// gateway's API: it is Hermod's, with its own routes and its own
        /// vocabulary, and putting it under /api/v1 would promise that this
        /// gateway versions it.
        /// </remarks>
        public static readonly HTTPPath  ExtAPIPath        = HTTPPath.Parse("/ext");

        /// <summary>
        /// The account made at a first start.
        /// </summary>
        public const String  DefaultAdminUser             = "root";

        /// <summary>
        /// The organization that account belongs to.
        /// </summary>
        /// <remarks>
        /// A gateway has no organizations to speak of, and this one exists
        /// because the HTTPExt API's sign-in refuses an account that is in
        /// none - "You do not have access to any organization!" - however
        /// right its password is. So there is exactly one, named after the
        /// thing it stands for.
        /// </remarks>
        public const String  DefaultOrganization          = "Gateway";

        /// <summary>
        /// The file of the bundle that is the web interface; its presence is
        /// what says there is one to serve at all.
        /// </summary>
        public const String  IndexFile           = "index.html";

        /// <summary>
        /// The icon of the bundle, which /favicon.ico is pointed at.
        /// </summary>
        public const String  FaviconSVG          = "favicon.svg";

        private readonly  DNSClient                       dnsClient;
        private           NTSClient                       ntsClient;

        /// <summary>
        /// Every time server of this gateway, and the rules for believing them.
        /// </summary>
        /// <remarks>
        /// Beside the single client rather than instead of it, because the two
        /// answer different questions. The group answers "what is the time",
        /// which several servers should agree on before a clock moves. The
        /// client answers "what is that one server doing", which is what the
        /// detailed test on the page asks and which a group would only blur.
        /// </remarks>
        private           TimeSourceGroup                 timeSources;

        /// <summary>
        /// How many of those servers this gateway was told must answer: by the
        /// last section that named "minServers", or the default of two.
        /// </summary>
        /// <remarks>
        /// Kept apart from the group's own quorum, which cannot be more than the
        /// servers it has switched on. A lone hostname holds a group to one, and
        /// if that one were all that was remembered, a list of four arriving
        /// afterwards would be held to one as well - where the same file, read
        /// at the next start, holds it to two.
        /// </remarks>
        private           Byte                            ntsQuorum       = NTSConfiguration.DefaultMinServers;

        /// <summary>
        /// What actually asks the servers of a group.
        /// </summary>
        /// <remarks>
        /// One engine for the life of this gateway, and that is not tidiness: it
        /// holds the key exchange of each server between rounds, and a new
        /// engine per synchronisation would pay a TLS handshake to every server
        /// every time and throw the cookies away unspent. It refreshes an
        /// exchange when it is older than half an hour or down to its last
        /// cookie, which is the same discipline the single client follows.
        /// </remarks>
        private readonly  MeasurementEngine               timeEngine;

        /// <summary>
        /// The name servers this gateway would ask, whether or not name
        /// resolution is switched on at the moment.
        /// </summary>
        /// <remarks>
        /// Kept beside the DNS client because switching name resolution off is
        /// done by taking its servers away - which is what being switched off
        /// actually means, for everything holding that client and not only for
        /// the parts of this gateway that remember to ask first. Switching it
        /// back on needs the list back, and this is where it waited.
        /// </remarks>
        private           IReadOnlyList<DNSServerConfig>  configuredDNSServers;

        /// <summary>
        /// Serialises changes to what this gateway is, so that two browsers
        /// saving at the same moment do not build half a gateway each.
        /// </summary>
        private readonly  SemaphoreSlim                   reconfigureLock = new (1, 1);


        private readonly  HTTPServer                      httpServer;
        private readonly  HTTPPath                        httpRootPath;

        /// <summary>
        /// When this gateway last managed to check its clock, what it found,
        /// and against whom.
        /// </summary>
        /// <remarks>
        /// Three fields rather than one object because they are written from
        /// one place and read from another, and the alternative - digging them
        /// back out of the JSON of the last check - would make the page depend
        /// on the shape of a diagnostic.
        /// </remarks>
        private           DateTimeOffset?                 lastTimeCheck;
        private           TimeSpan?                       lastTimeCheckOffset;
        private           String?                         lastTimeCheckServer;

        /// <summary>
        /// The clock that makes this gateway check its own, when NTS is on.
        /// </summary>
        private           ITimer?                         timeCheckTimer;

        /// <summary>
        /// What the file said about the time client, kept because the parts of
        /// it that are not the client itself - how often to check, and what the
        /// operator claims about the server - are read long afterwards.
        /// </summary>
        private           NTSConfiguration?               ntsSettings;


        private readonly  ConsoleLog?                     consoleLog;
        private readonly  FileLog?                        fileLog;
        private readonly  TraceBridge?                    traceBridge;

        private           Boolean                         started;

        #endregion

        #region Properties

        /// <summary>
        /// Everything that happens inside this gateway.
        /// </summary>
        public EventLog               Log                          { get; }

        /// <summary>
        /// Who may open the web interface: the accounts, the groups they are
        /// in, and the sessions and API keys they hold.
        /// </summary>
        public HTTPExtAPI             ExtAPI                       { get; }

        /// <summary>
        /// Whether those accounts are this gateway's own, or somebody else's
        /// that it was handed.
        /// </summary>
        /// <remarks>
        /// It decides two things. What is shut down when this gateway is: an
        /// HTTPExt API handed in outlives it, and disposing of somebody else's
        /// would take the sign-in away from whoever else is using it. And what
        /// this gateway may say about the accounts on its Configuration page -
        /// shared accounts are not its to describe as "the accounts of this
        /// gateway".
        /// </remarks>
        public Boolean                OwnsExtAPI                   { get; }

        /// <summary>
        /// Whether the HTTP server is this gateway's own, or one it was handed
        /// and shares with somebody else.
        /// </summary>
        /// <remarks>
        /// A shared server is started and stopped by whoever made it. A gateway
        /// that started one it did not make would take the same socket twice
        /// where several of these programs are on it, and a gateway that
        /// stopped one would close the web interface of every other program
        /// registered within it.
        /// </remarks>
        public Boolean                OwnsHTTPServer               { get; }

        /// <summary>
        /// Everything of this gateway - its web interface, its JSON API and,
        /// where the accounts are its own, those too - sits below this.
        /// </summary>
        /// <remarks>
        /// The root, which is what a gateway on a port of its own wants and
        /// what it always used to be. It is something else only where several
        /// of these programs share one HTTP server and are told apart by the
        /// first path segment rather than by the port.
        /// </remarks>
        public HTTPPath               BasePath                     { get; }

        /// <summary>
        /// The base path as it is written into a URL: the empty string at the
        /// root, and "/Gateway" or the like below one.
        /// </summary>
        /// <remarks>
        /// Its own property because the two forms are not interchangeable and
        /// the difference is exactly one character: <c>HTTPPath.Root</c> writes
        /// itself as "/", and "/" + "/index.html" is a URL nothing serves.
        /// </remarks>
        public String                 BasePathText
            => BasePath == HTTPPath.Root
                   ? ""
                   : BasePath.ToString().TrimEnd('/');

        /// <summary>
        /// The directory the accounts live in between starts.
        /// </summary>
        public String                 AccountsPath                 { get; }

        /// <summary>
        /// Where everything this gateway can be told in writing lives between
        /// starts: its name resolution and its time sources.
        /// </summary>
        public GatewayConfigFile      ConfigFile                   { get; }

        /// <summary>
        /// The password made up at a first start and shown once, or null when
        /// accounts were already there.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="Start"/> rather than by the constructor, because
        /// creating the account is asynchronous and a constructor that waited
        /// on it would be a constructor that can deadlock.
        /// </remarks>
        public String?                GeneratedPassword            { get; private set; }

        /// <summary>
        /// The clock this gateway reads. Its own, not the one NTS reports.
        /// </summary>
        public TimeProvider           TimeProvider                 { get; }

        /// <summary>
        /// When this gateway was made.
        /// </summary>
        public DateTimeOffset         CreatedAt                    { get; }

        /// <summary>
        /// The version of this assembly.
        /// </summary>
        public String                 Version                      { get; }

        /// <summary>
        /// How this gateway resolves names.
        /// </summary>
        public DNSClient              DNSClient                    => dnsClient;

        /// <summary>
        /// Where this gateway reads the time.
        /// </summary>
        public NTSClient              NTSClient                    => ntsClient;

        /// <summary>
        /// The time servers of this gateway, as a group.
        /// </summary>
        public TimeSourceGroup        TimeSources                  => timeSources;

        /// <summary>
        /// Whether this gateway resolves names at all.
        /// </summary>
        public Boolean                DNSEnabled                   { get; private set; } = true;

        /// <summary>
        /// Whether this gateway asks a time server at all.
        /// </summary>
        public Boolean                NTSEnabled                   { get; private set; } = true;

        /// <summary>
        /// Where the web interface comes from: the bundle embedded in this
        /// assembly, or a directory somebody pointed this gateway at.
        /// </summary>
        public IStaticContentSource   Frontend                     { get; }

        /// <summary>
        /// The JSON API the browser talks to.
        /// </summary>
        public GatewayHTTPAPI         API                          { get; }

        /// <summary>
        /// The web interface at "/", or null when there is no bundle to serve.
        /// </summary>
        public HTTPAPI?               WebInterface                 { get; }

        /// <summary>
        /// The TCP port the web interface listens on.
        /// </summary>
        public IPPort                 HTTPPort                     { get; }

        /// <summary>
        /// Where to point a browser.
        /// </summary>
        public URL                    WebInterfaceURL              { get; }

        /// <summary>
        /// The JSON API as a browser would type it: the server and the API's
        /// root path, which already carries the base path, with a slash at the end.
        /// </summary>
        public URL                    APIURL                       { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One gateway, with its web interface.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; DefaultHTTPPort by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this gateway sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this gateway's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts.</param>
        /// <param name="DNSClient">How to resolve names, or null to make a client.</param>
        /// <param name="NTSClient">Where to read the time, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="TimeProvider">The clock, or null for the system one.</param>
        public Gateway(IIPAddress?            HTTPHostname      = null,
                       IPPort?                HTTPPort          = null,
                       HTTPServer?            HTTPServer        = null,
                       HTTPPath?              BasePath          = null,
                       HTTPPath?              HTTPRootPath      = null,
                       HTTPExtAPI?            ExtAPI            = null,
                       String?                AccountsPath      = null,
                       GatewayConfigFile?     ConfigFile        = null,
                       DNSClient?             DNSClient         = null,
                       NTSClient?             NTSClient         = null,
                       IStaticContentSource?  Frontend          = null,
                       EventLog?              Log               = null,
                       Boolean                LogToConsole      = true,
                       LogLevel               ConsoleLogLevel   = LogLevel.Info,
                       String?                LogPath           = null,
                       Boolean                BridgeDebugLog    = true,
                       TimeProvider?          TimeProvider      = null)
        {

            #region The clock, before anything that wants to know the time

            // First of all, and not for tidiness: the event log below stamps
            // every entry with this, so a clock set afterwards would leave the
            // log reading the system one - and a log on a different clock than
            // the gateway it belongs to cannot be held against anything.
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.CreatedAt     = this.TimeProvider.GetUtcNow();

            #endregion

            #region The log, next - everything below it may want to say something

            this.Version      = typeof(Gateway).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            this.Log          = Log ?? new EventLog(TimeProvider: this.TimeProvider);

            this.consoleLog   = LogToConsole
                                    ? new ConsoleLog(this.Log, ConsoleLogLevel)
                                    : null;

            // Everything, and not what the console was told to show: a level
            // is chosen to keep a console readable, and a file nobody is
            // reading has no such problem. What is left out here cannot be
            // asked for afterwards.
            this.fileLog      = LogPath is not null
                                    ? new FileLog(this.Log, LogPath)
                                    : null;

            // Attached before anything else is built, so that what the DNS
            // client and the HTTP server say while they are being made is
            // already in the log a browser will see later.
            this.traceBridge  = BridgeDebugLog
                                    ? TraceBridge.Attach(this.Log)
                                    : null;

            this.Log.Notice($"Gateway v{this.Version} starting up.", "gateway");

            #endregion

            #region Where the accounts live

            // Ending in a separator, because the HTTPExt API builds the paths
            // of its files by putting strings together rather than with
            // Path.Combine: a directory that does not end in one would give it
            // "...accountsUsersAPI" and not "...accounts/UsersAPI".
            this.AccountsPath = AccountsPath ?? DefaultAccountsPath;

            if (!this.AccountsPath.EndsWith(Path.DirectorySeparatorChar))
                this.AccountsPath += Path.DirectorySeparatorChar;

            #endregion

            #region What the configuration file says

            this.ConfigFile = ConfigFile ?? new GatewayConfigFile(GatewayConfigFile.DefaultFileName);

            GatewayConfiguration? configuration = null;

            if (this.ConfigFile.Exists)
            {

                // A file that is there but cannot be read is not something to
                // paper over with defaults: somebody wrote down what their
                // gateway is and got it wrong, and quietly running as something
                // else instead would be worse than stopping.
                if (!this.ConfigFile.TryLoad(out configuration, out var configError))
                    throw new InvalidOperationException($"{configError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                this.Log.Info($"Configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            }

            #endregion

            #region The clients everything below shares

            this.dnsClient             = DNSClient ?? new DNSClient();
            this.configuredDNSServers  = [.. dnsClient.DNSServers];

            // The clock goes to the time client too: a gateway that reads one
            // clock itself and disciplines another would have two, which is one
            // more than a gateway may have.
            this.ntsClient     = NTSClient ?? new NTSClient(
                                                  DomainName.Parse(NTSConfiguration.DefaultHostname),
                                                  Timeout:       TimeSpan.FromSeconds(10),
                                                  DNSClient:     dnsClient,
                                                  TimeProvider:  this.TimeProvider
                                              );

            this.timeEngine    = new MeasurementEngine(
                                     new MonitoringConfig {
                                         DroneId       = "gateway",
                                         NTPTimeout    = TimeSpan.FromSeconds(5),
                                         NTSKETimeout  = TimeSpan.FromSeconds(10)
                                     },
                                     this.TimeProvider
                                 );

            // The four this gateway asks when nobody says otherwise - but only
            // when nobody handed it a client either. A caller that named its
            // own server means that server, and a group naming four others
            // beside it would be a report about somebody else's clock.
            this.timeSources   = NTSClient is null
                                     ? NTSConfiguration.DefaultGroup()
                                     : new TimeSourceGroup(
                                           "legal",
                                           [ new NTSServerEndpoint(
                                                 ntsClient.Hostname,
                                                 ntsClient.NTSKE_Port,
                                                 ntsClient.NTP_Port
                                             ) ]
                                       );

            // Last, and that is the whole precedence rule: what this
            // constructor was handed holds until the file says otherwise, and
            // what the file does not mention is left exactly as it was.
            if (configuration?.DNS is not null)
                ApplyDNSConfiguration(configuration.DNS);

            if (configuration?.NTS is not null)
            {

                // Checked here rather than when the file was read: a quorum
                // on its own is about the servers in effect, and which those
                // are is only known now.
                if (!TryCheckNTSQuorum(configuration.NTS, out var quorumError))
                    throw new InvalidOperationException($"{quorumError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                ApplyNTSConfiguration(configuration.NTS);

            }

            this.ntsSettings = configuration?.NTS;

            #endregion

            #region The HTTP server, the JSON API and the web interface

            var address        = HTTPHostname ?? IPv4Address.Localhost;
            var port           = HTTPPort     ?? DefaultHTTPPort;

            this.OwnsHTTPServer = HTTPServer is null;

            this.httpServer    = HTTPServer   ?? new HTTPServer(
                                                     IPAddress:       address,
                                                     TCPPort:         port,
                                                     HTTPServerName:  $"OpenChargingCloud Gateway v{Version}",
                                                     DNSClient:       dnsClient
                                                 );

            // The root unless somebody is putting several of these programs on
            // one server, where the first path segment is what tells them
            // apart. Everything below is relative to it, which is the whole
            // reason it is settled here and read rather than repeated.
            this.BasePath      = BasePath     ?? HTTPPath.Root;

            this.httpRootPath  = HTTPRootPath ?? this.BasePath + GatewayHTTPAPI.DefaultAPIPath;

            this.HTTPPort        = port;
            this.WebInterfaceURL = URL.Parse($"http://{address}:{port}{this.BasePath.ToString().TrimEnd('/')}/");

            // From the server rather than from the web interface's URL: the API's
            // root path already carries the base path, and behind a URL that ends
            // in the base path it would be named twice.
            this.APIURL          = URL.Parse($"http://{address}:{port}/{this.httpRootPath.ToString().Trim('/')}/");

            // 1) The HTTPExt API at "/ext". First of the three, because it is
            //    the one with a database behind it: whatever it finds wrong
            //    with its files, it should say so before a port is opened and
            //    before anybody is let in against accounts that were not read.
            //
            //    Or the one that was handed in, which is how several of these
            //    programs come to have one sign-in between them: the groups
            //    each of them makes as it starts land in one set of accounts,
            //    and the names overlap on purpose - an account in
            //    "systemadmin" is an administrator of every one of them.
            this.OwnsExtAPI    = ExtAPI is null;

            this.ExtAPI        = ExtAPI ?? new HTTPExtAPI(
                                     HTTPServer:             httpServer,
                                     RootPath:               this.BasePath + ExtAPIPath,
                                     HTTPServerName:         $"OpenChargingCloud Gateway v{Version}",
                                     HTTPServiceName:        $"OpenChargingCloud Gateway v{Version}",
                                     APIRobotEMailAddress:   EMailAddress.Parse("OpenChargingCloud Gateway Robot <robot@charging.cloud>"),
                                     APIRobotGPGPassphrase:  "",

                                     // Nothing here sends mail. A gateway that
                                     // notifies by e-mail is told so by whoever
                                     // runs it, with a submission client of
                                     // their own; until then a mailer that
                                     // swallows what it is given is better than
                                     // one that quietly retries against a host
                                     // nobody configured.
                                     SMTPSubmissionClient:   new NullMailer(),
                                     DisableNotifications:   true,

                                     // The cookie has to reach "/api", and its
                                     // path would otherwise be the root path of
                                     // this API - "/ext" - so a browser signed
                                     // in at /ext/login would send nothing to
                                     // the API and look signed out everywhere
                                     // else.
                                     HTTPCookiePath:         "/",

                                     // A secure cookie is dropped by a browser
                                     // over plain HTTP, and a gateway on a
                                     // bench is reached over plain HTTP. Tied
                                     // to the TLS the server is actually using
                                     // rather than switched off: on a gateway
                                     // with a certificate this stays on.
                                     UseSecureCookies:       false,

                                     // The shortest name a role of this gateway has,
                                     // because that is what a group identification has
                                     // to be allowed to be. Hermod's own floor is four
                                     // characters, which every role here clears today -
                                     // and the day one does not, the group is refused by
                                     // a returned result rather than an exception, which
                                     // is a refusal nobody is obliged to notice, and the
                                     // role it carries can never be held by anybody. The
                                     // local controller's "cpo" is three.
                                     MinUserGroupIdLength:   (Byte) UserRole.All.Min(role => role.Name.Length),

                                     LoggingPath:            AccountsPath,
                                     DatabaseFileName:       DefaultAccountsDatabaseFile,

                                     // Left on, and that is what makes the
                                     // directory above: switching it off skips
                                     // the CreateDirectory that the accounts
                                     // file is written into, and the first
                                     // account created would fail on a path
                                     // that was never made.
                                     DisableLogging:         false
                                 );

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this gateway are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This gateway signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // 2) The JSON API at "/api". Before the web interface, so that it
            //    is the more specific API and an unknown /api path never
            //    reaches the single-page-application stub below.
            this.API           = new GatewayHTTPAPI(
                                     HTTPServer:  httpServer,
                                     Gateway:     this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     httpRootPath,
                                     Version:     Version
                                 );

            // 3) The web interface at "/": the files of the bundle, and the
            //    single-page-application stub for every other page URL, so
            //    that a reload on /logs and a bookmark to it both work.
            this.Frontend      = Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(Gateway).Assembly);

            if (this.Frontend.TryGet(IndexFile, out _))
            {

                this.WebInterface = httpServer.AddHTTPAPI(this.BasePath);

                this.WebInterface.MapSinglePageApplication(
                    this.Frontend,
                    new SinglePageAppOptions {

                        // Three placeholders and not one. The bundle reads
                        // where it is and where its API is out of <meta> tags
                        // rather than assuming "/" and "/api/v1", because
                        // under a base path both of those are wrong - and a
                        // single-page application that guesses its own base
                        // path is one that works until somebody mounts it
                        // somewhere.
                        IndexTransform = html => html.
                                                     Replace("{{ServerVersion}}", $"v{Version}",         StringComparison.Ordinal).
                                                     Replace("{{BasePath}}",      BasePathText,          StringComparison.Ordinal).
                                                     Replace("{{APIBase}}",       $"{httpRootPath.ToString().TrimEnd('/')}/v1", StringComparison.Ordinal).
                                                     Replace("{{ExtBase}}",       this.ExtAPI.RootPath.ToString().TrimEnd('/'), StringComparison.Ordinal)

                    }
                );

                // Browsers ask for /favicon.ico whatever the page says, and a
                // bundle built by webpack carries an SVG. A literal route wins
                // over the catch-all, so this answers before the stub would -
                // and beats a 404 on every visit, which is a line in the log
                // and a broken icon in the tab.
                if (this.Frontend.TryGet(FaviconSVG, out _))
                    this.WebInterface.AddHandler(
                        HTTPPath.Parse("/favicon.ico"),
                        request => Task.FromResult(
                                       new HTTPResponse.Builder(request) {
                                           HTTPStatusCode  = HTTPStatusCode.TemporaryRedirect,
                                           Location        = Location.From(HTTPPath.Parse($"{BasePathText}/{FaviconSVG}")),
                                           CacheControl    = "public, max-age=3600"
                                       }.AsImmutable
                                   ),
                        HTTPMethod.GET
                    );

            }

            else
                this.Log.Error(
                    $"No web interface to serve ({this.Frontend.Description}): the JSON API answers, the browser gets nothing. " +
                    "Build the frontend (npm run build in Frontend/) or point the gateway at a directory with --frontend.",
                    "web"
                );

            #region Every request, into the log

            httpServer.OnHTTPRequest  += (server, request, cancellationToken) => {

                // The event stream is one request that stays open for as long
                // as a browser has the page open; logging it would say nothing
                // and logging its response would say it at the wrong moment.
                if (!IsEventStream(request))
                    this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "http");

                return Task.CompletedTask;

            };

            // Only OnHTTPResponse, and not OnHTTPError beside it: Hermod raises
            // both for the same response, and one line per request is what a
            // log is for.
            httpServer.OnHTTPResponse += (server, request, response, cancellationToken) => {

                if (IsEventStream(request))
                    return Task.CompletedTask;

                var code = response.HTTPStatusCode.Code;

                this.Log.Log(
                    code >= 500 ? LogLevel.Error
                        // A 401 is how the web interface asks whether anybody
                        // is signed in, and the answer "nobody" is not a fault.
                        : code == 401 ? LogLevel.Debug
                        : code >= 400 ? LogLevel.Warning
                        : LogLevel.Debug,
                    $"{code} {response.HTTPStatusCode.Name} for {request.HTTPMethod} {request.Path}",
                    "http"
                );

                return Task.CompletedTask;

            };

            #endregion

            #endregion

        }

        #endregion


        #region Start()

        /// <summary>
        /// Start listening.
        /// </summary>
        public async Task Start()
        {

            if (started)
                return;

            // Before the port opens, and that order is the point: a web
            // interface reachable before its accounts exist is a door with
            // nobody behind it.
            await EnsureAccounts();

            if (OwnsHTTPServer)
            {
                try
                {
                    await httpServer.Start();
                }
                catch (SocketException problem)
                {
                    throw new PortUnavailableException(HTTPPort, problem);
                }
            }

            StartCheckingTheClock();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");
            Log.Info   ($"The JSON API is at {APIURL}v1/status", "web", "http");

        }

        #endregion

        #region Stop()

        /// <summary>
        /// Stop listening.
        /// </summary>
        public async Task Stop()
        {

            if (!started)
                return;

            Log.Notice("The gateway is shutting down.", "gateway");

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            // Before the server, and that order is the whole point: every
            // browser with the Logs page open holds a request that is waiting
            // for the next log entry rather than for its socket, and the HTTP
            // server waits for every request it started. Closing the sockets
            // does not wake those, so they are ended here first.
            API.CloseEventStreams();

            // The streams above are ended whoever owns the server, because they
            // are this gateway's; the socket is closed only where it is this
            // gateway's too.
            if (OwnsHTTPServer)
                await httpServer.Stop();

            started = false;

        }

        #endregion


        #region (private) EnsureAccounts()

        /// <summary>
        /// Make the four groups and, at a first start, the one account that is
        /// in the last of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The groups are made every start rather than only the first, because
        /// they are this gateway's vocabulary and not somebody's data: a group
        /// deleted by hand would otherwise leave a role that can never be held
        /// again, and the routes asking for it would refuse everybody with no
        /// way to put it right.
        /// </para>
        /// <para>
        /// The account is made only when there is none at all. Nobody can sign
        /// in to a web interface whose accounts are empty, and an
        /// unauthenticated setup page would be a door of its own - so the
        /// password is made up here and shown once, on the console, to whoever
        /// started the process. It is never written down: what the accounts
        /// hold is the hash the HTTPExt API makes of it.
        /// </para>
        /// <para>
        /// The membership edge is put on the group <em>before</em> the group is
        /// stored, so that both go to disk in one write. Adding it afterwards
        /// would leave a group that is right in memory and wrong in the file,
        /// and the next start would read the file.
        /// </para>
        /// </remarks>
        private async Task EnsureAccounts()
        {

            // Read what is on disk first. The HTTPExt API writes its accounts
            // as it goes but does not read them back when it is built, so a
            // gateway that skipped this would find no accounts at every start,
            // make a second root beside the first, and refuse the password its
            // owner already has.
            await ExtAPI.LoadDatabase();

            var firstStart  = !ExtAPI.Users.Any();

            IUser?  admin   = null;

            #region The one account, when there is none

            if (firstStart)
            {

                var password  = RandomExtensions.RandomString(24);
                var userId    = User_Id.Parse(DefaultAdminUser);

                // CreateUser rather than AddUser: the password is set from
                // inside the OnAdded callback, where the user already has its
                // API back-reference, and that is the only place the password
                // store can be reached. AddUser followed by ChangePassword
                // looks equivalent and writes the account without one - which
                // is an account nobody can sign in to, and nothing says so.
                var organization  = await ExtAPI.CreateOrganizationIfNotExists(
                                              Organization_Id.Parse(DefaultOrganization),
                                              I18NString.Create(Languages.en, DefaultOrganization)
                                          );

                if (organization is not Organization vehicleOrganization)
                    throw new InvalidOperationException("The organization of this gateway could not be created, and an account outside one cannot sign in.");

                admin         = await ExtAPI.CreateUser(
                                          userId,
                                          I18NString.Create(Languages.en, DefaultAdminUser),
                                          SimpleEMailAddress.Parse($"{DefaultAdminUser}@localhost"),
                                          User2OrganizationEdgeLabel.IsAdmin,
                                          vehicleOrganization,
                                          Password:                  password,

                                          // Nothing is sent and nobody is told:
                                          // a gateway has no mail server, no
                                          // second user to notify, and the one
                                          // account it makes is announced on the
                                          // console it was started from.
                                          SkipDefaultNotifications:  true,
                                          SkipNewUserEMail:          true,
                                          SkipNewUserNotifications:  true,

                                          // Without this nobody can sign in, and
                                          // nothing says why: the sign-in paths
                                          // require an accepted EULA and refuse a
                                          // correct password without one. There is
                                          // no agreement to show here - whoever
                                          // started the process owns the gateway -
                                          // so it is accepted at the moment the
                                          // account is made.
                                          AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),

                                          IsAuthenticated:           true
                                      );

                if (admin is null)
                    throw new InvalidOperationException("The account of this gateway could not be created, so nobody could sign in to it.");

                GeneratedPassword = password;

                Log.Notice($"No accounts were found, so '{DefaultAdminUser}' was made up and put in the {UserRole.SystemAdmin.Name} group.",
                           "web", "auth");

            }

            #endregion

            #region The four groups

            foreach (var role in UserRole.All)
            {

                if (ExtAPI.TryGetUserGroup(role.GroupId, out _))
                    continue;

                var added = await ExtAPI.AddUserGroup(
                                      new UserGroup(
                                          role.GroupId,
                                          I18NString.Create(Languages.en, role.Name)
                                      )
                                  );

                // Looked at, and that is the point: this answers with a result
                // rather than throwing, so a group it declined to make would
                // otherwise leave a role nobody can ever hold - and every route
                // asking for it refusing everybody, with nothing anywhere to
                // say why. Better to stop before the port opens.
                if (added.Result != CommandResult.Success)
                    throw new InvalidOperationException(
                              $"The user group '{role.GroupId}' of this gateway could not be made: " +
                              $"{added.Description.FirstText()} A role without its group is a role nobody can hold."
                          );

            }

            #endregion

            #region The one account joins the one group that can fix the rest

            // Through AddUserToUserGroup, which writes a command of its own.
            // Putting the edge on the group object before storing it looks
            // equivalent and is not: what AddUserGroup writes is the group,
            // and a group's stored form does not carry its members - so the
            // membership was there until the next start and gone after it,
            // which is the worst shape a permission can have.
            if (admin is not null)
            {

                if (!ExtAPI.TryGetUser     (admin.Id,                     out var storedAdmin) ||
                    !ExtAPI.TryGetUserGroup(UserRole.SystemAdmin.GroupId, out var adminGroup)  ||
                     storedAdmin is not User      user ||
                     adminGroup  is not UserGroup group)
                {
                    throw new InvalidOperationException(
                              $"The account of this gateway could not be put in the {UserRole.SystemAdmin.Name} group, " +
                               "so the one account it has would be allowed to do nothing at all."
                          );
                }

                var joined = await ExtAPI.AddUserToUserGroup(
                                       user,
                                       User2UserGroupEdgeLabel.IsAdmin,
                                       group
                                   );

                // Looked at for the same reason as the group above: this
                // answers with a result too, and a membership it declined to
                // write leaves the one account able to do nothing at all -
                // with a password about to be printed that opens nothing.
                // A different result type from AddUserGroup's, and so a
                // different question: IsSuccess rather than Result.
                if (!joined.IsSuccess)
                    throw new InvalidOperationException(
                              $"The account '{DefaultAdminUser}' could not be put in the {UserRole.SystemAdmin.Name} group: " +
                              $"{joined.ErrorDescription?.FirstText()} It would be able to do nothing at all."
                          );

            }

            #endregion

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this gateway is, as the Configuration page of the web
        /// interface reads it.
        /// </summary>
        /// <remarks>
        /// Read-only: it answers "what am I running", not "change it". Nothing
        /// here is a secret - the accounts appear as the path they live at and
        /// the route to sign in, and never as anything about a password.
        /// </remarks>
        public JObject ConfigurationJSON()

            => new (

                   new JProperty("gateway",    new JObject(
                       new JProperty("version",        Version),
                       new JProperty("createdAt",      CreatedAt.ToString("o")),
                       new JProperty("machine",        Environment.MachineName),
                       new JProperty("runtime",        Environment.Version.ToString()),
                       new JProperty("os",             Environment.OSVersion.ToString())
                   )),

                   new JProperty("http",       new JObject(
                       new JProperty("serverName",     httpServer.HTTPServerName),
                       new JProperty("url",            WebInterfaceURL.ToString()),
                       new JProperty("basePath",       BasePath.ToString()),
                       new JProperty("apiPath",        httpRootPath.ToString()),
                       new JProperty("sharedServer",   !OwnsHTTPServer),
                       new JProperty("running",        started),
                       new JProperty("frontend",       Frontend.Description),
                       new JProperty("webInterface",   WebInterface is not null)
                   )),

                   new JProperty("web",        new JObject(
                       new JProperty("accountsPath",   AccountsPath),
                       new JProperty("sharedAccounts", !OwnsExtAPI),
                       new JProperty("signInAt",       $"{ExtAPI.RootPath.ToString().TrimEnd('/')}/login"),
                       new JProperty("users",          ExtAPI.Users.     Count()),
                       new JProperty("groups",         ExtAPI.UserGroups.Count()),
                       new JProperty("cookie",         ExtAPI.SessionCookieName.ToString()),
                       new JProperty("maxLifetime",    ExtAPI.MaxSignInSessionLifetime.ToString())
                   )),

                   new JProperty("log",        new JObject(
                       new JProperty("capacity",       Log.Capacity),
                       new JProperty("entries",        Log.Count),
                       new JProperty("lastId",         Log.LastId),
                       new JProperty("debugBridge",    traceBridge is not null),
                       new JProperty("console",        consoleLog is not null),
                       new JProperty("tags",           new JArray(Log.KnownTags))
                   )),

                   // The group, which is what sets the clock. This card used to
                   // lead with "NTS" and the host of the single client the
                   // detailed test starts from - one server, above the four that
                   // are actually asked, and with its root dot - and it left out
                   // every server that was switched off. The servers are now
                   // named the way the log names them when they change.
                   //
                   // And the last synchronisation - the button's, the prompt's
                   // or the clock check's - when it happened and how it went,
                   // or nothing while there has been none.
                   new JProperty("time",       new JObject(
                       new JProperty("ntsEnabled",     NTSEnabled),
                       new JProperty("timeServers",    Described(timeSources)),
                       new JProperty("minServers",     timeSources.MinServers),
                       new JProperty("checkedEvery",   TimeCheckEvery.ToString()),
                       new JProperty("lastSync",       lastTimeSync?.Value<String>("at")),
                       new JProperty("lastSyncResult", LastSyncSaid(lastTimeSync)),
                       new JProperty("now",            TimeProvider.GetUtcNow().ToString("o"))
                   )),

                   new JProperty("assemblies", new JArray(
                       BuiltFrom.Assemblies.Select(AssemblyJSON)
                   ))

               );

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

        #region (private static) IsEventStream(Request)

        /// <summary>
        /// Whether this request is a browser hanging on the event stream.
        /// </summary>
        private static Boolean IsEventStream(HTTPRequest Request)
            => Request.Path.ToString().EndsWith("/events", StringComparison.Ordinal);

        #endregion

        #region ShareConsoleWith(WriteBlock)

        /// <summary>
        /// Let somebody else decide when this gateway's log may write on the
        /// console, because they are writing on it too.
        /// </summary>
        /// <remarks>
        /// A gateway at a console assumes the console is its own and writes an
        /// entry whenever one happens, from whichever thread it happened on.
        /// That assumption stops holding the moment somebody is typing a command
        /// on the same screen: an entry arriving mid-word puts half a log line
        /// into the middle of a half-typed command and ruins both.
        ///
        /// So the writing is handed over rather than suppressed. Whoever owns
        /// the line takes the entry, clears what is being typed, writes the
        /// entry as one piece and puts the line back. Nothing is lost and
        /// nothing is delayed, which is what makes this better than the obvious
        /// alternative of going quiet while a command line is open.
        ///
        /// Has no effect on a gateway whose log does not reach the console.
        /// </remarks>
        /// <param name="WriteBlock">Runs what it is given with the console to itself.</param>
        public void ShareConsoleWith(Action<Action> WriteBlock)
        {

            if (consoleLog is not null)
                consoleLog.WriteBlock = WriteBlock;

        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening and let go of the console and the debug bridge.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await Stop();

            traceBridge?.Dispose();
            consoleLog? .Dispose();
            fileLog?    .Dispose();

            reconfigureLock.Dispose();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}

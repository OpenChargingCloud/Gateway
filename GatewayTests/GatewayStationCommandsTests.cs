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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP.Node.Serving.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Logins;

using cloud.charging.open.Gateway.CommandLine;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// stationKeys and stationPins on a gateway: the commands of every node
    /// that serves charging stations, WWCP_OCPP_Node's, registered by the
    /// gateway's command line - there once, naming "this gateway", and asking
    /// over SSH for the gateway's "stations" as the pages of its station
    /// server do.
    /// </summary>
    /// <remarks>
    /// What the commands answer in every case is tested with the local
    /// controller, which had them first. What is tested here is what is the
    /// gateway's: that it has them, in its words, behind its resource.
    /// </remarks>
    [TestFixture]
    public class GatewayStationCommandsTests
    {

        #region Data

        private const String  NoTimeServers  = """{ "dns": { "enabled": false }, "nts": { "groups": { "legal": { "enabled": false } } } }""";

        private String                 directory  = "";
        private Gateway?               gateway;
        private readonly List<String>  said       = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-stations-cli-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);

            lock (said)
                said.Clear();

        }

        [TearDown]
        public async Task LetTheGatewayGo()
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


        #region (private) AGateway() / AStartedGateway()

        /// <summary>
        /// A gateway whose name and time clients are switched off, on a free
        /// port of the loopback - made, and not started: a command line needs
        /// the gateway, not its port. Started through
        /// TestPorts.StartedOnFreshPorts, it is made again where its port was
        /// taken, and the gateway is the last one.
        /// </summary>
        private Gateway AGateway()
        {

            var configuration = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(configuration, NoTimeServers);

            gateway = new Gateway(
                          HTTPPort:          TestPorts.Free(),
                          AccountsPath:      Path.Combine(directory, "accounts"),
                          ConfigFile:        new WWCPConfigFile(configuration),
                          CertificatesPath:  Path.Combine(directory, "certificates"),
                          LogToConsole:      false,
                          BridgeDebugLog:    false
                      );

            gateway.Log.OnLogged += entry => { lock (said) said.Add(entry.Message); };

            return gateway;

        }

        /// <summary>
        /// A gateway that is started, and so has its accounts.
        /// </summary>
        private Task<Gateway> AStartedGateway()

            => TestPorts.StartedOnFreshPorts(AGateway);

        #endregion

        #region (private) OverSSHAs(Name, Role)

        /// <summary>
        /// The gateway's command line as an account of the given name sees it
        /// over SSH: made for the purpose and put in the group of the given
        /// role.
        /// </summary>
        private async Task<NodeCLI> OverSSHAs(User_Id Name,
                                              String  Role)
        {

            Assert.That(gateway!.ExtAPI.TryGetOrganization(Organization_Id.Parse("Gateway"), out var organization) &&
                        organization is Organization, Is.True, "the gateway's organization is not there");

            var account = await gateway.ExtAPI.CreateUser(
                                    Name,
                                    I18NString.Create(Languages.en, Name.ToString()),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                                  Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(gateway.ExtAPI.TryGetUser(Name, out var stored),           Is.True);
            Assert.That(gateway.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group),  Is.True, $"the gateway has no group '{Role}'");
            Assert.That((await gateway.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!)).IsSuccess,
                        Is.True, $"'{Name}' could not be put in '{Role}'");

            var session = new SshSessionInfo("42",
                                             Name,
                                             "publickey",
                                             null,
                                             IPSocket.Parse("192.0.2.7:50000"),
                                             IPSocket.Parse("10.10.0.1:22"),
                                             null,
                                             SshSessionRestrictions.None);

            return gateway.CommandLines!(new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask),
                                         new CLICaller(stored!, session));

        }

        #endregion

        #region (private static) LineOf(Key) / FingerprintOf(Key)

        private static String LineOf(ISshHostKey Key)

            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private static String FingerprintOf(ISshHostKey Key)

            => SshFingerprint.Sha256(Key.PublicKeyBlob);

        #endregion


        #region AGatewayHasTheStationCommandsOnce_InItsWords()

        /// <summary>
        /// Both commands are there once - at the console, in a command line
        /// derived from the gateway's, and over SSH whichever program runs the
        /// gateway - listed by help as they are typed, and naming the gateway.
        /// </summary>
        [Test]
        public async Task AGatewayHasTheStationCommandsOnce_InItsWords()
        {

            var gw = AGateway();

            using var cli      = new GatewayCLI(gw);
            using var bench    = new GatewayCLITests.BenchCLI(gw, new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask), CLICaller.Console);

            await using var terminal = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var overSSH  = gw.CommandLines!(terminal, CLICaller.Console);

            var help   = await cli.Execute("help");
            var keys   = await overSSH.Execute(StationKeysCommand.CommandName);
            var pins   = await overSSH.Execute(StationPinsCommand.CommandName);

            Assert.Multiple(() => {

                foreach (var commands in new[] { cli.Commands, bench.Commands, overSSH.Commands })
                {
                    Assert.That(commands.Count(command => command is StationKeysCommand), Is.EqualTo(1), "stationKeys");
                    Assert.That(commands.Count(command => command is StationPinsCommand), Is.EqualTo(1), "stationPins");
                }

                Assert.That(help,   Has.Some.StartsWith("stationKeys [<station> [add <authorized_keys line> | remove <fingerprint>]]"));
                Assert.That(help,   Has.Some.StartsWith("stationPins [<station> [add <key pin> [<label>] | remove <key pin>]]"));

                Assert.That(keys,   Has.Some.EqualTo("No charging station of this gateway has an SSH key."));
                Assert.That(keys,   Has.Some.EqualTo("OCPP-over-SSH is switched off: no charging station signs in over SSH."));
                Assert.That(pins,   Has.Some.EqualTo("No charging station of this gateway has a key pin."));

            });

        }

        #endregion

        #region StationKeys_AreGivenListedAndTakenAway_AtTheConsole()

        /// <summary>
        /// A key given to a station the gateway does not list yet lists it in
        /// the default group, is listed with who gave it, is logged, and is
        /// taken away by the beginning of its fingerprint.
        /// </summary>
        [Test]
        public async Task StationKeys_AreGivenListedAndTakenAway_AtTheConsole()
        {

            var gw         = AGateway();
            using var cli  = new GatewayCLI(gw);

            var key        = SshHostKey.GenerateEd25519();

            var added      = await cli.Execute($"stationKeys cs001 add {LineOf(key)}");
            var listed     = await cli.Execute("stationKeys cs001");
            var unknown    = await cli.Execute("stationKeys cs404");

            Assert.Multiple(() => {

                Assert.That(added.First(),  Is.EqualTo($"'cs001' is a charging station of this gateway now, in the group 'default', and signs in over SSH with the key {FingerprintOf(key)}."));
                Assert.That(listed,         Has.Some.StartsWith(FingerprintOf(key)).And.Some.Contains(" by the console"));
                Assert.That(unknown,        Is.EqualTo(new[] { "'cs404' is no charging station of this gateway." }));

                lock (said)
                    Assert.That(said,       Has.Some.EqualTo($"Somebody at the command line gave the charging station 'cs001' the SSH key {FingerprintOf(key)}."));

            });

            var removed    = await cli.Execute($"stationKeys cs001 remove {FingerprintOf(key)["SHA256:".Length..][..10]}");

            Assert.Multiple(() => {
                Assert.That(removed,                                           Is.EqualTo(new[] { $"The key {FingerprintOf(key)} lets 'cs001' in no more." }));
                Assert.That(gw.WebSocketLogins.TryGet(User_Id.Parse("cs001"), out var login),   Is.True);
                Assert.That(login!.HasSSHKey,                                  Is.False);
            });

        }

        #endregion

        #region StationPins_AreGivenListedAndTakenAway_AtTheConsole()

        /// <summary>
        /// A key pin given to a station the gateway does not list yet lists it,
        /// is listed with who gave it and its label, is logged, and is taken
        /// away in another notation than it was given in.
        /// </summary>
        [Test]
        public async Task StationPins_AreGivenListedAndTakenAway_AtTheConsole()
        {

            var gw         = AGateway();
            using var cli  = new GatewayCLI(gw);

            using var key  = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var pin        = KeyPin.FromKey(key);

            var added      = await cli.Execute($"stationPins cs001 add {pin.ToHexWithColons()} bay 1");
            var listed     = await cli.Execute("stationPins cs001");
            var unknown    = await cli.Execute("stationPins cs404");
            var removed    = await cli.Execute($"stationPins cs001 remove {pin.ToCurl()}");

            Assert.Multiple(() => {

                Assert.That(added.First(),  Is.EqualTo($"'cs001' is a charging station of this gateway now, in the group 'default', and is let in by the key pin {pin}."));
                Assert.That(listed.First(), Does.StartWith(pin.ToHex()).And.Contain(" by the console").And.EndWith("bay 1"));
                Assert.That(unknown,        Is.EqualTo(new[] { "'cs404' is no charging station of this gateway." }));
                Assert.That(removed,        Is.EqualTo(new[] { $"The key pin {pin} lets 'cs001' in no more." }));

                lock (said)
                    Assert.That(said,       Has.Some.EqualTo($"Somebody at the command line gave the charging station 'cs001' the key pin {pin}."));

            });

        }

        #endregion

        #region StationCommands_OverSSH_AskForTheGatewaysStations()

        /// <summary>
        /// Over SSH an operator, who may read the gateway's "stations" and not
        /// change them, may list the keys and pins and may give neither; an
        /// administrator may, and the log names who did it.
        /// </summary>
        [Test]
        public async Task StationCommands_OverSSH_AskForTheGatewaysStations()
        {

            await AStartedGateway();

            var key              = SshHostKey.GenerateEd25519();
            var pin              = new String('a', 64);

            using var @operator  = await OverSSHAs(User_Id.Parse("otto"), "operator");
            using var admin      = await OverSSHAs(User_Id.Parse("adam"), "systemadmin");

            var lookedAtKeys     = await @operator.Execute("stationKeys");
            var lookedAtPins     = await @operator.Execute("stationPins");
            var keyRefused       = await @operator.Execute($"stationKeys cs001 add {LineOf(key)}");
            var pinRefused       = await @operator.Execute($"stationPins cs001 add {pin}");
            var keyGiven         = await admin.    Execute($"stationKeys cs001 add {LineOf(key)}");
            var pinGiven         = await admin.    Execute($"stationPins cs001 add {pin}");

            Assert.Multiple(() => {

                Assert.That(lookedAtKeys,       Has.Some.EqualTo("No charging station of this gateway has an SSH key."));
                Assert.That(lookedAtPins,       Has.Some.EqualTo("No charging station of this gateway has a key pin."));
                Assert.That(keyRefused.Single(), Does.StartWith("'otto' may not do that here: it needs the "));
                Assert.That(pinRefused.Single(), Does.StartWith("'otto' may not do that here: it needs the "));
                Assert.That(keyGiven.First(),    Does.StartWith("'cs001' is a charging station of this gateway now"));
                Assert.That(pinGiven.First(),    Does.StartWith("'cs001' is let in by the key pin "));

                Assert.That(gateway!.WebSocketLogins.TryGet(User_Id.Parse("cs001"), out var login) &&
                            login.Keys.Single().CreatedBy == "adam" &&
                            login.Pins.Single().AddedBy   == "adam", Is.True);

                lock (said)
                {
                    Assert.That(said, Has.Some.EqualTo($"'adam' at the command line over SSH gave the charging station 'cs001' the SSH key {FingerprintOf(key)}."));
                    Assert.That(said, Has.Some.Contains("'otto' was refused stations:edit on 'stationKeys'"));
                }

            });

        }

        #endregion

    }

}

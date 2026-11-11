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
using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.Gateway.CommandLine;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// The command line of a gateway: the commands every node has, each of
    /// them once, a prompt that says which gateway it is, and the same for
    /// whoever signs in over SSH whichever program runs the gateway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A gateway has no command of its own yet. What is tested here is what
    /// one will stand on: the command line is the gateway library's, not the
    /// program's, so that a gateway made by a test - or by any program other
    /// than GatewayCLI - is told the same things; and a command line derived
    /// from it, as a program with commands of its own makes one, has its own
    /// commands beside the gateway's and the node's.
    /// </para>
    /// <para>
    /// The commands themselves - syncNTS, sshKeys and the rest - are
    /// WWCP_Node's, and tested there.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class GatewayCLITests
    {

        #region Data

        private String   directory  = "";
        private Gateway  gateway    = default!;

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// A gateway that is made and not started: a command line needs the
        /// gateway, not its port.
        /// </summary>
        [SetUp]
        public void MakeTheGateway()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-cli-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);

            var configuration = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(configuration, """{ "dns": { "enabled": false }, "nts": { "groups": { "legal": { "enabled": false } } } }""");

            gateway = new Gateway(
                          HTTPPort:          IPPort.Parse(TestPorts.Free()),
                          AccountsPath:      Path.Combine(directory, "accounts"),
                          ConfigFile:        new WWCPConfigFile(configuration),
                          CertificatesPath:  Path.Combine(directory, "certificates"),
                          LogToConsole:      false,
                          BridgeDebugLog:    false
                      );

        }

        [TearDown]
        public async Task LetTheGatewayGo()
        {

            await gateway.DisposeAsync();

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


        #region AGatewayHasEveryNodesCommandsOnce()

        /// <summary>
        /// The node's commands and Styx's help and quit, found without being
        /// listed - and each of them once, although the gateway's command line
        /// registers its own assembly beside the node's.
        /// </summary>
        [Test]
        public async Task AGatewayHasEveryNodesCommandsOnce()
        {

            using var cli = new GatewayCLI(gateway);

            var types = cli.Commands.Select(command => command.GetType()).ToArray();

            Assert.Multiple(() => {

                Assert.That(types, Is.Unique, "a command found twice");

                foreach (var type in new[] { typeof(SyncNTSCommand), typeof(SSHKeysCommand), typeof(APIKeysCommand), typeof(SSHAccessRulesCommand), typeof(LogCommand), typeof(WhoCommand), typeof(ListNTSGroupsCommand) })
                    Assert.That(types, Does.Contain(type), type.Name);

                Assert.That(types.Select(type => type.Name), Does.Contain("HelpCommand").And.Contain("QuitCommand"));

            });

            Assert.That(String.Join(" | ", await cli.Execute("syncNTS nowhere.example")),
                        Does.StartWith("'nowhere.example' is none of this gateway's time servers"),
                        "the node's command, in a gateway's words");

        }

        #endregion

        #region ThePromptSaysWhichGatewayItIs()

        /// <summary>
        /// "gateway:" and the port of its web interface, so that one of several
        /// on a bench can be told apart before anything is typed.
        /// </summary>
        [Test]
        public void ThePromptSaysWhichGatewayItIs()
        {

            using var cli = new PromptOf(gateway);

            Assert.That(cli.Prompt, Is.EqualTo($"gateway:{gateway.HTTPPort}> "));

        }

        #endregion

        #region OverSSHAGatewayHasItsCommandLineWhicheverProgramRunsIt()

        /// <summary>
        /// Whoever signs in over SSH gets the gateway's command line without
        /// the program that runs the gateway having to say so: this test is
        /// such a program, and says nothing.
        /// </summary>
        [Test]
        public async Task OverSSHAGatewayHasItsCommandLineWhicheverProgramRunsIt()
        {

            Assert.That(gateway.CommandLines, Is.Not.Null, "a session over SSH would get the node's command line only");

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = gateway.CommandLines!(terminal, CLICaller.Console);

            Assert.That(cli, Is.InstanceOf<GatewayCLI>());
            Assert.That(((GatewayCLI) cli).Gateway, Is.SameAs(gateway));

            Assert.That(String.Join(" | ", await cli.Execute("syncNTS nowhere.example")), Does.Not.StartWith("Unknown command"));

        }

        #endregion

        #region AProgramsOwnCommandsStandBesideTheGateways()

        /// <summary>
        /// A program with commands of its own derives its command line from the
        /// gateway's, registers its type and sets it for SSH anew: its own
        /// command is found in its assembly and told the gateway, and the
        /// node's are still there, once.
        /// </summary>
        [Test]
        public async Task AProgramsOwnCommandsStandBesideTheGateways()
        {

            gateway.CommandLines = (terminal, caller) => new BenchCLI(gateway, terminal, caller);

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = gateway.CommandLines(terminal, CLICaller.Console);

            var types = cli.Commands.Select(command => command.GetType()).ToArray();

            Assert.Multiple(() => {
                Assert.That(types, Is.Unique, "a command found twice");
                Assert.That(types, Does.Contain(typeof(BenchCommand)));
                Assert.That(types, Does.Contain(typeof(SyncNTSCommand)));
            });

            Assert.That(await cli.Execute("benchOnly"), Is.EqualTo(new[] { $"the bench's own, at gateway:{gateway.HTTPPort}" }));

        }

        #endregion


        #region Command lines of tests

        /// <summary>
        /// A gateway's command line whose prompt can be read.
        /// </summary>
        private sealed class PromptOf(Gateway Gateway) : GatewayCLI(Gateway)
        {
            public String Prompt
                => GetPrompt();
        }

        /// <summary>
        /// The command line of a program with a command of its own.
        /// </summary>
        public class BenchCLI : GatewayCLI
        {
            public BenchCLI(Gateway Gateway, ICLITerminal Terminal, CLICaller Caller)
                : base(Gateway, Terminal, Caller)
            {
                RegisterCLIType(typeof(BenchCLI));
            }
        }

        /// <summary>
        /// A command only that program has, which knows the gateway.
        /// </summary>
        public class BenchCommand(BenchCLI CLI) : ACLICommand<BenchCLI>(CLI),
                                                  ICLICommand
        {

            public static readonly String CommandName = "benchOnly";

            public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

                => Arguments.Length == 1 && CommandName.StartsWith(Arguments[0], StringComparison.CurrentCultureIgnoreCase)
                       ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                       : [];

            public override Task<String[]> Execute(String[]           Arguments,
                                                   CancellationToken  CancellationToken)

                => Task.FromResult<String[]>([ $"the bench's own, at gateway:{cli.Gateway.HTTPPort}" ]);

            public override String Help()
                => $"{CommandName} - a command only this program has";

        }

        #endregion

    }

}

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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.Gateway.CommandLine
{

    /// <summary>
    /// The command line of a running gateway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything a command needs is reachable from here, which is why every
    /// command takes one of these: the gateway itself, and through it its
    /// configuration, its log and everything the JSON API can do. A command is
    /// a third way of asking for the same thing, beside the web interface and
    /// the switches at a start - never an implementation of its own.
    /// </para>
    /// <para>
    /// Commands are not listed anywhere. The commands every node has - syncNTS
    /// among them - are the node's, and NodeCLI, which this derives from,
    /// finds them in WWCP_Node. The constructor registers this type as well,
    /// so that this assembly is walked for anything that implements
    /// ICLICommand and can be built from a GatewayCLI: a command of the
    /// gateway's own is a new file in this directory and nothing else. There
    /// is none yet.
    /// </para>
    /// <para>
    /// Here, beside the gateway, and not in the program that runs it: what a
    /// gateway can be told is the gateway's, and is told over SSH as well as at
    /// the console. The gateway hands it to whoever signs in over SSH by itself
    /// (see Gateway.cs), so that a gateway run by another program - a test -
    /// has the same commands as one run by the program GatewayCLI. A program
    /// with commands of its own derives from this one and sets
    /// WWCPNode.CommandLines anew.
    /// </para>
    /// </remarks>
    public class GatewayCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The gateway these commands are about.
        /// </summary>
        public Gateway Gateway { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given gateway.
        /// </summary>
        /// <param name="Gateway">The running gateway.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and WWCP_Node are searched either way.</param>
        public GatewayCLI(Gateway            Gateway,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Gateway, AssembliesWithCLICommands)

        {

            this.Gateway = Gateway;

            RegisterCLIType(typeof(GatewayCLI));

        }

        /// <summary>
        /// Create the command line of the given gateway on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        /// <param name="Gateway">The running gateway.</param>
        /// <param name="Terminal">What the command line is typed at and written on.</param>
        /// <param name="Caller">Who is typing at it.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and the node's are searched either way.</param>
        public GatewayCLI(Gateway            Gateway,
                          ICLITerminal       Terminal,
                          CLICaller          Caller,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Gateway, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.Gateway = Gateway;

            RegisterCLIType(typeof(GatewayCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// Which port this gateway answers on, because a machine that is one of
        /// several on a bench should say which one it is before it asks for a
        /// command.
        /// </summary>
        protected override String GetPrompt()

            => $"gateway:{Gateway.HTTPPort}> ";

        #endregion

    }

}

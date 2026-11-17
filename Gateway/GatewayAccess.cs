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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// What a gateway adds to the resources every node has, and the roles of
    /// the people around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything and are the only ones who may
    /// change anything - the certificates included, where that matters most:
    /// somebody who can add a root can make this gateway believe a time server
    /// or a name server nobody else would. A gateway adds two resources - its
    /// connections, under the name a charging station gives them, and the
    /// stations below it, under the name a local controller gives them - and
    /// one role: the operator, who runs it day to day.
    /// </para>
    /// <para>
    /// The configuration file may add roles to these and say differently what
    /// one of them may do - see the node's "roles" section. What is written
    /// here is what a gateway is when its file says nothing.
    /// </para>
    /// </remarks>
    public static class GatewayAccess
    {

        #region Resources

        /// <summary>
        /// Where this gateway dials, what it proves itself with there - the
        /// credentials, and the SSH keys made here - and where each connection
        /// stands: edited, any of that changed; run, a connection tested. The
        /// name and the meaning a charging station gives it, which the pages of
        /// the connections ask for.
        /// </summary>
        public const String  Connections  = "connections";

        /// <summary>
        /// The server the charging stations and local controllers below this
        /// gateway connect to, and the stations that may sign in to it with
        /// their login groups: read, looked at; edited, any of that changed. The
        /// name and the meaning a local controller gives it, which the pages of
        /// the station server ask for. The keys and certificates the server
        /// presents and the chains it accepts are the node's "certificates".
        /// </summary>
        public const String  Stations     = "stations";

        /// <summary>
        /// Its connections and the stations below it. Its configuration, its
        /// name servers, its time servers and its certificates are resources
        /// every node has.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ Connections, Stations ];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever runs this gateway day to day: may look at everything and ask
        /// whether the network works, and whether a connection would get
        /// through - but may not repoint it at other name and time servers or
        /// at another CSMS, may not change the certificates or the credentials
        /// it holds them to, and may not move its SSH server or let passwords
        /// open it.
        /// </summary>
        public static readonly Role  Operator  = new ("operator",
                                                      [ Permission.Read(Permission.AnyResource),
                                                        Permission.Run (NodeResources.DNS),
                                                        Permission.Run (NodeResources.NTS),
                                                        Permission.Run (Connections) ],
                                                      "runs the gateway day to day: looks at everything, and asks whether the network and its connections work");

        /// <summary>
        /// The one role a gateway adds to the node's viewer and administrators.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator ];

        #endregion

    }

}

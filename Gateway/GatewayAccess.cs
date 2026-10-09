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
    /// or a name server nobody else would. A gateway adds no resource of its
    /// own yet - what it is for, handing frames on between charging stations
    /// and whatever they talk to, is not here - and one role: the operator,
    /// who runs it day to day.
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
        /// None of its own yet: its configuration, its name servers and its
        /// time servers are resources every node has.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever runs this gateway day to day: may look at everything and ask
        /// whether the network works - but may not repoint it at other name and
        /// time servers, may not change the certificates it holds them to, and
        /// may not move its SSH server or let passwords open it.
        /// </summary>
        public static readonly Role  Operator  = new ("operator",
                                                      [ Permission.Read(Permission.AnyResource),
                                                        Permission.Run (NodeResources.DNS),
                                                        Permission.Run (NodeResources.NTS) ],
                                                      "runs the gateway day to day: looks at everything, and asks whether the network works");

        /// <summary>
        /// The one role a gateway adds to the node's viewer and administrators.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator ];

        #endregion

    }

}

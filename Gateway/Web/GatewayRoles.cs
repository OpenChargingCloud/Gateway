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

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.Gateway.Web
{

    /// <summary>
    /// The roles somebody may sign in to a gateway as.
    /// </summary>
    /// <remarks>
    /// The gateway's own rather than the node's, because what a role may do is
    /// what the kind of node does: a node told nothing knows a vehicle's -
    /// a driver who charges it and a service who commissions it - and nobody
    /// charges anything at a gateway. Handed to the node by their names, as
    /// its <see cref="WWCPNode.Roles"/>, which makes one user group per role
    /// at every start; what each of them may do is the gateway's to say, and
    /// its JSON API checks it on every request.
    /// </remarks>
    public static class GatewayRoles
    {

        /// <summary>
        /// May look at this gateway, and do nothing to it.
        /// </summary>
        public static readonly UserRole                 Viewer       = UserRole.Viewer;

        /// <summary>
        /// Whoever runs this gateway day to day: may ask whether the network
        /// works - but may not repoint it at other name and time servers.
        /// </summary>
        public static readonly UserRole                 Operator     = new ("operator",
                                                                            Permissions.ReadConfiguration      |
                                                                            Permissions.RunDiagnostics);

        /// <summary>
        /// Everything this gateway can be told, by whoever is trusted with all
        /// of it at once.
        /// </summary>
        /// <remarks>
        /// Under the node's name for it, <see cref="WWCPNode.AdminRole"/>, which
        /// is the group the account of a first start is put in - and, where
        /// several of these programs share one sign-in, the group whose members
        /// administer all of them. What it may do here is the gateway's:
        /// nothing about charging, because there is none.
        /// </remarks>
        public static readonly UserRole                 SystemAdmin  = new (WWCPNode.AdminRole,
                                                                            Permissions.ReadConfiguration      |
                                                                            Permissions.ChangeNetworkSettings  |
                                                                            Permissions.RunDiagnostics);

        /// <summary>
        /// Every role this gateway knows.
        /// </summary>
        public static readonly IReadOnlyList<UserRole>  All          = [ Viewer, Operator, SystemAdmin ];

    }

}
